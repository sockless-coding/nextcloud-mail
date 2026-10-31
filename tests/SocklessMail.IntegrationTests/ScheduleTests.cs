using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Scheduling;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Send later: scheduled through the API, sent by the background sender, checked over IMAP.</summary>
public sealed class ScheduleTests(GreenMailFixture mail) : IAsyncLifetime
{
    private const string Scheduler = "scheduler@example.test";
    private const string Recipient = "rcpt3@example.test";
    private const string Password = GreenMailFixture.ActionsPassword;

    private readonly MailApp _app = new(services => services.AddSingleton(new ScheduledSenderOptions
    {
        PollInterval = TimeSpan.FromMilliseconds(500),
        MinimumLead = TimeSpan.Zero,
        RetryDelays = [TimeSpan.FromMilliseconds(200)],
    }));

    private HttpClient _client = null!;
    private Guid _account;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        using (var imap = await ConnectAsync(Scheduler))
        {
            foreach (var name in new[] { "Sent", "Drafts" })
            {
                try
                {
                    await imap.GetFolderAsync(name, Ct);
                }
                catch (FolderNotFoundException)
                {
                    await imap.GetFolder(imap.PersonalNamespaces[0]).CreateAsync(name, true, Ct);
                }
            }
        }

        _client = _app.ClientAs("scheduler-user");
        _account = await AddAccountAsync(_client, mail, Scheduler, Password);
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

