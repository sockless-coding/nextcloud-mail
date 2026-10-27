using System.Buffers.Text;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Mail;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>
/// The header cache against Dovecot, once per way of finding changes: QRESYNC, CONDSTORE alone
/// (QRESYNC masked) and plain IMAP (both masked). Changes are made by a second IMAP client, the
/// way another mail program would, and must come out of the changes endpoint.
/// </summary>
public sealed class SyncTests(DovecotFixture dovecot) : IDisposable
{
    public static TheoryData<string> Modes() => ["qresync", "condstore", "plain"];

    private const string Inbox = "SU5CT1g"; // base64url("INBOX")

    private readonly List<MailApp> _apps = [];
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var app in _apps)
        {
            app.Dispose();
        }
    }

    private async Task<(HttpClient Client, Guid Account, string User, ImapClient Imap)> SetUpAsync(string mode)
    {
        var disabled = mode switch
        {
            "condstore" => ImapCapabilities.QuickResync,
            "plain" => ImapCapabilities.QuickResync | ImapCapabilities.CondStore,
            _ => ImapCapabilities.None,
        };
        var app = new MailApp(services => services.AddSingleton(new ImapFeatures { Disabled = disabled }));
        _apps.Add(app);
        var client = app.ClientAs("sync-user");
        var user = DovecotFixture.NewUser();
        var imap = await dovecot.ConnectAsync(user);
        await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        return (client, await AddAccountAsync(client, dovecot, user), user, imap);
    }

    private static MimeMessage Simple(string subject)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = subject } };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("", "someone@example.test"));
        return message;
    }

    private static async Task<List<UniqueId>> AppendAsync(IMailFolder folder, string prefix, int count, CancellationToken ct)
    {
        var uids = new List<UniqueId>();
        for (var i = 1; i <= count; i++)
        {
            uids.Add(await folder.AppendAsync(Simple($"{prefix} {i}"), MessageFlags.None, ct) ?? throw new InvalidOperationException("No UIDPLUS"));
        }

        return uids;
    }

    private async Task<JsonElement> GetAsync(HttpClient client, string path) => await Json(await client.GetAsync(path, Ct));

    private static string Id(JsonElement page, UniqueId uid) => $"{page.GetProperty("uidValidity").GetUInt32()}-{uid.Id}";

    private static Dictionary<string, JsonElement> ById(JsonElement messages) =>
        messages.EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!);

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Changes_made_by_another_client_are_reported(string mode)
    {
        var (client, account, _, imap) = await SetUpAsync(mode);
        var uids = await AppendAsync(imap.Inbox, "Message", 5, Ct);
        var page = await GetAsync(client, $"/api/accounts/{account}/folders/{Inbox}/messages");
        Assert.Equal(5, page.GetProperty("messages").GetArrayLength());
        Assert.Equal(5, page.GetProperty("unread").GetInt32());
        var token = page.GetProperty("token").GetInt64();

        await imap.Inbox.AddFlagsAsync(uids[1], MessageFlags.Seen, true, Ct);
        await imap.Inbox.AddFlagsAsync(uids[2], MessageFlags.Flagged, true, Ct);
        await imap.Inbox.AddFlagsAsync(uids[3], MessageFlags.Deleted, true, Ct);
        await imap.Inbox.ExpungeAsync(Ct);
        var added = (await AppendAsync(imap.Inbox, "Late", 1, Ct))[0];

        var changes = await GetAsync(client, $"/api/accounts/{account}/folders/{Inbox}/changes?since={token}");
        Assert.False(changes.GetProperty("reset").GetBoolean());
        Assert.Equal([Id(page, uids[3])], changes.GetProperty("removed").EnumerateArray().Select(r => r.GetString()));
        var changed = ById(changes.GetProperty("messages"));
        Assert.Equal([Id(page, added), Id(page, uids[2]), Id(page, uids[1])], changed.Keys);
        Assert.True(changed[Id(page, uids[1])].GetProperty("seen").GetBoolean());
        Assert.True(changed[Id(page, uids[2])].GetProperty("flagged").GetBoolean());
        Assert.Equal("Late 1", changed[Id(page, added)].GetProperty("subject").GetString());
        Assert.Equal(5, changes.GetProperty("total").GetInt32());
        Assert.Equal(4, changes.GetProperty("unread").GetInt32());

        var none = await GetAsync(client, $"/api/accounts/{account}/folders/{Inbox}/changes?since={changes.GetProperty("token").GetInt64()}");
        Assert.Empty(none.GetProperty("messages").EnumerateArray());
        Assert.Empty(none.GetProperty("removed").EnumerateArray());

        // The list itself reflects the same state.
        var list = ById((await GetAsync(client, $"/api/accounts/{account}/folders/{Inbox}/messages")).GetProperty("messages"));
        Assert.Equal(5, list.Count);
        Assert.DoesNotContain(Id(page, uids[3]), list.Keys);
        Assert.True(list[Id(page, uids[1])].GetProperty("seen").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task The_apps_own_changes_update_the_cache(string mode)
    {
        var (client, account, _, imap) = await SetUpAsync(mode);
        var uids = await AppendAsync(imap.Inbox, "Message", 3, Ct);
        await imap.GetFolder(imap.PersonalNamespaces[0]).CreateAsync("Archive", true, Ct);
        var page = await GetAsync(client, $"/api/accounts/{account}/folders/{Inbox}/messages");
        var token = page.GetProperty("token").GetInt64();

        (await client.PutAsJsonAsync($"/api/accounts/{account}/folders/{Inbox}/messages/flags",
            new { messageIds = new[] { Id(page, uids[0]) }, seen = true }, Ct)).EnsureSuccessStatusCode();
        var changes = await GetAsync(client, $"/api/accounts/{account}/folders/{Inbox}/changes?since={token}");
        Assert.True(ById(changes.GetProperty("messages"))[Id(page, uids[0])].GetProperty("seen").GetBoolean());
        Assert.Equal(2, changes.GetProperty("unread").GetInt32());
        token = changes.GetProperty("token").GetInt64();

        var archive = Base64Url.EncodeToString("Archive"u8);
        (await client.PostAsJsonAsync($"/api/accounts/{account}/folders/{Inbox}/messages/move",
            new { messageIds = new[] { Id(page, uids[1]) }, targetFolderId = archive }, Ct)).EnsureSuccessStatusCode();
        changes = await GetAsync(client, $"/api/accounts/{account}/folders/{Inbox}/changes?since={token}");
        Assert.Equal([Id(page, uids[1])], changes.GetProperty("removed").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(2, changes.GetProperty("total").GetInt32());

        var moved = await GetAsync(client, $"/api/accounts/{account}/folders/{archive}/messages");
        Assert.Equal(["Message 2"], moved.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString()));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Paging_extends_the_cached_window_and_keeps_it_in_sync(string mode)
    {
        var (client, account, _, imap) = await SetUpAsync(mode);
        var uids = await AppendAsync(imap.Inbox, "Message", 120, Ct);

        var seen = new List<string>();
        uint? before = null;
        long token = 0;
        do
        {
            var page = await GetAsync(client, $"/api/accounts/{account}/folders/{Inbox}/messages?limit=40{(before is null ? "" : $"&before={before}")}");
            seen.AddRange(page.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString()!));
            before = page.GetProperty("nextBefore").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextBefore").GetUInt32();
            token = page.GetProperty("token").GetInt64();
        }
        while (before is not null);

        Assert.Equal(Enumerable.Range(1, 120).Reverse().Select(i => $"Message {i}"), seen);

        // A change far down the folder, inside the window that paging built.
        await imap.Inbox.AddFlagsAsync(uids[4], MessageFlags.Flagged, true, Ct);
        var changes = await GetAsync(client, $"/api/accounts/{account}/folders/{Inbox}/changes?since={token}");
        var changed = changes.GetProperty("messages").EnumerateArray().Single();
        Assert.Equal("Message 5", changed.GetProperty("subject").GetString());
        Assert.True(changed.GetProperty("flagged").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_renumbered_folder_is_rebuilt_and_pollers_reload(string mode)
    {
        var (client, account, _, imap) = await SetUpAsync(mode);
        var root = imap.GetFolder(imap.PersonalNamespaces[0]);
        var box = await root.CreateAsync("Box", true, Ct) ?? throw new InvalidOperationException();
        await AppendAsync(box, "Old", 3, Ct);
        var boxId = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("Box"));
        var page = await GetAsync(client, $"/api/accounts/{account}/folders/{boxId}/messages");
        Assert.Equal(3, page.GetProperty("messages").GetArrayLength());

        await box.DeleteAsync(Ct);
        box = await root.CreateAsync("Box", true, Ct) ?? throw new InvalidOperationException();
        await AppendAsync(box, "New", 2, Ct);

        var changes = await GetAsync(client, $"/api/accounts/{account}/folders/{boxId}/changes?since={page.GetProperty("token").GetInt64()}");
        Assert.True(changes.GetProperty("reset").GetBoolean());
        Assert.NotEqual(page.GetProperty("uidValidity").GetUInt32(), changes.GetProperty("uidValidity").GetUInt32());

        var fresh = await GetAsync(client, $"/api/accounts/{account}/folders/{boxId}/messages");
        Assert.Equal(["New 2", "New 1"], fresh.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString()));
    }
}
