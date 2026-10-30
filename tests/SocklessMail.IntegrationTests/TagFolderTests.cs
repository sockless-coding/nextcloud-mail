using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Tags;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>
/// Tag folders and the tag scan against Dovecot: messages tagged by another program in folders
/// the app never opened are found, Trash is left out, a message in two folders shows once, and
/// app-kept tags find a message another program moved.
/// </summary>
public sealed class TagFolderTests(DovecotFixture dovecot) : IAsyncLifetime
{
    private const string UserId = "tag-folder-user";

    private readonly MailApp _app = new();
    private HttpClient _client = null!;
    private ImapClient _imap = null!;
    private Guid _account;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var user = DovecotFixture.NewUser();
        _imap = await dovecot.ConnectAsync(user);
        var root = _imap.GetFolder(_imap.PersonalNamespaces[0]);
        foreach (var name in new[] { "Archive", "Projects", "Trash" })
        {
            try
            {
                await _imap.GetFolderAsync(name, Ct);
            }
            catch (FolderNotFoundException)
            {
                await root.CreateAsync(name, true, Ct);
            }
        }

        _client = _app.ClientAs(UserId);
        _account = await AddAccountAsync(_client, dovecot, user);
    }

    public async ValueTask DisposeAsync()
    {
        _imap.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<IMailFolder> FolderAsync(string name) => name == "INBOX" ? _imap.Inbox : await _imap.GetFolderAsync(name, Ct);

    private async Task<string> AppendAsync(string folderName, string subject, string? keyword, string? messageId = null)
    {
        var folder = await FolderAsync(folderName);
        var message = new MimeMessage
        {
            Subject = subject,
            MessageId = messageId ?? $"{Guid.NewGuid():N}@example.test",
            Body = new TextPart("plain") { Text = subject },
        };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("", "someone@example.test"));
        await folder.OpenAsync(FolderAccess.ReadWrite, Ct);
        var uid = await folder.AppendAsync(
            new AppendRequest(message, MessageFlags.None) { Keywords = keyword is null ? null : new HashSet<string> { keyword } }, Ct);
        return MailboxIds.MessageId(folder.UidValidity, uid!.Value.Id);
    }

    private async Task<Guid> CreateTagAsync(string name, string? keyword = null) =>
        (await Json(await _client.PostAsJsonAsync("/api/tags", new { name, color = "#00aa00", keyword }, Ct))).GetProperty("id").GetGuid();

    private Task ScanAsync() => _app.Services.GetRequiredService<TagScanner>().ScanUserAsync(UserId, Ct);

    private async Task<(List<(string Subject, string Folder)> Hits, int Total, int Unread)> TagFolderAsync(Guid tag, string query = "")
    {
        var page = await Json(await _client.GetAsync($"/api/tags/{tag}/messages{query}", Ct));
        var hits = page.GetProperty("messages").EnumerateArray()
            .Select(h => (h.GetProperty("message").GetProperty("subject").GetString()!, h.GetProperty("folderFullName").GetString()!))
            .ToList();
        return (hits, page.GetProperty("total").GetInt32(), page.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task The_scan_finds_tagged_mail_in_folders_never_opened_and_leaves_out_trash()
    {
        await AppendAsync("INBOX", "inbox one", "$work");
        await AppendAsync("Projects", "project one", "$work");
        await AppendAsync("Archive", "archived one", "$work");
        await AppendAsync("Trash", "trashed one", "$work");
        await AppendAsync("Projects", "untagged", null);
        var work = await CreateTagAsync("Work", "$work");

        await ScanAsync();

        var (hits, total, unread) = await TagFolderAsync(work);
        Assert.Equal(["archived one", "inbox one", "project one"], hits.Select(h => h.Subject).Order());
        Assert.Equal(3, total);
        Assert.Equal(3, unread);
        Assert.Contains(("project one", "Projects"), hits);

        // Scoped to one folder, to each account's inbox, and narrowed by words.
        Assert.Equal(["project one"], (await TagFolderAsync(work, $"?folderId={MailboxIds.FolderId("Projects")}")).Hits.Select(h => h.Subject));
        Assert.Equal(["inbox one"], (await TagFolderAsync(work, "?role=inbox")).Hits.Select(h => h.Subject));
        Assert.Equal(["archived one"], (await TagFolderAsync(work, "?q=archived")).Hits.Select(h => h.Subject));
        Assert.Equal(3, (await TagFolderAsync(work, $"?accountId={_account}")).Total);
        Assert.Empty((await TagFolderAsync(work, $"?accountId={Guid.NewGuid()}")).Hits);

        var counts = (await Json(await _client.GetAsync("/api/tags/counts", Ct))).EnumerateArray().Single();
        Assert.Equal(work, counts.GetProperty("tagId").GetGuid());
        Assert.Equal(3, counts.GetProperty("total").GetInt32());

        // Another program takes the tag off: the next scan drops it.
        var projects = await FolderAsync("Projects");
        await projects.OpenAsync(FolderAccess.ReadWrite, Ct);
        var uids = await projects.SearchAsync(MailKit.Search.SearchQuery.HasKeyword("$work"), Ct);
        await projects.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Remove, ["$work"]) { Silent = true }, Ct);
        await ScanAsync();
        Assert.Equal(["archived one", "inbox one"], (await TagFolderAsync(work)).Hits.Select(h => h.Subject).Order());
    }

    [Fact]
    public async Task A_message_in_two_folders_shows_once_and_pages_are_complete()
    {
        var shared = $"{Guid.NewGuid():N}@example.test";
        await AppendAsync("INBOX", "labelled", "$label2", shared);
        await AppendAsync("Projects", "labelled", "$label2", shared);
        for (var i = 0; i < 5; i++)
        {
            await AppendAsync("Archive", $"old {i}", "$label2");
        }

        var tag = await CreateTagAsync("Work", "$label2");
        await ScanAsync();

        Assert.Equal(1, (await TagFolderAsync(tag)).Hits.Count(h => h.Subject == "labelled"));

        var seen = new List<string>();
        string? before = null;
        do
        {
            var page = await Json(await _client.GetAsync($"/api/tags/{tag}/messages?limit=2{(before is null ? "" : $"&before={before}")}", Ct));
            seen.AddRange(page.GetProperty("messages").EnumerateArray().Select(h => h.GetProperty("message").GetProperty("subject").GetString()!));
            before = page.GetProperty("nextBefore").GetString();
        }
        while (before is not null);

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task Deleting_a_tag_from_messages_reaches_folders_never_opened()
    {
        await AppendAsync("Projects", "far away", "$temp");
        var tag = await CreateTagAsync("Temp", "$temp");

        var response = await _client.DeleteAsync($"/api/tags/{tag}?fromMessages=true", Ct);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);

        var projects = await FolderAsync("Projects");
        await projects.OpenAsync(FolderAccess.ReadOnly, Ct);
        Assert.Empty(await projects.SearchAsync(MailKit.Search.SearchQuery.HasKeyword("$temp"), Ct));
    }

    [Fact]
    public async Task App_kept_tags_are_found_by_the_scan_after_another_program_moved_the_message()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MailDbContext>().Accounts.IgnoreQueryFilters().Where(a => a.Id == _account)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.TagStorage, TagStorage.Local), Ct);
        }

        var tag = await CreateTagAsync("Travel");
        var id = await AppendAsync("INBOX", "ticket", null);
        await Json(await _client.GetAsync($"/api/accounts/{_account}/folders/{MailboxIds.FolderId("INBOX")}/messages", Ct));
        await Json(await _client.PutAsJsonAsync($"/api/accounts/{_account}/folders/{MailboxIds.FolderId("INBOX")}/messages/tags",
            new { messageIds = new[] { id }, add = new[] { tag } }, Ct));
        Assert.Equal([("ticket", "INBOX")], (await TagFolderAsync(tag)).Hits);

        // Moved to a folder the app never opens; the scan notices it gone and finds it by Message-ID.
        await _imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        MailboxIds.TryParseMessageId(id, out var validity, out var uid);
        await _imap.Inbox.MoveToAsync(new UniqueId(validity, uid), await FolderAsync("Projects"), Ct);
        await ScanAsync();
        await ScanAsync();

        Assert.Equal([("ticket", "Projects")], (await TagFolderAsync(tag)).Hits);
    }
}
