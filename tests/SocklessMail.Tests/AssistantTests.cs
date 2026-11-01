using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using MimeKit;
using NodaTime;
using SocklessMail.Api.Assistant;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Sync;

namespace SocklessMail.Tests;

/// <summary>The Nextcloud Assistant: what goes into its tasks, and the task endpoints against a fake Nextcloud.</summary>
public sealed class AssistantTests : IDisposable
{
    private const string TaskTypes = """
        {"ocs":{"meta":{"status":"ok"},"data":{"types":{
          "core:text2text:summary":{"name":"Summarize","inputShape":{"input":{"type":"Text"}}},
          "core:text2text":{"name":"Free text to text prompt","inputShape":{"input":{"type":"Text"}}}}}}}
        """;

    private readonly TestApp _app = new();
    private readonly HttpClient _alice;

    public AssistantTests() => _alice = _app.ClientAs("alice");

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    private static readonly DateTimeZone Stockholm = DateTimeZoneProviders.Tzdb["Europe/Stockholm"];

    private static MessageText Message(string subject, string text, int hour, string from = "ann@example.test") =>
        new(subject, new MailboxAddress("Ann", from), new DateTimeOffset(2026, 10, 30, hour, 0, 0, TimeSpan.Zero), text, $"<{subject}-{hour}@x>");

    private static string Task(long id, string status, string appId = "sockless_mail", string? output = null, string user = "alice") =>
        JsonSerializer.Serialize(new
        {
            ocs = new
            {
                meta = new { status = "ok" },
                data = new
                {
                    task = new
                    {
                        id, type = "core:text2text:summary", status, userId = user, appId,
                        output = output is null ? null : new { output },
                        progress = 0.5, completionExpectedAt = 1793553810, userFacingErrorMessage = (string?)null,
                    },
                },
            },
        });

    [Fact]
    public void A_single_message_keeps_its_quoted_history()
    {
        var text = AssistantInput.Format([Message("Plans", "Sure.\r\n\r\nOn Monday, Bob wrote:\r\n> Lunch on Friday?", 8)], Stockholm);

        Assert.Equal("Subject: Plans\nFrom: Ann <ann@example.test>\nDate: 2026-10-30 09:00\n\nSure.\n\nOn Monday, Bob wrote:\n> Lunch on Friday?", text);
    }

    [Fact]
    public void A_conversation_leaves_out_what_each_message_quotes()
    {
        var text = AssistantInput.Format(
        [
            Message("Lunch", "Lunch on Friday?", 7, "bob@example.test"),
            Message("Re: Lunch", "Sure, at noon.\n\nOn Thu, 30 Oct 2026 at 08:00, Bob <bob@example.test>\nwrote:\n> Lunch on Friday?\n>\n", 8),
            Message("RE: Lunch", "Great.\n\n________________________________\nFrom: Ann\nSent: Thursday\nSure, at noon.", 9, "bob@example.test"),
            Message("RE: Lunch", "See you.\n-----Original Message-----\nFrom: Bob\nGreat.", 10),
        ], DateTimeZone.Utc);

        Assert.Equal(
            "Subject: Lunch\nFrom: Ann <bob@example.test>\nDate: 2026-10-30 07:00\n\nLunch on Friday?"
            + "\n\n-----\n\nSubject: Re: Lunch\nFrom: Ann <ann@example.test>\nDate: 2026-10-30 08:00\n\nSure, at noon."
            + "\n\n-----\n\nSubject: RE: Lunch\nFrom: Ann <bob@example.test>\nDate: 2026-10-30 09:00\n\nGreat."
            + "\n\n-----\n\nSubject: RE: Lunch\nFrom: Ann <ann@example.test>\nDate: 2026-10-30 10:00\n\nSee you.",
            text);
    }

