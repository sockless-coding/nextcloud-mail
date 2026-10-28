using System.Net;
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

/// <summary>Preview text in message lists, and the Unread, Flagged and Attachments filters.</summary>
public sealed class FilterTests(GreenMailFixture greenMail, DovecotFixture dovecot) : IDisposable
{
    private const string Inbox = "SU5CT1g";
    private const string GreenMailUser = "filters@example.test";
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly MailApp _app = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    public static TheoryData<string> Servers() => ["dovecot", "greenmail"];

    private static MimeMessage Message(string subject, string body, DateTimeOffset? date = null)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = body } };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("", "someone@example.test"));
        if (date is { } d)
        {
            message.Date = d;
        }

        return message;
    }

    /// <summary>A real attachment, or (with <paramref name="file"/> false) a multipart/mixed message with only text.</summary>
    private static MimeMessage Mixed(string subject, bool file)
    {
        var builder = new BodyBuilder { TextBody = $"Body of {subject}" };
        if (file)
        {
            builder.Attachments.Add("report.pdf", "%PDF-1.4 test"u8.ToArray(), new ContentType("application", "pdf"));
        }

        var message = Message(subject, "");
        message.Body = file ? builder.ToMessageBody() : new Multipart("mixed") { new TextPart("plain") { Text = $"Body of {subject}" } };
        return message;
    }

    private async Task<ImapClient> ConnectAsync(string server, string user)
    {
        if (server == "dovecot")
        {
            return await dovecot.ConnectAsync(user);
        }

        var client = new ImapClient();
        await client.ConnectAsync(greenMail.Host, greenMail.Imap, SecureSocketOptions.None, Ct);
        await client.AuthenticateAsync(user, GreenMailFixture.ActionsPassword, Ct);
        return client;
    }

    private async Task<Guid> AddAsync(HttpClient client, string server, string user) =>
        server == "dovecot"
            ? await AddAccountAsync(client, dovecot, user)
            : await AddAccountAsync(client, greenMail, user, GreenMailFixture.ActionsPassword);

    private static List<string?> Subjects(JsonElement page) =>
        page.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString()).ToList();

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Lists_carry_previews_and_filters_search_the_whole_folder(string server)
    {
        var user = server == "dovecot" ? DovecotFixture.NewUser() : GreenMailUser;
        var marker = $"f{Guid.NewGuid():N}"[..10];
        using (var imap = await ConnectAsync(server, user))
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            await imap.Inbox.AppendAsync(Message($"Plain {marker}", "Hello Alice,\r\n\r\nthe   numbers are in.\r\n"), MessageFlags.None, Ct);
            await imap.Inbox.AppendAsync(Message($"Read {marker}", "Already read"), MessageFlags.Seen, Ct);
            await imap.Inbox.AppendAsync(Message($"Starred {marker}", "Important"), MessageFlags.Seen | MessageFlags.Flagged, Ct);
            await imap.Inbox.AppendAsync(Mixed($"With file {marker}", file: true), MessageFlags.Seen, Ct);
            await imap.Inbox.AppendAsync(Mixed($"Mixed text {marker}", file: false), MessageFlags.Seen, Ct);
        }

        var client = _app.ClientAs($"filters-{server}");
        var account = await AddAsync(client, server, user);

        var list = await Json(await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/messages", Ct));
        var plain = list.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("subject").GetString() == $"Plain {marker}");
        Assert.Equal("Hello Alice, the numbers are in.", plain.GetProperty("preview").GetString());

        async Task<JsonElement> Filter(string filter, string query = "") =>
            await Json(await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/search?filter={filter}{(query == "" ? "" : $"&q={query}")}", Ct));

        var unread = await Filter("unread");
        Assert.Equal([$"Plain {marker}"], Subjects(unread));
        Assert.Equal(1, unread.GetProperty("total").GetInt32());
        Assert.Equal("Hello Alice, the numbers are in.", unread.GetProperty("messages")[0].GetProperty("preview").GetString());

        Assert.Equal([$"Starred {marker}"], Subjects(await Filter("flagged")));

        // multipart/mixed without a file is a candidate of the header search, but not a match.
        var attachments = await Filter("attachments");
        Assert.Equal([$"With file {marker}"], Subjects(attachments));
        Assert.True(attachments.GetProperty("messages")[0].GetProperty("hasAttachments").GetBoolean());

        // A filter and words together; no match is an empty page, not an error.
        Assert.Equal([$"Starred {marker}"], Subjects(await Filter("flagged", "Starred")));
        // Not "Plain": TEXT also searches the headers, and every message says text/plain.
        Assert.Empty(Subjects(await Filter("flagged", "Hello")));

        var invalid = await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/search?filter=starred", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var nothing = await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/search", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
    }

    [Fact]
    public async Task Rows_cached_without_a_preview_get_one_on_the_next_sync()
    {
        var user = DovecotFixture.NewUser();
        using (var imap = await dovecot.ConnectAsync(user))
        {
            await imap.Inbox.AppendAsync(Message("Old row", "Text from before previews"), MessageFlags.None, Ct);
        }

        var client = _app.ClientAs("backfill");
        var account = await AddAsync(client, "dovecot", user);
        await Json(await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/messages", Ct));

        // As if cached by an older version of the app.
        using (var scope = _app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MailDbContext>().Messages.IgnoreQueryFilters()
                .Where(m => m.OwnerUserId == "backfill")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Preview, (string?)null), Ct);
        }

        var list = await Json(await client.GetAsync($"/api/accounts/{account}/folders/{Inbox}/messages", Ct));
        Assert.Equal("Text from before previews", list.GetProperty("messages")[0].GetProperty("preview").GetString());
    }

    [Fact]
    public async Task All_inboxes_are_filtered_on_the_servers_and_merged_by_date()
    {
        // Unread messages in both inboxes, interleaved by date, plus read ones that must not show.
        async Task<string> MailboxAsync(string prefix, int[] hours)
        {
            var user = DovecotFixture.NewUser();
            using var imap = await dovecot.ConnectAsync(user);
            foreach (var hour in hours)
            {
                // Appended in date order, so UIDs follow the dates as for delivered mail.
                await imap.Inbox.AppendAsync(Message($"{prefix}{hour}", "unread", Start.AddHours(hour)), MessageFlags.None, Ct);
                await imap.Inbox.AppendAsync(Message($"{prefix}{hour} read", "read", Start.AddHours(hour).AddMinutes(1)), MessageFlags.Seen, Ct);
            }

            return user;
        }

        var client = _app.ClientAs("unified-filter");
        await AddAccountAsync(client, dovecot, await MailboxAsync("A", [1, 4, 5]));
        await AddAccountAsync(client, dovecot, await MailboxAsync("B", [2, 3, 6]));

        var subjects = new List<string?>();
        string? before = null;
        var pages = 0;
        do
        {
            var page = await Json(await client.GetAsync($"/api/unified/filter?filter=unread&limit=2{(before is null ? "" : $"&before={before}")}", Ct));
            Assert.Empty(page.GetProperty("errors").EnumerateArray());
            if (pages == 0)
            {
                Assert.Equal(6, page.GetProperty("total").GetInt32());
            }

            subjects.AddRange(page.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("message").GetProperty("subject").GetString()));
            before = page.GetProperty("nextBefore").GetString();
            pages++;
        }
        while (before is not null && pages < 10);

        Assert.Equal(["B6", "A5", "A4", "B3", "B2", "A1"], subjects);

        var invalid = await client.GetAsync("/api/unified/filter", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
