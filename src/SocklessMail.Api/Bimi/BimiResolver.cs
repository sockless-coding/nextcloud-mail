using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.Bimi;

/// <summary>What a BIMI lookup for one domain and selector found. Shared by all users: it is public DNS and web content.</summary>
public sealed class BimiLookup
{
    public required string Domain { get; init; }
    public required string Selector { get; init; }

    /// <summary>The logo (<see cref="BimiLogo.Hash"/>); null when the domain has none to show.</summary>
    public string? LogoHash { get; set; }

    public BimiOutcome Outcome { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A validated logo, stored once however many domains point to it.</summary>
public sealed class BimiLogo
{
    /// <summary>SHA-256 of <see cref="Svg"/>, lower-case hex.</summary>
    public required string Hash { get; init; }

    public required byte[] Svg { get; init; }
}

public enum BimiOutcome
{
    Logo,
    /// <summary>No DMARC policy, or one that does not quarantine or reject.</summary>
    NotEnforced,
    NoRecord,
    /// <summary>A BIMI record without a logo location (an explicit "no logo"), or one that is not HTTPS.</summary>
    Declined,
    /// <summary>The logo is too large, not SVG, or not a safe static SVG.</summary>
    InvalidLogo,
    /// <summary>DNS or the logo server failed; tried again soon.</summary>
    Failed,
}

/// <summary>
/// Finds the BIMI logo of a sender domain and caches it in the database, so DNS and the logo's
/// server are asked about each domain once a day, not once per message. A lookup checks that the
/// domain's DMARC policy is enforced (found with the DMARCbis tree walk), reads the BIMI record at
/// the domain or, failing that, at its organizational domain (where the DMARC policy was found),
/// then fetches the SVG through <see cref="PolicySocketConnector"/> and validates it
/// (<see cref="SvgLogo"/>). Concurrent requests for one domain share a single lookup.
/// </summary>
public sealed class BimiResolver(IServiceScopeFactory scopes, IBimiDns dns, IHttpClientFactory httpClients, TimeProvider clock, ILogger<BimiResolver> logger)
{
    public const string HttpClientName = "bimi";

    public static readonly TimeSpan LogoLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan NegativeLifetime = TimeSpan.FromHours(6);
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromHours(1);

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(string Domain, string Selector), Lazy<Task<BimiLogo?>>> _inflight = new();

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IBimiDns, SystemBimiDns>();
        services.AddSingleton<BimiSenders>();
        services.AddSingleton<BimiResolver>();
        services.AddHttpClient(HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SocklessMail-BIMI)");
                client.DefaultRequestHeaders.Accept.ParseAdd("image/svg+xml,*/*;q=0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(PolicyHttp.CreateHandler);
    }

    /// <returns>The logo, or null when the domain has none that may be shown.</returns>
    public async Task<BimiLogo?> GetAsync(string domain, string selector, CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            var lookup = await db.BimiLookups.AsNoTracking().SingleOrDefaultAsync(l => l.Domain == domain && l.Selector == selector, ct);
            if (lookup is not null && lookup.ExpiresAt > clock.GetUtcNow())
            {
                return lookup.LogoHash is { } hash ? await db.BimiLogos.AsNoTracking().SingleOrDefaultAsync(l => l.Hash == hash, ct) : null;
            }
        }

        // Not bound to this request: other requests may be waiting for the same lookup.
        var key = (domain, selector);
        return await _inflight.GetOrAdd(key, k => new Lazy<Task<BimiLogo?>>(() => RefreshAsync(k.Domain, k.Selector))).Value.WaitAsync(ct);
    }

    /// <summary>Drops expired lookups and the logos no lookup points to any more.</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var now = clock.GetUtcNow();
        await db.BimiLookups.Where(l => l.ExpiresAt < now).ExecuteDeleteAsync(ct);
        await db.BimiLogos.Where(logo => !db.BimiLookups.Any(l => l.LogoHash == logo.Hash)).ExecuteDeleteAsync(ct);
    }