    [Fact]
    public void Forwarded_content_and_outlook_headers_differ()
    {
        Assert.Equal("FYI\n---------- Forwarded message ----------\nFrom: Bob\nDate: Monday\nThe report.",
            AssistantInput.WithoutQuotes("FYI\n---------- Forwarded message ----------\nFrom: Bob\nDate: Monday\nThe report."));
        Assert.Equal("Thanks!", AssistantInput.WithoutQuotes("Thanks!\n\nFrom: Bob <bob@example.test>\nSent: Monday\nTo: Ann\nOld text"));
        Assert.Equal("Line one\nFrom: the start, it worked.", AssistantInput.WithoutQuotes("Line one\nFrom: the start, it worked."));
    }

    [Fact]
    public void A_long_conversation_drops_its_oldest_messages_first()
    {
        var big = new string('x', AssistantInput.MaxMessageLength);
        var messages = Enumerable.Range(0, 10).Select(i => Message($"M{i}", big, i)).ToList();

        var text = AssistantInput.Format(messages, DateTimeZone.Utc);

        Assert.True(text.Length <= AssistantInput.MaxInputLength);
        Assert.Contains("Subject: M9", text);
        Assert.DoesNotContain("Subject: M0\n", text);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public async Task The_assistant_is_offered_when_nextcloud_has_a_provider()
    {
        _app.Nextcloud.Responses["taskprocessing/tasktypes"] = (HttpStatusCode.OK, TaskTypes);

        var offered = await _alice.GetFromJsonAsync<JsonElement>("/api/assistant", Ct);

        Assert.True(offered.GetProperty("available").GetBoolean());
        Assert.True(offered.GetProperty("summarize").GetBoolean());
        var call = Assert.Single(_app.Nextcloud.Requests, r => r.PathAndQuery.Contains("tasktypes"));
        Assert.Contains("alice", call.Headers["AUTHORIZATION-APP-API"].FromBase64());
    }

    [Fact]
    public async Task Without_a_provider_or_with_the_admin_switch_off_nothing_is_offered()
    {
        _app.Nextcloud.Responses["taskprocessing/tasktypes"] = (HttpStatusCode.OK, """{"ocs":{"meta":{"status":"ok"},"data":{"types":{}}}}""");
        Assert.False((await _alice.GetFromJsonAsync<JsonElement>("/api/assistant", Ct)).GetProperty("available").GetBoolean());

        _app.Nextcloud.Responses["cloud/user"] = (HttpStatusCode.OK, """{"ocs":{"meta":{"status":"ok"},"data":{"id":"root","groups":["admin"]}}}""");
        var root = _app.ClientAs("root");
        Assert.True((await root.GetFromJsonAsync<JsonElement>("/api/admin/settings", Ct)).GetProperty("assistant").GetBoolean());
        (await root.PutAsJsonAsync("/api/admin/settings", new { assistant = false }, Ct)).EnsureSuccessStatusCode();

        _app.Nextcloud.Responses["taskprocessing/tasktypes"] = (HttpStatusCode.OK, TaskTypes);
        var bob = _app.ClientAs("bob");
        Assert.False((await bob.GetFromJsonAsync<JsonElement>("/api/assistant", Ct)).GetProperty("available").GetBoolean());
        // The other admin setting kept its value.
        Assert.True((await root.GetFromJsonAsync<JsonElement>("/api/admin/settings", Ct)).GetProperty("domainAgeChecks").GetBoolean());
    }

    [Fact]
    public async Task A_task_is_read_as_the_user_and_its_text_returned()
    {
        _app.Nextcloud.Responses["taskprocessing/task/42"] = (HttpStatusCode.OK, Task(42, "STATUS_SUCCESSFUL", output: "- It is about lunch"));

        var task = await _alice.GetFromJsonAsync<JsonElement>("/api/assistant/tasks/42", Ct);

        Assert.Equal(42, task.GetProperty("id").GetInt64());
        Assert.Equal("successful", task.GetProperty("status").GetString());
        Assert.Equal("- It is about lunch", task.GetProperty("output").GetString());
        Assert.Equal(0.5, task.GetProperty("progress").GetDouble());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793553810), task.GetProperty("expectedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Tasks_of_other_apps_or_unknown_ones_are_not_found()
    {
        _app.Nextcloud.Responses["taskprocessing/task/7"] = (HttpStatusCode.OK, Task(7, "STATUS_SUCCESSFUL", appId: "assistant", output: "secret"));
        _app.Nextcloud.Responses["taskprocessing/task/8"] = (HttpStatusCode.NotFound, "{}");

        Assert.Equal(HttpStatusCode.NotFound, (await _alice.GetAsync("/api/assistant/tasks/7", Ct)).StatusCode);
        var missing = await _alice.GetAsync("/api/assistant/tasks/8", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _alice.PostAsync("/api/assistant/tasks/7/cancel", null, Ct)).StatusCode);
        Assert.DoesNotContain(_app.Nextcloud.Requests, r => r.PathAndQuery.Contains("/cancel"));
    }

    [Fact]
    public async Task Cancelling_asks_nextcloud_to_cancel()
    {
        _app.Nextcloud.Responses["taskprocessing/task/9"] = (HttpStatusCode.OK, Task(9, "STATUS_SCHEDULED"));

        (await _alice.PostAsync("/api/assistant/tasks/9/cancel", null, Ct)).EnsureSuccessStatusCode();

        var cancel = Assert.Single(_app.Nextcloud.Requests, r => r.PathAndQuery.StartsWith("/ocs/v2.php/taskprocessing/tasks/9/cancel"));
        Assert.Equal(HttpMethod.Post, cancel.Method);
    }

    [Fact]
    public async Task Too_many_requests_say_so()
    {
        _app.Nextcloud.Responses["taskprocessing/task/5"] = (HttpStatusCode.TooManyRequests, "{}");

        var response = await _alice.GetAsync("/api/assistant/tasks/5", Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("rate_limited", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_webhook_nudges_only_the_tasks_owner()
    {
        await using var alice = _app.HubAs("alice");
        await using var bob = _app.HubAs("bob");
        var aliceGot = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bobGot = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.On<JsonElement>(MailHub.AssistantTaskChanged, nudge => aliceGot.TrySetResult(nudge));
        bob.On<JsonElement>(MailHub.AssistantTaskChanged, nudge => bobGot.TrySetResult(nudge));
        await alice.StartAsync(Ct);
        await bob.StartAsync(Ct);

        // Another app's task, and a task the caller does not own, are ignored.
        var other = JsonDocument.Parse(Task(1, "STATUS_SUCCESSFUL", appId: "assistant")).RootElement.GetProperty("ocs").GetProperty("data");
        (await _app.ClientAs("bob").PostAsJsonAsync(AssistantService.WebhookPath, other, Ct)).EnsureSuccessStatusCode();
        var notBobs = JsonDocument.Parse(Task(2, "STATUS_SUCCESSFUL")).RootElement.GetProperty("ocs").GetProperty("data");
        (await _app.ClientAs("bob").PostAsJsonAsync(AssistantService.WebhookPath, notBobs, Ct)).EnsureSuccessStatusCode();

        var done = JsonDocument.Parse(Task(3, "STATUS_SUCCESSFUL", output: "text")).RootElement.GetProperty("ocs").GetProperty("data");
        (await _alice.PostAsJsonAsync(AssistantService.WebhookPath, done, Ct)).EnsureSuccessStatusCode();

        var nudge = await aliceGot.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(3, nudge.GetProperty("id").GetInt64());
        // Only the id: the browser asks for the task itself.
        Assert.Single(nudge.EnumerateObject());
        await System.Threading.Tasks.Task.Delay(300, Ct);
        Assert.False(bobGot.Task.IsCompleted);
    }

    [Fact]
    public async Task The_webhook_needs_appapi_credentials()
    {
        var response = await _app.CreateClient().PostAsJsonAsync(AssistantService.WebhookPath, new { task = new { id = 1 } }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

internal static class Base64Text
{
    public static string FromBase64(this string value) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
