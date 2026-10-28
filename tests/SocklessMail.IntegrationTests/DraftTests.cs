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

/// <summary>HTML messages, and drafts saved while writing: replaced, continued and removed on sending.</summary>
public sealed class DraftTests(GreenMailFixture mail) : IAsyncLifetime
{
    private const string Drafter = "drafter@example.test";
    private const string Recipient = "rcpt2@example.test";
    private const string Password = GreenMailFixture.ActionsPassword;

    private readonly MailApp _app = new();
    private HttpClient _client = null!;
    private Guid _account;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _client = _app.ClientAs("drafter-user");
        _account = await AddAccountAsync(_client, mail, Drafter, Password);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private string Base => $"/api/accounts/{_account}";

    private static string Unique(string what) => $"{what} {Guid.NewGuid():N}"[..(what.Length + 9)];

    private async Task<ImapClient> ConnectAsync(string user)
    {
        var client = new ImapClient();
        await client.ConnectAsync(mail.Host, mail.Imap, SecureSocketOptions.None, Ct);
        await client.AuthenticateAsync(user, Password, Ct);
        return client;
    }

    /// <summary>Messages with the subject in a folder (waiting briefly for delivery when <paramref name="expect"/> is set).</summary>
    private async Task<List<(MimeMessage Message, MessageFlags Flags)>> FindAsync(string user, string folder, string subject, bool expect = true)
    {
        for (var attempt = 0; attempt < (expect ? 20 : 1); attempt++)
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

    private async Task<string> UploadAsync(string name, string content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", name);
        return (await Json(await _client.PostAsync("/api/uploads", form, Ct))).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Html_goes_out_cleaned_with_a_text_alternative()
    {
        var subject = Unique("Rich");
        await Json(await _client.PostAsJsonAsync($"{Base}/send", new
        {
            to = Recipient,
            subject,
            text = "ignored",
            html = "<p>Hello <strong>world</strong> <span style=\"color: rgb(193, 58, 58)\">red</span></p><p></p>"
                   + "<script>alert(1)</script><p onclick=\"x()\">Bye <a href=\"javascript:x()\">bad</a> <img src=\"https://example.test/t.gif\"></p>",
        }, Ct));

        var (message, _) = Assert.Single(await FindAsync(Recipient, "INBOX", subject));
        var html = message.HtmlBody!;
        Assert.Contains("<strong>world</strong>", html);
        Assert.Matches("<span style=\"color: [^\"]+\">red</span>", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("<img", html);
        // One Enter is one line, and an empty line keeps its height.
        Assert.Contains("<p style=\"margin:0\"><br></p>", html);
        Assert.Contains("Hello world red", message.TextBody);
        Assert.DoesNotContain("ignored", message.TextBody);
    }

    [Fact]
    public async Task Drafts_are_replaced_continued_and_removed_when_sent()
    {
        var subject = Unique("Draft");

        // Incomplete: an address still being typed is kept as written.
        var first = await Json(await _client.PutAsJsonAsync($"{Base}/drafts", new { to = "rcpt2@exa", subject, html = "<p>version one</p>" }, Ct));
        var (saved, flags) = Assert.Single(await FindAsync(Drafter, "Drafts", subject));
        Assert.True(flags.HasFlag(MessageFlags.Draft));
        Assert.Equal("rcpt2@exa", saved.Headers[HeaderId.To]);

        var upload = await UploadAsync("notes.txt", "draft notes");
        var second = await Json(await _client.PutAsJsonAsync($"{Base}/drafts", new
        {
            to = Recipient,
            subject,
            html = "<p>version <em>two</em></p>",
            attachmentIds = new[] { upload },
            replaces = new { folderId = first.GetProperty("folderId").GetString(), messageId = first.GetProperty("messageId").GetString() },
        }, Ct));
        (saved, _) = Assert.Single(await FindAsync(Drafter, "Drafts", subject));
        Assert.Contains("<em>two</em>", saved.HtmlBody);
        Assert.Equal("notes.txt", Assert.Single(saved.Attachments).ContentDisposition?.FileName);

        // Continue it: fields, text and attachments as saved.
        var draftsId = second.GetProperty("folderId").GetString()!;
        var secondId = second.GetProperty("messageId").GetString()!;
        var edit = await Json(await _client.GetAsync($"{Base}/folders/{draftsId}/messages/{secondId}/draft?mode=edit", Ct));
        Assert.Equal(Recipient, edit.GetProperty("to").GetString());
        Assert.Equal(subject, edit.GetProperty("subject").GetString());
        Assert.Contains("<em>two</em>", edit.GetProperty("html").GetString());
        var part = Assert.Single(edit.GetProperty("forwardedAttachments").EnumerateArray());

        // Saving a continued draft makes the new copy the source of its attachments.
        var third = await Json(await _client.PutAsJsonAsync($"{Base}/drafts", new
        {
            to = Recipient,
            subject,
            html = "<p>version three</p>",
            source = new { folderId = draftsId, messageId = secondId, mode = "edit", forwardPartIds = new[] { part.GetProperty("partId").GetString() } },
            replaces = new { folderId = draftsId, messageId = secondId },
        }, Ct));
        var source = third.GetProperty("source");
        Assert.Equal(third.GetProperty("messageId").GetString(), source.GetProperty("messageId").GetString());
        var kept = Assert.Single(third.GetProperty("attachments").EnumerateArray());
        Assert.Equal("notes.txt", kept.GetProperty("fileName").GetString());
        Assert.Single(await FindAsync(Drafter, "Drafts", subject));

        await Json(await _client.PostAsJsonAsync($"{Base}/send", new
        {
            to = Recipient,
            subject,
            html = "<p>final version</p>",
            source = new { folderId = draftsId, messageId = source.GetProperty("messageId").GetString(), mode = "edit", forwardPartIds = new[] { kept.GetProperty("partId").GetString() } },
            draft = new { folderId = draftsId, messageId = source.GetProperty("messageId").GetString() },
        }, Ct));

        var (sent, _) = Assert.Single(await FindAsync(Recipient, "INBOX", subject));
        Assert.Contains("final version", sent.HtmlBody);
        Assert.Equal("notes.txt", Assert.Single(sent.Attachments).ContentDisposition?.FileName);
        Assert.Empty(await FindAsync(Drafter, "Drafts", subject, expect: false));
    }

    [Fact]
    public async Task Replies_quote_the_original_as_html()
    {
        var subject = Unique("Quote me");
        var original = new MimeMessage { Subject = subject };
        original.From.Add(new MailboxAddress("Olga <Original>", "olga@example.test"));
        original.To.Add(MailboxAddress.Parse(Drafter));
        original.Body = new BodyBuilder { HtmlBody = "<div>The <b>plan</b><img src=\"cid:logo\"><script>x()</script></div>", TextBody = "The plan" }.ToMessageBody();
        string id;
        using (var imap = await ConnectAsync(Drafter))
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            var uid = await imap.Inbox.AppendAsync(original, MessageFlags.None, Ct);
            id = MailboxIds.MessageId(imap.Inbox.UidValidity, uid!.Value.Id);
        }

        var draft = await Json(await _client.GetAsync($"{Base}/folders/{MailboxIds.FolderId("INBOX")}/messages/{id}/draft?mode=reply", Ct));
        var html = draft.GetProperty("html").GetString()!;
        Assert.StartsWith("<p></p>", html);
        Assert.Contains("<blockquote><div>The <b>plan</b></div></blockquote>", html);
        Assert.Contains("&quot;Olga &lt;Original&gt;&quot;", html);
        Assert.Contains("> The plan", draft.GetProperty("text").GetString());
        Assert.Equal("", draft.GetProperty("bcc").GetString());
    }
}
