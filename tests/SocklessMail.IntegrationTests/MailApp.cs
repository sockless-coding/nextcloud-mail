using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Sync;

namespace SocklessMail.IntegrationTests;

/// <summary>
/// The real app on the test server, allowed to reach the local mail containers. IDLE watchers are
/// off unless <paramref name="services"/> turns them on, so tests control every IMAP round trip.
/// </summary>
public sealed class MailApp(Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
{
    public const string Secret = "integration-secret-0123456789abcdefghij";

    private readonly string _storage = Path.Combine(Path.GetTempPath(), "sockless-mail-it", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseSetting("APP_ID", "sockless_mail");
        builder.UseSetting("APP_VERSION", "0.3.0");
        builder.UseSetting("APP_SECRET", Secret);
        builder.UseSetting("NEXTCLOUD_URL", "http://nextcloud.test/");
        builder.UseSetting("APP_PORT", "9080");
        builder.UseSetting("APP_PERSISTENT_STORAGE", _storage);
        builder.UseSetting("SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS", "true");
        builder.ConfigureTestServices(collection =>
        {
            collection.AddSingleton(new WatcherOptions { MaxWatchers = 0 });
            services?.Invoke(collection);
        });
    }

    public HttpClient ClientAs(string userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(AppApiHeaders.ExAppId, "sockless_mail");
        client.DefaultRequestHeaders.Add(AppApiHeaders.ExAppVersion, "0.3.0");
        client.DefaultRequestHeaders.Add(AppApiHeaders.Authorization, AppApiHeaders.BuildAuthorization(userId, Secret));
        client.DefaultRequestHeaders.Add("X-Sockless-Request", "1");
        return client;
    }

    /// <summary>Adds an account for the GreenMail mailbox over the API and returns its id.</summary>
    public static Task<Guid> AddAccountAsync(HttpClient client, GreenMailFixture mail, string address, string password) =>
        AddAccountAsync(client, mail.Host, mail.Imap, mail.Smtp, address, password);

    /// <summary>Adds an account for a Dovecot user (IMAP only; there is no SMTP to test against).</summary>
    public static Task<Guid> AddAccountAsync(HttpClient client, DovecotFixture dovecot, string address) =>
        AddAccountAsync(client, dovecot.Host, dovecot.Imap, dovecot.Imap, address, DovecotFixture.Password);

    private static async Task<Guid> AddAccountAsync(HttpClient client, string host, int imap, int smtp, string address, string password)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new
        {
            displayName = address,
            emailAddress = address,
            username = address,
            password,
            imap = new { host, port = imap, security = "none" },
            smtp = new { host, port = smtp, security = "none" },
        });
        response.EnsureSuccessStatusCode();
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_storage, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
