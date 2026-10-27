using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using SocklessMail.Api.Mailbox;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Flags, move, delete and folder management, verified on the server over IMAP.</summary>
public sealed class MailboxActionTests(GreenMailFixture mail) : IAsyncLifetime
{
    private const string Actions = "actions@example.test";
    private const string NoTrash = "notrash@example.test";

    private readonly MailApp _app = new();
    private HttpClient _client = null!;
    private Guid _account;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        using (var imap = await ConnectAsync(Actions))
        {
            var root = imap.GetFolder(imap.PersonalNamespaces[0]);
            foreach (var name in new[] { "Trash", "Archive" })
            {
                try
                {
                    await imap.GetFolderAsync(name, Ct);
                }
                catch (FolderNotFoundException)
                {
                    await root.CreateAsync(name, true, Ct);
                }
            }
        }

        _client = _app.ClientAs("actions-user");
        _account = await AddAccountAsync(_client, mail, Actions, GreenMailFixture.ActionsPassword);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private string Base => $"/api/accounts/{_account}";
    private static string Inbox => MailboxIds.FolderId("INBOX");

    private async Task<ImapClient> ConnectAsync(string user)
    {
        var client = new ImapClient();
        await client.ConnectAsync(mail.Host, mail.Imap, SecureSocketOptions.None, Ct);
        await client.AuthenticateAsync(user, GreenMailFixture.ActionsPassword, Ct);
        return client;
    }

    /// <summary>Appends messages with a unique subject prefix and returns their API ids, oldest first.</summary>
    private async Task<(string Prefix, List<string> Ids)> SeedAsync(int count, string user = Actions)
    {
        var prefix = $"T{Guid.NewGuid():N}"[..10];
        using var imap = await ConnectAsync(user);
        await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var message = new MimeMessage { Subject = $"{prefix} {i}", Body = new TextPart("plain") { Text = "x" } };
            message.From.Add(MailboxAddress.Parse("someone@example.test"));
            message.To.Add(MailboxAddress.Parse(user));
            var uid = await imap.Inbox.AppendAsync(message, MessageFlags.None, Ct);
            ids.Add(MailboxIds.MessageId(imap.Inbox.UidValidity, uid!.Value.Id));
        }

