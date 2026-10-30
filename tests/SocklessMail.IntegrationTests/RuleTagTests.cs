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
using SocklessMail.Api.Tags;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>The rules' "Tag" action against Dovecot: as a keyword on the server and kept by the app, with and without a move.</summary>
public sealed class RuleTagTests(DovecotFixture dovecot) : IAsyncLifetime
{
    private const string UserId = "rule-tag-user";
    private static readonly string Inbox = MailboxIds.FolderId("INBOX");
    private static readonly string Archive = MailboxIds.FolderId("Archive");

    private readonly MailApp _app = new();
    private HttpClient _client = null!;
    private ImapClient _imap = null!;
    private Guid _account;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var user = DovecotFixture.NewUser();
        _imap = await dovecot.ConnectAsync(user);
        await _imap.GetFolder(_imap.PersonalNamespaces[0]).CreateAsync("Archive", true, Ct);
        _client = _app.ClientAs(UserId);
        _account = await AddAccountAsync(_client, dovecot, user);
    }

    public async ValueTask DisposeAsync()
    {
        _imap.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<Guid> TagAsync(string name) =>
        (await Json(await _client.PostAsJsonAsync("/api/tags", new { name }, Ct))).GetProperty("id").GetGuid();

    private async Task<JsonElement> SaveRuleAsync(string from, params object[] actions) =>
        (await Json(await _client.PutAsJsonAsync($"/api/accounts/{_account}/rules", new
        {
            rules = new[] { new { name = $"Rule {from}", enabled = true, matchAll = true, stop = false, conditions = new[] { new { field = "from", op = "contains", value = from } }, actions } },
        }, Ct))).GetProperty("rules")[0];

    private async Task DeliverAsync(string subject, string from)
    {
        var message = new MimeMessage { Subject = subject, MessageId = $"{Guid.NewGuid():N}@example.test", Body = new TextPart("plain") { Text = "hello" } };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("me@example.test"));
        await _imap.Inbox.AppendAsync(message, MessageFlags.None, Ct);
    }

    private async Task<Dictionary<string, string[]>> ListedAsync(string folderId) =>
        (await Json(await _client.GetAsync($"/api/accounts/{_account}/folders/{folderId}/messages", Ct))).GetProperty("messages").EnumerateArray()
        .ToDictionary(m => m.GetProperty("subject").GetString()!, m => m.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()!).ToArray());

    private async Task<HashSet<string>> ServerKeywordsAsync(string folderName, string subject)
    {
        var folder = folderName == "INBOX" ? _imap.Inbox : await _imap.GetFolderAsync(folderName, Ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, Ct);
        var uids = await folder.SearchAsync(SearchQuery.SubjectContains(subject), Ct);
        var summary = (await folder.FetchAsync(uids, MessageSummaryItems.Flags, Ct)).Single();
        await folder.CloseAsync(false, Ct);
        return summary.Keywords.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<(string Subject, string Folder)>> TagFolderAsync(Guid tag) =>
        (await Json(await _client.GetAsync($"/api/tags/{tag}/messages", Ct))).GetProperty("messages").EnumerateArray()
        .Select(h => (h.GetProperty("message").GetProperty("subject").GetString()!, h.GetProperty("folderFullName").GetString()!))
        .ToList();

    private async Task ForceLocalStorageAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MailDbContext>().Accounts.IgnoreQueryFilters().Where(a => a.Id == _account)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TagStorage, TagStorage.Local), Ct);
    }

    [Fact]
    public async Task New_mail_is_tagged_and_kept_in_the_inbox()
    {
        var tag = await TagAsync("Invoices");
        await SaveRuleAsync("billing@", new { type = "tag", tagId = tag });

        await DeliverAsync("Your invoice", "billing@example.test");
        await DeliverAsync("Hello", "friend@example.test");

        var listed = await ListedAsync(Inbox);
        Assert.Equal(["$invoices"], listed["Your invoice"]);
        Assert.Empty(listed["Hello"]);
        Assert.Contains("$invoices", await ServerKeywordsAsync("INBOX", "Your invoice"));
        Assert.Equal([("Your invoice", "INBOX")], await TagFolderAsync(tag));
    }

    [Fact]
    public async Task A_tag_travels_with_the_message_the_rule_moves()
    {
        var tag = await TagAsync("Receipts");
        await SaveRuleAsync("shop@", new { type = "move", folderId = Archive }, new { type = "tag", tagId = tag });

        await DeliverAsync("Receipt", "shop@example.test");
        await ListedAsync(Inbox);

        Assert.Contains("$receipts", await ServerKeywordsAsync("Archive", "Receipt"));
        Assert.Equal(["$receipts"], (await ListedAsync(Archive))["Receipt"]);
        Assert.Equal([("Receipt", "Archive")], await TagFolderAsync(tag));
    }

    [Fact]
    public async Task App_kept_tags_follow_the_message_the_rule_moves()
    {
        await ForceLocalStorageAsync();
        var tag = await TagAsync("Travel");
        await SaveRuleAsync("airline@", new { type = "tag", tagId = tag }, new { type = "move", folderId = Archive });

        await DeliverAsync("Boarding pass", "airline@example.test");
        await ListedAsync(Inbox);

        Assert.DoesNotContain("$travel", await ServerKeywordsAsync("Archive", "Boarding pass"));
        Assert.Equal([("Boarding pass", "Archive")], await TagFolderAsync(tag));
        Assert.Equal(["$travel"], (await ListedAsync(Archive))["Boarding pass"]);
    }

    [Fact]
    public async Task Running_on_existing_messages_tags_them_in_place()
    {
        await DeliverAsync("Older report", "reports@example.test");
        await ListedAsync(Inbox);
        var tag = await TagAsync("Reports");
        var rule = await SaveRuleAsync("reports@", new { type = "tag", tagId = tag });
        var ruleId = rule.GetProperty("id").GetGuid();

        var preview = await Json(await _client.PostAsJsonAsync($"/api/accounts/{_account}/rules/{ruleId}/preview", new { folderId = Inbox }, Ct));
        await Json(await _client.PostAsJsonAsync($"/api/accounts/{_account}/rules/{ruleId}/run",
            new { folderId = Inbox, messageIds = preview.GetProperty("messageIds") }, Ct));

        Assert.Equal(["$reports"], (await ListedAsync(Inbox))["Older report"]);
        Assert.Equal([("Older report", "INBOX")], await TagFolderAsync(tag));
    }

    [Fact]
    public async Task A_deleted_tag_is_reported_on_the_rule_and_others_can_still_be_saved()
    {
        var tag = await TagAsync("Gone");
        await SaveRuleAsync("old@", new { type = "tag", tagId = tag });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/tags/{tag}", Ct)).StatusCode);

        await DeliverAsync("Still arrives", "old@example.test");
        await ListedAsync(Inbox);

        var rules = (await Json(await _client.GetAsync($"/api/accounts/{_account}/rules", Ct))).GetProperty("rules");
        Assert.Contains("tag was deleted", rules[0].GetProperty("error").GetString());
        // Saving the list again (as reordering does) keeps the rule.
        await Json(await _client.PutAsJsonAsync($"/api/accounts/{_account}/rules", new { rules }, Ct));

        // A tag that never was the user's is refused.
        var bad = await _client.PutAsJsonAsync($"/api/accounts/{_account}/rules", new
        {
            rules = new[] { new { name = "Bad", enabled = true, matchAll = true, stop = false, conditions = new[] { new { field = "from", op = "contains", value = "x" } }, actions = new[] { new { type = "tag", tagId = Guid.NewGuid() } } } },
        }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
