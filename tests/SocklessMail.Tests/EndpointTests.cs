using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Tests;

public sealed class EndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Heartbeat_needs_no_auth()
    {
        var response = await _app.CreateClient().GetAsync("/heartbeat", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await Json(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Init_is_not_implemented_so_appapi_skips_it()
    {
        var response = await _app.ClientAs("").PostAsync("/init", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Api_without_credentials_is_401()
    {
        var response = await _app.CreateClient().GetAsync("/api/me", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-secret", "sockless_mail")]
    [InlineData(TestApp.Secret, "another_app")]
    public async Task Api_with_bad_credentials_is_401(string secret, string appId)
    {
        var response = await _app.ClientAs("alice", secret, appId).GetAsync("/api/me", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_in_system_context_is_403()
    {
        // Anonymous visitors reach PUBLIC routes with an empty user; /api must still refuse them.
        var response = await _app.ClientAs("").GetAsync("/api/me", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("alice", "Alice Liddell")]
    [InlineData("bob", "Bob")]
    public async Task Me_returns_the_verified_user_and_calls_nextcloud_as_that_user(string userId, string displayName)
    {
        _app.Nextcloud.Responses["cloud/user"] = (HttpStatusCode.OK,
            JsonSerializer.Serialize(new { ocs = new { meta = new { status = "ok" }, data = new { id = userId, displayname = displayName } } }));

        var response = await _app.ClientAs(userId).GetAsync("/api/me", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(userId, body.GetProperty("userId").GetString());
        Assert.Equal(displayName, body.GetProperty("displayName").GetString());

        var call = Assert.Single(_app.Nextcloud.Requests);
        Assert.Equal("/ocs/v2.php/cloud/user?format=json", call.PathAndQuery);
        Assert.Equal("sockless_mail", call.Headers[AppApiHeaders.ExAppId]);
        Assert.Equal("0.1.0", call.Headers[AppApiHeaders.ExAppVersion]);
        Assert.Equal("true", call.Headers["OCS-APIRequest"]);
        Assert.Equal($"{userId}:{TestApp.Secret}",
            Encoding.UTF8.GetString(Convert.FromBase64String(call.Headers[AppApiHeaders.Authorization])));
    }

    [Fact]
    public async Task Me_still_answers_when_nextcloud_is_down()
    {
        _app.Nextcloud.Responses["cloud/user"] = (HttpStatusCode.InternalServerError, "{}");
        var body = await Json(await _app.ClientAs("alice").GetAsync("/api/me", Ct));
        Assert.Equal("alice", body.GetProperty("userId").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("displayName").ValueKind);
    }

    [Fact]
    public async Task Api_responses_are_not_cacheable()
    {
        var response = await _app.ClientAs("alice").GetAsync("/api/me", Ct);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task State_changing_api_request_without_csrf_header_is_403()
    {
        var response = await _app.ClientAs("alice").PostAsync("/api/me", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("csrf_check_failed", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task State_changing_api_request_with_csrf_header_passes_the_guard()
    {
        var client = _app.ClientAs("alice");
        client.DefaultRequestHeaders.Add("X-Sockless-Request", "1");
        var response = await client.PostAsync("/api/me", null, Ct);
        // Passes the guard and reaches routing: /api/me has no POST handler.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Cross_site_api_request_is_403()
    {
        var client = _app.ClientAs("alice");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        var response = await client.GetAsync("/api/me", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Static_assets_need_no_auth()
    {
        var response = await _app.CreateClient().GetAsync("/img/app.svg", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("")]
    public async Task Enabled_requires_the_app_secret(string userId)
    {
        var response = await _app.ClientAs(userId, "wrong-secret").PutAsync("/enabled?enabled=1", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_app.Nextcloud.Requests);
    }

    [Fact]
    public async Task Enabling_registers_the_top_menu_page()
    {
        var response = await _app.ClientAs("").PutAsync("/enabled?enabled=1", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", (await Json(response)).GetProperty("error").GetString());

        var calls = _app.Nextcloud.Requests.ToArray();
        Assert.All(calls, c => Assert.Equal(HttpMethod.Post, c.Method));
        Assert.Equal(
            ["top-menu", "initial-state", "script", "style"],
            calls.Select(c => c.PathAndQuery.Split('?')[0].Split('/')[^1]));
        Assert.All(calls, c => Assert.StartsWith("/ocs/v2.php/apps/app_api/api/v1/ui/", c.PathAndQuery));

        var menu = JsonDocument.Parse(calls[0].Body).RootElement;
        Assert.Equal("sockless_mail", menu.GetProperty("name").GetString());
        Assert.Equal("Sockless Mail", menu.GetProperty("displayName").GetString());

        // Not on HaRP in this test host, so the browser must use the PHP proxy.
        var state = JsonDocument.Parse(calls[1].Body).RootElement;
        Assert.Equal("proxy", state.GetProperty("value").GetProperty("apiMode").GetString());
    }

    [Fact]
    public async Task Enabling_reports_a_registration_failure_to_appapi()
    {
        _app.Nextcloud.Responses["ui/top-menu"] = (HttpStatusCode.BadRequest, "{}");
        var body = await Json(await _app.ClientAs("").PutAsync("/enabled?enabled=1", null, Ct));
        Assert.Contains("could not register", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Disabling_succeeds_even_when_unregistering_fails()
    {
        _app.Nextcloud.Responses["ui/"] = (HttpStatusCode.InternalServerError, "{}");
        var response = await _app.ClientAs("").PutAsync("/enabled?enabled=0", null, Ct);
        Assert.Equal("", (await Json(response)).GetProperty("error").GetString());
        Assert.Equal(4, _app.Nextcloud.Requests.Count(r => r.Method == HttpMethod.Delete));
    }
}