    private async Task<BimiLogo?> RefreshAsync(string domain, string selector)
    {
        try
        {
            using var timeout = new CancellationTokenSource(LookupTimeout);
            BimiOutcome outcome;
            byte[]? svg = null;
            try
            {
                (outcome, svg) = await LookUpAsync(domain, selector, timeout.Token);
            }
            catch (Exception e) when (e is BimiDnsException or OperationCanceledException)
            {
                logger.LogInformation("BIMI lookup for {Domain} failed ({ExceptionType})", domain, e.GetType().Name);
                outcome = BimiOutcome.Failed;
            }

            return await StoreAsync(domain, selector, outcome, svg);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "BIMI lookup for {Domain} could not be stored", domain);
            return null;
        }
        finally
        {
            _inflight.TryRemove((domain, selector), out _);
        }
    }

    private async Task<(BimiOutcome, byte[]?)> LookUpAsync(string domain, string selector, CancellationToken ct)
    {
        if (await FindDmarcAsync(domain, ct) is not { } dmarc || !dmarc.Record.Enforces(dmarc.Domain == domain))
        {
            return (BimiOutcome.NotEnforced, null);
        }

        var record = await FindBimiAsync($"{selector}._bimi.{domain}", ct);
        if (record is null && OrganizationalDomain(domain, dmarc.Domain, dmarc.Record) is { } org && org != domain)
        {
            record = await FindBimiAsync($"{selector}._bimi.{org}", ct);
        }

        if (record is null)
        {
            return (BimiOutcome.NoRecord, null);
        }

        if (record.Location is null || !Uri.TryCreate(record.Location, UriKind.Absolute, out var location) || location.Scheme != Uri.UriSchemeHttps)
        {
            return (BimiOutcome.Declined, null);
        }

        return await FetchAsync(location, ct);
    }

    private async Task<(DmarcRecord Record, string Domain)?> FindDmarcAsync(string domain, CancellationToken ct)
    {
        foreach (var candidate in BimiRecords.TreeWalk(domain))
        {
            if (BimiRecords.ParseDmarc(await dns.TxtAsync($"_dmarc.{candidate}", ct)) is { } record)
            {
                return (record, candidate);
            }
        }

        return null;
    }

    private async Task<BimiRecord?> FindBimiAsync(string name, CancellationToken ct) =>
        BimiRecords.ParseBimi(await dns.TxtAsync(name, ct));

    /// <summary>
    /// Where the DMARC policy was found; for a public suffix domain's policy (psd=y), the domain
    /// one label below it, so a logo never comes from a registry for its customers' subdomains.
    /// </summary>
    private static string? OrganizationalDomain(string domain, string policyDomain, DmarcRecord record)
    {
        if (!record.PublicSuffix)
        {
            return policyDomain;
        }

        var labels = domain.Split('.');
        var policyLabels = policyDomain.Split('.').Length;
        return labels.Length > policyLabels ? string.Join('.', labels[^(policyLabels + 1)..]) : null;
    }

    private async Task<(BimiOutcome, byte[]?)> FetchAsync(Uri location, CancellationToken ct)
    {
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).GetAsync(location, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return (BimiOutcome.Failed, null);
            }

            if (response.Content.Headers.ContentLength > SvgLogo.MaxBytes)
            {
                return (BimiOutcome.InvalidLogo, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > SvgLogo.MaxBytes)
                {
                    return (BimiOutcome.InvalidLogo, null);
                }

                buffer.Write(chunk, 0, read);
            }

            var svg = buffer.ToArray();
            return SvgLogo.IsSafe(svg) ? (BimiOutcome.Logo, svg) : (BimiOutcome.InvalidLogo, null);
        }
        catch (Exception e) when (e is HttpRequestException or MailConnectionException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("BIMI logo could not be fetched from {Host} ({ExceptionType})", location.Host, e.GetType().Name);
            return (BimiOutcome.Failed, null);
        }
    }

    private async Task<BimiLogo?> StoreAsync(string domain, string selector, BimiOutcome outcome, byte[]? svg)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var now = clock.GetUtcNow();
        var lookup = await db.BimiLookups.SingleOrDefaultAsync(l => l.Domain == domain && l.Selector == selector);
        if (lookup is null)
        {
            lookup = new BimiLookup { Domain = domain, Selector = selector };
            db.BimiLookups.Add(lookup);
        }

        BimiLogo? logo = null;
        if (svg is not null)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(svg));
            logo = await db.BimiLogos.SingleOrDefaultAsync(l => l.Hash == hash);
            if (logo is null)
            {
                logo = new BimiLogo { Hash = hash, Svg = svg };
                db.BimiLogos.Add(logo);
            }

            lookup.LogoHash = hash;
        }
        else if (outcome != BimiOutcome.Failed || lookup.LogoHash is null)
        {
            lookup.LogoHash = null;
        }
        else
        {
            // A passing DNS or web server failure keeps the logo already known.
            logo = await db.BimiLogos.SingleOrDefaultAsync(l => l.Hash == lookup.LogoHash);
        }

        lookup.Outcome = outcome;
        lookup.CheckedAt = now;
        lookup.ExpiresAt = now + outcome switch
        {
            BimiOutcome.Logo => LogoLifetime,
            BimiOutcome.Failed => FailureLifetime,
            _ => NegativeLifetime,
        };
        await db.SaveChangesAsync();
        return logo;
    }
}
