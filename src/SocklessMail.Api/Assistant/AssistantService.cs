using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MimeKit;
using NodaTime;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Compose;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Assistant;

/// <summary>
/// What the user can ask the Assistant for; nothing when the admin turned it off or no provider
/// handles it. <see cref="Rewrite"/> lists the <see cref="RewriteAction"/>s on offer.
/// <see cref="Tones"/> are the provider's own tones for rewriting, when it has a task for that
/// (otherwise <see cref="AssistantPrompts.Tones"/> apply); <see cref="Languages"/> are the
/// translation targets (also for translating messages in the reader, when Rewrite has "translate").
/// <see cref="CheckScam"/>: the reader's phishing and scam check.
/// </summary>
public sealed record AssistantDto(
    bool Available, bool Summarize, bool Write, IReadOnlyList<string> Rewrite,
    IReadOnlyList<AssistantOption> Tones, IReadOnlyList<AssistantOption> Languages, bool CheckScam)
{
    public static readonly AssistantDto None = new(false, false, false, [], [], [], false);
}

/// <summary>
/// A Nextcloud TaskProcessing task of this app. <see cref="Status"/> is "scheduled", "running",
/// "successful", "failed", "cancelled" or "unknown"; <see cref="Output"/> is the generated text
/// once successful, <see cref="Error"/> the provider's message for the user when it failed.
/// </summary>
public sealed record AssistantTaskDto(long Id, string Status, double? Progress, string? Output, string? Error, DateTimeOffset? ExpectedAt);

/// <param name="Scope">"conversation" (default) or "message".</param>
/// <param name="TimeZone">The user's IANA time zone, for the dates in the task's input.</param>
public sealed record SummaryInput(string? Scope, string? TimeZone);

/// <summary>The scheduled summary and how many messages went into it.</summary>
public sealed record SummaryDto(AssistantTaskDto Task, int MessageCount);

/// <summary>The message a reply or forward is written to (its account must be the user's).</summary>
public sealed record WriteSource(Guid AccountId, string FolderId, string MessageId, ReplyMode Mode);

/// <param name="Instruction">What the message should say; may be empty for a reply.</param>
/// <param name="Tone">One of <see cref="AssistantPrompts.Tones"/>; "neutral" when left out.</param>
/// <param name="Current">The user's own text so far, to build on.</param>
public sealed record WriteInput(
    string? Instruction, string? Tone, string? Subject, string? To, string? SenderName, string? Current, WriteSource? Source, string? TimeZone);

/// <param name="Tone">For <see cref="RewriteAction.Tone"/>: a value of <see cref="AssistantDto.Tones"/>, or of <see cref="AssistantPrompts.Tones"/> when that is empty.</param>
/// <param name="Language">For <see cref="RewriteAction.Translate"/>: a value of <see cref="AssistantDto.Languages"/>.</param>
/// <param name="Instruction">For <see cref="RewriteAction.Custom"/>.</param>
/// <param name="Language">A value of <see cref="AssistantDto.Languages"/>.</param>
public sealed record MessageTranslationInput(string? Language);

/// <summary>The scheduled translation; <see cref="Truncated"/> when only the start of a long message went in.</summary>
public sealed record MessageTranslationDto(AssistantTaskDto Task, bool Truncated);

/// <param name="Language">The user's language, by its English name, for the reasons ("German"); English when unusable.</param>
public sealed record ScamCheckInput(string? Language);

public sealed record RewriteInput(string? Text, RewriteAction? Action, string? Tone, string? Language, string? Instruction);

