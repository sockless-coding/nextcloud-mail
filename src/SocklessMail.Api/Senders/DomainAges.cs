using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.Senders;

/// <summary>
/// When a registered domain (<see cref="SenderDomain.Registered"/>) was registered, as its registry's
/// RDAP server says. Shared by all users: it is public registry data.
/// </summary>
public sealed class DomainAge
{
    public required string Domain { get; init; }

    /// <summary>The "registration" event; null unless <see cref="Outcome"/> is <see cref="DomainAgeOutcome.Found"/>.</summary>
    public DateTimeOffset? RegisteredAt { get; set; }

    public DomainAgeOutcome Outcome { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public enum DomainAgeOutcome
{
    Found,
    /// <summary>No RDAP server for the ending, the registry does not know the domain, or says nothing about its registration.</summary>
    Unknown,
    /// <summary>The bootstrap file or the RDAP server failed (or limited us); tried again soon.</summary>
    Failed,
}

/// <summary>
/// How old sender domains are, for the "newly registered domain" warning. Lookups go to the domain's
/// registry over RDAP (RFC 9082/9083), found through IANA's bootstrap file (RFC 9224, kept for a week),
/// and are stored in <see cref="MailDbContext.DomainAges"/>: a date for 30 days, no answer for a day.
/// The sender checks never wait for a registry: <see cref="FindAsync"/> answers from the database and
/// queues what is missing, and a few lookups at a time run in the background
/// (<see cref="ExecuteAsync"/>). Admins can turn this off (<see cref="AppSettings.DomainAgeChecks"/>);
/// then no registry is asked.
/// </summary>
public sealed class DomainAges(
    IServiceScopeFactory scopes, AppSettings settings, IHttpClientFactory httpClients, TimeProvider clock, ILogger<DomainAges> logger)
    : BackgroundService
{
    public const string HttpClientName = "rdap";
    public const string BootstrapUrl = "https://data.iana.org/rdap/dns.json";

    public static readonly TimeSpan FoundLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan UnknownLifetime = TimeSpan.FromDays(1);
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan BootstrapLifetime = TimeSpan.FromDays(7);

    public const int MaxQueued = 500;
    public const int MaxParallelLookups = 4;
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);

    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueued)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    /// <summary>Queued or being looked up: not queued twice.</summary>
    private readonly ConcurrentDictionary<string, byte> _queued = new();

    private readonly SemaphoreSlim _bootstrapLock = new(1, 1);
    /// <summary>The bootstrap file's servers (null while it could never be fetched), and when to fetch it again.</summary>
    private sealed record Bootstrap(IReadOnlyDictionary<string, Uri>? Servers, DateTimeOffset ValidUntil);

