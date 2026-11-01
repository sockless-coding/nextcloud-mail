using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Data;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>
/// Conversations of real mailboxes: a thread that branches (two people answer the first message),
/// with the user's reply in Sent, a late reply filed in Archive and one in Trash.
/// </summary>
public sealed class ConversationTests : IAsyncLifetime
{
    private const string Owner = "threads@example.test";

    private readonly GreenMailFixture _mail;
    private readonly MailApp _app = new();
    private readonly HttpClient _client;
    private Guid _accountId;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ConversationTests(GreenMailFixture mail)
    {
        _mail = mail;
        _client = _app.ClientAs("threads-user");
    }

    public async ValueTask InitializeAsync()
    {
        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync(_mail.Host, _mail.Imap, SecureSocketOptions.None, Ct);
            await imap.AuthenticateAsync(Owner, GreenMailFixture.ActionsPassword, Ct);
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            if (imap.Inbox.Count == 0)
            {
                var personal = imap.GetFolder(imap.PersonalNamespaces[0]);
                var sent = await personal.CreateAsync("Sent", true, Ct) ?? throw new InvalidOperationException("No Sent folder");
                var archive = await personal.CreateAsync("Archive", true, Ct) ?? throw new InvalidOperationException("No Archive folder");
                var trash = await personal.CreateAsync("Trash", true, Ct) ?? throw new InvalidOperationException("No Trash folder");

                var day = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
                await imap.Inbox.AppendAsync(Mail("bob@example.test", "Plan", "<a@t>", null, day), cancellationToken: Ct);
                await sent.AppendAsync(Mail(Owner, "Re: Plan", "<b@t>", ["<a@t>"], day.AddHours(1), "bob@example.test"), cancellationToken: Ct);
                // Carol answers the first message too: a second branch.
                await imap.Inbox.AppendAsync(Mail("carol@example.test", "Re: Plan", "<c@t>", ["<a@t>"], day.AddHours(2)), cancellationToken: Ct);
                await imap.Inbox.AppendAsync(Mail("bob@example.test", "Re: Plan", "<d@t>", ["<a@t>", "<b@t>"], day.AddHours(3)), cancellationToken: Ct);
                await archive.AppendAsync(Mail("dave@example.test", "Re: Plan", "<e@t>", ["<a@t>", "<c@t>"], day.AddHours(4)), cancellationToken: Ct);
                await trash.AppendAsync(Mail("eve@example.test", "Re: Plan", "<g@t>", ["<a@t>"], day.AddHours(5)), cancellationToken: Ct);
                await imap.Inbox.AppendAsync(Mail("carol@example.test", "Lunch", "<f@t>", null, day.AddHours(6)), cancellationToken: Ct);
            }
        }

