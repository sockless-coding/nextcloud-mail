using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.AppApi;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Saving a real message's attachment into Files: read over IMAP, written over (fake) WebDAV.</summary>
public sealed class SaveToFilesTests : IAsyncLifetime
{
    private const string Filer = "filer@example.test";
    private static readonly byte[] PdfBytes = "%PDF-1.4 fake invoice"u8.ToArray();

    private readonly GreenMailFixture _mail;
    private readonly FakeFiles _files = new();
    private readonly MailApp _app;
    private readonly HttpClient _client;
    private Guid _accountId;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public SaveToFilesTests(GreenMailFixture mail)
    {
        _mail = mail;
        _app = new MailApp(services => services.AddHttpClient<NextcloudClient>().ConfigurePrimaryHttpMessageHandler(() => _files));
        _client = _app.ClientAs("filer-user");
    }

    public async ValueTask InitializeAsync()
    {
        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync(_mail.Host, _mail.Imap, SecureSocketOptions.None, Ct);
            await imap.AuthenticateAsync(Filer, GreenMailFixture.ActionsPassword, Ct);
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            if (imap.Inbox.Count == 0)
            {
                var builder = new BodyBuilder { TextBody = "See attached." };
                builder.Attachments.Add("invoice.pdf", PdfBytes, new ContentType("application", "pdf"));
                var message = new MimeMessage { Subject = "Invoice", Body = builder.ToMessageBody() };
                message.From.Add(MailboxAddress.Parse("billing@example.test"));
                message.To.Add(MailboxAddress.Parse(Filer));
                await imap.Inbox.AppendAsync(message, cancellationToken: Ct);
            }
        }

        _accountId = await AddAccountAsync(_client, _mail, Filer, GreenMailFixture.ActionsPassword);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private string Base => $"/api/accounts/{_accountId}";

    [Fact]
    public async Task An_attachment_is_saved_into_the_chosen_folder()
    {
        var folders = (await Json(await _client.GetAsync($"{Base}/folders", Ct))).EnumerateArray();
        var inbox = folders.First(f => f.GetProperty("fullName").GetString() == "INBOX").GetProperty("id").GetString();
        var messages = (await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages?limit=10", Ct))).GetProperty("messages");
        var id = messages.EnumerateArray().First(m => m.GetProperty("subject").GetString() == "Invoice").GetProperty("id").GetString();
        var message = await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages/{id}", Ct));
        var partId = message.GetProperty("attachments")[0].GetProperty("partId").GetString();

        var response = await _client.PostAsJsonAsync($"{Base}/folders/{inbox}/messages/{id}/save-to-files",
            new { folder = "/Mail attachments", partIds = new[] { partId, "9.9" } }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await Json(response)).GetProperty("saved").EnumerateArray().ToArray();
        Assert.Equal("/Mail attachments/invoice.pdf", saved[0].GetProperty("path").GetString());
        Assert.Equal("not_found", saved[1].GetProperty("error").GetString());

        var (path, user, content) = Assert.Single(_files.Puts);
        Assert.Equal("/remote.php/dav/files/filer-user/Mail%20attachments/invoice.pdf", path);
        Assert.Equal("filer-user", user);
        Assert.Equal(PdfBytes, content);
    }

    [Fact]
    public async Task Another_users_account_is_not_found()
    {
        var other = _app.ClientAs("someone-else");
        var response = await other.PostAsJsonAsync($"{Base}/folders/x/messages/y/save-to-files",
            new { folder = "/", partIds = new[] { "2" } }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_files.Puts);
    }

    /// <summary>An empty folder "Mail attachments" that accepts uploads.</summary>
    private sealed class FakeFiles : HttpMessageHandler
    {
        public ConcurrentQueue<(string Path, string User, byte[] Content)> Puts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            switch (request.Method.Method)
            {
                case "PROPFIND":
                    return new HttpResponseMessage((HttpStatusCode)207)
                    {
                        Content = new StringContent(
                            $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>{path}</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>""",
                            Encoding.UTF8, "application/xml"),
                    };
                case "PUT":
                    var auth = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.GetValues(AppApiHeaders.Authorization).Single()));
                    Puts.Enqueue((path, auth[..auth.IndexOf(':')], await request.Content!.ReadAsByteArrayAsync(ct)));
                    return new HttpResponseMessage(HttpStatusCode.Created);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}