    private async Task<List<(MimeMessage Message, MessageFlags Flags)>> FindAsync(string user, string folder, string subject, bool expect = true)
    {
        for (var attempt = 0; attempt < (expect ? 40 : 1); attempt++)
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

    private static string Unique(string what) => $"{what} {Guid.NewGuid():N}"[..(what.Length + 9)];

    private async Task<JsonElement[]> ListAsync() =>
        [.. (await Json(await _client.GetAsync("/api/scheduled", Ct))).EnumerateArray()];

    private async Task<JsonElement> WaitForStatusAsync(string id, string status)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var row = (await ListAsync()).FirstOrDefault(m => m.GetProperty("id").GetString() == id);
            if (row.ValueKind != JsonValueKind.Undefined && row.GetProperty("status").GetString() == status)
            {
                return row;
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException($"Scheduled message {id} never became {status}.");
    }

    private async Task<JsonElement> ScheduleAsync(object body) =>
        await Json(await _client.PostAsJsonAsync($"{Base}/schedule", body, Ct));

    [Fact]
    public async Task Message_goes_out_at_its_time_with_a_copy_in_sent_and_its_attachment()
    {
        var subject = Unique("Later");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("agenda"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "agenda.txt");
        var upload = (await Json(await _client.PostAsync("/api/uploads", form, Ct))).GetProperty("id").GetString();
        var draft = await Json(await _client.PutAsJsonAsync($"{Base}/drafts", new { to = Recipient, subject, text = "draft" }, Ct));
        Assert.Single(await FindAsync(Scheduler, "Drafts", subject));

        var scheduledAt = DateTimeOffset.UtcNow;
        var sendAt = scheduledAt.AddSeconds(2);
        var scheduled = await ScheduleAsync(new
        {
            sendAt,
            to = $"Rita <{Recipient}>",
            bcc = "rcpt2@example.test",
            subject,
            text = "See you then.",
            attachmentIds = new[] { upload },
            draft = new { folderId = draft.GetProperty("folderId").GetString(), messageId = draft.GetProperty("messageId").GetString() },
        });
        Assert.Equal("scheduled", scheduled.GetProperty("status").GetString());
        Assert.Equal(subject, scheduled.GetProperty("subject").GetString());
        Assert.Equal(2, scheduled.GetProperty("recipients").GetArrayLength());
        Assert.True(scheduled.GetProperty("hasAttachments").GetBoolean());
        Assert.Equal("See you then.", scheduled.GetProperty("preview").GetString());

        // Scheduling replaces the draft, and nothing is sent before its time.
        Assert.Empty(await FindAsync(Scheduler, "Drafts", subject, expect: false));
        Assert.Empty(await FindAsync(Recipient, "INBOX", subject, expect: false));

        var (received, _) = Assert.Single(await FindAsync(Recipient, "INBOX", subject));
        Assert.Equal("agenda.txt", Assert.Single(received.Attachments).ContentDisposition?.FileName);
        Assert.True(received.Date >= sendAt.AddSeconds(-1), $"Dated {received.Date}, scheduled for {sendAt}");
        Assert.Single(await FindAsync("rcpt2@example.test", "INBOX", subject));
        var (copy, flags) = Assert.Single(await FindAsync(Scheduler, "Sent", subject));
        Assert.True(flags.HasFlag(MessageFlags.Seen));
        Assert.Equal(received.MessageId, copy.MessageId);
        Assert.Equal("rcpt2@example.test", Assert.Single(copy.Bcc.Mailboxes).Address);

        for (var attempt = 0; attempt < 20 && (await ListAsync()).Length > 0; attempt++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task A_reply_marks_its_original_once_it_is_sent()
    {
        var subject = Unique("Question");
        string messageId;
        using (var imap = await ConnectAsync(Scheduler))
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            var original = new MimeMessage();
            original.From.Add(MailboxAddress.Parse(Recipient));
            original.To.Add(MailboxAddress.Parse(Scheduler));
            original.Subject = subject;
            original.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("example.test");
            original.Body = new TextPart("plain") { Text = "Lunch?" };
            var uid = await imap.Inbox.AppendAsync(original, MessageFlags.None, Ct);
            messageId = MailboxIds.MessageId(imap.Inbox.UidValidity, uid!.Value.Id);
        }

        var folderId = MailboxIds.FolderId("INBOX");
        await ScheduleAsync(new
        {
            sendAt = DateTimeOffset.UtcNow.AddSeconds(1),
            to = Recipient,
            subject = "Re: " + subject,
            text = "Yes.",
            source = new { folderId, messageId, mode = "reply" },
        });

        Assert.Single(await FindAsync(Recipient, "INBOX", "Re: " + subject));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var (_, flags) = Assert.Single(await FindAsync(Scheduler, "INBOX", subject));
            if (flags.HasFlag(MessageFlags.Answered))
            {
                return;
            }

            await Task.Delay(250, Ct);
        }

        Assert.Fail("The original was not marked answered.");
    }

    [Fact]
    public async Task Send_now_reschedule_and_cancel_back_to_drafts()
    {
        var later = DateTimeOffset.UtcNow.AddHours(2);
        var nowSubject = Unique("Now");
        var now = await ScheduleAsync(new { sendAt = later, to = Recipient, subject = nowSubject, text = "now" });
        var sent = await Json(await _client.PostAsync($"/api/scheduled/{now.GetProperty("id").GetString()}/send-now", null, Ct));
        Assert.True(sent.GetProperty("sent").GetBoolean());
        Assert.Single(await FindAsync(Recipient, "INBOX", nowSubject));

        var cancelSubject = Unique("Cancel");
        var cancel = await ScheduleAsync(new { sendAt = later, to = Recipient, cc = "rcpt2@example.test", subject = cancelSubject, html = "<p>maybe <b>not</b></p>" });
        var id = cancel.GetProperty("id").GetString();

        var tomorrow = later.AddDays(1);
        var moved = await Json(await _client.PutAsJsonAsync($"/api/scheduled/{id}", new { sendAt = tomorrow }, Ct));
        Assert.Equal(tomorrow.ToUnixTimeSeconds(), moved.GetProperty("sendAt").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Equal(tomorrow.ToUnixTimeSeconds(), Assert.Single(await ListAsync(), m => m.GetProperty("id").GetString() == id).GetProperty("sendAt").GetDateTimeOffset().ToUnixTimeSeconds());

        // Readable as it will go out: recipients, sanitized HTML.
        var detail = await Json(await _client.GetAsync($"/api/scheduled/{id}", Ct));
        Assert.Equal(id, detail.GetProperty("message").GetProperty("id").GetString());
        Assert.Equal(Recipient, Assert.Single(detail.GetProperty("to").EnumerateArray()).GetProperty("address").GetString());
        Assert.Equal("rcpt2@example.test", Assert.Single(detail.GetProperty("cc").EnumerateArray()).GetProperty("address").GetString());
        Assert.Contains("<b>not</b>", detail.GetProperty("html").GetString());
        Assert.Empty(detail.GetProperty("attachments").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _app.ClientAs("someone-else").GetAsync($"/api/scheduled/{id}", Ct)).StatusCode);

        var cancelled = await Json(await _client.DeleteAsync($"/api/scheduled/{id}", Ct));
        Assert.NotNull(cancelled.GetProperty("draft").GetProperty("messageId").GetString());
        var (draft, flags) = Assert.Single(await FindAsync(Scheduler, "Drafts", cancelSubject));
        Assert.True(flags.HasFlag(MessageFlags.Draft));
        Assert.Contains("<b>not</b>", draft.HtmlBody);
        Assert.Equal("rcpt2@example.test", Assert.Single(draft.Cc.Mailboxes).Address);
        Assert.Empty(await ListAsync());
        Assert.Empty(await FindAsync(Recipient, "INBOX", cancelSubject, expect: false));
    }

    [Fact]
    public async Task Times_in_the_past_or_too_far_ahead_are_refused()
    {
        foreach (var sendAt in new DateTimeOffset?[] { null, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2) })
        {
            var response = await _client.PostAsJsonAsync($"{Base}/schedule", new { sendAt, to = Recipient, subject = "x", text = "x" }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await Json400(response)).GetProperty("errors").TryGetProperty("sendAt", out _));
        }

