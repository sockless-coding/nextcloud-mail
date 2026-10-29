using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.AppApi;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>
/// Answering an invitation end to end: the invitation arrives over SMTP, the answer is stored
/// through (fake) CalDAV and the iMIP reply reaches the organizer's mailbox over real SMTP.
/// </summary>
public sealed class InvitationFlowTests : IAsyncLifetime
{
    private const string Attendee = "invitee@example.test";
    private const string Organizer = "organizer@example.test";

    private readonly GreenMailFixture _mail;
    private readonly FakeCalendar _calendar = new();
    private readonly MailApp _app;
    private readonly HttpClient _client;
    private readonly string _uid = $"flow-{Guid.NewGuid():N}";
    private Guid _accountId;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public InvitationFlowTests(GreenMailFixture mail)
    {
        _mail = mail;
        _app = new MailApp(services => services.AddHttpClient<NextcloudClient>().ConfigurePrimaryHttpMessageHandler(() => _calendar));
        _client = _app.ClientAs("invitee-user");
    }

    public async ValueTask InitializeAsync()
    {
        var builder = new BodyBuilder { TextBody = "Please come." };
        var message = new MimeMessage { Subject = $"Invitation {_uid}" };
        message.From.Add(MailboxAddress.Parse(Organizer));
        message.To.Add(MailboxAddress.Parse(Attendee));
        var calendar = new TextPart("calendar") { Text = Ics("REQUEST") };
        calendar.ContentType.Parameters.Add("method", "REQUEST");
        message.Body = new MultipartAlternative { new TextPart("plain") { Text = "Please come." }, calendar };
        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync(_mail.Host, _mail.Smtp, SecureSocketOptions.None, Ct);
            await smtp.SendAsync(message, Ct);
        }

        _accountId = await AddAccountAsync(_client, _mail, Attendee, GreenMailFixture.ActionsPassword);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private string Ics(string method) => string.Join("\r\n",
        "BEGIN:VCALENDAR", $"METHOD:{method}", "PRODID:-//test//EN", "VERSION:2.0",
        "BEGIN:VEVENT", $"UID:{_uid}", "DTSTAMP:20261029T100000Z", "SEQUENCE:1",
        "DTSTART;TZID=Europe/Berlin:20261110T090000", "DTEND;TZID=Europe/Berlin:20261110T100000",
        "SUMMARY:Planning", "X-CUSTOM:kept",
        $"ORGANIZER;CN=Olga:mailto:{Organizer}",
        $"ATTENDEE;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;CN=Ivy:mailto:{Attendee}",
        "END:VEVENT", "END:VCALENDAR", "");

    private async Task<string> InvitationPathAsync()
    {
        var folders = (await Json(await _client.GetAsync($"/api/accounts/{_accountId}/folders", Ct))).EnumerateArray();
        var inbox = folders.First(f => f.GetProperty("fullName").GetString() == "INBOX").GetProperty("id").GetString();
        var messages = (await Json(await _client.GetAsync($"/api/accounts/{_accountId}/folders/{inbox}/messages?limit=20", Ct))).GetProperty("messages");
        var id = messages.EnumerateArray().First(m => m.GetProperty("subject").GetString() == $"Invitation {_uid}").GetProperty("id").GetString();
        var message = await Json(await _client.GetAsync($"/api/accounts/{_accountId}/folders/{inbox}/messages/{id}", Ct));
        var part = Assert.Single(message.GetProperty("calendarParts").EnumerateArray());
        Assert.Equal("REQUEST", part.GetProperty("method").GetString());
        return $"/api/accounts/{_accountId}/folders/{inbox}/messages/{id}/invitations/{part.GetProperty("partId").GetString()}";
    }

