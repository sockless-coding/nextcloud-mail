using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Tags;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

/// <summary>
/// Microsoft sign-in with the Graph consent: the user consents to both resources at once, tokens
/// are asked for one resource at a time, and a sign-in without the consent is told apart.
/// </summary>
public sealed class OutlookGraphTests : IDisposable
{
    private const string Callback = "https://cloud.example/exapps/sockless_mail/oauth/callback";

    private readonly TestApp _app = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Microsoft_sign_in_asks_for_graph_but_redeems_mail_and_graph_tokens_separately()
    {
        _app.Nextcloud.Responses["cloud/user"] = (HttpStatusCode.OK,
            """{"ocs":{"meta":{"status":"ok"},"data":{"id":"root","groups":["admin"]}}}""");
        Assert.Equal(HttpStatusCode.OK, (await _app.ClientAs("root").PutAsJsonAsync("/api/admin/oauth/microsoft",
            new { clientId = "client-id", clientSecret = "client-secret", redirectUri = Callback }, Ct)).StatusCode);
        var alice = _app.ClientAs("alice");
        var started = await JsonAsync(await alice.PostAsJsonAsync("/api/oauth/flows", new { provider = "microsoft" }, Ct));
        var query = QueryHelpers.ParseQuery(new Uri(started.GetProperty("authorizeUrl").GetString()!).Query);
        var consent = query["scope"].ToString().Split(' ');
        Assert.Contains("https://outlook.office.com/IMAP.AccessAsUser.All", consent);
        Assert.Contains("https://graph.microsoft.com/Mail.ReadWrite", consent);
        Assert.Contains("https://graph.microsoft.com/MailboxSettings.ReadWrite", consent);

        await _app.ClientAs("").GetAsync($"/oauth/callback?state={Uri.EscapeDataString(query["state"]!)}&code=c", Ct);
        var (_, exchange) = Assert.Single(_app.TokenEndpoint.Requests);
        Assert.DoesNotContain("graph.microsoft.com", exchange["scope"]);
        Assert.Contains("IMAP.AccessAsUser.All", exchange["scope"]);
        var flowId = started.GetProperty("flowId").GetString();
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/accounts/oauth", new { flowId }, Ct)).StatusCode);

        using var db = _app.CreateDbContext("alice");
        var account = await db.Accounts.SingleAsync(Ct);
        var logins = _app.Services.GetRequiredService<MailLogins>();
        Assert.Equal("access-1", await logins.GraphTokenAsync(account, Ct));
        var graphScope = _app.TokenEndpoint.Requests.Last().Form["scope"];
        Assert.Contains("https://graph.microsoft.com/Mail.ReadWrite", graphScope);
        Assert.Contains("offline_access", graphScope);
        Assert.DoesNotContain("outlook.office.com", graphScope);

        // A sign-in from before the Graph consent.
        logins.Forget(account.Id);
        _app.TokenEndpoint.Respond = _ => (HttpStatusCode.BadRequest,
            new { error = "invalid_grant", error_description = "AADSTS65001: The user or administrator has not consented to use the application." });
        await Assert.ThrowsAsync<GraphConsentRequiredException>(() => logins.GraphTokenAsync(account, Ct));
    }

    [Fact]
    public async Task Accounts_without_a_microsoft_sign_in_have_no_graph()
    {
        await CreateAsync(_app.ClientAs("alice"));
        using var db = _app.CreateDbContext("alice");
        Assert.Null(await _app.Services.GetRequiredService<MailLogins>().GraphTokenAsync(await db.Accounts.SingleAsync(Ct), Ct));
    }

    [Theory]
    [InlineData("#ff0000", "preset0")]
    [InlineData("#0082c9", "preset7")]
    [InlineData("#2e7d32", "preset19")]
    [InlineData("#000000", "preset14")]
    public void Tag_colors_become_the_nearest_outlook_color(string color, string preset) =>
        Assert.Equal(preset, OutlookColors.Nearest(color));

    [Fact]
    public void Outlook_colors_become_tag_colors()
    {
        Assert.Equal("#13a10e", OutlookColors.ToHex("preset4"));
        Assert.Null(OutlookColors.ToHex("none"));
        Assert.Null(OutlookColors.ToHex("preset25"));
    }
}
