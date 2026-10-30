using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Tags;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>
/// Tags against Dovecot: as IMAP keywords on the server, and kept by the app for an account whose
/// server keeps none (forced in the database, as Dovecot keeps keywords).
/// </summary>
public sealed class TagTests(DovecotFixture dovecot) : IAsyncLifetime
{
    private const string UserId = "tag-user";
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

    private string Base => $"/api/accounts/{_account}";

    private async Task<string> AppendAsync(string subject, IMailFolder? folder = null, string? keyword = null)
    {
        folder ??= _imap.Inbox;
        var message = new MimeMessage { Subject = subject, MessageId = $"{Guid.NewGuid():N}@example.test", Body = new TextPart("plain") { Text = subject } };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("", "someone@example.test"));
        var keywords = keyword is null ? null : new HashSet<string> { keyword };
        // Opened, so UidValidity is the folder's (APPENDUID alone does not set it).
        await folder.OpenAsync(FolderAccess.ReadWrite, Ct);
        var uid = await folder.AppendAsync(new AppendRequest(message, MessageFlags.None) { Keywords = keywords }, Ct);
        return MailboxIds.MessageId(folder.UidValidity, uid!.Value.Id);
    }

    private async Task<JsonElement> CreateTagAsync(string name, string color = "#aa0000", string? keyword = null) =>
        await Json(await _client.PostAsJsonAsync("/api/tags", new { name, color, keyword }, Ct));

    private async Task<JsonElement> SetTagsAsync(string folderId, IEnumerable<string> ids, Guid[]? add = null, Guid[]? remove = null) =>
        await Json(await _client.PutAsJsonAsync($"{Base}/folders/{folderId}/messages/tags", new { messageIds = ids, add, remove }, Ct));

    private async Task<Dictionary<string, string[]>> ListedKeywordsAsync(string folderId)
    {
        var page = await Json(await _client.GetAsync($"{Base}/folders/{folderId}/messages", Ct));
        return page.GetProperty("messages").EnumerateArray().ToDictionary(
            m => m.GetProperty("subject").GetString()!,
            m => m.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()!).ToArray());
    }

    private async Task<HashSet<string>> ServerKeywordsAsync(IMailFolder folder, string subject)
    {
        await folder.OpenAsync(FolderAccess.ReadOnly, Ct);
        var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(subject), Ct);
        var summary = (await folder.FetchAsync(uids, MessageSummaryItems.Flags, Ct)).Single();
        return summary.Keywords.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task ForceLocalStorageAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        await db.Accounts.IgnoreQueryFilters().Where(a => a.Id == _account)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TagStorage, TagStorage.Local), Ct);
    }

    [Fact]
    public async Task A_tag_is_an_imap_keyword_that_follows_rename()
    {
        var tag = await CreateTagAsync("Invoices 2026");
        Assert.Equal("$invoices_2026", tag.GetProperty("keyword").GetString());
        var id = await AppendAsync("pay me");
        await AppendAsync("other");
        await ListedKeywordsAsync(Inbox);

        var result = await SetTagsAsync(Inbox, [id], add: [tag.GetProperty("id").GetGuid()]);

        Assert.Equal(["$invoices_2026"], result.GetProperty("keywords").GetProperty(id).EnumerateArray().Select(k => k.GetString()));
        Assert.Contains("$invoices_2026", await ServerKeywordsAsync(_imap.Inbox, "pay me"));
        var listed = await ListedKeywordsAsync(Inbox);
        Assert.Equal(["$invoices_2026"], listed["pay me"]);
        Assert.Empty(listed["other"]);
        var account = await Json(await _client.GetAsync($"{Base}", Ct));
        Assert.Equal("keywords", account.GetProperty("tagStorage").GetString());

        // A rename keeps the keyword, so the message keeps the tag.
        var renamed = await Json(await _client.PutAsJsonAsync($"/api/tags/{tag.GetProperty("id").GetGuid()}", new { name = "Bills" }, Ct));
        Assert.Equal("$invoices_2026", renamed.GetProperty("keyword").GetString());

        await SetTagsAsync(Inbox, [id], remove: [tag.GetProperty("id").GetGuid()]);
        Assert.DoesNotContain("$invoices_2026", await ServerKeywordsAsync(_imap.Inbox, "pay me"));
        Assert.Empty((await ListedKeywordsAsync(Inbox))["pay me"]);
    }

    [Fact]
    public async Task Keywords_set_by_another_program_show_up_and_are_suggested()
    {
        await AppendAsync("from thunderbird", keyword: "$label1");
        var plain = await AppendAsync("plain");
        await ListedKeywordsAsync(Inbox);

        // Another client tags a message the cache already holds (found by CONDSTORE).
        await _imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        MailboxIds.TryParseMessageId(plain, out var validity, out var uid);
        await _imap.Inbox.StoreAsync(new UniqueId(validity, uid), new StoreFlagsRequest(StoreAction.Add, ["projectx"]) { Silent = true }, Ct);

        var listed = await ListedKeywordsAsync(Inbox);
        Assert.Equal(["$label1"], listed["from thunderbird"]);
        Assert.Equal(["projectx"], listed["plain"]);

        var suggestions = (await Json(await _client.GetAsync("/api/tags/suggestions", Ct))).EnumerateArray().ToList();
        var label1 = suggestions.Single(s => s.GetProperty("keyword").GetString() == "$label1");
        Assert.Equal("Important", label1.GetProperty("name").GetString());
        Assert.Contains(suggestions, s => s.GetProperty("keyword").GetString() == "projectx");

        // Adopting it makes it a tag; it is no longer suggested.
        var adopted = await CreateTagAsync("Important", "#ff0000", "$label1");
        Assert.Equal("$label1", adopted.GetProperty("keyword").GetString());
        suggestions = (await Json(await _client.GetAsync("/api/tags/suggestions", Ct))).EnumerateArray().ToList();
        Assert.DoesNotContain(suggestions, s => s.GetProperty("keyword").GetString() == "$label1");
    }

    [Fact]
    public async Task Deleting_a_tag_can_take_it_off_its_messages()
    {
        var tag = await CreateTagAsync("Temp");
        var tagId = tag.GetProperty("id").GetGuid();
        var id = await AppendAsync("tagged");
        await ListedKeywordsAsync(Inbox);
        await SetTagsAsync(Inbox, [id], add: [tagId]);

        var response = await _client.DeleteAsync($"/api/tags/{tagId}?fromMessages=true", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain("$temp", await ServerKeywordsAsync(_imap.Inbox, "tagged"));
        Assert.Empty((await Json(await _client.GetAsync("/api/tags", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task App_kept_tags_stay_off_the_server_and_follow_moves()
    {
        await ForceLocalStorageAsync();
        var tagId = (await CreateTagAsync("Receipts")).GetProperty("id").GetGuid();
        var id = await AppendAsync("receipt");
        await ListedKeywordsAsync(Inbox);

        await SetTagsAsync(Inbox, [id], add: [tagId]);

        Assert.DoesNotContain("$receipts", await ServerKeywordsAsync(_imap.Inbox, "receipt"));
        Assert.Equal(["$receipts"], (await ListedKeywordsAsync(Inbox))["receipt"]);
        var opened = await Json(await _client.GetAsync($"{Base}/folders/{Inbox}/messages/{id}", Ct));
        Assert.Equal(["$receipts"], opened.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()));

        // Moved by the app: the index follows the UID map.
        await Json(await _client.PostAsJsonAsync($"{Base}/folders/{Inbox}/messages/move", new { messageIds = new[] { id }, targetFolderId = Archive }, Ct));
        Assert.Equal(["$receipts"], (await ListedKeywordsAsync(Archive))["receipt"]);
    }

    [Fact]
    public async Task App_kept_tags_are_found_again_after_another_program_moved_the_message()
    {
        await ForceLocalStorageAsync();
        var tagId = (await CreateTagAsync("Travel")).GetProperty("id").GetGuid();
        var id = await AppendAsync("ticket");
        await ListedKeywordsAsync(Inbox);
        await SetTagsAsync(Inbox, [id], add: [tagId]);

        await _imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        MailboxIds.TryParseMessageId(id, out var validity, out var uid);
        await _imap.Inbox.MoveToAsync(new UniqueId(validity, uid), await _imap.GetFolderAsync("Archive", Ct), Ct);

        // The inbox sync loses the message's place; the archive's finds it by Message-ID.
        Assert.DoesNotContain("ticket", (await ListedKeywordsAsync(Inbox)).Keys);
        Assert.Equal(["$travel"], (await ListedKeywordsAsync(Archive))["ticket"]);
    }

    [Fact]
    public async Task Tag_changes_are_validated()
    {
        var id = await AppendAsync("x");
        var tagId = (await CreateTagAsync("One")).GetProperty("id").GetGuid();

        var unknown = await _client.PutAsJsonAsync($"{Base}/folders/{Inbox}/messages/tags", new { messageIds = new[] { id }, add = new[] { Guid.NewGuid() } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var both = await _client.PutAsJsonAsync($"{Base}/folders/{Inbox}/messages/tags", new { messageIds = new[] { id }, add = new[] { tagId }, remove = new[] { tagId } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        var none = await _client.PutAsJsonAsync($"{Base}/folders/{Inbox}/messages/tags", new { messageIds = new[] { id } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        // Another user's tag is no tag at all.
        var other = await Json(await _app.ClientAs("someone-else").PostAsJsonAsync("/api/tags", new { name = "Theirs" }, Ct));
        var foreign = await _client.PutAsJsonAsync($"{Base}/folders/{Inbox}/messages/tags",
            new { messageIds = new[] { id }, add = new[] { other.GetProperty("id").GetGuid() } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
    }
}
