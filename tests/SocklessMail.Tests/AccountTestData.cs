using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SocklessMail.Tests;

internal static class AccountTestData
{
    public const string Password = "correct horse battery staple";

    /// <summary>Settings pointing at loopback: the address policy refuses them without any network traffic.</summary>
    public static object Input(string name = "Work", string? password = Password, Guid? accountId = null) => new
    {
        displayName = name,
        emailAddress = "alice@example.com",
        username = "alice@example.com",
        password,
        imap = new { host = "127.0.0.1", port = 993, security = "sslTls" },
        smtp = new { host = "127.0.0.1", port = 587, security = "startTls" },
        accountId,
    };

    public static async Task<Guid> CreateAsync(HttpClient client, string name = "Work")
    {
        var response = await client.PostAsJsonAsync("/api/accounts", Input(name), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
