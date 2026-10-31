using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Scheduling;

/// <summary>A user's own send later presets, replacing the built-in ones. No row: the built-in ones.</summary>
public sealed class SendLaterPresetList
{
    public required string OwnerUserId { get; init; }

    /// <summary><see cref="SendLaterPreset"/>s as a JSON array.</summary>
    public required string Presets { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// "When" in words the browser turns into a moment in the user's time zone (the server never
/// needs to). <see cref="Kind"/>: today, tomorrow or weekday at <see cref="Time"/> (HH:MM, the
/// next <see cref="Weekday"/>, 0 Sunday … 6 Saturday), or hours from now (<see cref="Hours"/>).
/// <see cref="ShowOn"/> limits the weekdays it is offered on.
/// </summary>
public sealed record SendLaterPreset(string Id, string Label, string Kind, string? Time, int? Weekday, int? Hours, int[]? ShowOn);

/// <summary>Null presets: the user has none of their own, so the app offers its built-in ones.</summary>
public sealed record SendLaterPresetsDto(IReadOnlyList<SendLaterPreset>? Presets);

public sealed record SendLaterPresetsInput(IReadOnlyList<SendLaterPreset>? Presets);

public sealed partial class SendLaterPresets(MailDbContext db, TimeProvider clock)
{
    public const int MaxPresets = 20;
    public const int MaxLabelLength = 60;
    public const int MaxHours = 24 * 30;

    private static readonly HashSet<string> Kinds = ["today", "tomorrow", "weekday", "hours"];

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex TimePattern();

    public async Task<SendLaterPresetsDto> GetAsync(string userId, CancellationToken ct)
    {
        var row = await db.SendLaterPresets.AsNoTracking().SingleOrDefaultAsync(p => p.OwnerUserId == userId, ct);
        return new SendLaterPresetsDto(row is null ? null : JsonSerializer.Deserialize<SendLaterPreset[]>(row.Presets, JsonSerializerOptions.Web));
    }

    /// <summary>Replaces the user's presets; an empty list is allowed (only the date and time picker is offered then).</summary>
    public async Task<SendLaterPresetsDto> SaveAsync(string userId, IReadOnlyList<SendLaterPreset>? presets, CancellationToken ct)
    {
        var cleaned = Validate(presets ?? throw new MailboxValidationException("presets", "Send the list of presets."));
        var row = await db.SendLaterPresets.SingleOrDefaultAsync(p => p.OwnerUserId == userId, ct);
        if (row is null)
        {
            row = new SendLaterPresetList { OwnerUserId = userId, Presets = "" };
            db.Add(row);
        }

        row.Presets = JsonSerializer.Serialize(cleaned, JsonSerializerOptions.Web);
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new SendLaterPresetsDto(cleaned);
    }

    /// <summary>Back to the built-in presets.</summary>
    public async Task ResetAsync(string userId, CancellationToken ct) =>
        await db.SendLaterPresets.Where(p => p.OwnerUserId == userId).ExecuteDeleteAsync(ct);

    private static SendLaterPreset[] Validate(IReadOnlyList<SendLaterPreset> presets)
    {
        if (presets.Count > MaxPresets)
        {
            throw new MailboxValidationException("presets", $"At most {MaxPresets} presets.");
        }

        var ids = new HashSet<string>();
        return presets.Select((p, i) =>
        {
            string Field(string name) => $"presets[{i}].{name}";
            if (p.Id is null || !IdPattern().IsMatch(p.Id) || !ids.Add(p.Id))
            {
                throw new MailboxValidationException(Field("id"), "Each preset needs its own id (lowercase letters, digits and dashes).");
            }

            var label = (p.Label ?? "").ReplaceLineEndings(" ").Trim();
            if (label.Length == 0 || label.Length > MaxLabelLength)
            {
                throw new MailboxValidationException(Field("label"), $"Give the preset a name of at most {MaxLabelLength} characters.");
            }

            if (p.Kind is null || !Kinds.Contains(p.Kind))
            {
                throw new MailboxValidationException(Field("kind"), "Choose today, tomorrow, a weekday or a number of hours.");
            }

            if (p.Kind == "hours")
            {
                if (p.Hours is not (>= 1 and <= MaxHours))
                {
                    throw new MailboxValidationException(Field("hours"), $"Choose between 1 and {MaxHours} hours.");
                }
            }
            else if (p.Time is null || !TimePattern().IsMatch(p.Time))
            {
                throw new MailboxValidationException(Field("time"), "Choose a time (HH:MM).");
            }

            if (p.Kind == "weekday" && p.Weekday is not (>= 0 and <= 6))
            {
                throw new MailboxValidationException(Field("weekday"), "Choose a day of the week.");
            }

            if (p.ShowOn is { } days && days.Any(d => d is < 0 or > 6))
            {
                throw new MailboxValidationException(Field("showOn"), "Days of the week are 0 (Sunday) to 6 (Saturday).");
            }

            var showOn = p.ShowOn is { Length: > 0 and < 7 } some ? some.Distinct().Order().ToArray() : null;
            return p.Kind == "hours"
                ? new SendLaterPreset(p.Id, label, p.Kind, null, null, p.Hours, showOn)
                : new SendLaterPreset(p.Id, label, p.Kind, p.Time, p.Kind == "weekday" ? p.Weekday : null, null, showOn);
        }).ToArray();
    }
}
