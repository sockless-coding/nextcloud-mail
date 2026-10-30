using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using NodaTime;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Calendar;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Tasks;

/// <summary>
/// Whether the user can make tasks: the Tasks app is enabled for them and they have a task list
/// to write to. <see cref="AppUrl"/> is the Tasks app's address relative to the Nextcloud root.
/// </summary>
public sealed record TasksDto(bool Available, string? AppUrl, IReadOnlyList<CalendarInfo> Lists);

/// <param name="Due">"yyyy-MM-dd" for a day, or an ISO 8601 moment with its offset; null for none.</param>
/// <param name="TimeZone">The user's IANA time zone, for the date of the message in the notes.</param>
public sealed record CreateTaskInput(string? CalendarId, string? Title, string? Due, bool? IncludeText, string? TimeZone);

public sealed record CreatedTask(string CalendarId, string Name, string Uid);

/// <summary>A task's due date: a day (<see cref="Date"/>) or a moment (<see cref="Instant"/>).</summary>
public sealed record TaskDue(DateOnly? Date, DateTimeOffset? Instant)
{
    /// <summary>Null for no due date; throws <see cref="MailboxValidationException"/> for an unreadable one.</summary>
    public static TaskDue? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.Year is >= 1900 and <= 9999 ? new TaskDue(date, null) : throw Invalid();
        }

        // A moment must say its offset: the server does not know the user's zone.
        if (value.Length > 10 && (value.EndsWith('Z') || value[^6] is '+' or '-')
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            && instant.Year is >= 1900 and <= 9999)
        {
            return new TaskDue(null, instant.ToUniversalTime());
        }

        throw Invalid();
    }

    private static MailboxValidationException Invalid() => new("due", "The due date is not a valid date.");
}

/// <summary>A VTODO for Nextcloud Tasks, made from a message.</summary>
public static class TaskBuilder
{
    public const int MaxTitleLength = 500;

    /// <summary>The notes carry at most this much of the message text.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>Where the task came from; a later version can link back to the message with it.</summary>
    public const string MessageIdProperty = "X-SOCKLESS-MAIL-MESSAGE-ID";

    public static string Build(string uid, string title, string? notes, TaskDue? due, string? messageId, DateTimeOffset now)
    {
        var stamp = Utc(now);
        var todo = new ICalComponent { Name = "VTODO" };
        todo.Properties.Add(Line("UID", uid));
        todo.Properties.Add(Line("DTSTAMP", stamp));
        todo.Properties.Add(Line("CREATED", stamp));
        todo.Properties.Add(Line("LAST-MODIFIED", stamp));
        todo.Properties.Add(Line("SUMMARY", ContentLine.Escape(Clean(title))));
        if (!string.IsNullOrWhiteSpace(notes))
        {
            todo.Properties.Add(Line("DESCRIPTION", ContentLine.Escape(Clean(notes))));
        }

        if (due?.Date is { } date)
        {
            var line = Line("DUE", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            line.SetParameter("VALUE", "DATE");
            todo.Properties.Add(line);
        }
        else if (due?.Instant is { } instant)
        {
            todo.Properties.Add(Line("DUE", Utc(instant)));
        }

        todo.Properties.Add(Line("STATUS", "NEEDS-ACTION"));
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            todo.Properties.Add(Line(MessageIdProperty, ContentLine.Escape(Clean(messageId.Trim('<', '>', ' ')))));
        }

        var calendar = new ICalComponent { Name = "VCALENDAR" };
        calendar.Properties.Add(Line("VERSION", "2.0"));
        calendar.Properties.Add(Line("PRODID", "-//Sockless Mail//EN"));
        calendar.Children.Add(todo);
        return calendar.Serialize();
    }