    private volatile Bootstrap? _bootstrap;

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<DomainAges>();
        services.AddHostedService(sp => sp.GetRequiredService<DomainAges>());
        services.AddHttpClient(HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SocklessMail-RDAP)");
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rdap+json"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.5));
            })
            .ConfigurePrimaryHttpMessageHandler(PolicyHttp.CreateHandler);
    }

    /// <summary>
    /// What is known about these registered domains, from the database. Those without a current answer
    /// are queued for a lookup and left out. Null when admins turned the lookups off.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DomainAge>?> FindAsync(IReadOnlyCollection<string> domains, CancellationToken ct)
    {
        if (!await settings.DomainAgeChecksAsync(ct))
        {
            return null;
        }

        if (domains.Count == 0)
        {
            return new Dictionary<string, DomainAge>();
        }

        var now = clock.GetUtcNow();
        Dictionary<string, DomainAge> found;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            var list = domains.ToList();
            found = (await db.DomainAges.AsNoTracking().Where(a => list.Contains(a.Domain)).ToListAsync(ct))
                .Where(a => a.ExpiresAt > now)
                .ToDictionary(a => a.Domain);
        }

        foreach (var domain in domains.Where(d => !found.ContainsKey(d)))
        {
            if (_queued.TryAdd(domain, 0) && !_queue.Writer.TryWrite(domain))
            {
                // Full: asked again with the next check.
                _queued.TryRemove(domain, out _);
            }
        }

        return found;
    }

    /// <summary>Whole days since <paramref name="age"/>'s registration; null when unknown (or dated in the future, which no registry means).</summary>
    public int? DaysOld(DomainAge? age) =>
        age?.RegisteredAt is { } registered && clock.GetUtcNow() - registered is { Ticks: >= 0 } old ? (int)old.TotalDays : null;

    /// <summary>Drops answers that expired a while ago (a fresh one would be asked for anyway).</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var before = clock.GetUtcNow() - UnknownLifetime;
        await db.DomainAges.Where(a => a.ExpiresAt < before).ExecuteDeleteAsync(ct);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Parallel.ForEachAsync(
            _queue.Reader.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLookups, CancellationToken = stoppingToken },
            async (domain, ct) =>
            {
                try
                {
                    await RefreshAsync(domain, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Domain age of {Domain} could not be stored", domain);
                }
                finally
                {
                    _queued.TryRemove(domain, out _);
                }
            });

    /// <summary>Asks the registry about <paramref name="domain"/> and stores the answer.</summary>
    internal async Task<DomainAge> RefreshAsync(string domain, CancellationToken ct)
    {
        DomainAgeOutcome outcome;
        DateTimeOffset? registered = null;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(LookupTimeout);
            try
            {
                (outcome, registered) = await LookUpAsync(domain, timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException or MailConnectionException or JsonException
                                          || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogInformation("RDAP lookup for {Domain} failed ({ExceptionType})", domain, e.GetType().Name);
                outcome = DomainAgeOutcome.Failed;
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var now = clock.GetUtcNow();
        var age = await db.DomainAges.SingleOrDefaultAsync(a => a.Domain == domain, ct);
        if (age is null)
        {
            age = new DomainAge { Domain = domain };
            db.DomainAges.Add(age);
        }

        if (outcome != DomainAgeOutcome.Failed || age.Outcome != DomainAgeOutcome.Found)
        {
            age.Outcome = outcome;
            age.RegisteredAt = registered;
        }

        // A passing failure keeps a date already known (it does not change), for another hour.
        age.CheckedAt = now;
        age.ExpiresAt = now + outcome switch
        {
            DomainAgeOutcome.Failed => FailureLifetime,
            DomainAgeOutcome.Found => FoundLifetime,
            _ => UnknownLifetime,
        };
        await db.SaveChangesAsync(ct);
        return age;
    }

    private async Task<(DomainAgeOutcome, DateTimeOffset?)> LookUpAsync(string domain, CancellationToken ct)
    {
        if (await ServerForAsync(domain, ct) is not { } server)
        {
            return (DomainAgeOutcome.Unknown, null);
        }

        using var response = await httpClients.CreateClient(HttpClientName).GetAsync(new Uri(server, $"domain/{domain}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (DomainAgeOutcome.Unknown, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("RDAP server {Host} answered {Status} for {Domain}", server.Host, (int)response.StatusCode, domain);
            return (DomainAgeOutcome.Failed, null);
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return RdapRegistration(json.RootElement) is { } registered ? (DomainAgeOutcome.Found, registered) : (DomainAgeOutcome.Unknown, null);
    }

    /// <summary>The RDAP base URL for <paramref name="domain"/>'s ending, from the bootstrap file; null when there is none.</summary>
    private async Task<Uri?> ServerForAsync(string domain, CancellationToken ct)
    {
        var servers = await BootstrapAsync(ct);
        var labels = domain.Split('.');
        // Longest matching ending first; the file lists top-level domains today.
        for (var i = 1; i < labels.Length; i++)
        {
            if (servers.TryGetValue(string.Join('.', labels[i..]), out var server))
            {
                return server;
            }
        }

        return null;
    }

    private async Task<IReadOnlyDictionary<string, Uri>> BootstrapAsync(CancellationToken ct)
    {
        if (Current() is { } servers)
        {
            return servers;
        }

        await _bootstrapLock.WaitAsync(ct);
        try
        {
            if (Current() is { } fresh)
            {
                return fresh;
            }

            try
            {
                using var response = await httpClients.CreateClient(HttpClientName).GetAsync(BootstrapUrl, ct);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var parsed = ParseBootstrap(json.RootElement);
                _bootstrap = new Bootstrap(parsed, clock.GetUtcNow() + BootstrapLifetime);
                return parsed;
            }
            catch (Exception e) when (e is HttpRequestException or MailConnectionException or JsonException
                                          || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Keep using an old file if there is one; ask IANA again in a while.
                logger.LogInformation("RDAP bootstrap file could not be fetched ({ExceptionType})", e.GetType().Name);
                _bootstrap = new Bootstrap(_bootstrap?.Servers, clock.GetUtcNow() + FailureLifetime);
                return _bootstrap.Servers ?? throw new HttpRequestException("No RDAP bootstrap file.", e);
            }
        }
        finally
        {
            _bootstrapLock.Release();
        }

        IReadOnlyDictionary<string, Uri>? Current()
        {
            if (_bootstrap is not { } bootstrap || clock.GetUtcNow() >= bootstrap.ValidUntil)
            {
                // None yet, or due again: fetch it (if that fails, an old file stays in use).
                return null;
            }

            return bootstrap.Servers ?? throw new HttpRequestException("No RDAP bootstrap file.");
        }
    }

    /// <summary>Ending → RDAP base URL (HTTPS only, ending in a slash) from IANA's dns.json.</summary>
    internal static Dictionary<string, Uri> ParseBootstrap(JsonElement root)
    {
        var servers = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array)
        {
            return servers;
        }

        foreach (var service in services.EnumerateArray())
        {
            if (service is not { ValueKind: JsonValueKind.Array } || service.GetArrayLength() < 2
                || service[0].ValueKind != JsonValueKind.Array || service[1].ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var url = service[1].EnumerateArray()
                .Select(u => u.ValueKind == JsonValueKind.String ? u.GetString() : null)
                .Select(u => Uri.TryCreate(u?.EndsWith('/') == true ? u : $"{u}/", UriKind.Absolute, out var uri) ? uri : null)
                .FirstOrDefault(u => u?.Scheme == Uri.UriSchemeHttps);
            if (url is null)
            {
                continue;
            }

            foreach (var ending in service[0].EnumerateArray())
            {
                if (ending.ValueKind == JsonValueKind.String && ending.GetString() is { Length: > 0 } name)
                {
                    servers[name.ToLowerInvariant()] = url;
                }
            }
        }

        return servers;
    }

    /// <summary>The date of the "registration" event in an RDAP domain object.</summary>
    internal static DateTimeOffset? RdapRegistration(JsonElement domain)
    {
        if (domain.ValueKind != JsonValueKind.Object || !domain.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var e in events.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("eventAction", out var action) && action.ValueKind == JsonValueKind.String
                && string.Equals(action.GetString(), "registration", StringComparison.OrdinalIgnoreCase)
                && e.TryGetProperty("eventDate", out var date) && date.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(date.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var registered))
            {
                return registered.ToUniversalTime();
            }
        }

        return null;
    }
}
