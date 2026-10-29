using System.Globalization;
using System.Text.RegularExpressions;
using NodaTime;
using NodaTime.TimeZones;

namespace SocklessMail.Api.Calendar;

/// <summary>
/// A DTSTART/DTEND for display. Exactly one of: <see cref="Instant"/> (a moment, shown in the
/// reader's own time zone), <see cref="Date"/> (all day, "yyyy-MM-dd") or <see cref="Local"/>
/// (a wall-clock time whose zone could not be resolved, "yyyy-MM-ddTHH:mm:ss", shown as written,
/// with <see cref="TimeZone"/> naming the zone if there was one).
/// </summary>
public sealed record EventTime(DateTimeOffset? Instant, string? Date, string? Local, string? TimeZone);

public static partial class ICalTime
{
    [GeneratedRegex(@"^(\d{4})(\d{2})(\d{2})(?:T(\d{2})(\d{2})(\d{2})(Z)?)?$")]
    private static partial Regex DateTimeFormat();

    /// <summary>"(UTC+01:00) Amsterdam, Berlin", "GMT-05.00": Outlook-style names that carry their offset.</summary>
    [GeneratedRegex(@"(?:UTC|GMT)\s*([+-])(\d{1,2})[:.]?(\d{2})?")]
    private static partial Regex OffsetInName();

    public static EventTime? Resolve(ContentLine? line, ICalComponent calendar)
    {
        if (line is null || DateTimeFormat().Match(line.Value.Trim()) is not { Success: true } m)
        {
            return null;
        }

        int Part(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        if (!m.Groups[4].Success)
        {
            return new EventTime(null, $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", null, null);
        }

        LocalDateTime local;
        try
        {
            local = new LocalDateTime(Part(1), Part(2), Part(3), Part(4), Part(5), Math.Min(Part(6), 59));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (m.Groups[7].Success)
        {
            return new EventTime(local.InUtc().ToDateTimeOffset(), null, null, null);
        }

        var tzid = line.Parameter("TZID");
        if (tzid is null)
        {
            return new EventTime(null, null, Format(local), null);
        }

        return Zone(tzid, calendar) is { } zone
            ? new EventTime(local.InZoneLeniently(zone).ToDateTimeOffset().ToUniversalTime(), null, null, tzid)
            : new EventTime(null, null, Format(local), tzid);
    }

    /// <summary>The end: DTEND, else DTSTART + DURATION, else the start (a day later for all-day events).</summary>
    public static EventTime? End(ICalComponent vevent, ICalComponent calendar, EventTime? start)
    {
        if (Resolve(vevent.Property("DTEND"), calendar) is { } end)
        {
            return end;
        }

        if (start is null)
        {
            return null;
        }

        var duration = Duration(vevent.Property("DURATION")?.Value);
        if (start.Date is { } date)
        {
            var days = duration is { } d ? Math.Max(1, (int)Math.Round(d.TotalDays)) : 1;
            return start with { Date = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        }

        var span = duration ?? TimeSpan.Zero;
        return start.Instant is { } instant
            ? start with { Instant = instant + span }
            : start with { Local = Format(LocalDateTime.FromDateTime(DateTime.Parse(start.Local!, CultureInfo.InvariantCulture) + span)) };
    }

    /// <summary>"PT1H30M", "P1D", "P2W" → a time span; null when it does not parse.</summary>
    public static TimeSpan? Duration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().TrimStart('+');
        var negative = text.StartsWith('-');
        text = text.TrimStart('-');
        var m = Regex.Match(text, @"^P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success)
        {
            return null;
        }

        int Group(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        var span = new TimeSpan(Group(1) * 7 + Group(2), Group(3), Group(4), Group(5));
        return negative ? -span : span;
    }

    /// <summary>
    /// IANA ids ("Europe/Berlin", also "/Europe/Berlin"), Windows ids ("W. Europe Standard Time"),
    /// names with an offset, a VTIMEZONE's X-LIC-LOCATION, or a VTIMEZONE with one fixed offset.
    /// </summary>
    private static DateTimeZone? Zone(string tzid, ICalComponent calendar)
    {
        var tzdb = DateTimeZoneProviders.Tzdb;
        var id = tzid.Trim().TrimStart('/');
        if (tzdb.GetZoneOrNull(id) is { } iana)
        {
            return iana;
        }

        if (TzdbDateTimeZoneSource.Default.WindowsMapping.PrimaryMapping.TryGetValue(id, out var mapped) && tzdb.GetZoneOrNull(mapped) is { } windows)
        {
            return windows;
        }

        var definition = calendar.ChildrenNamed("VTIMEZONE").FirstOrDefault(z => z.Property("TZID")?.Value == tzid);
        if (definition?.Property("X-LIC-LOCATION")?.Value is { } location && tzdb.GetZoneOrNull(location.Trim()) is { } licensed)
        {
            return licensed;
        }

        if (OffsetInName().Match(id) is { Success: true } named)
        {
            var minutes = int.Parse(named.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                          + (named.Groups[3].Success ? int.Parse(named.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
            return DateTimeZone.ForOffset(Offset.FromSeconds((named.Groups[1].Value == "-" ? -minutes : minutes) * 60));
        }

        // Only a definition without daylight saving can be read without evaluating its rules.
        var offsets = definition?.Children.Select(c => c.Property("TZOFFSETTO")?.Value.Trim()).Distinct().ToList();
        if (offsets is [{ } single] && Regex.Match(single, @"^([+-])(\d{2})(\d{2})$") is { Success: true } fixedOffset)
        {
            var minutes = int.Parse(fixedOffset.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(fixedOffset.Groups[3].Value, CultureInfo.InvariantCulture);
            return DateTimeZone.ForOffset(Offset.FromSeconds((fixedOffset.Groups[1].Value == "-" ? -minutes : minutes) * 60));
        }

        return null;
    }

    private static string Format(LocalDateTime local) => local.ToString("uuuu-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}
