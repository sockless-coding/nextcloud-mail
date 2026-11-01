using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    /// <summary>A 1×1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private async Task<string> UploadImageAsync(string name)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", name);
        return (await Json(await _client.PostAsync("/api/uploads", form, Ct))).GetProperty("id").GetString()!;
    }

    /// <summary>The image with the Content-ID, beside the HTML in a multipart/related.</summary>
    private static MimePart InlineImage(MimeMessage message, string contentId)
    {
        static IEnumerable<Multipart> Multiparts(MimeEntity? entity) =>
            entity is Multipart multipart ? multipart.SelectMany(Multiparts).Prepend(multipart) : [];

        var related = Assert.Single(Multiparts(message.Body).OfType<MultipartRelated>());
        Assert.Contains(related, p => p is TextPart { IsHtml: true });
        var image = Assert.IsType<MimePart>(Assert.Single(related, p => p.ContentId == contentId));
        Assert.Equal("image/png", image.ContentType.MimeType);
        Assert.Equal(ContentDisposition.Inline, image.ContentDisposition?.Disposition);
        return image;
    }

    [Fact]
    public async Task Images_in_the_text_go_out_as_inline_parts_through_drafts()
    {
        var subject = Unique("Picture");
        var upload = await UploadImageAsync("screenshot.png");
        var cid = $"{upload}@inline.sockless-mail";
        // As the editor writes it: shown from a blob URL, marked with its Content-ID. Images
        // without one (remote, data:) never reach a recipient.
        var html = $"<p>Look: <img src=\"blob:https://cloud.example/1\" data-cid=\"{cid}\" width=\"400\" alt=\"The chart\" data-pending=\"\"></p>"
                   + "<p><img src=\"https://tracker.example/t.gif\"><img src=\"data:image/png;base64,AAAA\"></p>";

        var first = await Json(await _client.PutAsJsonAsync($"{Base}/drafts", new { to = Recipient, subject, html }, Ct));
        var (saved, _) = Assert.Single(await FindAsync(Drafter, "Drafts", subject));
        Assert.Contains($"src=\"cid:{cid}\"", saved.HtmlBody);
        Assert.DoesNotContain("tracker.example", saved.HtmlBody);
        Assert.DoesNotContain("data:image", saved.HtmlBody);
        Assert.DoesNotContain("blob:", saved.HtmlBody);
        InlineImage(saved, cid);
        Assert.Empty(saved.Attachments);

        // Continued: the image is the draft's own part now, listed so the editor can show it.
        var draftsId = first.GetProperty("folderId").GetString()!;
        var firstId = first.GetProperty("messageId").GetString()!;
        var edit = await Json(await _client.GetAsync($"{Base}/folders/{draftsId}/messages/{firstId}/draft?mode=edit", Ct));
        Assert.Contains($"src=\"cid:{cid}\"", edit.GetProperty("html").GetString());
        Assert.Contains("width=\"400\"", edit.GetProperty("html").GetString());
        var image = Assert.Single(edit.GetProperty("inlineImages").EnumerateArray());
        Assert.Equal(cid, image.GetProperty("contentId").GetString());
        Assert.Empty(edit.GetProperty("forwardedAttachments").EnumerateArray());
        var part = await _client.GetAsync($"{Base}/folders/{draftsId}/messages/{firstId}/parts/{image.GetProperty("partId").GetString()}", Ct);
        Assert.Equal(Png, await part.Content.ReadAsByteArrayAsync(Ct));

        // Saving the continued draft carries the image over; its upload is gone from then on.
        var second = await Json(await _client.PutAsJsonAsync($"{Base}/drafts", new
        {
            to = Recipient,
            subject,
            html = $"<p>Look again: <img src=\"blob:x\" data-cid=\"{cid}\" alt=\"The chart\"></p>",
            source = new { folderId = draftsId, messageId = firstId, mode = "edit", forwardPartIds = Array.Empty<string>() },
            replaces = new { folderId = draftsId, messageId = firstId },
        }, Ct));
        var secondId = second.GetProperty("messageId").GetString()!;
        (saved, _) = Assert.Single(await FindAsync(Drafter, "Drafts", subject));
        InlineImage(saved, cid);
        // An upload that is gone (expired, removed) is refused on sending rather than left out.
        var gone = $"{Guid.NewGuid():N}@inline.sockless-mail";
        var stale = await _client.PostAsJsonAsync($"{Base}/send", new { to = Recipient, subject, html = $"<p><img data-cid=\"{gone}\"></p>" }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, stale.StatusCode);
        var problem = JsonDocument.Parse(await stale.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Contains("image", problem.GetProperty("errors").GetProperty("text")[0].GetString());

        await Json(await _client.PostAsJsonAsync($"{Base}/send", new
        {
            to = Recipient,
            subject,
            html = $"<p>Final: <img src=\"cid:{cid}\" alt=\"The chart\"></p>",
            source = new { folderId = draftsId, messageId = secondId, mode = "edit", forwardPartIds = Array.Empty<string>() },
            draft = new { folderId = draftsId, messageId = secondId },
        }, Ct));

        var (sent, _) = Assert.Single(await FindAsync(Recipient, "INBOX", subject));
        Assert.Contains($"<img alt=\"The chart\" src=\"cid:{cid}\" style=\"max-width:100%;height:auto\">", sent.HtmlBody);
        var sentImage = InlineImage(sent, cid);
        using var bytes = new MemoryStream();
        await sentImage.Content!.DecodeToAsync(bytes, Ct);
        Assert.Equal(Png, bytes.ToArray());
        Assert.Empty(sent.Attachments);
        Assert.Empty(await FindAsync(Drafter, "Drafts", subject, expect: false));
    }

    [Fact]
    public async Task Only_images_can_be_shown_in_the_text()
    {
        var upload = await UploadAsync("notes.txt", "not a picture");
        var response = await _client.PostAsJsonAsync($"{Base}/send", new
        {
            to = Recipient,
            subject = Unique("Not an image"),
            html = $"<p><img src=\"blob:x\" data-cid=\"{upload}@inline.sockless-mail\"></p>",
        }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
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

    [Theory]
    [InlineData("reply")]
    [InlineData("forward")]
    public async Task Quoted_originals_keep_their_inline_images(string mode)
    {
        var subject = Unique($"Pictured {mode}");
        var original = new MimeMessage { Subject = subject };
        original.From.Add(MailboxAddress.Parse("olga@example.test"));
        original.To.Add(MailboxAddress.Parse(Drafter));
        var body = new BodyBuilder
        {
            HtmlBody = "<p>Our logo <img src=\"cid:logo@olga.example\" alt=\"Logo\"> and <img src=\"https://tracker.example/t.gif\"></p>",
        };
        var logo = body.LinkedResources.Add("logo.png", Png, ContentType.Parse("image/png"));
        logo.ContentId = "logo@olga.example";
        original.Body = body.ToMessageBody();
        string id;
        using (var imap = await ConnectAsync(Drafter))
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            id = MailboxIds.MessageId(imap.Inbox.UidValidity, (await imap.Inbox.AppendAsync(original, MessageFlags.None, Ct))!.Value.Id);
        }

        var inbox = MailboxIds.FolderId("INBOX");
        var draft = await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}/draft?mode={mode}", Ct));
        var html = draft.GetProperty("html").GetString()!;
        Assert.Contains("<img alt=\"Logo\" src=\"cid:logo@olga.example\">", html);
        Assert.DoesNotContain("tracker.example", html);
        var image = Assert.Single(draft.GetProperty("inlineImages").EnumerateArray());
        Assert.Equal("logo@olga.example", image.GetProperty("contentId").GetString());
        // Not offered as an attachment to forward: it goes along as part of the text.
        Assert.Empty(draft.GetProperty("forwardedAttachments").EnumerateArray());

        await Json(await _client.PostAsJsonAsync($"{Base}/send", new
        {
            to = Recipient,
            subject = draft.GetProperty("subject").GetString(),
            html = "<p>See below</p>" + html,
            source = new { folderId = inbox, messageId = id, mode, forwardPartIds = Array.Empty<string>() },
        }, Ct));
        var (sent, _) = Assert.Single(await FindAsync(Recipient, "INBOX", subject));
        Assert.Contains("src=\"cid:logo@olga.example\"", sent.HtmlBody);
        InlineImage(sent, "logo@olga.example");
    }

    [Fact]
    public async Task Images_of_pasted_web_content_are_fetched_by_the_server()
    {
        await using var web = await TestWebServer.StartAsync(Ct, ("/img/chart.png", "image/png", Png), ("/page.html", "text/html", "<p>x</p>"u8.ToArray()));

        var upload = await Json(await _client.PostAsJsonAsync("/api/uploads/import", new { url = web.Url("/img/chart.png") }, Ct));
        Assert.Equal("chart.png", upload.GetProperty("fileName").GetString());
        Assert.Equal("image/png", upload.GetProperty("contentType").GetString());
        var id = upload.GetProperty("id").GetString()!;

        var content = await _client.GetAsync($"/api/uploads/{id}/content", Ct);
        Assert.Equal(Png, await content.Content.ReadAsByteArrayAsync(Ct));
        Assert.Contains("sandbox", content.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _app.ClientAs("someone-else").GetAsync($"/api/uploads/{id}/content", Ct)).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity,
            (await _client.PostAsJsonAsync("/api/uploads/import", new { url = web.Url("/page.html") }, Ct)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity,
            (await _client.PostAsJsonAsync("/api/uploads/import", new { url = "file:///etc/passwd" }, Ct)).StatusCode);

        // Only images are shown from uploads.
        var text = await UploadAsync("notes.txt", "text");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _client.GetAsync($"/api/uploads/{text}/content", Ct)).StatusCode);
    }
}

/// <summary>A web server on localhost with fixed responses (pages whose images are pasted).</summary>
internal sealed class TestWebServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestWebServer(WebApplication app) => _app = app;

    public static async Task<TestWebServer> StartAsync(CancellationToken ct, params (string Path, string ContentType, byte[] Content)[] responses)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        foreach (var (path, contentType, content) in responses)
        {
            app.MapGet(path, () => Results.Bytes(content, contentType));
        }

        await app.StartAsync(ct);
        return new TestWebServer(app);
    }

    public string Url(string path) => _app.Urls.First().TrimEnd('/') + path;

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
