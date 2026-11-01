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
using SocklessMail.Api.Assistant;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Summaries of real messages: the conversation found over IMAP, the task scheduled with a (fake) Nextcloud.</summary>
public sealed class AssistantTests : IAsyncLifetime
{
    private const string Owner = "summaries@example.test";

    private readonly GreenMailFixture _mail;
    private readonly FakeTaskProcessing _nextcloud = new();
    private readonly MailApp _app;
    private readonly HttpClient _client;
    private Guid _accountId;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public AssistantTests(GreenMailFixture mail)
    {
        _mail = mail;
        _app = new MailApp(services => services.AddHttpClient<NextcloudClient>().ConfigurePrimaryHttpMessageHandler(() => _nextcloud));
        _client = _app.ClientAs("summaries-user");
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
                await sent.OpenAsync(FolderAccess.ReadWrite, Ct);

                var day = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
                await imap.Inbox.AppendAsync(Mail("bob@example.test", Owner, "Budget", "Can we cut the budget by 10%?", "<a@x>", null, day), cancellationToken: Ct);
                await sent.AppendAsync(Mail(Owner, "bob@example.test", "Re: Budget", "Only by 5%.\n\nOn Monday, Bob wrote:\n> Can we cut the budget by 10%?", "<b@x>", ["<a@x>"], day.AddHours(1)), cancellationToken: Ct);
                await imap.Inbox.AppendAsync(Mail("bob@example.test", Owner, "Re: Budget", "Fine, 5% it is.\n> Only by 5%.", "<c@x>", ["<a@x>", "<b@x>"], day.AddHours(2)), cancellationToken: Ct);
                await imap.Inbox.AppendAsync(Mail("carol@example.test", Owner, "Lunch", "Lunch tomorrow?", "<d@x>", null, day.AddHours(3)), cancellationToken: Ct);
            }
        }

        _accountId = await AddAccountAsync(_client, _mail, Owner, GreenMailFixture.ActionsPassword);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static MimeMessage Mail(string from, string to, string subject, string text, string id, string[]? references, DateTimeOffset date)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = text }, MessageId = id.Trim('<', '>'), Date = date };
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

    private async Task<(string Inbox, string Id)> FindAsync(string subject)
    {
        var folders = (await Json(await _client.GetAsync($"{Base}/folders", Ct))).EnumerateArray();
        var inbox = folders.First(f => f.GetProperty("fullName").GetString() == "INBOX").GetProperty("id").GetString()!;
        var messages = (await Json(await _client.GetAsync($"{Base}/folders/{inbox}/messages?limit=20", Ct))).GetProperty("messages");
        return (inbox, messages.EnumerateArray().First(m => m.GetProperty("subject").GetString() == subject).GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task A_conversation_is_summarized_from_the_inbox_and_sent()
    {
        var (inbox, id) = await FindAsync("Re: Budget");

        var response = await _client.PostAsJsonAsync($"{Base}/folders/{inbox}/messages/{id}/summary", new { timeZone = "Europe/Stockholm" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Json(response);
        Assert.Equal(3, result.GetProperty("messageCount").GetInt32());
        Assert.Equal("scheduled", result.GetProperty("task").GetProperty("status").GetString());

        var schedule = JsonDocument.Parse(Assert.Single(_nextcloud.Schedules)).RootElement;
        Assert.Equal(AssistantService.SummaryType, schedule.GetProperty("type").GetString());
        Assert.Equal("sockless_mail", schedule.GetProperty("appId").GetString());
        Assert.Equal(AssistantService.WebhookPath, schedule.GetProperty("webhookUri").GetString());
        Assert.Equal("AppAPI:sockless_mail:POST", schedule.GetProperty("webhookMethod").GetString());
        var input = schedule.GetProperty("input").GetProperty("input").GetString()!;
        Assert.Equal(
            "Subject: Budget\nFrom: bob@example.test\nDate: 2026-10-20 11:00\n\nCan we cut the budget by 10%?"
            + "\n\n-----\n\nSubject: Re: Budget\nFrom: summaries@example.test\nDate: 2026-10-20 12:00\n\nOnly by 5%."
            + "\n\n-----\n\nSubject: Re: Budget\nFrom: bob@example.test\nDate: 2026-10-20 13:00\n\nFine, 5% it is.",
            input);
    }

    [Fact]
    public async Task One_message_alone_keeps_its_quotes()
    {
        var (inbox, id) = await FindAsync("Re: Budget");

        var result = await Json(await _client.PostAsJsonAsync($"{Base}/folders/{inbox}/messages/{id}/summary", new { scope = "message" }, Ct));

        Assert.Equal(1, result.GetProperty("messageCount").GetInt32());
        var input = JsonDocument.Parse(Assert.Single(_nextcloud.Schedules)).RootElement.GetProperty("input").GetProperty("input").GetString();
        Assert.EndsWith("Fine, 5% it is.\n> Only by 5%.", input);
    }

    [Fact]
    public async Task A_message_outside_any_conversation_is_summarized_alone()
    {
        var (inbox, id) = await FindAsync("Lunch");

        var result = await Json(await _client.PostAsJsonAsync($"{Base}/folders/{inbox}/messages/{id}/summary", new { }, Ct));

        Assert.Equal(1, result.GetProperty("messageCount").GetInt32());
    }

    [Fact]
    public async Task A_reply_is_written_from_the_conversation()
    {
        _nextcloud.Types = """{"core:text2text":{"name":"Free prompt"}}""";
        var (inbox, id) = await FindAsync("Re: Budget");

        var response = await _client.PostAsJsonAsync("/api/assistant/write", new
        {
            instruction = "Agree", tone = "formal", subject = "Re: Re: Budget",
            source = new { accountId = _accountId, folderId = inbox, messageId = id, mode = "reply" }, timeZone = "UTC",
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var schedule = JsonDocument.Parse(Assert.Single(_nextcloud.Schedules)).RootElement;
        Assert.Equal(AssistantService.FreePromptType, schedule.GetProperty("type").GetString());
        var prompt = schedule.GetProperty("input").GetProperty("input").GetString()!;
        Assert.Contains("Write their reply to the last message of the conversation below.", prompt);
        Assert.Contains("Use a formal tone.", prompt);
        Assert.Contains("What the user wants to say: Agree", prompt);
        Assert.EndsWith("Subject: Re: Budget\nFrom: bob@example.test\nDate: 2026-10-20 11:00\n\nFine, 5% it is.", prompt);
        Assert.Contains("Can we cut the budget by 10%?", prompt);
    }

    [Fact]
    public async Task A_message_is_translated_with_its_subject()
    {
        _nextcloud.Types = """{"core:text2text:translate":{"inputShapeEnumValues":{"target_language":[{"name":"Deutsch","value":"de"}]}}}""";
        var (inbox, id) = await FindAsync("Lunch");

        var response = await _client.PostAsJsonAsync($"{Base}/folders/{inbox}/messages/{id}/translation", new { language = "de" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await Json(response)).GetProperty("truncated").GetBoolean());
        var schedule = JsonDocument.Parse(Assert.Single(_nextcloud.Schedules)).RootElement;
        Assert.Equal(AssistantService.TranslateType, schedule.GetProperty("type").GetString());
        Assert.Equal("Lunch\n\nLunch tomorrow?", schedule.GetProperty("input").GetProperty("input").GetString());
        Assert.Equal("de", schedule.GetProperty("input").GetProperty("target_language").GetString());
    }

    [Fact]
    public async Task The_scam_check_reads_the_message_and_its_headers()
    {
        _nextcloud.Types = """{"core:text2text":{}}""";
        var (inbox, id) = await FindAsync("Re: Budget");

        var response = await _client.PostAsJsonAsync($"{Base}/folders/{inbox}/messages/{id}/scam-check", new { language = "Swedish" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var schedule = JsonDocument.Parse(Assert.Single(_nextcloud.Schedules)).RootElement;
        Assert.Equal(AssistantService.FreePromptType, schedule.GetProperty("type").GetString());
        var prompt = schedule.GetProperty("input").GetProperty("input").GetString()!;
        Assert.Contains("in Swedish.", prompt);
        Assert.Contains("From: bob@example.test\n", prompt);
        Assert.Contains("Subject: Re: Budget\n", prompt);
        Assert.Contains("Links (the text shown → where the link leads):\n- none\n", prompt);
        Assert.Contains("Fine, 5% it is.", prompt);
    }

    [Fact]
    public async Task Without_a_provider_nothing_is_scheduled()
    {
        _nextcloud.Types = "{}";
        var (inbox, id) = await FindAsync("Lunch");

        var response = await _client.PostAsJsonAsync($"{Base}/folders/{inbox}/messages/{id}/summary", new { }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("assistant_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Empty(_nextcloud.Schedules);
    }

    /// <summary>Offers a summary provider and records scheduled tasks; every other OCS call succeeds empty.</summary>
    private sealed class FakeTaskProcessing : HttpMessageHandler
    {
        public ConcurrentQueue<string> Schedules { get; } = new();

        public string Types { get; set; } = """{"core:text2text:summary":{"name":"Summarize"}}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var data = "[]";
            if (path.EndsWith("/taskprocessing/tasktypes"))
            {
                data = """{"types":""" + Types + "}";
            }
            else if (path.EndsWith("/taskprocessing/schedule"))
            {
                Schedules.Enqueue(await request.Content!.ReadAsStringAsync(ct));
                data = """{"task":{"id":11,"type":"core:text2text:summary","status":"STATUS_SCHEDULED","userId":"summaries-user","appId":"sockless_mail","output":null}}""";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ocs":{"meta":{"status":"ok"},"data":""" + data + "}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
