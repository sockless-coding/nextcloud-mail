using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Time.Testing;
using SocklessMail.Api.OAuth;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

/// <summary>
/// OAuth2 sign-in against a fake token endpoint: admin settings, the flow from start to account,
/// and who may use a flow. Mail servers are never contacted here (see the integration tests).
/// </summary>
public sealed class OAuthTests : IDisposable
{
    private const string Callback = "https://cloud.example/exapps/sockless_mail/oauth/callback";
    private const string AdminGroups = """{"ocs":{"meta":{"status":"ok"},"data":{"id":"root","groups":["admin","users"]}}}""";
    private const string UserGroups = """{"ocs":{"meta":{"status":"ok"},"data":{"id":"alice","groups":["users"]}}}""";

    private readonly TestApp _app = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    private async Task ConfigureGoogleAsync()
    {
        _app.Nextcloud.Responses["cloud/user"] = (HttpStatusCode.OK, AdminGroups);
        var response = await _app.ClientAs("root").PutAsJsonAsync("/api/admin/oauth/google",
            new { clientId = "client-id", clientSecret = "client-secret", redirectUri = Callback }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Starts a flow as <paramref name="client"/>'s user and returns its id and the authorize URL's query.
    /// For an existing account no provider is sent: it is the one of the account's grant.
    /// </summary>
    private async Task<(string FlowId, Dictionary<string, string> Query)> StartAsync(HttpClient client, Guid? accountId = null)
    {
        var response = await client.PostAsJsonAsync("/api/oauth/flows", new { provider = accountId is null ? "google" : null, accountId }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var started = await JsonAsync(response);
        var url = new Uri(started.GetProperty("authorizeUrl").GetString()!);
        var query = QueryHelpers.ParseQuery(url.Query).ToDictionary(q => q.Key, q => q.Value.ToString());
        return (started.GetProperty("flowId").GetString()!, query);
    }

    /// <summary>The provider's redirect, arriving without a Nextcloud user (a cross-site navigation).</summary>
    private Task<HttpResponseMessage> CallbackAsync(string query, string user = "") =>
        _app.ClientAs(user).GetAsync($"/oauth/callback?{query}", Ct);

    [Fact]
    public async Task Admin_settings_are_for_admins_and_never_show_the_secret()
    {
        _app.Nextcloud.Responses["cloud/user"] = (HttpStatusCode.OK, UserGroups);
        var alice = _app.ClientAs("alice");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/admin/oauth", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/api/admin/oauth/google", new { clientId = "x", clientSecret = "y", redirectUri = Callback }, Ct)).StatusCode);
        Assert.False((await JsonAsync(await alice.GetAsync("/api/me", Ct))).GetProperty("isAdmin").GetBoolean());

        await ConfigureGoogleAsync();
        var root = _app.ClientAs("root");
        Assert.True((await JsonAsync(await root.GetAsync("/api/me", Ct))).GetProperty("isAdmin").GetBoolean());
        var body = await (await root.GetAsync("/api/admin/oauth", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("client-secret", body);
        var google = JsonDocument.Parse(body).RootElement.EnumerateArray().Single(p => p.GetProperty("provider").GetString() == "google");
        Assert.True(google.GetProperty("configured").GetBoolean());
        Assert.True(google.GetProperty("hasClientSecret").GetBoolean());
        Assert.Equal(Callback, google.GetProperty("redirectUri").GetString());

        // Users only learn which providers are available.
        var providers = await JsonAsync(await alice.GetAsync("/api/oauth/providers", Ct));
        Assert.Equal(["google"], providers.EnumerateArray().Select(p => p.GetProperty("id").GetString()));

        // An empty secret keeps the stored one; a plain-http callback is refused unless it is local.
        Assert.Equal(HttpStatusCode.OK, (await root.PutAsJsonAsync("/api/admin/oauth/google", new { clientId = "client-2", redirectUri = Callback }, Ct)).StatusCode);
        var invalid = await root.PutAsJsonAsync("/api/admin/oauth/google", new { clientId = "client-2", redirectUri = "http://cloud.example/cb" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await root.PutAsJsonAsync("/api/admin/oauth/yahoo", new { clientId = "x" }, Ct)).StatusCode);

        // An empty client id switches the provider off.
        await root.PutAsJsonAsync("/api/admin/oauth/google", new { clientId = "" }, Ct);
        Assert.Empty((await JsonAsync(await alice.GetAsync("/api/oauth/providers", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task Signing_in_creates_an_account_with_the_providers_servers()
    {
        await ConfigureGoogleAsync();
        var alice = _app.ClientAs("alice");
        var (flowId, query) = await StartAsync(alice);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Callback, query["redirect_uri"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("pending", (await JsonAsync(await alice.GetAsync($"/api/oauth/flows/{flowId}", Ct))).GetProperty("status").GetString());

        var page = await CallbackAsync($"state={Uri.EscapeDataString(query["state"])}&code=the-code");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("alice@gmail.com", await page.Content.ReadAsStringAsync(Ct));
        Assert.Contains("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single());

        // The code went to the token endpoint with the verifier matching the challenge (PKCE).
        var (_, form) = Assert.Single(_app.TokenEndpoint.Requests);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("client-secret", form["client_secret"]);
        Assert.Equal(query["code_challenge"], WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));

        var flow = await JsonAsync(await alice.GetAsync($"/api/oauth/flows/{flowId}", Ct));
        Assert.Equal("done", flow.GetProperty("status").GetString());
        Assert.Equal("alice@gmail.com", flow.GetProperty("email").GetString());

        var created = await alice.PostAsJsonAsync("/api/accounts/oauth", new { flowId, displayName = "Gmail" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = await JsonAsync(created);
        Assert.Equal("oAuth2", account.GetProperty("authMethod").GetString());
        Assert.Equal("Gmail", account.GetProperty("displayName").GetString());
        Assert.Equal("alice@gmail.com", account.GetProperty("username").GetString());
        Assert.Equal("Alice Example", account.GetProperty("senderName").GetString());
        Assert.Equal("imap.gmail.com", account.GetProperty("imap").GetProperty("host").GetString());

        // A grant is used once; a replayed redirect does nothing.
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/accounts/oauth", new { flowId }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallbackAsync($"state={Uri.EscapeDataString(query["state"])}&code=the-code")).StatusCode);
        Assert.Single(_app.TokenEndpoint.Requests);
    }

    [Fact]
    public async Task A_flow_belongs_to_the_user_who_started_it()
    {
        await ConfigureGoogleAsync();
        var alice = _app.ClientAs("alice");
        var bob = _app.ClientAs("bob");
        var (flowId, query) = await StartAsync(alice);
        var state = Uri.EscapeDataString(query["state"]);

        // Bob's session on the redirect cannot complete alice's sign-in.
        Assert.Equal(HttpStatusCode.Forbidden, (await CallbackAsync($"state={state}&code=c", "bob")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallbackAsync($"state={state}&code=c", "alice")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/oauth/flows/{flowId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsJsonAsync("/api/accounts/oauth", new { flowId }, Ct)).StatusCode);
        Assert.Empty((await JsonAsync(await bob.GetAsync("/api/accounts", Ct))).EnumerateArray());
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/accounts/oauth", new { flowId }, Ct)).StatusCode);

        // Another user's account cannot be the target of a sign-in either.
        var bobsAccount = await CreateAsync(bob);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync("/api/oauth/flows", new { provider = "google", accountId = bobsAccount }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_states_cancelled_sign_ins_and_missing_grants_fail_cleanly()
    {
        await ConfigureGoogleAsync();
        var alice = _app.ClientAs("alice");

        Assert.Equal(HttpStatusCode.BadRequest, (await CallbackAsync("state=made-up&code=c")).StatusCode);

        var (cancelled, query) = await StartAsync(alice);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallbackAsync($"state={Uri.EscapeDataString(query["state"])}&error=access_denied")).StatusCode);
        var flow = await JsonAsync(await alice.GetAsync($"/api/oauth/flows/{cancelled}", Ct));
        Assert.Equal("failed", flow.GetProperty("status").GetString());
        Assert.Equal("The sign-in was cancelled.", flow.GetProperty("error").GetString());

        // Without a refresh token the account could not keep working.
        _app.TokenEndpoint.Respond = _ => (HttpStatusCode.OK, FakeTokenEndpoint.Grant("alice@gmail.com", refreshToken: null));
        var (_, noRefresh) = await StartAsync(alice);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallbackAsync($"state={Uri.EscapeDataString(noRefresh["state"])}&code=c")).StatusCode);

        _app.TokenEndpoint.Respond = _ => (HttpStatusCode.BadRequest, new { error = "invalid_grant" });
        var (_, rejected) = await StartAsync(alice);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallbackAsync($"state={Uri.EscapeDataString(rejected["state"])}&code=c")).StatusCode);

        // The callback route is only reachable through AppAPI.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().GetAsync("/oauth/callback?state=x&code=c", Ct)).StatusCode);
    }

    [Fact]
    public async Task Signing_in_again_must_use_the_same_address()
    {
        await ConfigureGoogleAsync();
        var alice = _app.ClientAs("alice");
        var (first, query) = await StartAsync(alice);
        await CallbackAsync($"state={Uri.EscapeDataString(query["state"])}&code=c");
        var accountId = (await JsonAsync(await alice.PostAsJsonAsync("/api/accounts/oauth", new { flowId = first }, Ct))).GetProperty("id").GetGuid();

        _app.TokenEndpoint.Respond = _ => (HttpStatusCode.OK, FakeTokenEndpoint.Grant("someone.else@gmail.com"));
        var (other, otherQuery) = await StartAsync(alice, accountId);
        await CallbackAsync($"state={Uri.EscapeDataString(otherQuery["state"])}&code=c");
        var refused = await alice.PostAsJsonAsync($"/api/accounts/{accountId}/oauth", new { flowId = other }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("different_account", (await JsonAsync(refused)).GetProperty("error").GetString());

        _app.TokenEndpoint.Respond = _ => (HttpStatusCode.OK, FakeTokenEndpoint.Grant("Alice@Gmail.com", refreshToken: "refresh-2"));
        var (again, againQuery) = await StartAsync(alice, accountId);
        await CallbackAsync($"state={Uri.EscapeDataString(againQuery["state"])}&code=c");
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/api/accounts/{accountId}/oauth", new { flowId = again }, Ct)).StatusCode);

        // A flow started for an existing account cannot create a new one.
        var (scoped, scopedQuery) = await StartAsync(alice, accountId);
        await CallbackAsync($"state={Uri.EscapeDataString(scopedQuery["state"])}&code=c");
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/accounts/oauth", new { flowId = scoped }, Ct)).StatusCode);
    }

    [Fact]
    public void Flows_expire_and_are_limited_per_user()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var flows = new OAuthFlows(clock);
        var flow = flows.Start("alice", "google", Callback, null)!;
        Assert.Same(flow, flows.FindByState(flow.State));
        Assert.Null(flows.Get("bob", flow.Id));

        clock.Advance(OAuthFlows.Lifetime);
        Assert.Null(flows.FindByState(flow.State));
        Assert.Null(flows.Get("alice", flow.Id));

        for (var i = 0; i < OAuthFlows.MaxFlowsPerUser; i++)
        {
            Assert.NotNull(flows.Start("alice", "google", Callback, null));
        }

        Assert.Null(flows.Start("alice", "google", Callback, null));
        Assert.NotNull(flows.Start("bob", "google", Callback, null));
    }

    [Fact]
    public void The_pkce_challenge_follows_rfc_7636()
    {
        // The example of RFC 7636, appendix B.
        var flow = new OAuthFlow
        {
            Id = "x", State = "y", UserId = "u", ProviderId = "google", RedirectUri = Callback,
            CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk",
        };
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthFlows.Challenge(flow));
    }

    [Theory]
    [InlineData("""{"email":"a@example.com","name":"A"}""", "a@example.com")]
    [InlineData("""{"preferred_username":"b@outlook.com"}""", "b@outlook.com")]
    [InlineData("""{"preferred_username":"not-an-address"}""", null)]
    [InlineData("""{}""", null)]
    public void The_address_comes_from_the_id_token(string claims, string? expected)
    {
        var token = $"e30.{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(claims))}.sig";
        Assert.Equal(expected, OAuthTokens.ReadIdentity(token)?.Email);
        Assert.Null(OAuthTokens.ReadIdentity("garbage"));
    }
}