    /// <summary>"From: …" and "Date: …" lines, then (if wanted) the message text, shortened.</summary>
    public static string Notes(MessageText message, bool includeText, DateTimeZone zone)
    {
        var notes = new StringBuilder();
        if (message.From is { } from)
        {
            notes.Append("From: ").Append(string.IsNullOrWhiteSpace(from.Name) ? from.Address : $"{from.Name} <{from.Address}>").Append('\n');
        }

        var local = Instant.FromDateTimeOffset(message.Date).InZone(zone).LocalDateTime;
        notes.Append("Date: ").Append(local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        var text = message.Text.Replace("\r\n", "\n").Trim();
        if (includeText && text.Length > 0)
        {
            notes.Append('\n').Append(Shorten(text, MaxTextLength));
        }

        return notes.ToString().TrimEnd();
    }

    /// <summary>Cut at a word (or line) boundary near the limit, marked with an ellipsis.</summary>
    public static string Shorten(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = text.LastIndexOfAny([' ', '\n'], max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd() + "…";
    }

    /// <summary>Control characters other than line breaks have no place in a task.</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static ContentLine Line(string name, string value) => new() { Name = name, Value = value };

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Tasks made from messages, in the user's Nextcloud task lists (CalDAV calendars with VTODO).</summary>
public sealed class TaskService(
    NextcloudClient nextcloud, NextcloudCalendars calendars, MailboxService mailbox, TaskApps apps, TimeProvider clock)
{
    public async Task<TasksDto> GetAsync(string userId, CancellationToken ct)
    {
        var appUrl = await apps.TasksUrlAsync(nextcloud, userId, ct);
        if (appUrl is null)
        {
            return new TasksDto(false, null, []);
        }

        var lists = await calendars.ListAsync(userId, ct, "VTODO");
        return new TasksDto(lists.Any(l => l.Writable), appUrl, lists);
    }

    public async Task<CreatedTask> CreateAsync(
        string userId, MailAccount account, string folderId, string messageId, CreateTaskInput input, CancellationToken ct)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            throw new MailboxValidationException("title", "The task needs a title.");
        }

        if (title.Length > TaskBuilder.MaxTitleLength)
        {
            throw new MailboxValidationException("title", $"The title may be at most {TaskBuilder.MaxTitleLength} characters.");
        }

        var due = TaskDue.Parse(input.Due);
        var zone = input.TimeZone is { Length: > 0 } tz ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz) ?? DateTimeZone.Utc : DateTimeZone.Utc;
        if (await apps.TasksUrlAsync(nextcloud, userId, ct) is null)
        {
            throw new CalendarException("tasks_unavailable", "The Tasks app is not enabled for you in Nextcloud.", StatusCodes.Status409Conflict);
        }

        var list = (await calendars.ListAsync(userId, ct, "VTODO")).FirstOrDefault(l => l.Id == input.CalendarId)
                   ?? throw new CalendarException("not_found", "The task list no longer exists.", StatusCodes.Status404NotFound);
        if (!list.Writable)
        {
            throw new CalendarException("read_only", "This task list is read-only.", StatusCodes.Status403Forbidden);
        }

        var message = await mailbox.GetMessageTextAsync(account, folderId, messageId, ct);
        var uid = Guid.NewGuid().ToString();
        var name = NextcloudCalendars.ObjectName(uid);
        var text = TaskBuilder.Build(uid, title, TaskBuilder.Notes(message, input.IncludeText != false, zone), due, message.MessageId, clock.GetUtcNow());
        await calendars.PutAsync(userId, list.Id, name, text, null, create: true, ct);
        return new CreatedTask(list.Id, name, uid);
    }
}

/// <summary>
/// Whether the Tasks app is in a user's Nextcloud navigation (enabled, and for them), remembered
/// for a few minutes per user: the menu asks on every start.
/// </summary>
public sealed class TaskApps(TimeProvider clock, ILogger<TaskApps> logger)
{
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (string? Url, DateTimeOffset Until)> _known = new();

    /// <summary>The Tasks app's address, or null when the user does not have it.</summary>
    public async Task<string?> TasksUrlAsync(NextcloudClient nextcloud, string userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_known.TryGetValue(userId, out var known) && known.Until > now)
        {
            return known.Url;
        }

        string? url = null;
        try
        {
            var entries = await nextcloud.OcsAsync(HttpMethod.Get, "core/navigation/apps", userId: userId, ct: ct);
            if (entries.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.TryGetProperty("app", out var app) && app.GetString() == "tasks")
                    {
                        // The browser opens it on its own origin: only a path will do.
                        url = entry.TryGetProperty("href", out var href) && href.GetString() is { } h && h.StartsWith('/') && !h.StartsWith("//")
                            ? h
                            : "/apps/tasks/";
                        break;
                    }
                }
            }
        }
        catch (Exception e) when (e is NextcloudApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Not known now: ask again next time rather than hide tasks for minutes.
            logger.LogWarning("Could not read the Nextcloud navigation: {Error}", e.Message);
            return null;
        }

        _known[userId] = (url, now + Remember);
        return url;
    }
}