        // The message is checked when it is scheduled, not when it is due.
        var bad = await _client.PostAsJsonAsync($"{Base}/schedule", new { sendAt = DateTimeOffset.UtcNow.AddHours(1), to = "not an address", text = "x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task Unreachable_server_is_retried_then_fails()
    {
        var account = await Json(await _client.GetAsync(Base, Ct));
        await Json(await _client.PutAsJsonAsync(Base, new
        {
            displayName = Scheduler,
            emailAddress = Scheduler,
            username = Scheduler,
            imap = account.GetProperty("imap"),
            // Nothing listens there.
            smtp = new { host = mail.Host, port = 1, security = "none" },
        }, Ct));

        var scheduled = await ScheduleAsync(new { sendAt = DateTimeOffset.UtcNow.AddMilliseconds(500), to = Recipient, subject = Unique("Down"), text = "x" });
        var failed = await WaitForStatusAsync(scheduled.GetProperty("id").GetString()!, "failed");
        Assert.Equal(2, failed.GetProperty("attempts").GetInt32());
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("error").GetString()));
        Assert.DoesNotContain("Trying again", failed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_message_cut_off_by_a_restart_fails_instead_of_going_out_twice()
    {
        var subject = Unique("Restart");
        var scheduled = await ScheduleAsync(new { sendAt = DateTimeOffset.UtcNow.AddHours(1), to = Recipient, subject, text = "x" });
        var id = Guid.Parse(scheduled.GetProperty("id").GetString()!);
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SocklessMail.Api.Data.MailDbContext>();
            await db.ScheduledMessages.IgnoreQueryFilters().Where(m => m.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.Status, ScheduledStatus.Sending), Ct);
        }

        await _app.Services.GetRequiredService<ScheduledSender>().RecoverAsync(Ct);

        var failed = Assert.Single(await ListAsync());
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal("send_interrupted", failed.GetProperty("errorCode").GetString());

        // The user decides: trying again sends it.
        Assert.True((await Json(await _client.PostAsync($"/api/scheduled/{id}/send-now", null, Ct))).GetProperty("sent").GetBoolean());
        Assert.Single(await FindAsync(Recipient, "INBOX", subject));
    }

    [Fact]
    public async Task Other_users_cannot_see_or_touch_them()
    {
        var scheduled = await ScheduleAsync(new { sendAt = DateTimeOffset.UtcNow.AddHours(1), to = Recipient, subject = Unique("Mine"), text = "x" });
        var id = scheduled.GetProperty("id").GetString();
        var other = _app.ClientAs("someone-else");

        Assert.Empty((await Json(await other.GetAsync("/api/scheduled", Ct))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/scheduled/{id}/send-now", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/scheduled/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/scheduled/{id}", new { sendAt = DateTimeOffset.UtcNow.AddHours(3) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Base}/schedule", new { sendAt = DateTimeOffset.UtcNow.AddHours(1), to = Recipient, text = "x" }, Ct)).StatusCode);
        Assert.Single(await ListAsync());
    }

    private static async Task<JsonElement> Json400(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
