using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Rules;
using SocklessMail.Api.Sync;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Mail rules run against Dovecot: by INBOX syncs, by the background runner and by the IDLE watcher.</summary>
public sealed class RuleTests(DovecotFixture dovecot) : IAsyncLifetime
{
    private const string UserId = "rules-user";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private static readonly string Inbox = MailboxIds.FolderId("INBOX");
    private static readonly string Archive = MailboxIds.FolderId("Archive");

    private MailApp _app = null!;
    private HttpClient _client = null!;
    private ImapClient _imap = null!;
    private Guid _account;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private async Task SetUpAsync(Action<IServiceCollection>? services = null)
    {
        _app = new MailApp(services);
        _client = _app.ClientAs(UserId);
        var user = DovecotFixture.NewUser();
        _imap = await dovecot.ConnectAsync(user);
        var root = _imap.GetFolder(_imap.PersonalNamespaces[0]);
        foreach (var name in new[] { "Archive", "Trash", "Copies" })
        {
            await root.CreateAsync(name, true, Ct);
        }

        _account = await AddAccountAsync(_client, dovecot, user);
    }

    public async ValueTask DisposeAsync()
    {
        _imap?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static object Rule(string name, object condition, object[] actions, bool stop = false, bool enabled = true) => new
    {
        name,
        enabled,
        matchAll = true,
        stop,
        conditions = new[] { condition },
        actions,
    };

    private static object FromContains(string value) => new { field = "from", op = "contains", value };

    private async Task<JsonElement> SaveAsync(params object[] rules) =>
        (await Json(await _client.PutAsJsonAsync($"/api/accounts/{_account}/rules", new { rules }, Ct))).GetProperty("rules");

    private async Task DeliverAsync(string subject, string from = "someone@example.test", MessageFlags flags = MessageFlags.None, string folderName = "INBOX")
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "hello" } };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("me@example.test"));
        var folder = folderName == "INBOX" ? _imap.Inbox : await _imap.GetFolderAsync(folderName, Ct);
        await folder.AppendAsync(message, flags, Ct);
    }

    /// <summary>Subjects in a folder on the server, with their flags.</summary>
    private async Task<Dictionary<string, MessageFlags>> ServerAsync(string folderName)
    {
        var folder = folderName == "INBOX" ? _imap.Inbox : await _imap.GetFolderAsync(folderName, Ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, Ct);
        var uids = await folder.SearchAsync(SearchQuery.All, Ct);
        var summaries = uids.Count == 0 ? [] : await folder.FetchAsync(uids, MessageSummaryItems.Envelope | MessageSummaryItems.Flags, Ct);
        await folder.CloseAsync(false, Ct);
        return summaries.ToDictionary(s => s.Envelope!.Subject ?? "", s => s.Flags!.Value);
    }

    private async Task<List<string>> InboxListAsync() =>
        (await Json(await _client.GetAsync($"/api/accounts/{_account}/folders/{Inbox}/messages", Ct)))
        .GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString()!).ToList();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task New_mail_is_sorted_before_the_inbox_lists_it()
    {
        await SetUpAsync();
        await DeliverAsync("Old news", "news@example.test");
        await SaveAsync(
            Rule("Newsletters", FromContains("news@"), [new { type = "markRead" }, new { type = "move", folderId = Archive }]),
            Rule("Flag boss", FromContains("boss@"), [new { type = "flag" }]));

        await DeliverAsync("Fresh news", "news@example.test");
        await DeliverAsync("From the boss", "boss@example.test");
        await DeliverAsync("Unrelated");

        var listed = await InboxListAsync();
        Assert.DoesNotContain("Fresh news", listed);
        Assert.Contains("Unrelated", listed);
        // Mail from before the rules were enabled stays as it was.
        Assert.Contains("Old news", listed);

        var archive = await ServerAsync("Archive");
        Assert.True(archive["Fresh news"].HasFlag(MessageFlags.Seen));
        var inbox = await ServerAsync("INBOX");
        Assert.True(inbox["From the boss"].HasFlag(MessageFlags.Flagged));
        Assert.False(inbox["Unrelated"].HasFlag(MessageFlags.Flagged));
        Assert.False(inbox["Old news"].HasFlag(MessageFlags.Seen));
    }

    [Fact]
    public async Task Rules_run_in_order_until_a_move_or_stop()
    {
        await SetUpAsync();
        await SaveAsync(
            Rule("Copy", FromContains("a@"), [new { type = "copy", folderId = MailboxIds.FolderId("Copies") }], stop: true),
            Rule("Never reached for a@", FromContains("example.test"), [new { type = "flag" }]),
            Rule("Trash b@", FromContains("b@"), [new { type = "trash" }]),
            Rule("Never reached for b@", FromContains("example.test"), [new { type = "markRead" }]));

        await DeliverAsync("To a", "a@example.test");
        await DeliverAsync("To b", "b@example.test");
        await DeliverAsync("To c", "c@example.test");
        await InboxListAsync();

        var inbox = await ServerAsync("INBOX");
        Assert.Equal(MessageFlags.None, inbox["To a"] & (MessageFlags.Flagged | MessageFlags.Seen));
        Assert.Contains("To a", (await ServerAsync("Copies")).Keys);
        Assert.False((await ServerAsync("Trash"))["To b"].HasFlag(MessageFlags.Seen));
        Assert.DoesNotContain("To b", inbox.Keys);
        Assert.True(inbox["To c"].HasFlag(MessageFlags.Flagged));
        Assert.True(inbox["To c"].HasFlag(MessageFlags.Seen));
    }

    [Fact]
    public async Task A_missing_folder_is_reported_on_its_rule_and_the_others_still_run()
    {
        await SetUpAsync();
        await SaveAsync(
            Rule("Flag", FromContains("news@"), [new { type = "flag" }]),
            Rule("Gone", FromContains("news@"), [new { type = "move", folderId = MailboxIds.FolderId("Nowhere") }]));

        await DeliverAsync("Stuck", "news@example.test");
        Assert.Contains("Stuck", await InboxListAsync());
        Assert.True((await ServerAsync("INBOX"))["Stuck"].HasFlag(MessageFlags.Flagged));

        var rules = (await Json(await _client.GetAsync($"/api/accounts/{_account}/rules", Ct))).GetProperty("rules");
        Assert.Equal(JsonValueKind.Null, rules[0].GetProperty("error").ValueKind);
        Assert.Contains("Nowhere", rules[1].GetProperty("error").GetString());

        // The cursor moved on: the next sync does not try again.
        await InboxListAsync();
        await using var scope = _app.Services.GetRequiredService<IServiceScopeFactory>().CreateUserScope(UserId);
        var cursor = await scope.ServiceProvider.GetRequiredService<MailDbContext>().RuleCursors.SingleAsync(Ct);
        Assert.Equal(_imap.Inbox.UidNext!.Value.Id - 1, cursor.LastUid);
    }

    [Fact]
    public async Task The_runner_sorts_mail_without_anyone_asking()
    {
        await SetUpAsync();
        await SaveAsync(Rule("Archive", FromContains("news@"), [new { type = "move", folderId = Archive }]));
        await DeliverAsync("Overnight", "news@example.test");

        await _app.Services.GetRequiredService<RuleRunner>().RunAllAsync(Ct);

        Assert.Contains("Overnight", (await ServerAsync("Archive")).Keys);
        Assert.DoesNotContain("Overnight", (await ServerAsync("INBOX")).Keys);
    }

    [Fact]
    public async Task Disabling_all_rules_forgets_the_position()
    {
        await SetUpAsync();
        await SaveAsync(Rule("Archive", FromContains("news@"), [new { type = "move", folderId = Archive }]));
        await SaveAsync(Rule("Archive", FromContains("news@"), [new { type = "move", folderId = Archive }], enabled: false));
        await DeliverAsync("While disabled", "news@example.test");

        // Enabled again: only mail from now on.
        await SaveAsync(Rule("Archive", FromContains("news@"), [new { type = "move", folderId = Archive }]));
        await DeliverAsync("After", "news@example.test");
        var listed = await InboxListAsync();
        Assert.Contains("While disabled", listed);
        Assert.DoesNotContain("After", listed);
    }

    [Fact]
    public async Task The_watcher_sorts_new_mail_while_idling()
    {
        await SetUpAsync(services => services.AddSingleton(new WatcherOptions
        {
            MaxWatchers = 5,
            ReconcileInterval = TimeSpan.FromMilliseconds(500),
            FirstRetryDelay = TimeSpan.FromSeconds(1),
        }));
        await SaveAsync(Rule("Archive", FromContains("news@"), [new { type = "move", folderId = Archive }]));
        await InboxListAsync();
        var watchers = _app.Services.GetRequiredService<MailboxWatchers>();
        await WaitUntilAsync(() => Task.FromResult(watchers.IsCurrent(_account, "INBOX")), "the inbox is watched");

        await DeliverAsync("Pushed news", "news@example.test");
        await DeliverAsync("Pushed other");
        await WaitUntilAsync(async () => (await ServerAsync("Archive")).ContainsKey("Pushed news"), "the watcher ran the rule");

        await using var scope = _app.Services.GetRequiredService<IServiceScopeFactory>().CreateUserScope(UserId);
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        await WaitUntilAsync(() => db.Messages.AnyAsync(m => m.Subject == "Pushed other"), "the other message is cached");
        Assert.False(await db.Messages.AnyAsync(m => m.Subject == "Pushed news", Ct));
    }

    [Fact]
    public async Task Rules_count_their_matches_and_keep_the_count_when_edited()
    {
        await SetUpAsync();
        var rule = (await SaveAsync(Rule("Flag", FromContains("news@"), [new { type = "flag" }])))[0];
        await DeliverAsync("One", "news@example.test");
        await DeliverAsync("Two", "news@example.test");
        await DeliverAsync("Other");
        await InboxListAsync();

        var saved = (await Json(await _client.GetAsync($"/api/accounts/{_account}/rules", Ct))).GetProperty("rules")[0];
        Assert.Equal(2, saved.GetProperty("matchCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, saved.GetProperty("lastMatchedAt").ValueKind);

        var edited = (await SaveAsync(new
        {
            id = rule.GetProperty("id").GetGuid(),
            name = "Flag news",
            enabled = true,
            matchAll = true,
            stop = false,
            conditions = new[] { FromContains("news@example") },
            actions = new[] { new { type = "flag" } },
        }))[0];
        Assert.Equal(2, edited.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task A_rule_runs_on_existing_messages_after_a_preview()
    {
        await SetUpAsync();
        await DeliverAsync("Old one", "news@example.test");
        await DeliverAsync("Old two", "news@example.test");
        await DeliverAsync("Keep");
        // Disabled: running on existing messages does not need the rule switched on.
        var rule = (await SaveAsync(Rule("Archive", FromContains("news@"),
            [new { type = "markRead" }, new { type = "move", folderId = Archive }], enabled: false)))[0];
        var ruleBase = $"/api/accounts/{_account}/rules/{rule.GetProperty("id").GetGuid()}";
        Assert.Equal(3, (await InboxListAsync()).Count);

        var preview = await Json(await _client.PostAsJsonAsync($"{ruleBase}/preview", new { folderId = Inbox }, Ct));
        Assert.Equal(3, preview.GetProperty("scanned").GetInt32());
        Assert.Equal(2, preview.GetProperty("matched").GetInt32());
        var ids = preview.GetProperty("messageIds").EnumerateArray().Select(i => i.GetString()).ToList();
        Assert.Equal(2, ids.Count);

        var run = await Json(await _client.PostAsJsonAsync($"{ruleBase}/run", new { folderId = Inbox, messageIds = ids }, Ct));
        Assert.Equal(2, run.GetProperty("applied").GetInt32());

        var archive = await ServerAsync("Archive");
        Assert.True(archive["Old one"].HasFlag(MessageFlags.Seen));
        Assert.True(archive["Old two"].HasFlag(MessageFlags.Seen));
        Assert.Equal(["Keep"], (await ServerAsync("INBOX")).Keys);
        // The cache dropped them at once, without waiting for a sync.
        await using (var scope = _app.Services.GetRequiredService<IServiceScopeFactory>().CreateUserScope(UserId))
        {
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            Assert.False(await db.Messages.AnyAsync(m => m.Subject.StartsWith("Old"), Ct));
        }

        // Run on the rule's own target: nothing to move, but the rest still applies.
        await DeliverAsync("Unread in archive", "news@example.test", folderName: "Archive");
        preview = await Json(await _client.PostAsJsonAsync($"{ruleBase}/preview", new { folderId = Archive }, Ct));
        ids = preview.GetProperty("messageIds").EnumerateArray().Select(i => i.GetString()).ToList();
        run = await Json(await _client.PostAsJsonAsync($"{ruleBase}/run", new { folderId = Archive, messageIds = ids }, Ct));
        Assert.Equal(3, run.GetProperty("applied").GetInt32());
        archive = await ServerAsync("Archive");
        Assert.Equal(3, archive.Count);
        Assert.True(archive["Unread in archive"].HasFlag(MessageFlags.Seen));

        var counted = (await Json(await _client.GetAsync($"/api/accounts/{_account}/rules", Ct))).GetProperty("rules")[0];
        Assert.Equal(5, counted.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task Running_on_existing_messages_refuses_stale_ids_and_unknown_rules()
    {
        await SetUpAsync();
        await DeliverAsync("One", "news@example.test");
        var rule = (await SaveAsync(Rule("Flag", FromContains("news@"), [new { type = "flag" }])))[0];
        var ruleBase = $"/api/accounts/{_account}/rules/{rule.GetProperty("id").GetGuid()}";

        var stale = await _client.PostAsJsonAsync($"{ruleBase}/run", new { folderId = Inbox, messageIds = new[] { "1-1" } }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
        var unknown = await _client.PostAsJsonAsync($"/api/accounts/{_account}/rules/{Guid.NewGuid()}/preview", new { folderId = Inbox }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);
        var noFolder = await _client.PostAsJsonAsync($"{ruleBase}/preview", new { folderId = MailboxIds.FolderId("Nowhere") }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, noFolder.StatusCode);
    }

    [Fact]
    public async Task A_draft_rule_is_tried_on_the_newest_messages()
    {
        await SetUpAsync();
        await DeliverAsync("One", "news@example.test");
        await DeliverAsync("Two");
        await DeliverAsync("Three", "news@example.test");

        var result = await Json(await _client.PostAsJsonAsync($"/api/accounts/{_account}/rules/test",
            new { rule = Rule("Try", FromContains("news@"), [new { type = "flag" }]) }, Ct));
        Assert.Equal(3, result.GetProperty("scanned").GetInt32());
        Assert.Equal(["Three", "One"], result.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("subject").GetString()));
        // Trying acts on nothing.
        Assert.All((await ServerAsync("INBOX")).Values, flags => Assert.False(flags.HasFlag(MessageFlags.Flagged)));
    }
}