public sealed class AssistantException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The Nextcloud Assistant's tasks (TaskProcessing OCS API), run as the user with the provider the
/// admin set up. Nextcloud calls <see cref="WebhookPath"/> when a task ends; the browser also asks
/// <see cref="GetTaskAsync"/>, which is all it has without the push channel.
/// </summary>
public sealed partial class AssistantService(
    NextcloudClient nextcloud, AssistantTypes types, AppSettings settings, MailboxService mailbox, AccountService accounts, AppApiOptions options)
{
    /// <summary>Called by Nextcloud (through AppAPI, with the task's user) when one of our tasks ends.</summary>
    public const string WebhookPath = "/assistant/task-done";

    public const string SummaryType = "core:text2text:summary";
    public const string FreePromptType = "core:text2text";
    public const string ChangeToneType = "core:text2text:changetone";
    public const string ImproveType = "core:text2text:improve";
    public const string TranslateType = "core:text2text:translate";

    /// <summary>The newest messages of a conversation that go into its summary.</summary>
    public const int MaxConversationMessages = 10;

    /// <summary>The newest messages of a conversation a reply is written from.</summary>
    public const int MaxReplyContextMessages = 5;

    public async Task<AssistantDto> GetAsync(string userId, CancellationToken ct)
    {
        if (!await settings.AssistantAsync(ct))
        {
            return AssistantDto.None;
        }

        var available = await types.GetAsync(nextcloud, userId, ct);
        var free = available.ContainsKey(FreePromptType);
        var summarize = free || available.ContainsKey(SummaryType);
        var rewrite = RewriteActions(available).Select(a => JsonNamingPolicy.CamelCase.ConvertName(a.ToString())).ToList();
        var tones = available.TryGetValue(ChangeToneType, out var changeTone) ? EnumValues(changeTone, "tone") : [];
        var languages = available.TryGetValue(TranslateType, out var translate) ? EnumValues(translate, "target_language")
            : free ? AssistantPrompts.Languages
            : [];
        return new AssistantDto(summarize || free || rewrite.Count > 0, summarize, free, rewrite, tones, languages, free);
    }

    /// <summary>A reply, the note for a forward, or a new message, from what the user says it should say.</summary>
    public async Task<AssistantTaskDto> WriteAsync(string userId, WriteInput input, CancellationToken ct)
    {
        await RequireEnabledAsync(ct);
        if (!(await types.GetAsync(nextcloud, userId, ct)).ContainsKey(FreePromptType))
        {
            throw Unavailable();
        }

        var tone = string.IsNullOrWhiteSpace(input.Tone) ? "neutral" : input.Tone;
        if (!AssistantPrompts.Tones.Contains(tone))
        {
            throw Invalid("tone", "Choose one of the offered tones.");
        }

        CheckLength("instruction", input.Instruction, AssistantPrompts.MaxInstructionLength);
        CheckLength("current", input.Current, AssistantPrompts.MaxRewriteLength);
        var mode = input.Source?.Mode is ReplyMode.Reply or ReplyMode.ReplyAll or ReplyMode.Forward ? input.Source.Mode : (ReplyMode?)null;
        if (mode is not (ReplyMode.Reply or ReplyMode.ReplyAll) && string.IsNullOrWhiteSpace(input.Instruction))
        {
            throw Invalid("instruction", "Say what the message should be about.");
        }

        string? conversation = null;
        if (mode is not null)
        {
            var source = input.Source!;
            var account = await accounts.GetAsync(userId, source.AccountId, ct) ?? throw NotFound();
            var messages = await mailbox.GetConversationTextAsync(
                account, source.FolderId, source.MessageId, mode == ReplyMode.Forward ? 1 : MaxReplyContextMessages, ct);
            conversation = AssistantInput.Format(messages, Zone(input.TimeZone));
        }

        var prompt = AssistantPrompts.Write(mode, input.Instruction, tone, input.Subject, input.To, input.SenderName, input.Current, conversation);
        return await ScheduleAsync(userId, FreePromptType, new Dictionary<string, object> { ["input"] = prompt }, "write", ct);
    }

    /// <summary>The text rewritten as asked: with the provider's task for it when there is one, otherwise with the free prompt.</summary>
    public async Task<AssistantTaskDto> RewriteAsync(string userId, RewriteInput input, CancellationToken ct)
    {
        await RequireEnabledAsync(ct);
        var available = await types.GetAsync(nextcloud, userId, ct);
        var action = input.Action ?? throw Invalid("action", "Say how the text should change.");
        if (!RewriteActions(available).Contains(action))
        {
            throw Unavailable();
        }

        var text = input.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw Invalid("text", "There is no text to rewrite.");
        }

        CheckLength("text", text, AssistantPrompts.MaxRewriteLength);
        CheckLength("instruction", input.Instruction, AssistantPrompts.MaxInstructionLength);
        string? tone = null, language = null;
        switch (action)
        {
            case RewriteAction.Tone:
                tone = input.Tone;
                var hasChangeTone = available.TryGetValue(ChangeToneType, out var changeTone);
                var tones = hasChangeTone
                    ? EnumValues(changeTone, "tone").Select(t => t.Value)
                    : AssistantPrompts.Tones.Where(t => t != "neutral");
                if (tone is null || !tones.Contains(tone))
                {
                    throw Invalid("tone", "Choose one of the offered tones.");
                }

                if (hasChangeTone)
                {
                    return await ScheduleAsync(userId, ChangeToneType, new Dictionary<string, object> { ["input"] = text, ["tone"] = tone }, "rewrite", ct);
                }

                break;
            case RewriteAction.Translate:
                return await TranslateAsync(userId, available, text, input.Language, "rewrite", ct);
            case RewriteAction.Custom when string.IsNullOrWhiteSpace(input.Instruction):
                throw Invalid("instruction", "Say how the text should change.");
        }

        var instructions = AssistantPrompts.Instructions(action, tone, language, input.Instruction);
        return available.ContainsKey(ImproveType)
            ? await ScheduleAsync(userId, ImproveType, new Dictionary<string, object> { ["input"] = text, ["instructions"] = instructions }, "rewrite", ct)
            : await ScheduleAsync(userId, FreePromptType, new Dictionary<string, object> { ["input"] = AssistantPrompts.Rewrite(instructions, text) }, "rewrite", ct);
    }

    /// <summary>A message (subject and text) translated, for reading.</summary>
    public async Task<MessageTranslationDto> TranslateMessageAsync(
        string userId, MailAccount account, string folderId, string messageId, MessageTranslationInput input, CancellationToken ct)
    {
        await RequireEnabledAsync(ct);
        var available = await types.GetAsync(nextcloud, userId, ct);
        if (!RewriteActions(available).Contains(RewriteAction.Translate))
        {
            throw Unavailable();
        }

        CheckLanguage(available, input.Language);
        var message = await mailbox.GetMessageTextAsync(account, folderId, messageId, ct);
        var text = message.Text.Replace("\r\n", "\n").Trim();
        var truncated = text.Length > AssistantPrompts.MaxRewriteLength;
        if (truncated)
        {
            text = text[..AssistantPrompts.MaxRewriteLength];
        }

        var source = string.IsNullOrWhiteSpace(message.Subject) ? text : $"{message.Subject.Trim()}\n\n{text}";
        return new MessageTranslationDto(await TranslateAsync(userId, available, source, input.Language, "translate", ct), truncated);
    }

    /// <summary>The Assistant's judgement whether a message is phishing or a scam (JSON, see <see cref="ScamEvidence"/>).</summary>
    public async Task<AssistantTaskDto> CheckScamAsync(
        string userId, MailAccount account, string folderId, string messageId, ScamCheckInput input, CancellationToken ct)
    {
        await RequireEnabledAsync(ct);
        if (!(await types.GetAsync(nextcloud, userId, ct)).ContainsKey(FreePromptType))
        {
            throw Unavailable();
        }

        var message = await mailbox.GetMessageAsync(account, folderId, messageId, false, ct);
        var raw = await mailbox.GetHeadersAsync(account, folderId, messageId, ct);
        var headers = HeaderList.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw.Headers)), ct);
        var language = input.Language is { Length: > 0 and <= 40 } l && LanguageName().IsMatch(l) ? l : "English";
        var prompt = ScamEvidence.Build(message, headers, language);
        return await ScheduleAsync(userId, FreePromptType, new Dictionary<string, object> { ["input"] = prompt }, "scam", ct);
    }

    /// <summary>Text into one of the offered languages: the provider's translation task when there is one, otherwise the free prompt.</summary>
    private async Task<AssistantTaskDto> TranslateAsync(
        string userId, IReadOnlyDictionary<string, JsonElement> available, string text, string? language, string customId, CancellationToken ct)
    {
        var hasTranslate = CheckLanguage(available, language);
        if (hasTranslate)
        {
            return await ScheduleAsync(userId, TranslateType, new Dictionary<string, object>
            {
                ["input"] = text, ["origin_language"] = "detect_language", ["target_language"] = language!,
            }, customId, ct);
        }

        var instructions = AssistantPrompts.Instructions(RewriteAction.Translate, null, language, null);
        return await ScheduleAsync(userId, FreePromptType, new Dictionary<string, object> { ["input"] = AssistantPrompts.Rewrite(instructions, text) }, customId, ct);
    }

    /// <summary>Throws unless <paramref name="language"/> is offered; true when the provider's translation task offers it.</summary>
    private static bool CheckLanguage(IReadOnlyDictionary<string, JsonElement> available, string? language)
    {
        var hasTranslate = available.TryGetValue(TranslateType, out var translate);
        var languages = hasTranslate ? EnumValues(translate, "target_language") : AssistantPrompts.Languages;
        if (language is null || languages.All(l => l.Value != language))
        {
            throw Invalid("language", "Choose one of the offered languages.");
        }

        return hasTranslate;
    }

    [GeneratedRegex(@"^[\p{L} ()-]+$")]
    private static partial Regex LanguageName();

    /// <summary>The rewrites some task type of the user's Nextcloud can do; the free prompt does them all.</summary>
    private static IEnumerable<RewriteAction> RewriteActions(IReadOnlyDictionary<string, JsonElement> available)
    {
        var free = available.ContainsKey(FreePromptType);
        var improve = free || available.ContainsKey(ImproveType);
        if (free || available.ContainsKey(ChangeToneType))
        {
            yield return RewriteAction.Tone;
        }

        if (improve)
        {
            yield return RewriteAction.Fix;
            yield return RewriteAction.Shorter;
            yield return RewriteAction.Clearer;
        }

        if (free || available.ContainsKey(TranslateType))
        {
            yield return RewriteAction.Translate;
        }

        if (improve)
        {
            yield return RewriteAction.Custom;
        }
    }

    /// <summary>A task type's choices for one of its inputs ({ name, value }), as its provider offers them.</summary>
    private static IReadOnlyList<AssistantOption> EnumValues(JsonElement type, string input)
    {
        if (type.ValueKind != JsonValueKind.Object
            || !type.TryGetProperty("inputShapeEnumValues", out var values) || values.ValueKind != JsonValueKind.Object
            || !values.TryGetProperty(input, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray()
            .Where(o => o.ValueKind == JsonValueKind.Object
                        && o.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        && o.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
            .Select(o => new AssistantOption(o.GetProperty("name").GetString()!, o.GetProperty("value").GetString()!))
            .ToList();
    }

    private static void CheckLength(string field, string? value, int max)
    {
        if (value is { Length: > 0 } && value.Length > max)
        {
            throw Invalid(field, $"This is too long for the Assistant (at most {max} characters).");
        }
    }

    private static DateTimeZone Zone(string? timeZone) =>
        timeZone is { Length: > 0 } tz ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz) ?? DateTimeZone.Utc : DateTimeZone.Utc;

    public async Task<SummaryDto> SummarizeAsync(
        string userId, MailAccount account, string folderId, string messageId, SummaryInput input, CancellationToken ct)
    {
        await RequireEnabledAsync(ct);
        var available = await types.GetAsync(nextcloud, userId, ct);
        var type = available.ContainsKey(SummaryType) ? SummaryType
            : available.ContainsKey(FreePromptType) ? FreePromptType
            : throw Unavailable();

        var conversation = input.Scope != "message";
        var messages = await mailbox.GetConversationTextAsync(account, folderId, messageId, conversation ? MaxConversationMessages : 1, ct);
        var text = AssistantInput.Format(messages, Zone(input.TimeZone));
        if (type == FreePromptType)
        {
            text = SummaryPrompt(messages.Count) + text;
        }

        var task = await ScheduleAsync(userId, type, new Dictionary<string, object> { ["input"] = text }, "summary", ct);
        return new SummaryDto(task, messages.Count);
    }

    public async Task<AssistantTaskDto> GetTaskAsync(string userId, long taskId, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, $"taskprocessing/task/{taskId}", null, userId, ct);
        return ToOwnTask(data);
    }

    public async Task<AssistantTaskDto> CancelAsync(string userId, long taskId, CancellationToken ct)
    {
        // Through GetTaskAsync first: only this app's tasks.
        await GetTaskAsync(userId, taskId, ct);
        await CallAsync(HttpMethod.Post, $"taskprocessing/tasks/{taskId}/cancel", null, userId, ct);
        return await GetTaskAsync(userId, taskId, ct);
    }

    /// <summary>Whether a task Nextcloud reports to <see cref="WebhookPath"/> is one of ours.</summary>
    public bool IsOwnTask(JsonElement task) =>
        task.ValueKind == JsonValueKind.Object
        && task.TryGetProperty("appId", out var appId) && appId.GetString() == options.AppId
        && task.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number;

    private async Task<AssistantTaskDto> ScheduleAsync(
        string userId, string type, Dictionary<string, object> input, string customId, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Post, "taskprocessing/schedule", new
        {
            input,
            type,
            appId = options.AppId,
            customId,
            webhookUri = WebhookPath,
            webhookMethod = $"AppAPI:{options.AppId}:POST",
        }, userId, ct);
        return ToOwnTask(data);
    }

    private async Task<JsonElement> CallAsync(HttpMethod method, string path, object? body, string userId, CancellationToken ct)
    {
        try
        {
            return await nextcloud.OcsAsync(method, path, body, userId, ct);
        }
        catch (NextcloudApiException e)
        {
            throw e.StatusCode switch
            {
                StatusCodes.Status404NotFound => NotFound(),
                StatusCodes.Status429TooManyRequests => new AssistantException(
                    "rate_limited", "Too many Assistant requests. Try again in a minute or two.", StatusCodes.Status429TooManyRequests),
                StatusCodes.Status400BadRequest or StatusCodes.Status412PreconditionFailed => Unavailable(),
                _ => new AssistantException("assistant_failed", "Nextcloud did not accept the Assistant request.", StatusCodes.Status502BadGateway),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AssistantException("nextcloud_unreachable", "Nextcloud could not be reached.", StatusCodes.Status502BadGateway);
        }
    }

    private AssistantTaskDto ToOwnTask(JsonElement data)
    {
        var task = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("task", out var t) ? t : default;
        if (!IsOwnTask(task))
        {
            throw NotFound();
        }

        return ToDto(task);
    }

    internal static AssistantTaskDto ToDto(JsonElement task)
    {
        var status = task.TryGetProperty("status", out var s) && s.GetString() is { } value
            ? (value.StartsWith("STATUS_", StringComparison.Ordinal) ? value[7..] : value).ToLowerInvariant()
            : "unknown";
        double? progress = task.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
        string? output = task.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.Object
                         && o.TryGetProperty("output", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
        var error = task.TryGetProperty("userFacingErrorMessage", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        DateTimeOffset? expected = task.TryGetProperty("completionExpectedAt", out var x) && x.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(x.GetInt64())
            : null;
        return new AssistantTaskDto(task.GetProperty("id").GetInt64(), status, progress, output, error, expected);
    }

    private static string SummaryPrompt(int messages) =>
        (messages > 1
            ? "Summarize the following email conversation in a few short bullet points: what it is about, what each person wants, what was decided, and what is still open or needs to be done."
            : "Summarize the following email in a few short bullet points: what it is about, what the sender wants, and anything that needs to be done.")
        + " Write the summary in the language of the emails. Do not add anything that is not in them.\n\n";

    private async Task RequireEnabledAsync(CancellationToken ct)
    {
        if (!await settings.AssistantAsync(ct))
        {
            throw new AssistantException("assistant_disabled", "Your administrator turned the Assistant off for mail.", StatusCodes.Status403Forbidden);
        }
    }

    private static AssistantException Unavailable() =>
        new("assistant_unavailable", "The Nextcloud Assistant cannot do this here: no AI provider is set up for it.", StatusCodes.Status409Conflict);

    private static AssistantException Invalid(string field, string message) =>
        new("invalid_" + field, message, StatusCodes.Status400BadRequest);

    private static AssistantException NotFound() =>
        new("not_found", "The Assistant task no longer exists.", StatusCodes.Status404NotFound);
}

/// <summary>
/// The task types a user's Nextcloud offers (types with a provider), by id, remembered for a few
/// minutes per user: the app asks on every start, and admins rarely change providers.
/// </summary>
public sealed class AssistantTypes(TimeProvider clock, ILogger<AssistantTypes> logger)
{
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, JsonElement> Types, DateTimeOffset Until)> _known = new();

    public async Task<IReadOnlyDictionary<string, JsonElement>> GetAsync(NextcloudClient nextcloud, string userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_known.TryGetValue(userId, out var known) && known.Until > now)
        {
            return known.Types;
        }

        var types = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            var data = await nextcloud.OcsAsync(HttpMethod.Get, "taskprocessing/tasktypes", userId: userId, ct: ct);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("types", out var list) && list.ValueKind == JsonValueKind.Object)
            {
                foreach (var type in list.EnumerateObject())
                {
                    types[type.Name] = type.Value;
                }
            }
        }
        catch (Exception e) when (e is NextcloudApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Not known now: ask again next time rather than hide the Assistant for minutes.
            logger.LogWarning("Could not read the Assistant's task types: {Error}", e.Message);
            return types;
        }

        _known[userId] = (types, now + Remember);
        return types;
    }
}
