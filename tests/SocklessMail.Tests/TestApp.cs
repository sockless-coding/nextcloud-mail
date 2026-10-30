using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Data;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Rules;
using SocklessMail.Api.Senders;
using SocklessMail.Api.Sync;

namespace SocklessMail.Tests;

/// <summary>
/// The real app on the test server, with its own storage directory (database and key ring) and a
/// fake Nextcloud. Pass a <paramref name="storageDirectory"/> to start a second instance over the
/// same data, e.g. to simulate a restart with different settings.
/// </summary>
public sealed class TestApp(string? storageDirectory = null, IReadOnlyDictionary<string, string>? settings = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    public const string Secret = "test-secret-0123456789abcdefghijklmnop";

    public static readonly AppApiOptions Options = new()
    {
        AppId = "sockless_mail",
        AppVersion = "0.1.0",
        AppSecret = Secret,
        NextcloudUrl = new Uri("http://nextcloud.test/"),
        AppPort = 9080,
    };

    private readonly bool _ownsStorage = storageDirectory is null;
    private readonly Action<IServiceCollection>? _configureServices = services;

    public string StorageDirectory { get; } =
        storageDirectory ?? Path.Combine(Path.GetTempPath(), "sockless-mail-tests", Guid.NewGuid().ToString("N"));

    public FakeNextcloud Nextcloud { get; } = new();

    /// <summary>Every OAuth2 provider's token endpoint.</summary>
    public FakeTokenEndpoint TokenEndpoint { get; } = new();

    /// <summary>IANA's RDAP bootstrap file and the registries: no domain is known unless a test adds it.</summary>
    public FakeRdap Rdap { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseSetting("APP_ID", Options.AppId);
        builder.UseSetting("APP_VERSION", Options.AppVersion);
        builder.UseSetting("APP_SECRET", Options.AppSecret);
        builder.UseSetting("NEXTCLOUD_URL", Options.NextcloudUrl.ToString());
        builder.UseSetting("APP_PORT", Options.AppPort.ToString());
        builder.UseSetting("APP_PERSISTENT_STORAGE", StorageDirectory);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<NextcloudClient>().ConfigurePrimaryHttpMessageHandler(() => Nextcloud);
            services.AddHttpClient(OAuthTokens.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => TokenEndpoint);
            services.AddHttpClient(DomainAges.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Rdap);
            // Test accounts point at made-up servers; no background IMAP connections.
            services.AddSingleton(new WatcherOptions { MaxWatchers = 0 });
            services.AddSingleton(new RuleRunnerOptions { Interval = TimeSpan.Zero });
            services.AddSingleton(new SocklessMail.Api.Tags.TagScannerOptions { Interval = TimeSpan.Zero });
            _configureServices?.Invoke(services);
        });
    }

    /// <summary>A client that sends what HaRP or the PHP proxy would forward for <paramref name="userId"/>.</summary>
    public HttpClient ClientAs(string userId, string secret = Secret, string appId = "sockless_mail")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(AppApiHeaders.ExAppId, appId);
        client.DefaultRequestHeaders.Add(AppApiHeaders.ExAppVersion, "0.1.0");
        client.DefaultRequestHeaders.Add("AA-VERSION", "32");
        client.DefaultRequestHeaders.Add(AppApiHeaders.Authorization, AppApiHeaders.BuildAuthorization(userId, secret));
        // What the frontend sends on every request (CSRF guard).
        client.DefaultRequestHeaders.Add("X-Sockless-Request", "1");
        return client;
    }

    /// <summary>A push connection over WebSockets with what HaRP would forward (null: no AppAPI headers).</summary>
    public HubConnection HubAs(string? userId)
    {
        var headers = userId is null ? [] : new Dictionary<string, string>
        {
            [AppApiHeaders.ExAppId] = "sockless_mail",
            [AppApiHeaders.ExAppVersion] = "0.1.0",
            [AppApiHeaders.Authorization] = AppApiHeaders.BuildAuthorization(userId, Secret),
            ["X-Sockless-Request"] = "1",
        };
        var server = Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/api/hub"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                foreach (var (name, value) in headers)
                {
                    options.Headers[name] = value;
                }

                options.WebSocketFactory = async (context, ct) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        foreach (var (name, value) in headers)
                        {
                            request.Headers[name] = value;
                        }
                    };
                    return await client.ConnectAsync(context.Uri, ct);
                };
            })
            .Build();
    }

    /// <summary>A DbContext acting for <paramref name="userId"/> (null: no user, as in background work).</summary>
    public MailDbContext CreateDbContext(string? userId)
    {
        var options = Services.CreateScope().ServiceProvider.GetRequiredService<DbContextOptions<MailDbContext>>();
        return new MailDbContext(options, new FixedUser(userId));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsStorage)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(StorageDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A lingering handle on Windows; the temp directory is cleaned up eventually.
            }
        }
    }

    private sealed class FixedUser(string? userId) : ICurrentUser
    {
        public string? UserId => userId;
    }
}

public sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>Records every OCS call and answers with a canned response per path.</summary>
public sealed class FakeNextcloud : HttpMessageHandler
{
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Response for any path containing the key; default is an empty OCS success.</summary>
    public ConcurrentDictionary<string, (HttpStatusCode Status, string Json)> Responses { get; } = new();

    /// <summary>Asked first, e.g. by <see cref="FakeDav"/>; null falls back to <see cref="Responses"/>.</summary>
    public Func<RecordedRequest, HttpResponseMessage?>? Respond { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, headers, body);
        Requests.Enqueue(recorded);
        if (Respond?.Invoke(recorded) is { } custom)
        {
            return custom;
        }

        var (status, json) = Responses.FirstOrDefault(r => request.RequestUri.AbsolutePath.Contains(r.Key)).Value;
        return new HttpResponseMessage(status == 0 ? HttpStatusCode.OK : status)
        {
            Content = new StringContent(json ?? """{"ocs":{"meta":{"status":"ok"},"data":[]}}""", Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>Answers token requests with <see cref="Respond"/> (default: a fresh grant for alice@gmail.com) and records the forms.</summary>
public sealed class FakeTokenEndpoint : HttpMessageHandler
{
    public ConcurrentQueue<(Uri Url, Dictionary<string, string> Form)> Requests { get; } = new();

    public Func<Dictionary<string, string>, (HttpStatusCode Status, object Body)> Respond { get; set; } =
        _ => (HttpStatusCode.OK, Grant("alice@gmail.com"));

    /// <summary>A token response with an (unsigned) ID token for <paramref name="email"/>.</summary>
    public static object Grant(string email, string accessToken = "access-1", string? refreshToken = "refresh-1", int expiresIn = 3600) => new
    {
        access_token = accessToken,
        refresh_token = refreshToken,
        expires_in = expiresIn,
        token_type = "Bearer",
        id_token = IdToken(new { email, name = "Alice Example" }),
    };

    public static string IdToken(object claims) =>
        $"e30.{Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims))}.sig";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Requests.Enqueue((request.RequestUri!, form));
        var (status, body) = Respond(form);
        return new HttpResponseMessage(status) { Content = JsonContent.Create(body) };
    }
}

/// <summary>
/// RDAP for the tests: a bootstrap file sending ".com", ".net" and ".example" to one registry, which knows
/// the domains in <see cref="Registered"/> (others are 404).
/// </summary>
public sealed class FakeRdap : HttpMessageHandler
{
    public const string Server = "https://rdap.registry.test/v1/";

    public ConcurrentDictionary<string, DateTimeOffset> Registered { get; } = new();
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Answers instead, when set (e.g. 429 or 500 for a failing registry).</summary>
    public HttpStatusCode? Status { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requests.Enqueue(url);
        if (Status is { } status)
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }

        object? body = null;
        if (url == DomainAges.BootstrapUrl)
        {
            body = new { version = "1.0", services = new object[] { new object[] { new[] { "com", "net", "example" }, new[] { Server } } } };
        }
        else if (url.StartsWith($"{Server}domain/", StringComparison.Ordinal)
                 && Registered.TryGetValue(url[($"{Server}domain/".Length)..], out var registered))
        {
            body = new
            {
                objectClassName = "domain",
                events = new[] { new { eventAction = "last changed", eventDate = "2026-10-01T00:00:00Z" }, new { eventAction = "registration", eventDate = registered.ToString("O") } },
            };
        }

        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
    }
}
