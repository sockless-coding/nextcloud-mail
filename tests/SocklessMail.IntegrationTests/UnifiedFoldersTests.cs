using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MimeKit;
using SocklessMail.Api.Mailbox;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Unified folders (a special-use folder of several accounts merged by date) and smart folders, paged and polled.</summary>
public sealed class UnifiedFoldersTests(DovecotFixture dovecot) : IDisposable
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

    /// <summary>A new Dovecot user with messages in the given folders (created at the top level when not INBOX), appended in order.</summary>
    private async Task<string> MailboxWithFoldersAsync(params (string Folder, string Subject, DateTimeOffset Date, MessageFlags Flags)[] messages)
    {
        var user = DovecotFixture.NewUser();
        using var imap = await dovecot.ConnectAsync(user);
        var root = imap.GetFolder(imap.PersonalNamespaces[0]);
        var folders = new Dictionary<string, IMailFolder> { ["INBOX"] = imap.Inbox };
        foreach (var (name, subject, date, flags) in messages)
        {
            if (!folders.TryGetValue(name, out var folder))
            {
                folders[name] = folder = await root.CreateAsync(name, isMessageFolder: true, Ct)
                                         ?? throw new InvalidOperationException($"Creating {name} failed.");
            }

            await folder.AppendAsync(Message(subject, date), flags, Ct);
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
            var page = await Json(await client.GetAsync($"/api/unified/inbox/messages?limit=7{(before is null ? "" : $"&before={before}")}", Ct));
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

        var page = await Json(await client.GetAsync("/api/unified/inbox/messages", Ct));
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

        var changes = await Json(await client.GetAsync($"/api/unified/inbox/changes?since={Uri.EscapeDataString(token)}", Ct));
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
        var again = await Json(await client.GetAsync($"/api/unified/inbox/changes?since={Uri.EscapeDataString(changes.GetProperty("token").GetString()!)}", Ct));
        Assert.False(again.GetProperty("reset").GetBoolean());
        Assert.Empty(again.GetProperty("messages").EnumerateArray());
        Assert.Empty(again.GetProperty("removed").EnumerateArray());

        // A new account's inbox cannot be merged into the old token: reload.
        await AddAccountAsync(client, dovecot, await MailboxWithAsync([("C1", Start)]));
        var added = await Json(await client.GetAsync($"/api/unified/inbox/changes?since={Uri.EscapeDataString(again.GetProperty("token").GetString()!)}", Ct));
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

        var page = await Json(await client.GetAsync("/api/unified/inbox/messages", Ct));

        Assert.Equal(["Fine"], Subjects(page));
        Assert.Equal(account, page.GetProperty("messages")[0].GetProperty("accountId").GetGuid());
        var error = Assert.Single(page.GetProperty("errors").EnumerateArray());
        Assert.Equal(broken, error.GetProperty("accountId").GetGuid());

        // The broken inbox has no cache state, so polling is not reset by it.
        var changes = await Json(await client.GetAsync($"/api/unified/inbox/changes?since={Uri.EscapeDataString(page.GetProperty("token").GetString()!)}", Ct));
        Assert.False(changes.GetProperty("reset").GetBoolean());
        Assert.Single(changes.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task A_unified_folder_merges_that_folder_of_every_account()
    {
        var client = _app.ClientAs("unified-sent");
        // Sent found by its name in both spellings; the third account has none.
        var accountA = await AddAccountAsync(client, dovecot, await MailboxWithFoldersAsync(
            ("INBOX", "Inbox A", Start.AddHours(9), MessageFlags.None),
            ("Sent", "Sent A1", Start.AddHours(1), MessageFlags.Seen),
            ("Sent", "Sent A2", Start.AddHours(3), MessageFlags.Seen)));
        var userB = await MailboxWithFoldersAsync(("Sent Items", "Sent B1", Start.AddHours(2), MessageFlags.None));
        var accountB = await AddAccountAsync(client, dovecot, userB);
        await AddAccountAsync(client, dovecot, await MailboxWithFoldersAsync(("INBOX", "Inbox C", Start, MessageFlags.None)));

        var page = await Json(await client.GetAsync("/api/unified/sent/messages", Ct));

        Assert.Empty(page.GetProperty("errors").EnumerateArray());
        Assert.Equal(["Sent A2", "Sent B1", "Sent A1"], Subjects(page));
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("unread").GetInt32());
        var hits = page.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal((accountA, MailboxIds.FolderId("Sent"), "Sent"),
            (hits[0].GetProperty("accountId").GetGuid(), hits[0].GetProperty("folderId").GetString(), hits[0].GetProperty("folderFullName").GetString()));
        Assert.Equal((accountB, MailboxIds.FolderId("Sent Items")), (hits[1].GetProperty("accountId").GetGuid(), hits[1].GetProperty("folderId").GetString()));

        // Polling covers each account's folder.
        using (var imap = await dovecot.ConnectAsync(userB))
        {
            var sentItems = await imap.GetFolderAsync("Sent Items", Ct);
            await sentItems.AppendAsync(Message("Sent B2", Start.AddHours(4)), MessageFlags.Seen, Ct);
        }

        var changes = await Json(await client.GetAsync($"/api/unified/sent/changes?since={Uri.EscapeDataString(page.GetProperty("token").GetString()!)}", Ct));
        Assert.False(changes.GetProperty("reset").GetBoolean());
        var added = Assert.Single(changes.GetProperty("messages").EnumerateArray());
        Assert.Equal("Sent B2", added.GetProperty("message").GetProperty("subject").GetString());
        Assert.Equal(MailboxIds.FolderId("Sent Items"), added.GetProperty("folderId").GetString());

        // No account has a Junk folder: an empty list, not an error.
        var junk = await Json(await client.GetAsync("/api/unified/junk/messages", Ct));
        Assert.Empty(junk.GetProperty("messages").EnumerateArray());
        Assert.Empty(junk.GetProperty("errors").EnumerateArray());
        Assert.Equal("", junk.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Smart_folders_filter_their_folders_of_every_account()
    {
        var client = _app.ClientAs("unified-smart");
        var userA = await MailboxWithFoldersAsync(
            ("INBOX", "F1", Start.AddHours(1), MessageFlags.Flagged),
            ("INBOX", "N1", Start.AddHours(2), MessageFlags.None),
            ("Archive", "F3", Start.AddHours(3), MessageFlags.Flagged | MessageFlags.Seen),
            ("Trash", "F9", Start.AddHours(9), MessageFlags.Flagged));
        await AddAccountAsync(client, dovecot, userA);
        await AddAccountAsync(client, dovecot, await MailboxWithFoldersAsync(
            ("INBOX", "F2", Start.AddMinutes(150), MessageFlags.Flagged),
            ("Sent", "F4", Start.AddHours(4), MessageFlags.Flagged | MessageFlags.Seen)));

        // Flagged: inbox, archive and sent, not trash; paged across folders and accounts.
        var subjects = new List<string?>();
        string? before = null;
        string? token = null;
        var pages = 0;
        do
        {
            var query = before is null ? "" : $"&before={Uri.EscapeDataString(before)}";
            var page = await Json(await client.GetAsync($"/api/unified/flagged/messages?limit=2{query}", Ct));
            Assert.Empty(page.GetProperty("errors").EnumerateArray());
            if (pages == 0)
            {
                Assert.Equal(4, page.GetProperty("total").GetInt32());
                Assert.Equal(JsonValueKind.Null, page.GetProperty("unread").ValueKind);
                token = page.GetProperty("token").GetString();
            }

            subjects.AddRange(Subjects(page));
            before = page.GetProperty("nextBefore").GetString();
            pages++;
        }
        while (before is not null && pages < 10);

        Assert.Equal(["F4", "F3", "F2", "F1"], subjects);

        // Unread: the inboxes only.
        var unread = await Json(await client.GetAsync("/api/unified/unread/messages", Ct));
        Assert.Equal(["F2", "N1", "F1"], Subjects(unread));

        // Changes of the smart folder's folders come through its token; the client applies the filter.
        using (var imap = await dovecot.ConnectAsync(userA))
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            var n1 = await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains("N1"), Ct);
            await imap.Inbox.AddFlagsAsync(n1, MessageFlags.Flagged, true, Ct);
        }

        var changes = await Json(await client.GetAsync($"/api/unified/flagged/changes?since={Uri.EscapeDataString(token!)}", Ct));
        Assert.False(changes.GetProperty("reset").GetBoolean());
        var changed = Assert.Single(changes.GetProperty("messages").EnumerateArray());
        Assert.Equal("N1", changed.GetProperty("message").GetProperty("subject").GetString());
        Assert.True(changed.GetProperty("message").GetProperty("flagged").GetBoolean());

        // Smart folders take no further filter.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/unified/flagged/filter?filter=unread", Ct)).StatusCode);
    }

    [Fact]
    public async Task Malformed_cursors_and_tokens_are_rejected()
    {
        var client = _app.ClientAs("unified-invalid");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/unified/inbox/messages?before=nonsense", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/unified/inbox/changes?since=nonsense", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/unified/flagged/messages?before=nonsense", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/unified/outbox/messages", Ct)).StatusCode);
    }
}
