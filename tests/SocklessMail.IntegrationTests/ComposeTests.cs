using System.Net;
using System.Net.Http.Headers;
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

/// <summary>Sending through the API, checked in the recipients' mailboxes and in Sent over IMAP.</summary>
public sealed class ComposeTests(GreenMailFixture mail) : IAsyncLifetime
{
    private const string Composer = "composer@example.test";
    private const string Password = GreenMailFixture.ActionsPassword;

    private readonly MailApp _app = new();
    private HttpClient _client = null!;
    private Guid _account;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        using (var imap = await ConnectAsync(Composer))
        {
            try
            {
                await imap.GetFolderAsync("Sent", Ct);
            }
            catch (FolderNotFoundException)
            {
                await imap.GetFolder(imap.PersonalNamespaces[0]).CreateAsync("Sent", true, Ct);
            }
        }

        _client = _app.ClientAs("composer-user");
        _account = await AddAccountAsync(_client, mail, Composer, Password);
        // A sender name, as set in the account dialog.
        var account = await Json(await _client.GetAsync($"/api/accounts/{_account}", Ct));
        await Json(await _client.PutAsJsonAsync($"/api/accounts/{_account}", new
        {
            displayName = "Composer",
            senderName = "Cora Composer",
            emailAddress = Composer,
            username = Composer,
            imap = account.GetProperty("imap"),
            smtp = account.GetProperty("smtp"),
        }, Ct));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private string Base => $"/api/accounts/{_account}";

    private async Task<ImapClient> ConnectAsync(string user)
    {
        var client = new ImapClient();
        await client.ConnectAsync(mail.Host, mail.Imap, SecureSocketOptions.None, Ct);
        await client.AuthenticateAsync(user, Password, Ct);
        return client;
    }

    /// <summary>Waits briefly for delivery, then returns the messages with the subject in the user's folder.</summary>
    private async Task<List<(MimeMessage Message, MessageFlags Flags)>> FindAsync(string user, string subject, string folder = "INBOX")
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var imap = await ConnectAsync(user);
            var f = folder == "INBOX" ? imap.Inbox : await imap.GetFolderAsync(folder, Ct);
            await f.OpenAsync(FolderAccess.ReadOnly, Ct);
            var uids = await f.SearchAsync(SearchQuery.SubjectContains(subject), Ct);
            if (uids.Count > 0)
            {
                var result = new List<(MimeMessage, MessageFlags)>();
                foreach (var summary in await f.FetchAsync(uids, MessageSummaryItems.Flags | MessageSummaryItems.UniqueId, Ct))
                {
                    result.Add((await f.GetMessageAsync(summary.UniqueId, Ct), summary.Flags!.Value));
                }

                return result;
            }