        _accountId = await AddAccountAsync(_client, _mail, Owner, GreenMailFixture.ActionsPassword);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static MimeMessage Mail(string from, string subject, string id, string[]? references, DateTimeOffset date, string to = Owner)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = subject }, MessageId = id.Trim('<', '>'), Date = date };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        if (references is not null)
        {
            message.InReplyTo = references[^1].Trim('<', '>');
            foreach (var reference in references)
            {
                message.References.Add(reference.Trim('<', '>'));
            }
        }

        return message;
    }

    private string Base => $"/api/accounts/{_accountId}";

    private async Task<string> FolderAsync(string fullName)
    {
        var folders = (await Json(await _client.GetAsync($"{Base}/folders", Ct))).EnumerateArray();
        return folders.First(f => f.GetProperty("fullName").GetString() == fullName).GetProperty("id").GetString()!;
    }

    private async Task<string> FindAsync(string folder, string sender)
    {
        var messages = (await Json(await _client.GetAsync($"{Base}/folders/{folder}/messages?limit=20", Ct))).GetProperty("messages");
        return messages.EnumerateArray()
            .First(m => m.GetProperty("from")[0].GetProperty("address").GetString() == sender)
            .GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ConversationAsync(string folder, string id) =>
        await Json(await _client.GetAsync($"{Base}/folders/{folder}/messages/{id}/conversation", Ct));

    private static List<string?> Ids(JsonElement conversation) =>
        conversation.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("messageIdHeader").GetString()).ToList();

    private async Task EnableAsync()
    {
        var account = await Json(await _client.PutAsJsonAsync($"{Base}/conversations", new { enabled = true }, Ct));
        Assert.True(account.GetProperty("conversations").GetBoolean());
    }

    [Fact]
    public async Task Conversations_are_off_until_turned_on_per_account()
    {
        var accounts = await Json(await _client.GetAsync("/api/accounts", Ct));
        Assert.False(accounts.EnumerateArray().Single().GetProperty("conversations").GetBoolean());

        await EnableAsync();

        accounts = await Json(await _client.GetAsync("/api/accounts", Ct));
        Assert.True(accounts.EnumerateArray().Single().GetProperty("conversations").GetBoolean());
        var other = _app.ClientAs("someone-else");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Base}/conversations", new { enabled = false }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_conversation_gathers_the_inbox_sent_and_cached_folders_but_not_trash()
    {
        var inbox = await FolderAsync("INBOX");
        // Archive is searched by nobody: its message is found because its folder was cached.
        await FindAsync(await FolderAsync("Archive"), "dave@example.test");
        var id = await FindAsync(inbox, "bob@example.test");

        var conversation = await ConversationAsync(inbox, id);

        Assert.Equal(["a@t", "b@t", "c@t", "d@t", "e@t"], Ids(conversation));
        Assert.False(conversation.GetProperty("truncated").GetBoolean());
        var messages = conversation.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal([null, "a@t", "a@t", "b@t", "c@t"], messages.Select(m => m.GetProperty("parentIdHeader").GetString()));
        Assert.Equal(["inbox", "sent", "inbox", "inbox", "archive"], messages.Select(m => m.GetProperty("folderRole").GetString()));
        Assert.Equal("Archive", messages[4].GetProperty("folderName").GetString());
        Assert.Equal("Re: Plan", messages[3].GetProperty("message").GetProperty("subject").GetString());
    }

    [Fact]
    public async Task A_message_opened_in_trash_brings_its_trash_along()
    {
        var trash = await FolderAsync("Trash");
        var id = await FindAsync(trash, "eve@example.test");

        var conversation = await ConversationAsync(trash, id);

        Assert.Contains("g@t", Ids(conversation));
        Assert.Contains("a@t", Ids(conversation));
    }

    [Fact]
    public async Task A_message_outside_any_conversation_stands_alone()
    {
        var inbox = await FolderAsync("INBOX");
        var lunch = (await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages?limit=20", Ct))).GetProperty("messages")
            .EnumerateArray().First(m => m.GetProperty("subject").GetString() == "Lunch").GetProperty("id").GetString()!;

        Assert.Equal(["f@t"], Ids(await ConversationAsync(inbox, lunch)));
    }

    [Fact]
    public async Task With_conversations_on_the_inbox_sync_caches_sent_and_links_the_replies()
    {
        await EnableAsync();

        await FindAsync(await FolderAsync("INBOX"), "bob@example.test");

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var rows = await db.Messages.IgnoreQueryFilters()
            .Join(db.Folders.IgnoreQueryFilters().Where(f => f.AccountId == _accountId), m => m.FolderId, f => f.Id, (m, f) => new { f.FullName, m.MessageIdHeader, m.ConversationId })
            .ToListAsync(Ct);
        Assert.Contains(rows, r => r.FullName == "Sent" && r.MessageIdHeader == "b@t");
        var plan = rows.Where(r => r.MessageIdHeader is "a@t" or "b@t" or "c@t" or "d@t").Select(r => r.ConversationId).Distinct().ToList();
        Assert.NotNull(Assert.Single(plan));
        Assert.NotEqual(plan[0], rows.Single(r => r.MessageIdHeader == "f@t").ConversationId);
    }

    [Fact]
    public async Task Rows_cached_before_conversations_get_their_headers_once_turned_on()
    {
        var inbox = await FolderAsync("INBOX");
        await FindAsync(inbox, "bob@example.test");
        using (var scope = _app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MailDbContext>().Messages.IgnoreQueryFilters()
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.MessageIdHeader, (string?)null).SetProperty(m => m.ConversationId, (Guid?)null), Ct);
        }

        await EnableAsync();
        await FindAsync(inbox, "bob@example.test");

        using (var scope = _app.Services.CreateScope())
        {
            var headers = await scope.ServiceProvider.GetRequiredService<MailDbContext>().Messages.IgnoreQueryFilters()
                .Select(m => new { m.MessageIdHeader, m.ParentIdHeader, m.ConversationId }).ToListAsync(Ct);
            Assert.All(headers, h => Assert.NotNull(h.ConversationId));
            Assert.Contains(headers, h => h.MessageIdHeader == "d@t" && h.ParentIdHeader == "b@t");
        }
    }

    private async Task<List<JsonElement>> InboxMessagesAsync(string inbox) =>
        (await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages?limit=20", Ct))).GetProperty("messages").EnumerateArray().ToList();

    private static string? ConversationOf(IEnumerable<JsonElement> messages, string sender, string subject) =>
        messages.First(m => m.GetProperty("from")[0].GetProperty("address").GetString() == sender && m.GetProperty("subject").GetString() == subject)
            .GetProperty("conversationId").GetString();

    [Fact]
    public async Task List_messages_carry_their_conversation()
    {
        var messages = await InboxMessagesAsync(await FolderAsync("INBOX"));

        var plan = ConversationOf(messages, "bob@example.test", "Plan");
        Assert.NotNull(plan);
        Assert.Equal(plan, ConversationOf(messages, "carol@example.test", "Re: Plan"));
        Assert.Equal(plan, ConversationOf(messages, "bob@example.test", "Re: Plan"));
        Assert.NotEqual(plan, ConversationOf(messages, "carol@example.test", "Lunch"));
    }

    [Fact]
    public async Task Members_sum_up_a_conversation_in_one_folder()
    {
        await EnableAsync();
        var inbox = await FolderAsync("INBOX");
        var plan = ConversationOf(await InboxMessagesAsync(inbox), "bob@example.test", "Plan");

        var result = await Json(await _client.PostAsJsonAsync("/api/conversations/members",
            new { items = new[] { new { accountId = _accountId, folderId = inbox, conversationId = plan } } }, Ct));

        var members = Assert.Single(result.EnumerateArray());
        Assert.Equal(plan, members.GetProperty("conversationId").GetString());
        // Only the inbox's three; the reply in Sent counts as the user having answered.
        Assert.Equal(["bob@example.test", "carol@example.test", "bob@example.test"],
            members.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("from")[0].GetProperty("address").GetString()));
        Assert.True(members.GetProperty("replied").GetBoolean());

        var other = await Json(await _app.ClientAs("someone-else").PostAsJsonAsync("/api/conversations/members",
            new { items = new[] { new { accountId = _accountId, folderId = inbox, conversationId = plan } } }, Ct));
        Assert.Empty(other.EnumerateArray());
    }

    [Fact]
    public async Task Server_searches_carry_the_conversation_the_cache_knows()
    {
        await EnableAsync();
        var inbox = await FolderAsync("INBOX");
        var plan = ConversationOf(await InboxMessagesAsync(inbox), "bob@example.test", "Plan");

        var found = await Json(await _client.GetAsync($"{Base}/folders/{inbox}/search?q=Plan", Ct));

        var messages = found.GetProperty("messages").EnumerateArray().ToList();
        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.Equal(plan, m.GetProperty("conversationId").GetString()));
    }
}
