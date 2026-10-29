using System.Text.Json;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Data;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Which messages may show a BIMI logo, as decided from headers fetched with the list, against Dovecot.</summary>
public sealed class BimiTests(DovecotFixture dovecot) : IDisposable
{
    private const string Inbox = "SU5CT1g"; // base64url("INBOX")

    private readonly MailApp _app = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    private static MimeMessage Message(string subject, string from, params string[] authenticationResults)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = subject } };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(new MailboxAddress("", "someone@example.test"));
        // Prepended like a receiving server would, so the first one given ends up on top.
        foreach (var results in authenticationResults.Reverse())
        {
            message.Headers.Insert(0, new Header("Authentication-Results", results));
        }

        return message;
    }

    private async Task<(HttpClient Client, Guid Account)> SetUpAsync(params MimeMessage[] messages)
    {
        var user = DovecotFixture.NewUser();
        using (var imap = await dovecot.ConnectAsync(user))
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            foreach (var message in messages)
            {
                await imap.Inbox.AppendAsync(message, MessageFlags.None, Ct);
            }

            await imap.DisconnectAsync(true, Ct);
        }

        var client = _app.ClientAs("bimi-user");
        return (client, await AddAccountAsync(client, dovecot, user));
    }

    private async Task<Dictionary<string, JsonElement>> ListAsync(HttpClient client, Guid account) =>
        (await Json(await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/messages", Ct)))
            .GetProperty("messages").EnumerateArray().ToDictionary(m => m.GetProperty("subject").GetString()!);

    private static string? Domain(JsonElement message) =>
        message.GetProperty("bimi") is { ValueKind: JsonValueKind.Object } bimi ? bimi.GetProperty("domain").GetString() : null;

    [Fact]
    public async Task ListAndMessageCarryTheSenderDomainAfterDmarcPass()
    {
        var (client, account) = await SetUpAsync(
            Message("Verified", "news@brand.example", "mx.local.test; dkim=pass header.d=brand.example; dmarc=pass header.from=brand.example"),
            Message("Failed", "news@brand.example", "mx.local.test; dmarc=fail header.from=brand.example"),
            Message("Forged", "ceo@bank.example",
                "mx.local.test; dmarc=none header.from=bank.example",
                "mx.local.test; dmarc=pass header.from=bank.example"),
            Message("Unauthenticated", "someone@brand.example"));

        var list = await ListAsync(client, account);
        Assert.Equal("brand.example", Domain(list["Verified"]));
        Assert.Null(Domain(list["Failed"]));
        Assert.Null(Domain(list["Forged"]));
        Assert.Null(Domain(list["Unauthenticated"]));

        var id = list["Verified"].GetProperty("id").GetString();
        var message = await Json(await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/messages/{id}", Ct));
        Assert.Equal("brand.example", Domain(message));
    }

    [Fact]
    public async Task SyncFillsInRowsCachedBeforeBimi()
    {
        var (client, account) = await SetUpAsync(
            Message("Verified", "news@brand.example", "mx.local.test; dmarc=pass header.from=brand.example"));
        await ListAsync(client, account);

        // As the migration leaves rows cached by an earlier version.
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<MailDbContext>().Messages.IgnoreQueryFilters()
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.BimiDomain, (string?)null), Ct));
        }

        // The sync before the page is read fills it in.
        Assert.Equal("brand.example", Domain((await ListAsync(client, account))["Verified"]));
    }
}
