using System.Buffers.Text;
using System.Net;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Searching one folder on the server, and all cached folders of all accounts.</summary>
public sealed class SearchTests(GreenMailFixture greenMail, DovecotFixture dovecot) : IDisposable
{
    private const string Inbox = "SU5CT1g";
    private const string GreenMailUser = "searcher@example.test";

    private readonly MailApp _app = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    public static TheoryData<string> Servers() => ["dovecot", "greenmail"];

    private static MimeMessage Message(string subject, string body, string fromName = "Sender", string from = "sender@example.test")
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = body } };
        message.From.Add(new MailboxAddress(fromName, from));
        message.To.Add(new MailboxAddress("", "someone@example.test"));
        return message;
    }

    private async Task<ImapClient> ConnectAsync(string server, string user)
    {
        if (server == "dovecot")
        {
            return await dovecot.ConnectAsync(user);
        }

        var client = new ImapClient();
        await client.ConnectAsync(greenMail.Host, greenMail.Imap, SecureSocketOptions.None, Ct);
        await client.AuthenticateAsync(user, GreenMailFixture.ActionsPassword, Ct);
        return client;
    }

    private async Task<Guid> AddAsync(HttpClient client, string server, string user) =>
        server == "dovecot"
            ? await AddAccountAsync(client, dovecot, user)
            : await AddAccountAsync(client, greenMail, user, GreenMailFixture.ActionsPassword);

    private static IEnumerable<string?> Subjects(JsonElement page) =>
        page.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString());

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task A_folder_is_searched_on_the_server_including_bodies(string server)
    {
        var user = server == "dovecot" ? DovecotFixture.NewUser() : GreenMailUser;
        var marker = $"m{Guid.NewGuid():N}"[..12];
        using (var imap = await ConnectAsync(server, user))
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            string[] words = ["one", "two", "three", "four", "five", "six", "seven"];
            for (var i = 1; i <= 7; i++)
            {
                await imap.Inbox.AppendAsync(Message($"Report {i}", $"Numbers for {marker} quarter {words[i - 1]}"), MessageFlags.None, Ct);
            }

            await imap.Inbox.AppendAsync(Message($"Invoice {marker}", "Please pay"), MessageFlags.None, Ct);
            await imap.Inbox.AppendAsync(Message("Grüße", $"Viele Grüße aus Köln {marker}"), MessageFlags.None, Ct);
            await imap.Inbox.AppendAsync(Message("Unrelated", "nothing here"), MessageFlags.None, Ct);
        }

        var client = _app.ClientAs("searcher");
        var account = await AddAsync(client, server, user);
        async Task<JsonElement> Search(string query, string extra = "") =>
            await Json(await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/search?q={Uri.EscapeDataString(query)}{extra}", Ct));

        // A word only in the bodies, paged newest first.
        var first = await Search($"{marker} quarter", "&limit=4");
        Assert.Equal(7, first.GetProperty("total").GetInt32());
        Assert.Equal(["Report 7", "Report 6", "Report 5", "Report 4"], Subjects(first));
        var second = await Search($"{marker} quarter", $"&limit=4&before={first.GetProperty("nextBefore").GetUInt32()}");
        Assert.Equal(["Report 3", "Report 2", "Report 1"], Subjects(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);

        // Every term must match, subjects count too. (How a phrase matches is up to the server:
        // Dovecot's full-text index matches its words, GreenMail the exact substring.)
        Assert.Equal(["Report 3"], Subjects(await Search($"{marker} \"quarter three\"")));
        Assert.Equal([$"Invoice {marker}"], Subjects(await Search($"invoice {marker}")));
        Assert.Equal(["Grüße"], Subjects(await Search($"Köln {marker}")));
        Assert.Empty(Subjects(await Search($"{marker} absent")));

        var empty = await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/search?q=%20", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Cached_headers_are_searched_across_accounts_and_folders()
    {
        var client = _app.ClientAs("everywhere");
        var users = new[] { DovecotFixture.NewUser(), DovecotFixture.NewUser() };
        var accounts = new List<Guid>();
        foreach (var user in users)
        {
            using var imap = await dovecot.ConnectAsync(user);
            await imap.Inbox.AppendAsync(Message("Invoice March", "body", "Åsa Lindqvist", "asa@example.test"), MessageFlags.None, Ct);
            await imap.Inbox.AppendAsync(Message("Lunch", "invoice only in the body"), MessageFlags.None, Ct);
            var archive = await imap.GetFolder(imap.PersonalNamespaces[0]).CreateAsync("Archive", true, Ct) ?? throw new InvalidOperationException();
            await archive.AppendAsync(Message("Old invoice", "body"), MessageFlags.None, Ct);
            accounts.Add(await AddAccountAsync(client, dovecot, user));
        }

        // Only folders shown before are cached: both inboxes, and the first account's archive.
        var archiveId = Base64Url.EncodeToString("Archive"u8);
        foreach (var account in accounts)
        {
            await Json(await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/messages", Ct));
        }

        await Json(await client.GetAsync($"/api/accounts/{accounts[0]}/folders/{archiveId}/messages", Ct));

        async Task<JsonElement> Search(string query) =>
            await Json(await client.GetAsync($"/api/search?q={Uri.EscapeDataString(query)}", Ct));

        var hits = (await Search("invoice")).GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(3, hits.Count);
        Assert.Equal(2, hits.Count(h => h.GetProperty("message").GetProperty("subject").GetString() == "Invoice March"));
        var archived = Assert.Single(hits, h => h.GetProperty("folderFullName").GetString() == "Archive");
        Assert.Equal(accounts[0], archived.GetProperty("accountId").GetGuid());
        Assert.Equal(archiveId, archived.GetProperty("folderId").GetString());
        Assert.Equal(accounts.ToHashSet(), hits.Select(h => h.GetProperty("accountId").GetGuid()).ToHashSet());

        // Names and addresses count, case-insensitively beyond ASCII; every term must match.
        Assert.Equal(2, (await Search("ÅSA march")).GetProperty("hits").GetArrayLength());
        Assert.Equal(2, (await Search("asa@example")).GetProperty("hits").GetArrayLength());
        Assert.Equal(0, (await Search("åsa lunch")).GetProperty("hits").GetArrayLength());

        var empty = await client.GetAsync("/api/search?q=", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }
}
