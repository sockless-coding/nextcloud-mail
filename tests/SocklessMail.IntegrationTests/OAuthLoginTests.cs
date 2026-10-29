using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Mail;
using SocklessMail.Api.OAuth;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>
/// XOAUTH2 against GreenMail, which accepts the user's password as the bearer token. A test
/// provider points at GreenMail and a fake token endpoint, so the whole path runs for real:
/// sign-in, access tokens from the refresh token (rotated ones saved), IMAP and SMTP logins, and
/// a revoked grant.
/// </summary>
public sealed class OAuthLoginTests : IDisposable
{
    private const string Address = "oauth@example.test";
    private const string Callback = "https://cloud.example/exapps/sockless_mail/oauth/callback";

    private readonly MailApp _app;
    private readonly ConcurrentQueue<Dictionary<string, string>> _requests = new();
    private Func<Dictionary<string, string>, (HttpStatusCode, object)> _respond;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public OAuthLoginTests(GreenMailFixture greenMail)
    {
        var provider = new OAuthProvider(
            "test", "Test", "https://login.test/authorize", "https://login.test/token", "mail openid email",
            new Dictionary<string, string>(),
            new ServerSettings(greenMail.Host, greenMail.Imap, MailSecurity.None),
            new ServerSettings(greenMail.Host, greenMail.Smtp, MailSecurity.None));
        _respond = form => (HttpStatusCode.OK, Grant(form["grant_type"] == "authorization_code" ? "refresh-1" : null));
        _app = new MailApp(services =>
        {
            services.AddSingleton(new OAuthProviderCatalog([provider]));
            services.AddHttpClient(OAuthTokens.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new TokenEndpoint(this));
        });
    }

    public void Dispose() => _app.Dispose();

    /// <summary>GreenMail takes the password as the token; the ID token names the GreenMail user.</summary>
    private static object Grant(string? refreshToken, string accessToken = GreenMailFixture.ActionsPassword, int expiresIn = 3600) => new
    {
        access_token = accessToken,
        refresh_token = refreshToken,
        expires_in = expiresIn,
        id_token = $"e30.{WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { email = Address }))}.sig",
    };

    private sealed class TokenEndpoint(OAuthLoginTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct)).ToDictionary(p => p.Key, p => p.Value.ToString());
            test._requests.Enqueue(form);
            var (status, body) = test._respond(form);
            return new HttpResponseMessage(status) { Content = JsonContent.Create(body) };
        }
    }

    private async Task<(HttpClient Client, Guid Account)> SignInAsync()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<OAuthProviderCatalog>();
            await scope.ServiceProvider.GetRequiredService<OAuthClientStore>()
                .SaveAsync(catalog.Find("test")!, new OAuthClientInput("client", "secret", null, Callback), Ct);
        }

        var client = _app.ClientAs("oauth-user");
        var started = await Json(await client.PostAsJsonAsync("/api/oauth/flows", new { provider = "test" }, Ct));
        var state = QueryHelpers.ParseQuery(new Uri(started.GetProperty("authorizeUrl").GetString()!).Query)["state"].ToString();
        var callback = await _app.ClientAs("").GetAsync($"/oauth/callback?state={Uri.EscapeDataString(state)}&code=code-1", Ct);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        var account = await Json(await client.PostAsJsonAsync("/api/accounts/oauth", new { flowId = started.GetProperty("flowId").GetString() }, Ct));
        return (client, account.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_oauth_account_logs_in_to_imap_and_smtp_with_access_tokens()
    {
        var (client, account) = await SignInAsync();

        var test = await Json(await client.PostAsync($"/api/accounts/{account}/test", null, Ct));
        Assert.True(test.GetProperty("imap").GetProperty("ok").GetBoolean(), test.ToString());
        Assert.True(test.GetProperty("smtp").GetProperty("ok").GetBoolean(), test.ToString());

        var folders = await Json(await client.GetAsync($"/api/accounts/{account}/folders", Ct));
        Assert.Contains(folders.EnumerateArray(), f => f.GetProperty("fullName").GetString() == "INBOX");

        // One code exchange, then a single refresh: the access token is reused until it nears expiry.
        Assert.Equal(["authorization_code", "refresh_token"], _requests.Select(r => r["grant_type"]));
        Assert.Equal("refresh-1", _requests.Last()["refresh_token"]);
    }

    [Fact]
    public async Task Rotated_refresh_tokens_are_kept_and_a_revoked_grant_asks_to_sign_in_again()
    {
        var (client, account) = await SignInAsync();

        // Short-lived tokens: every connection refreshes. The provider rotates the refresh token.
        var next = 1;
        _respond = form => (HttpStatusCode.OK, Grant($"refresh-{++next}", expiresIn: 60));
        await Json(await client.GetAsync($"/api/accounts/{account}/folders", Ct));
        await Json(await client.GetAsync($"/api/accounts/{account}/folders", Ct));
        var refreshes = _requests.Where(r => r["grant_type"] == "refresh_token").Select(r => r["refresh_token"]).ToList();
        Assert.Equal(["refresh-1", "refresh-2"], refreshes);

        // A refresh token the provider refuses: the account reports it, no mail server is asked.
        _respond = _ => (HttpStatusCode.BadRequest, new { error = "invalid_grant" });
        var refused = await client.GetAsync($"/api/accounts/{account}/folders", Ct);
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("oauth_signin_required", error.GetProperty("error").GetString());

        var test = await Json(await client.PostAsync($"/api/accounts/{account}/test", null, Ct));
        Assert.Equal("oauth_signin_required", test.GetProperty("imap").GetProperty("errorCode").GetString());

        // The unified inbox lists the account among its errors instead of failing.
        var unified = await Json(await client.GetAsync("/api/unified/inbox/messages", Ct));
        Assert.Equal("oauth_signin_required", Assert.Single(unified.GetProperty("errors").EnumerateArray()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_token_the_mail_server_rejects_is_an_authentication_failure()
    {
        var (client, account) = await SignInAsync();
        _respond = _ => (HttpStatusCode.OK, Grant(null, accessToken: "not-the-password"));

        var test = await Json(await client.PostAsync($"/api/accounts/{account}/test", null, Ct));

        Assert.False(test.GetProperty("imap").GetProperty("ok").GetBoolean());
        Assert.Equal("auth_failed", test.GetProperty("imap").GetProperty("errorCode").GetString());
    }
}