        return (prefix, ids);
    }

    private async Task<IList<IMessageSummary>> ServerStateAsync(string folder, string prefix, string user = Actions)
    {
        using var imap = await ConnectAsync(user);
        var f = folder == "INBOX" ? imap.Inbox : await imap.GetFolderAsync(folder, Ct);
        await f.OpenAsync(FolderAccess.ReadOnly, Ct);
        var uids = await f.SearchAsync(SearchQuery.SubjectContains(prefix), Ct);
        return uids.Count == 0 ? [] : await f.FetchAsync(uids, MessageSummaryItems.Flags | MessageSummaryItems.Envelope, Ct);
    }

    [Fact]
    public async Task Flags_are_set_and_cleared_on_the_server()
    {
        var (prefix, ids) = await SeedAsync(3);

        var response = await _client.PutAsJsonAsync($"{Base}/folders/{Inbox}/messages/flags", new { messageIds = ids[..2], seen = true, flagged = true }, Ct);
        Assert.Equal(2, (await Json(response)).GetProperty("updated").GetInt32());

        var state = (await ServerStateAsync("INBOX", prefix)).ToDictionary(s => s.Envelope!.Subject ?? "", s => s.Flags!.Value);
        Assert.True(state[$"{prefix} 0"].HasFlag(MessageFlags.Seen) && state[$"{prefix} 0"].HasFlag(MessageFlags.Flagged));
        Assert.True(state[$"{prefix} 1"].HasFlag(MessageFlags.Seen));
        Assert.False(state[$"{prefix} 2"].HasFlag(MessageFlags.Seen));

        await Json(await _client.PutAsJsonAsync($"{Base}/folders/{Inbox}/messages/flags", new { messageIds = ids[..1], seen = false }, Ct));
        state = (await ServerStateAsync("INBOX", prefix)).ToDictionary(s => s.Envelope!.Subject ?? "", s => s.Flags!.Value);
        Assert.False(state[$"{prefix} 0"].HasFlag(MessageFlags.Seen));
        Assert.True(state[$"{prefix} 0"].HasFlag(MessageFlags.Flagged), "a flag not mentioned must stay untouched");
    }

    [Fact]
    public async Task Messages_are_moved()
    {
        var (prefix, ids) = await SeedAsync(2);

        var response = await _client.PostAsJsonAsync($"{Base}/folders/{Inbox}/messages/move",
            new { messageIds = ids, targetFolderId = MailboxIds.FolderId("Archive") }, Ct);

        Assert.Equal(2, (await Json(response)).GetProperty("moved").GetInt32());
        Assert.Empty(await ServerStateAsync("INBOX", prefix));
        Assert.Equal(2, (await ServerStateAsync("Archive", prefix)).Count);
    }

    [Fact]
    public async Task Delete_moves_to_trash_and_deleting_in_trash_is_permanent()
    {
        var (prefix, ids) = await SeedAsync(1);

        var first = await Json(await _client.PostAsJsonAsync($"{Base}/folders/{Inbox}/messages/delete", new { messageIds = ids }, Ct));
        Assert.True(first.GetProperty("movedToTrash").GetBoolean());
        var trashId = first.GetProperty("trashFolderId").GetString()!;
        Assert.Equal(MailboxIds.FolderId("Trash"), trashId);
        Assert.Empty(await ServerStateAsync("INBOX", prefix));
        Assert.Single(await ServerStateAsync("Trash", prefix));

        var inTrash = (await Json(await _client.GetAsync($"{Base}/folders/{trashId}/messages", Ct))).GetProperty("messages")
            .EnumerateArray().Single(m => m.GetProperty("subject").GetString()!.StartsWith(prefix)).GetProperty("id").GetString();
        var second = await Json(await _client.PostAsJsonAsync($"{Base}/folders/{trashId}/messages/delete", new { messageIds = new[] { inTrash } }, Ct));
        Assert.False(second.GetProperty("movedToTrash").GetBoolean());
        Assert.Empty(await ServerStateAsync("Trash", prefix));
    }

    [Fact]
    public async Task Without_trash_delete_asks_before_deleting_permanently()
    {
        var client = _app.ClientAs("no-trash-user");
        var account = await AddAccountAsync(client, mail, NoTrash, GreenMailFixture.ActionsPassword);
        var (prefix, ids) = await SeedAsync(1, NoTrash);
        var path = $"/api/accounts/{account}/folders/{Inbox}/messages/delete";

        var refused = await client.PostAsJsonAsync(path, new { messageIds = ids }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("no_trash", JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString());
        Assert.Single(await ServerStateAsync("INBOX", prefix, NoTrash));

        await Json(await client.PostAsJsonAsync(path, new { messageIds = ids, permanent = true }, Ct));
        Assert.Empty(await ServerStateAsync("INBOX", prefix, NoTrash));
    }

    [Fact]
    public async Task Ids_from_before_a_renumbering_are_refused()
    {
        var (_, ids) = await SeedAsync(1);
        var stale = "1-" + ids[0].Split('-')[1];

        var response = await _client.PutAsJsonAsync($"{Base}/folders/{Inbox}/messages/flags", new { messageIds = new[] { stale }, seen = true }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("{ \"messageIds\": [], \"seen\": true }")]
    [InlineData("{ \"messageIds\": [\"garbage\"], \"seen\": true }")]
    [InlineData("{ \"messageIds\": [\"1-1\"] }")]
    public async Task Invalid_flag_requests_are_400(string body)
    {
        var response = await _client.PutAsync($"{Base}/folders/{Inbox}/messages/flags",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Move_to_the_same_folder_is_400()
    {
        var (_, ids) = await SeedAsync(1);
        var response = await _client.PostAsJsonAsync($"{Base}/folders/{Inbox}/messages/move", new { messageIds = ids, targetFolderId = Inbox }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Folders_are_created_nested_and_renamed()
    {
        var name = $"Project {Guid.NewGuid():N}"[..14];

        var created = await Json(await _client.PostAsJsonAsync($"{Base}/folders", new { name }, Ct));
        Assert.Equal(name, created.GetProperty("name").GetString());
        var child = await Json(await _client.PostAsJsonAsync($"{Base}/folders", new { name = "Child", parentId = created.GetProperty("id").GetString() }, Ct));
        Assert.Equal(created.GetProperty("id").GetString(), child.GetProperty("parentId").GetString());

        var renamed = await Json(await _client.PutAsJsonAsync($"{Base}/folders/{created.GetProperty("id").GetString()}", new { name = name + " renamed" }, Ct));
        Assert.Equal(name + " renamed", renamed.GetProperty("name").GetString());

        var folders = (await Json(await _client.GetAsync($"{Base}/folders", Ct))).EnumerateArray().Select(f => f.GetProperty("fullName").GetString()).ToList();
        Assert.Contains(name + " renamed", folders);
        Assert.DoesNotContain(name, folders);
        Assert.Contains(folders, f => f!.StartsWith(name + " renamed") && f.EndsWith("Child"));
    }

    [Fact]
    public async Task Duplicate_folder_name_is_409()
    {
        var response = await _client.PostAsJsonAsync($"{Base}/folders", new { name = "Archive" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("INBOX")]
    [InlineData("a*b")]
    public async Task Invalid_folder_names_are_400(string name)
    {
        var response = await _client.PostAsJsonAsync($"{Base}/folders", new { name }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Inbox_cannot_be_renamed()
    {
        var response = await _client.PutAsJsonAsync($"{Base}/folders/{Inbox}", new { name = "Other" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
