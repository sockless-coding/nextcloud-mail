using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Tests;

/// <summary>The real app on the test server, talking to a fake Nextcloud.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
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

    public FakeNextcloud Nextcloud { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseSetting("APP_ID", Options.AppId);
        builder.UseSetting("APP_VERSION", Options.AppVersion);
        builder.UseSetting("APP_SECRET", Options.AppSecret);
        builder.UseSetting("NEXTCLOUD_URL", Options.NextcloudUrl.ToString());
        builder.UseSetting("APP_PORT", Options.AppPort.ToString());
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<NextcloudClient>().ConfigurePrimaryHttpMessageHandler(() => Nextcloud));
    }

    /// <summary>A client that sends what HaRP or the PHP proxy would forward for <paramref name="userId"/>.</summary>
    public HttpClient ClientAs(string userId, string secret = Secret, string appId = "sockless_mail")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(AppApiHeaders.ExAppId, appId);
        client.DefaultRequestHeaders.Add(AppApiHeaders.ExAppVersion, "0.1.0");
        client.DefaultRequestHeaders.Add("AA-VERSION", "32");
        client.DefaultRequestHeaders.Add(AppApiHeaders.Authorization, AppApiHeaders.BuildAuthorization(userId, secret));
        return client;
    }
}

public sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>Records every OCS call and answers with a canned response per path.</summary>
public sealed class FakeNextcloud : HttpMessageHandler
{
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Response for any path containing the key; default is an empty OCS success.</summary>
    public ConcurrentDictionary<string, (HttpStatusCode Status, string Json)> Responses { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, headers, body));

        var (status, json) = Responses.FirstOrDefault(r => request.RequestUri.AbsolutePath.Contains(r.Key)).Value;
        return new HttpResponseMessage(status == 0 ? HttpStatusCode.OK : status)
        {
            Content = new StringContent(json ?? """{"ocs":{"meta":{"status":"ok"},"data":[]}}""", Encoding.UTF8, "application/json"),
        };
    }
}