            await Task.Delay(250, Ct);
        }

        return [];
    }

    private async Task<string> UploadAsync(string name, byte[] content, string type)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return (await Json(await _client.PostAsync("/api/uploads", form, Ct))).GetProperty("id").GetString()!;
    }

    private static string Unique(string what) => $"{what} {Guid.NewGuid():N}"[..(what.Length + 9)];

    /// <summary>Puts a message into the composer's inbox and returns its API message id.</summary>
    private async Task<string> ReceiveAsync(MimeMessage message)
    {
        using var imap = await ConnectAsync(Composer);
        await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        var uid = await imap.Inbox.AppendAsync(message, MessageFlags.None, Ct);
        return MailboxIds.MessageId(imap.Inbox.UidValidity, uid!.Value.Id);
    }

    [Fact]
    public async Task Message_reaches_to_cc_and_bcc_and_a_copy_is_kept_in_sent()
    {
        var subject = Unique("Hello");

        var result = await Json(await _client.PostAsJsonAsync($"{Base}/send", new
        {
            to = "Rcpt One <rcpt1@example.test>",
            cc = "rcpt2@example.test",
            bcc = "rcpt3@example.test",
            subject,
            text = "Plain text body\nsecond line",
        }, Ct));

        Assert.True(result.GetProperty("savedToSent").GetBoolean());
        Assert.Equal(MailboxIds.FolderId("Sent"), result.GetProperty("sentFolderId").GetString());

        var (received, _) = Assert.Single(await FindAsync("rcpt1@example.test", subject));
        Assert.Equal("\"Cora Composer\" <composer@example.test>", received.From.ToString());
        Assert.Equal("rcpt2@example.test", received.Cc.ToString());
        Assert.Empty(received.Bcc);
        Assert.Null(received.Headers[HeaderId.Bcc]);
        Assert.Contains("second line", received.TextBody);
        Assert.False(string.IsNullOrEmpty(received.MessageId));

        Assert.Single(await FindAsync("rcpt2@example.test", subject));
        var (bccCopy, _) = Assert.Single(await FindAsync("rcpt3@example.test", subject));
        Assert.Null(bccCopy.Headers[HeaderId.Bcc]);

        var (sent, sentFlags) = Assert.Single(await FindAsync(Composer, subject, "Sent"));
        Assert.True(sentFlags.HasFlag(MessageFlags.Seen));
        Assert.Equal("rcpt3@example.test", sent.Bcc.ToString());
        Assert.Equal(received.MessageId, sent.MessageId);
    }

    [Fact]
    public async Task Uploaded_attachments_are_sent_and_then_removed()
    {
        var subject = Unique("Attached");
        var pdf = "%PDF-1.4 attachment test"u8.ToArray();
        var upload = await UploadAsync("report.pdf", pdf, "application/pdf");

        await Json(await _client.PostAsJsonAsync($"{Base}/send", new { to = "rcpt1@example.test", subject, text = "see attachment", attachmentIds = new[] { upload } }, Ct));

        var (received, _) = Assert.Single(await FindAsync("rcpt1@example.test", subject));
        var attachment = Assert.IsType<MimePart>(Assert.Single(received.Attachments));
        Assert.Equal("report.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType.MimeType);
        using var bytes = new MemoryStream();
        await attachment.Content!.DecodeToAsync(bytes, Ct);
        Assert.Equal(pdf, bytes.ToArray());

        // Sent uploads are deleted: using the id again fails.
        var again = await _client.PostAsJsonAsync($"{Base}/send", new { to = "rcpt1@example.test", subject, attachmentIds = new[] { upload } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Reply_is_threaded_and_marks_the_original_answered()
    {
        var subject = Unique("Question");
        var original = new MimeMessage { Subject = subject, MessageId = $"{Guid.NewGuid():N}@example.test", Body = new TextPart("plain") { Text = "Can you help?" } };
        original.From.Add(new MailboxAddress("Rcpt One", "rcpt1@example.test"));
        original.To.Add(MailboxAddress.Parse(Composer));
        original.References.Add("root@example.test");
        var id = await ReceiveAsync(original);

        var draft = await Json(await _client.GetAsync($"{Base}/folders/{MailboxIds.FolderId("INBOX")}/messages/{id}/draft?mode=reply", Ct));
        Assert.Contains("rcpt1@example.test", draft.GetProperty("to").GetString());
        Assert.Equal($"Re: {subject}", draft.GetProperty("subject").GetString());
        Assert.Contains("> Can you help?", draft.GetProperty("text").GetString());

        await Json(await _client.PostAsJsonAsync($"{Base}/send", new
        {
            to = draft.GetProperty("to").GetString(),
            subject = draft.GetProperty("subject").GetString(),
            text = "Sure." + draft.GetProperty("text").GetString(),
            source = new { folderId = MailboxIds.FolderId("INBOX"), messageId = id, mode = "reply" },
        }, Ct));

        var (reply, _) = Assert.Single(await FindAsync("rcpt1@example.test", $"Re: {subject}"));
        Assert.Equal(original.MessageId, reply.InReplyTo);
        Assert.Equal(["root@example.test", original.MessageId], reply.References);

        var (_, flags) = Assert.Single(await FindAsync(Composer, subject).ContinueWith(t => t.Result.Where(m => m.Message.Subject == subject).ToList(), Ct));
        Assert.True(flags.HasFlag(MessageFlags.Answered));
    }

    [Fact]
    public async Task Reply_all_draft_copies_everyone_but_me()
    {
        var subject = Unique("Team");
        var original = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "Hi all" } };
        original.From.Add(MailboxAddress.Parse("rcpt1@example.test"));
        original.To.AddRange([MailboxAddress.Parse(Composer), MailboxAddress.Parse("rcpt2@example.test")]);
        original.Cc.Add(MailboxAddress.Parse("rcpt3@example.test"));
        var id = await ReceiveAsync(original);

        var draft = await Json(await _client.GetAsync($"{Base}/folders/{MailboxIds.FolderId("INBOX")}/messages/{id}/draft?mode=replyAll", Ct));

        Assert.Equal("rcpt1@example.test", draft.GetProperty("to").GetString());
        Assert.Equal("rcpt2@example.test, rcpt3@example.test", draft.GetProperty("cc").GetString());
    }

    [Fact]
    public async Task Forward_carries_the_original_attachments()
    {
        var subject = Unique("Invoice");
        var builder = new BodyBuilder { TextBody = "Invoice attached" };
        builder.Attachments.Add("invoice.pdf", "%PDF invoice"u8.ToArray(), new ContentType("application", "pdf"));
        var original = new MimeMessage { Subject = subject, Body = builder.ToMessageBody() };
        original.From.Add(MailboxAddress.Parse("rcpt2@example.test"));
        original.To.Add(MailboxAddress.Parse(Composer));
        var id = await ReceiveAsync(original);

        var draft = await Json(await _client.GetAsync($"{Base}/folders/{MailboxIds.FolderId("INBOX")}/messages/{id}/draft?mode=forward", Ct));
        Assert.Equal("", draft.GetProperty("to").GetString());
        Assert.Equal($"Fwd: {subject}", draft.GetProperty("subject").GetString());
        Assert.Contains("---------- Forwarded message ----------", draft.GetProperty("text").GetString());
        var partId = Assert.Single(draft.GetProperty("forwardedAttachments").EnumerateArray()).GetProperty("partId").GetString();

        await Json(await _client.PostAsJsonAsync($"{Base}/send", new
        {
            to = "rcpt1@example.test",
            subject = draft.GetProperty("subject").GetString(),
            text = draft.GetProperty("text").GetString(),
            source = new { folderId = MailboxIds.FolderId("INBOX"), messageId = id, mode = "forward", forwardPartIds = new[] { partId } },
        }, Ct));

        var (forwarded, _) = Assert.Single(await FindAsync("rcpt1@example.test", $"Fwd: {subject}"));
        var attachment = Assert.IsType<MimePart>(Assert.Single(forwarded.Attachments));
        Assert.Equal("invoice.pdf", attachment.FileName);
        using var bytes = new MemoryStream();
        await attachment.Content!.DecodeToAsync(bytes, Ct);
        Assert.Equal("%PDF invoice"u8.ToArray(), bytes.ToArray());
    }

    [Fact]
    public async Task Without_a_sent_folder_the_send_succeeds_with_a_warning()
    {
        var client = _app.ClientAs("nosent-user");
        var account = await AddAccountAsync(client, mail, "nosent@example.test", Password);
        var subject = Unique("NoSent");

        var result = await Json(await client.PostAsJsonAsync($"/api/accounts/{account}/send", new { to = "rcpt1@example.test", subject, text = "x" }, Ct));

        Assert.False(result.GetProperty("savedToSent").GetBoolean());
        Assert.Contains("no Sent folder", result.GetProperty("warning").GetString());
        Assert.Single(await FindAsync("rcpt1@example.test", subject));
    }

    [Fact]
    public async Task Draft_of_a_missing_message_is_404()
    {
        var response = await _client.GetAsync($"{Base}/folders/{MailboxIds.FolderId("INBOX")}/messages/1-999999/draft?mode=reply", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
