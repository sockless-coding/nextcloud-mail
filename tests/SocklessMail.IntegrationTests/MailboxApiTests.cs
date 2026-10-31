using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Mailbox;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Seeds the reader@ mailbox once: 120 numbered messages, an HTML newsletter, a plain text mail, folders.</summary>
public sealed class ReaderMailbox(GreenMailFixture mail) : IAsyncLifetime
{
    public const int NumberedMessages = 120;
    public static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    public static readonly byte[] PdfBytes = "%PDF-1.4 fake report"u8.ToArray();

    public GreenMailFixture Mail { get; } = mail;

    public async ValueTask InitializeAsync()
    {
        using var client = await ConnectAsync(Mail);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        for (var i = 1; i <= NumberedMessages; i++)
        {
            await inbox.AppendAsync(Simple($"Message {i:000}", "numbered", start.AddMinutes(i)));
        }

        await inbox.AppendAsync(Plain());
        await inbox.AppendAsync(Newsletter());

        var root = client.GetFolder(client.PersonalNamespaces[0]);
        await root.CreateAsync("Sent", true);
        var projects = await root.CreateAsync("Projects", false) ?? throw new InvalidOperationException("Projects not created");
        var year = await projects.CreateAsync("2026", true) ?? throw new InvalidOperationException("2026 not created");
        await year.AppendAsync(Simple("Kick-off", "in a subfolder", start));
        var big = await root.CreateAsync("Big", true) ?? throw new InvalidOperationException("Big not created");
        await big.AppendAsync(Large());
        var suspicious = await root.CreateAsync("Suspicious", true) ?? throw new InvalidOperationException("Suspicious not created");
        await suspicious.AppendAsync(Phish());
        await client.DisconnectAsync(true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static async Task<ImapClient> ConnectAsync(GreenMailFixture mail)
    {
        var client = new ImapClient();
        await client.ConnectAsync(mail.Host, mail.Imap, SecureSocketOptions.None);
        await client.AuthenticateAsync(GreenMailFixture.ReaderAddress, GreenMailFixture.ReaderPassword);
        return client;
    }

    private static MimeMessage Simple(string subject, string text, DateTimeOffset date)
    {
        var message = new MimeMessage { Subject = subject, Date = date, Body = new TextPart("plain") { Text = text } };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(MailboxAddress.Parse(GreenMailFixture.ReaderAddress));
        return message;
    }

    private static MimeMessage Plain()
    {
        var message = Simple("Plain", "Line one\nVisit https://example.com/page <now> & then", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        return message;
    }

    /// <summary>Over the source view's limit, in 8-bit UTF-8 so the cut can land inside a character.</summary>
    private static MimeMessage Large()
    {
        var line = new string('é', 333) + "x";
        var text = string.Join("\n", Enumerable.Repeat(line, MailboxService.MaxSourceViewBytes / 600));
        var message = Simple("Large", "", new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        message.Body = new TextPart("plain") { Text = text, ContentTransferEncoding = ContentEncoding.EightBit };
        return message;
    }

    /// <summary>A look-alike of paypal.com (Cyrillic а) that failed DMARC, with replies going to another look-alike.</summary>
    private static MimeMessage Phish()
    {
        var message = Simple("Your account is limited", "Log in to restore it.", new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        message.From.Clear();
        message.From.Add(new MailboxAddress("service@paypal.com", "service@pаypal.com"));
        message.ReplyTo.Add(new MailboxAddress("PayPal", "billing@paypa1.com"));
        message.Headers.Insert(0, new Header("Authentication-Results", "mx.local.test; dmarc=fail header.from=xn--pypal-4ve.com"));
        return message;
    }

    private static MimeMessage Newsletter()
    {
        var builder = new BodyBuilder
        {
            HtmlBody = """
                <html><head><style>.title { color: #c00 }</style></head><body>
                <h1 class="title">Newsletter</h1>
                <script>alert('x')</script>
                <p onclick="alert('y')">Read <a href="https://example.com/article">more</a></p>
                <img src="https://tracker.example/pixel.gif" alt="pixel">
                <img src="cid:logo@sockless" alt="logo">
                </body></html>
                """,
            TextBody = "Newsletter (text version)",
        };
        var logo = builder.LinkedResources.Add("logo.png", PngBytes, new ContentType("image", "png"));
        logo.ContentId = "logo@sockless";
        builder.Attachments.Add("report.pdf", PdfBytes, new ContentType("application", "pdf"));

        var message = new MimeMessage { Subject = "Newsletter", Date = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), Body = builder.ToMessageBody() };
        message.From.Add(new MailboxAddress("News", "news@example.test"));
        message.To.Add(MailboxAddress.Parse(GreenMailFixture.ReaderAddress));
        return message;
    }
}

public sealed class MailboxApiTests : IClassFixture<ReaderMailbox>, IAsyncLifetime
{
    private readonly ReaderMailbox _mailbox;
    private readonly MailApp _app = new();
    private readonly HttpClient _client;
    private Guid _accountId;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public MailboxApiTests(ReaderMailbox mailbox)
    {
        _mailbox = mailbox;
        _client = _app.ClientAs("reader-user");
    }

    public async ValueTask InitializeAsync() =>
        _accountId = await AddAccountAsync(_client, _mailbox.Mail, GreenMailFixture.ReaderAddress, GreenMailFixture.ReaderPassword);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private string Base => $"/api/accounts/{_accountId}";

    private async Task<JsonElement[]> FoldersAsync() =>
        (await Json(await _client.GetAsync($"{Base}/folders", Ct))).EnumerateArray().ToArray();

    private async Task<string> FolderIdAsync(string fullNameEnd) =>
        (await FoldersAsync()).First(f => f.GetProperty("fullName").GetString()!.EndsWith(fullNameEnd)).GetProperty("id").GetString()!;

    private async Task<JsonElement> PageAsync(string folderId, string query = "") =>
        await Json(await _client.GetAsync($"{Base}/folders/{folderId}/messages{query}", Ct));

    private async Task<string> MessageIdAsync(string subject)
    {
        var inbox = await FolderIdAsync("INBOX");
        return (await PageAsync(inbox, "?limit=10")).GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("subject").GetString() == subject).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Folders_are_ordered_with_special_use_counts_and_hierarchy()
    {
        var folders = await FoldersAsync();

        var inbox = folders[0];
        Assert.Equal("inbox", inbox.GetProperty("specialUse").GetString());
        Assert.Equal(ReaderMailbox.NumberedMessages + 2, inbox.GetProperty("total").GetInt32());
        Assert.Equal(ReaderMailbox.NumberedMessages + 2, inbox.GetProperty("unread").GetInt32());

        var sent = folders.Single(f => f.GetProperty("name").GetString() == "Sent");
        Assert.Equal("sent", sent.GetProperty("specialUse").GetString());
        Assert.True(Array.IndexOf(folders, sent) < Array.FindIndex(folders, f => f.GetProperty("name").GetString() == "Projects"));

        var projects = folders.Single(f => f.GetProperty("name").GetString() == "Projects");
        var year = folders.Single(f => f.GetProperty("name").GetString() == "2026");
        Assert.Equal(projects.GetProperty("id").GetString(), year.GetProperty("parentId").GetString());
        Assert.Equal(1, year.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Messages_are_paged_newest_first_without_gaps_or_duplicates()
    {
        var inbox = await FolderIdAsync("INBOX");
        var subjects = new List<string>();
        string query = "?limit=50";
        int pages = 0;
        while (true)
        {
            var page = await PageAsync(inbox, query);
            pages++;
            subjects.AddRange(page.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString()!));
            if (page.GetProperty("nextBefore").ValueKind == JsonValueKind.Null)
            {
                break;
            }

            query = $"?limit=50&before={page.GetProperty("nextBefore").GetString()}";
        }

        Assert.Equal(3, pages);
        Assert.Equal(ReaderMailbox.NumberedMessages + 2, subjects.Count);
        Assert.Equal(subjects.Count, subjects.Distinct().Count());
        Assert.Equal(["Newsletter", "Plain", "Message 120", "Message 119"], subjects.Take(4));
        Assert.Equal("Message 001", subjects[^1]);
    }

    [Fact]
    public async Task Envelope_data_is_listed()
    {
        var inbox = await FolderIdAsync("INBOX");
        var newsletter = (await PageAsync(inbox, "?limit=1")).GetProperty("messages")[0];

        Assert.Equal("Newsletter", newsletter.GetProperty("subject").GetString());
        Assert.Equal("news@example.test", newsletter.GetProperty("from")[0].GetProperty("address").GetString());
        Assert.Equal("News", newsletter.GetProperty("from")[0].GetProperty("name").GetString());
        Assert.True(newsletter.GetProperty("hasAttachments").GetBoolean());
        Assert.False(newsletter.GetProperty("seen").GetBoolean());
    }

    [Fact]
    public async Task Html_message_is_sanitized_with_remote_images_blocked()
    {
        var id = await MessageIdAsync("Newsletter");
        var inbox = await FolderIdAsync("INBOX");

        var message = await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}", Ct));
        var html = message.GetProperty("html").GetString()!;

        Assert.Contains("Newsletter", html);
        Assert.Contains(".title", html);
        Assert.DoesNotContain("alert", html);
        Assert.DoesNotContain("tracker.example", html);
        Assert.True(message.GetProperty("hasRemoteContent").GetBoolean());
        Assert.False(message.GetProperty("remoteContentAllowed").GetBoolean());

        var logo = new HtmlParser().ParseDocument(html).QuerySelector("img[alt=logo]")!;
        var logoPath = logo.GetAttribute(MessageHtmlSanitizer.SourceAttribute)!;
        var image = await _client.GetAsync($"/api/{logoPath}", Ct);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", image.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(ReaderMailbox.PngBytes, await image.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Allowing_remote_content_routes_images_through_the_proxy()
    {
        var id = await MessageIdAsync("Newsletter");
        var inbox = await FolderIdAsync("INBOX");

        var message = await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}?remote=allow", Ct));

        var pixel = new HtmlParser().ParseDocument(message.GetProperty("html").GetString()!).QuerySelector("img[alt=pixel]")!;
        var path = pixel.GetAttribute(MessageHtmlSanitizer.SourceAttribute)!;
        Assert.StartsWith("image-proxy?token=", path);
        Assert.DoesNotContain("tracker", path);

        // The token is bound to its user.
        var other = _app.ClientAs("someone-else");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/{path}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Attachment_downloads_with_safe_headers()
    {
        var id = await MessageIdAsync("Newsletter");
        var inbox = await FolderIdAsync("INBOX");
        var message = await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}", Ct));

        var attachment = Assert.Single(message.GetProperty("attachments").EnumerateArray());
        Assert.Equal("report.pdf", attachment.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", attachment.GetProperty("contentType").GetString());

        var response = await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}/parts/{attachment.GetProperty("partId").GetString()}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(ReaderMailbox.PdfBytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Plain_text_is_escaped_and_links_become_clickable()
    {
        var id = await MessageIdAsync("Plain");
        var inbox = await FolderIdAsync("INBOX");
        var html = (await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}", Ct))).GetProperty("html").GetString()!;

        Assert.Contains("&lt;now&gt;", html);
        Assert.Contains("href=\"https://example.com/page\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public async Task A_look_alike_sender_carries_a_warning_and_ordinary_mail_none()
    {
        var folder = await FolderIdAsync("Suspicious");
        var id = (await PageAsync(folder)).GetProperty("messages")[0].GetProperty("id").GetString()!;
        var warning = (await Json(await _client.GetAsync($"{Base}/folders/{folder}/messages/{id}", Ct))).GetProperty("senderWarning");

        Assert.Equal("danger", warning.GetProperty("level").GetString());
        Assert.Equal("pаypal.com", warning.GetProperty("domain").GetString());
        Assert.Equal("xn--pypal-4ve.com", warning.GetProperty("asciiDomain").GetString());
        Assert.Equal("xn--pypal-4ve.com", warning.GetProperty("registered").GetString());
        var reasons = warning.GetProperty("reasons").EnumerateArray()
            .Select(r => $"{r.GetProperty("code").GetString()}/{r.GetProperty("field").GetString()}/{r.GetProperty("looksLike").GetString()}")
            .ToList();
        Assert.Equal(["lookalike/from/paypal.com", "nameShowsDomain/from/paypal.com", "dmarcFail/from/", "lookalike/replyTo/paypal.com"], reasons);

        var plain = await MessageIdAsync("Plain");
        var inbox = await FolderIdAsync("INBOX");
        Assert.Equal(JsonValueKind.Null, (await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{plain}", Ct))).GetProperty("senderWarning").ValueKind);
    }

    [Fact]
    public async Task Reading_a_message_does_not_mark_it_as_read()
    {
        var id = await MessageIdAsync("Message 118");
        var inbox = await FolderIdAsync("INBOX");
        await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}", Ct));

        using var imap = await ReaderMailbox.ConnectAsync(_mailbox.Mail);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, Ct);
        MailboxIds.TryParseMessageId(id, out _, out var uid);
        var summary = (await imap.Inbox.FetchAsync([new UniqueId(uid)], MessageSummaryItems.Flags, Ct)).Single();
        Assert.False(summary.Flags!.Value.HasFlag(MessageFlags.Seen));
    }

    [Fact]
    public async Task Headers_are_returned_raw_without_the_body_or_marking_read()
    {
        var id = await MessageIdAsync("Message 117");
        var inbox = await FolderIdAsync("INBOX");

        var headers = (await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}/headers", Ct)))
            .GetProperty("headers").GetString()!;

        Assert.Contains("Subject: Message 117\r\n", headers);
        Assert.Contains("From: Sender <sender@example.test>\r\n", headers);
        Assert.DoesNotContain("numbered", headers);
        Assert.False(await IsSeenAsync(id));
    }

    [Fact]
    public async Task Source_is_the_whole_message_without_marking_read()
    {
        var id = await MessageIdAsync("Message 116");
        var inbox = await FolderIdAsync("INBOX");

        var source = await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}/source", Ct));
        var text = source.GetProperty("text").GetString()!;

        Assert.False(source.GetProperty("truncated").GetBoolean());
        Assert.Contains("Subject: Message 116\r\n", text);
        Assert.EndsWith("numbered", text.TrimEnd());
        Assert.Equal(Encoding.UTF8.GetByteCount(text), source.GetProperty("size").GetInt64());
        Assert.False(await IsSeenAsync(id));
    }

    [Fact]
    public async Task A_large_source_is_cut_at_the_limit_on_a_character_boundary()
    {
        var big = await FolderIdAsync("Big");
        var id = (await PageAsync(big)).GetProperty("messages").EnumerateArray().Single().GetProperty("id").GetString()!;

        var source = await Json(await _client.GetAsync($"{Base}/folders/{big}/messages/{id}/source", Ct));
        var text = source.GetProperty("text").GetString()!;

        Assert.True(source.GetProperty("truncated").GetBoolean());
        Assert.True(source.GetProperty("size").GetInt64() > MailboxService.MaxSourceViewBytes);
        var bytes = Encoding.UTF8.GetByteCount(text);
        Assert.InRange(bytes, MailboxService.MaxSourceViewBytes - 3, MailboxService.MaxSourceViewBytes);
        Assert.Contains("ééé", text);                // decoded as UTF-8, not Latin-1
        Assert.DoesNotContain('�', text);
    }

    [Fact]
    public async Task Message_downloads_as_eml_with_safe_headers()
    {
        var id = await MessageIdAsync("Newsletter");
        var inbox = await FolderIdAsync("INBOX");

        var response = await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}/source?download=true", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("message/rfc822", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Newsletter.eml", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        var message = await MimeMessage.LoadAsync(await response.Content.ReadAsStreamAsync(Ct), Ct);
        Assert.Equal("Newsletter", message.Subject);
        using var pdf = new MemoryStream();
        await ((MimePart)message.Attachments.Single()).Content!.DecodeToAsync(pdf, Ct);
        Assert.Equal(ReaderMailbox.PdfBytes, pdf.ToArray());
        Assert.False(await IsSeenAsync(id));
    }

    private async Task<bool> IsSeenAsync(string id)
    {
        using var imap = await ReaderMailbox.ConnectAsync(_mailbox.Mail);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, Ct);
        MailboxIds.TryParseMessageId(id, out _, out var uid);
        var summary = (await imap.Inbox.FetchAsync([new UniqueId(uid)], MessageSummaryItems.Flags, Ct)).Single();
        return summary.Flags!.Value.HasFlag(MessageFlags.Seen);
    }

    [Theory]
    [InlineData("messages/1-1/headers")]
    [InlineData("messages/1-1/source")]
    [InlineData("messages/1-1/source?download=true")]
    [InlineData("messages/1-1")]          // wrong UIDVALIDITY
    [InlineData("messages/not-an-id")]
    [InlineData("messages/{valid}/parts/99")]
    public async Task Stale_or_unknown_ids_are_404(string suffix)
    {
        var inbox = await FolderIdAsync("INBOX");
        var valid = await MessageIdAsync("Newsletter");
        var response = await _client.GetAsync($"{Base}/folders/{inbox}/{suffix.Replace("{valid}", valid)}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_folder_is_404()
    {
        var response = await _client.GetAsync($"{Base}/folders/{MailboxIds.FolderId("Does/Not/Exist")}/messages", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Another_user_cannot_read_the_mailbox()
    {
        var inbox = await FolderIdAsync("INBOX");
        var id = await MessageIdAsync("Newsletter");
        var other = _app.ClientAs("someone-else");

        foreach (var path in new[]
                 {
                     $"{Base}/folders",
                     $"{Base}/folders/{inbox}/messages",
                     $"{Base}/folders/{inbox}/messages/{id}",
                     $"{Base}/folders/{inbox}/messages/{id}/parts/2",
                     $"{Base}/folders/{inbox}/messages/{id}/headers",
                     $"{Base}/folders/{inbox}/messages/{id}/source",
                     $"{Base}/folders/{inbox}/messages/{id}/source?download=true",
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Connections_are_pooled_and_reused()
    {
        var inbox = await FolderIdAsync("INBOX");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => PageAsync(inbox, "?limit=5")));

        var pool = _app.Services.GetRequiredService<ImapConnections>();
        Assert.InRange(pool.OpenCount, 1, 3);
    }

    [Fact]
    public async Task A_dropped_connection_is_replaced_transparently()
    {
        var inbox = await FolderIdAsync("INBOX");
        var pool = _app.Services.GetRequiredService<ImapConnections>();
        // Outside a request there is no current user, so the per-user query filter must be bypassed explicitly.
        var db = _app.Services.CreateScope().ServiceProvider.GetRequiredService<SocklessMail.Api.Data.MailDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == _accountId, Ct);
        var credential = _app.Services.GetRequiredService<CredentialProtector>().Unprotect(account.Id, account.ProtectedCredential);

        // Leave a pooled connection behind that the server has already closed.
        await pool.RunAsync(account, credential, async (client, ct) =>
        {
            await client.DisconnectAsync(false, ct);
            return 0;
        }, Ct);

        var page = await PageAsync(inbox, "?limit=1");
        Assert.Equal("Newsletter", page.GetProperty("messages")[0].GetProperty("subject").GetString());
    }
}