    [Fact]
    public async Task Accepting_stores_the_event_and_replies_to_the_organizer()
    {
        var path = await InvitationPathAsync();
        var before = await Json(await _client.GetAsync(path, Ct));
        Assert.Equal("Planning", before.GetProperty("event").GetProperty("summary").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("existing").ValueKind);

        var response = await _client.PostAsJsonAsync($"{path}/respond", new { response = "accepted", calendarId = "personal", sendReply = true }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await Json(response);
        Assert.Equal("ACCEPTED", after.GetProperty("existing").GetProperty("myStatus").GetString());

        // Written twice: silenced for Nextcloud's own scheduling, then handed back.
        var puts = _calendar.Puts.ToArray();
        Assert.Equal(2, puts.Length);
        Assert.Contains("SCHEDULE-AGENT=CLIENT", puts[0].Unfolded);
        Assert.DoesNotContain("SCHEDULE-AGENT", puts[1].Unfolded);
        Assert.Equal("*", puts[0].IfNoneMatch);
        Assert.Contains("X-CUSTOM:kept", puts[1].Unfolded);
        Assert.Contains($"PARTSTAT=ACCEPTED:mailto:{Attendee}", puts[1].Unfolded);

        // The organizer's mailbox has the iMIP reply, from the attendee's own address.
        using var imap = new ImapClient();
        await imap.ConnectAsync(_mail.Host, _mail.Imap, SecureSocketOptions.None, Ct);
        // Created by GreenMail when the invitation was delivered to it: logs in with its address.
        await imap.AuthenticateAsync(Organizer, Organizer, Ct);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, Ct);
        MimeMessage? reply = null;
        for (var i = 0; i < imap.Inbox.Count; i++)
        {
            var candidate = await imap.Inbox.GetMessageAsync(i, Ct);
            if (candidate.Subject == "Accepted: Planning" && candidate.BodyParts.OfType<TextPart>().Any(p => p.Text.Contains(_uid)))
            {
                reply = candidate;
            }
        }

        Assert.NotNull(reply);
        Assert.Equal(Attendee, ((MailboxAddress)reply.From.Single()).Address);
        var calendar = reply.BodyParts.OfType<TextPart>().Single(p => p.ContentType.IsMimeType("text", "calendar"));
        Assert.Equal("REPLY", calendar.ContentType.Parameters["method"]);
        Assert.Contains("SEQUENCE:1", calendar.Text);
    }

    [Fact]
    public async Task Declining_an_event_not_in_the_calendar_stores_nothing()
    {
        var path = await InvitationPathAsync();

        var response = await _client.PostAsJsonAsync($"{path}/respond", new { response = "declined", sendReply = false }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_calendar.Puts);
    }

    [Fact]
    public async Task Accepting_needs_a_writable_calendar()
    {
        var path = await InvitationPathAsync();

        var response = await _client.PostAsJsonAsync($"{path}/respond", new { response = "accepted", calendarId = "birthdays" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_calendar.Puts);
    }

    /// <summary>A calendar home with "personal" (writable) and "birthdays" (read-only); stores PUTs in memory.</summary>
    private sealed class FakeCalendar : HttpMessageHandler
    {
        public sealed record Put(string Name, string Unfolded, string? IfNoneMatch, string? IfMatch);

        private readonly ConcurrentDictionary<string, (string Text, int Version)> _objects = new();

        public ConcurrentQueue<Put> Puts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            switch (request.Method.Method)
            {
                case "PROPFIND":
                    return MultiStatus(
                        Calendar("personal", "Personal", "<d:privilege><d:read/></d:privilege><d:privilege><d:write-content/></d:privilege><d:privilege><d:bind/></d:privilege>")
                        + Calendar("birthdays", "Birthdays", "<d:privilege><d:read/></d:privilege>"));
                case "REPORT":
                    var calendar = path.TrimEnd('/').Split('/')[^1];
                    var found = _objects.Where(o => o.Key.StartsWith(calendar + "/", StringComparison.Ordinal) && body.Contains(o.Value.Text.Split("UID:")[1].Split('\r')[0]))
                        .Select(o => $"<d:response><d:href>/remote.php/dav/calendars/invitee-user/{o.Key}</d:href><d:propstat><d:prop><d:getetag>\"v{o.Value.Version}\"</d:getetag><c:calendar-data>{SecurityElement.Escape(o.Value.Text)}</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
                    return MultiStatus(string.Concat(found));
                case "PUT":
                    var key = string.Join('/', path.Split('/')[^2..]);
                    _objects.TryGetValue(key, out var existing);
                    Puts.Enqueue(new Put(key, body.Replace("\r\n ", ""),
                        request.Headers.IfNoneMatch.FirstOrDefault()?.ToString(), request.Headers.IfMatch.FirstOrDefault()?.ToString()));
                    _objects[key] = (body, existing.Version + 1);
                    var response = new HttpResponseMessage(existing.Text is null ? HttpStatusCode.Created : HttpStatusCode.NoContent);
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"v{existing.Version + 1}\"");
                    return response;
                default:
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
        }

        private static string Calendar(string id, string name, string privileges) =>
            $"<d:response><d:href>/remote.php/dav/calendars/invitee-user/{id}/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/><c:calendar/></d:resourcetype><d:displayname>{name}</d:displayname><d:current-user-privilege-set>{privileges}</d:current-user-privilege-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

        private static HttpResponseMessage MultiStatus(string responses) =>
            new((HttpStatusCode)207)
            {
                Content = new StringContent(
                    $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">{responses}</d:multistatus>""",
                    Encoding.UTF8, "application/xml"),
            };
    }
}
