using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MimeKit;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>The inboxes of several accounts merged into one list by date, paged and polled.</summary>
public sealed class UnifiedInboxTests(DovecotFixture dovecot) : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MailApp _app = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    private static MimeMessage Message(string subject, DateTimeOffset date)
    {
        var message = new MimeMessage { Subject = subject, Date = date, Body = new TextPart("plain") { Text = subject } };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("", "someone@example.test"));
        return message;
    }

    /// <summary>Appends messages in order (so UIDs follow the dates) to a new Dovecot user's inbox.</summary>
    private async Task<string> MailboxWithAsync(IEnumerable<(string Subject, DateTimeOffset Date)> messages)
    {
        var user = DovecotFixture.NewUser();
        using var imap = await dovecot.ConnectAsync(user);
        foreach (var (subject, date) in messages)
        {
            await imap.Inbox.AppendAsync(Message(subject, date), MessageFlags.None, Ct);
        }

        return user;
    }

    private static IEnumerable<string?> Subjects(JsonElement page) =>
        page.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("message").GetProperty("subject").GetString());

    [Fact]
    public async Task Inboxes_are_merged_by_date_across_pages()
    {
        // 60 messages in the first inbox: more than the initial cache window of 50, so paging
        // has to extend it on the server. The second inbox shares some timestamps with the first.
        var first = Enumerable.Range(1, 60).Select(i => ($"A{i}", Start.AddHours(2 * i))).ToList();
        var second = Enumerable.Range(1, 8).Select(i => ($"B{i}", Start.AddHours(i % 2 == 0 ? 2 * i : 2 * i + 1))).ToList();
        var client = _app.ClientAs("unified");
        var accountA = await AddAccountAsync(client, dovecot, await MailboxWithAsync(first));
        var accountB = await AddAccountAsync(client, dovecot, await MailboxWithAsync(second));

        // Date, then account id, then UID; all descending.
        var expected = first.Select((m, i) => (m.Item1, m.Item2, Account: accountA, Uid: i))
            .Concat(second.Select((m, i) => (m.Item1, m.Item2, Account: accountB, Uid: i)))
            .OrderByDescending(m => m.Item2).ThenByDescending(m => m.Account).ThenByDescending(m => m.Uid)
            .Select(m => (string?)m.Item1)
            .ToList();

        var subjects = new List<string?>();
        string? before = null;
        var pages = 0;
        do
        {
            var page = await Json(await client.GetAsync($"/api/unified/messages?limit=7{(before is null ? "" : $"&before={before}")}", Ct));
            Assert.Empty(page.GetProperty("errors").EnumerateArray());
            if (pages == 0)
            {
                Assert.Equal(68, page.GetProperty("total").GetInt32());
                Assert.Equal(68, page.GetProperty("unread").GetInt32());
                var hit = page.GetProperty("messages")[0];
                Assert.Equal("SU5CT1g", hit.GetProperty("folderId").GetString());
            }

            subjects.AddRange(Subjects(page));
            before = page.GetProperty("nextBefore").GetString();
            pages++;
        }
        while (before is not null && pages < 20);

        Assert.Equal(expected, subjects);
    }

    [Fact]
    public async Task Changes_in_any_inbox_are_reported_once()
    {
        var client = _app.ClientAs("unified-changes");
        var userA = await MailboxWithAsync([("A1", Start), ("A2", Start.AddHours(2))]);
        var userB = await MailboxWithAsync([("B1", Start.AddHours(1))]);
        var accountA = await AddAccountAsync(client, dovecot, userA);
        var accountB = await AddAccountAsync(client, dovecot, userB);

        var page = await Json(await client.GetAsync("/api/unified/messages", Ct));
        Assert.Equal(["A2", "B1", "A1"], Subjects(page));
        var token = page.GetProperty("token").GetString()!;
        string IdOf(string subject) => page.GetProperty("messages").EnumerateArray()
            .Single(h => h.GetProperty("message").GetProperty("subject").GetString() == subject)
            .GetProperty("message").GetProperty("id").GetString()!;

        using (var imap = await dovecot.ConnectAsync(userA))
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            var uids = await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.All, Ct);
            await imap.Inbox.AddFlagsAsync(uids[0], MessageFlags.Seen, true, Ct);
            await imap.Inbox.AddFlagsAsync(uids[1], MessageFlags.Deleted, true, Ct);
            await imap.Inbox.ExpungeAsync(Ct);
        }

        using (var imap = await dovecot.ConnectAsync(userB))
        {
            await imap.Inbox.AppendAsync(Message("B2", Start.AddHours(5)), MessageFlags.None, Ct);
        }

        var changes = await Json(await client.GetAsync($"/api/unified/changes?since={Uri.EscapeDataString(token)}", Ct));
        Assert.False(changes.GetProperty("reset").GetBoolean());
        Assert.Equal(3, changes.GetProperty("total").GetInt32());
        Assert.Equal(2, changes.GetProperty("unread").GetInt32());
        var changed = changes.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(
            [(accountA, "A1", true), (accountB, "B2", false)],
            changed.Select(h => (h.GetProperty("accountId").GetGuid(), h.GetProperty("message").GetProperty("subject").GetString(),
                h.GetProperty("message").GetProperty("seen").GetBoolean())).OrderBy(h => h.Item2));
        var removed = Assert.Single(changes.GetProperty("removed").EnumerateArray());
        Assert.Equal(accountA, removed.GetProperty("accountId").GetGuid());
        Assert.Equal(IdOf("A2"), removed.GetProperty("messageId").GetString());

        // Nothing new after the new token.
        var again = await Json(await client.GetAsync($"/api/unified/changes?since={Uri.EscapeDataString(changes.GetProperty("token").GetString()!)}", Ct));
        Assert.False(again.GetProperty("reset").GetBoolean());
        Assert.Empty(again.GetProperty("messages").EnumerateArray());
        Assert.Empty(again.GetProperty("removed").EnumerateArray());

        // A new account's inbox cannot be merged into the old token: reload.
        await AddAccountAsync(client, dovecot, await MailboxWithAsync([("C1", Start)]));
        var added = await Json(await client.GetAsync($"/api/unified/changes?since={Uri.EscapeDataString(again.GetProperty("token").GetString()!)}", Ct));
        Assert.True(added.GetProperty("reset").GetBoolean());
    }

    [Fact]
    public async Task An_unreachable_account_is_reported_without_failing_the_list()
    {
        var client = _app.ClientAs("unified-broken");
        var account = await AddAccountAsync(client, dovecot, await MailboxWithAsync([("Fine", Start)]));
        // Nothing listens on port 1.
        var created = await client.PostAsJsonAsync("/api/accounts", new
        {
            displayName = "Broken",
            emailAddress = "broken@example.test",
            username = "broken",
            password = "x",
            imap = new { host = "127.0.0.1", port = 1, security = "none" },
            smtp = new { host = "127.0.0.1", port = 1, security = "none" },
        }, Ct);
        var broken = (await Json(created)).GetProperty("id").GetGuid();

        var page = await Json(await client.GetAsync("/api/unified/messages", Ct));

        Assert.Equal(["Fine"], Subjects(page));
        Assert.Equal(account, page.GetProperty("messages")[0].GetProperty("accountId").GetGuid());
        var error = Assert.Single(page.GetProperty("errors").EnumerateArray());
        Assert.Equal(broken, error.GetProperty("accountId").GetGuid());

        // The broken inbox has no cache state, so polling is not reset by it.
        var changes = await Json(await client.GetAsync($"/api/unified/changes?since={Uri.EscapeDataString(page.GetProperty("token").GetString()!)}", Ct));
        Assert.False(changes.GetProperty("reset").GetBoolean());
        Assert.Single(changes.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task Malformed_cursors_and_tokens_are_rejected()
    {
        var client = _app.ClientAs("unified-invalid");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/unified/messages?before=nonsense", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/unified/changes?since=nonsense", Ct)).StatusCode);
    }
}
