using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SocklessMail.Api.AppApi;

namespace SocklessMail.IntegrationTests;

/// <summary>The real app on the test server, allowed to reach the local GreenMail container.</summary>
public sealed class MailApp : WebApplicationFactory<Program>
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
    public static async Task<Guid> AddAccountAsync(HttpClient client, GreenMailFixture mail, string address, string password)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new
        {
            displayName = address,
            emailAddress = address,
            username = address,
            password,
            imap = new { host = mail.Host, port = mail.Imap, security = "none" },
            smtp = new { host = mail.Host, port = mail.Smtp, security = "none" },
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
