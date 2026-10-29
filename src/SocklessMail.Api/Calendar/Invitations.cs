using System.Globalization;
using MimeKit;

namespace SocklessMail.Api.Calendar;

public sealed record PersonDto(string Address, string? Name, string? Status);

public sealed record AttendeeDto(string Address, string? Name, string Status, string Role);

public sealed record EventDto(
    string Summary,
    string? Location,
    string? Description,
    EventTime? Start,
    EventTime? End,
    string? Recurrence,
    EventTime? RecurrenceId,
    string? Status,
    int Sequence,
    IReadOnlyList<AttendeeDto> Attendees);

/// <summary>
/// Calendar data from a message (an iMIP invitation, cancellation or reply, or a plain .ics)
/// and the operations on it. Everything works on the iCalendar text (<see cref="ICalComponent"/>),
/// so what the app does not change reaches the calendar exactly as it was sent.
/// </summary>
public sealed class Invitation
{
    public const int MaxEvents = 50;

    private Invitation(ICalComponent root, string method, IReadOnlyList<ICalComponent> events)
    {
        Root = root;
        Method = method;
        Events = events;
    }

    public ICalComponent Root { get; }

    /// <summary>REQUEST, CANCEL, REPLY, PUBLISH, …; empty for a plain .ics.</summary>
    public string Method { get; }

    public IReadOnlyList<ICalComponent> Events { get; }

    /// <summary>The events' UIDs in order of appearance; an iMIP message has exactly one.</summary>
    public IReadOnlyList<string> Uids => [.. Events.Select(Uid).Distinct()];

    /// <exception cref="FormatException">Not calendar data, or no event in it.</exception>
    public static Invitation Parse(string text)
    {
        var root = ICalComponent.Parse(text);
        var events = root.ChildrenNamed("VEVENT").Where(e => !string.IsNullOrWhiteSpace(e.Property("UID")?.Value)).ToList();
        if (events.Count == 0)
        {
            throw new FormatException("The calendar data contains no event.");
        }

        if (events.Count > MaxEvents)
        {
            throw new FormatException($"The calendar data contains more than {MaxEvents} events.");
        }

        return new Invitation(root, (root.Property("METHOD")?.Value ?? "").Trim().ToUpperInvariant(), events);
    }

    public static string Uid(ICalComponent vevent) => vevent.Property("UID")!.Value.Trim();

    /// <summary>The master event (no RECURRENCE-ID) of a UID, else its first instance.</summary>
    public static ICalComponent? MainEvent(IEnumerable<ICalComponent> events) =>
        events.FirstOrDefault(e => e.Property("RECURRENCE-ID") is null) ?? events.FirstOrDefault();

    /// <summary>Which occurrence a VEVENT is: "" for the master, else its RECURRENCE-ID.</summary>
    public static string Key(ICalComponent vevent) =>
        vevent.Property("RECURRENCE-ID") is { } id ? $"{id.Parameter("TZID")}|{id.Value.Trim()}" : "";

    public static EventDto Describe(ICalComponent vevent, ICalComponent calendar)
    {
        var start = ICalTime.Resolve(vevent.Property("DTSTART"), calendar);
        return new EventDto(
            vevent.Property("SUMMARY")?.Text.Trim() is { Length: > 0 } summary ? summary : "(no title)",
            NullIfEmpty(vevent.Property("LOCATION")?.Text),
            NullIfEmpty(vevent.Property("DESCRIPTION")?.Text) is { } d ? (d.Length > 2000 ? d[..2000] + "…" : d) : null,
            start,
            ICalTime.End(vevent, calendar, start),
            vevent.Property("RRULE")?.Value.Trim(),
            ICalTime.Resolve(vevent.Property("RECURRENCE-ID"), calendar),
            vevent.Property("STATUS")?.Value.Trim().ToUpperInvariant(),
            Sequence(vevent),
            [.. vevent.PropertiesNamed("ATTENDEE").Where(a => a.Address is not null).Select(a => new AttendeeDto(
                a.Address!, NullIfEmpty(a.Parameter("CN")),
                (a.Parameter("PARTSTAT") ?? "NEEDS-ACTION").ToUpperInvariant(),
                (a.Parameter("ROLE") ?? "REQ-PARTICIPANT").ToUpperInvariant()))]);
    }

    public static int Sequence(ICalComponent vevent) =>
        int.TryParse(vevent.Property("SEQUENCE")?.Value.Trim(), CultureInfo.InvariantCulture, out var s) ? s : 0;

    public static PersonDto? Organizer(ICalComponent vevent) =>
        vevent.Property("ORGANIZER") is { Address: { } address } o ? new PersonDto(address, NullIfEmpty(o.Parameter("CN")), null) : null;

    /// <summary>The ATTENDEE line for one of <paramref name="addresses"/>, or null.</summary>
    public static ContentLine? Attendee(ICalComponent vevent, IReadOnlyCollection<string> addresses) =>
        vevent.PropertiesNamed("ATTENDEE").FirstOrDefault(a => a.Address is { } address && addresses.Contains(address, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The calendar object to store for an attendee's answer: the invitation's events (all of one
    /// UID) merged into <paramref name="existing"/> (replacing the occurrences it carries), with
    /// METHOD removed and the attendee's PARTSTAT set. With <paramref name="clientScheduling"/>,
    /// ORGANIZER carries SCHEDULE-AGENT=CLIENT, so Nextcloud sends no reply of its own.
    /// </summary>
    public static string ForAttendee(
        IReadOnlyList<ICalComponent> events, ICalComponent source, ICalComponent? existing,
        string address, string? name, string? partStatus, bool clientScheduling)
    {
        var target = existing?.Clone() ?? NewCalendar(source);
        foreach (var incoming in events)
        {
            var copy = incoming.Clone();
            if (partStatus is not null)
            {
                var attendee = Attendee(copy, [address]);
                if (attendee is null)
                {
                    attendee = new ContentLine { Name = "ATTENDEE", Value = "mailto:" + address };
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        attendee.SetParameter("CN", name);
                    }

                    copy.Properties.Add(attendee);
                }

                attendee.SetParameter("PARTSTAT", partStatus);
                attendee.RemoveParameter("RSVP");
                attendee.RemoveParameter("SCHEDULE-STATUS");
            }

            Replace(target, copy);
        }

        CopyTimeZones(source, target);
        return SetClientScheduling(target, clientScheduling).Serialize();
    }

    /// <summary>A stored object with SCHEDULE-AGENT=CLIENT added to, or removed from, every ORGANIZER.</summary>
    public static ICalComponent SetClientScheduling(ICalComponent calendar, bool client)
    {
        foreach (var organizer in calendar.ChildrenNamed("VEVENT").SelectMany(e => e.PropertiesNamed("ORGANIZER")))
        {
            if (client)
            {
                organizer.SetParameter("SCHEDULE-AGENT", "CLIENT");
            }
            else
            {
                organizer.RemoveParameter("SCHEDULE-AGENT");
            }
        }

        return calendar;
    }

    /// <summary>
    /// A cancellation applied to the stored object: null when the whole event is cancelled (delete
    /// it); otherwise the object with the cancelled occurrences excluded (EXDATE on the master)
    /// and their own copies removed.
    /// </summary>
    public static ICalComponent? Cancel(IReadOnlyList<ICalComponent> cancelled, ICalComponent existing)
    {
        if (cancelled.Any(e => e.Property("RECURRENCE-ID") is null))
        {
            return null;
        }

        var target = existing.Clone();
        var master = target.ChildrenNamed("VEVENT").FirstOrDefault(e => e.Property("RECURRENCE-ID") is null);
        foreach (var instance in cancelled)
        {
            var key = Key(instance);
            target.Children.RemoveAll(c => c.Name == "VEVENT" && Key(c) == key);
            if (master is not null)
            {
                var exdate = instance.Property("RECURRENCE-ID")!.Clone();
                exdate.Name = "EXDATE";
                exdate.RemoveParameter("RANGE");
                master.Properties.Add(exdate);
            }
        }

        return target.ChildrenNamed("VEVENT").Any() ? target : null;
    }

    /// <summary>
    /// An attendee's reply applied to the organizer's stored object: their PARTSTAT in each
    /// occurrence the reply is about. Null when nothing changes.
    /// </summary>
    public static ICalComponent? ApplyReply(IReadOnlyList<ICalComponent> replies, ICalComponent existing)
    {
        var target = existing.Clone();
        var changed = false;
        foreach (var reply in replies)
        {
            var key = Key(reply);
            var occurrence = target.ChildrenNamed("VEVENT").FirstOrDefault(e => Key(e) == key);
            foreach (var answer in reply.PropertiesNamed("ATTENDEE").Where(a => a.Address is not null))
            {
                var attendee = occurrence is null ? null : Attendee(occurrence, [answer.Address!]);
                var status = answer.Parameter("PARTSTAT")?.ToUpperInvariant();
                if (attendee is null || status is null || attendee.Parameter("PARTSTAT")?.ToUpperInvariant() == status)
                {
                    continue;
                }

                attendee.SetParameter("PARTSTAT", status);
                attendee.RemoveParameter("RSVP");
                attendee.RemoveParameter("SCHEDULE-STATUS");
                changed = true;
            }
        }

        return changed ? target : null;
    }

    /// <summary>
    /// The iMIP REPLY (RFC 6047) to the organizer: a short text and the iTIP REPLY with only the
    /// answering attendee (<paramref name="myAttendee"/>, or the sender when the invitation does
    /// not list them), one VEVENT per occurrence the invitation was about.
    /// </summary>
    public static MimeMessage BuildReply(
        IReadOnlyList<ICalComponent> events, ICalComponent source, MailboxAddress from,
        ContentLine? myAttendee, string partStatus, string? comment, DateTimeOffset now)
    {
        var main = MainEvent(events)!;
        var organizer = main.Property("ORGANIZER")!;
        var reply = new ICalComponent { Name = "VCALENDAR" };
        reply.Properties.Add(new ContentLine { Name = "PRODID", Value = "-//Sockless Mail//EN" });
        reply.Properties.Add(new ContentLine { Name = "VERSION", Value = "2.0" });
        reply.Properties.Add(new ContentLine { Name = "METHOD", Value = "REPLY" });
        foreach (var vevent in events)
        {
            var answer = new ICalComponent { Name = "VEVENT" };
            foreach (var copied in new[] { "UID", "SEQUENCE", "RECURRENCE-ID", "DTSTART", "DTEND", "DURATION", "SUMMARY" })
            {
                answer.Properties.AddRange(vevent.PropertiesNamed(copied).Select(p => p.Clone()));
            }

            answer.Properties.Add(new ContentLine { Name = "DTSTAMP", Value = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) });
            var replyOrganizer = (vevent.Property("ORGANIZER") ?? organizer).Clone();
            replyOrganizer.RemoveParameter("SCHEDULE-AGENT");
            replyOrganizer.RemoveParameter("SCHEDULE-STATUS");
            answer.Properties.Add(replyOrganizer);

            var attendee = new ContentLine { Name = "ATTENDEE", Value = "mailto:" + (myAttendee?.Address ?? from.Address) };
            var name = myAttendee?.Parameter("CN") ?? from.Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                attendee.SetParameter("CN", name);
            }

            attendee.SetParameter("PARTSTAT", partStatus);
            answer.Properties.Add(attendee);
            if (!string.IsNullOrWhiteSpace(comment))
            {
                answer.Properties.Add(new ContentLine { Name = "COMMENT", Value = ContentLine.Escape(comment.Trim()) });
            }

            reply.Children.Add(answer);
        }

        CopyTimeZones(source, reply);

        var summary = main.Property("SUMMARY")?.Text.Trim() is { Length: > 0 } s ? s : "(no title)";
        var verb = partStatus switch
        {
            "ACCEPTED" => "Accepted",
            "TENTATIVE" => "Tentatively accepted",
            _ => "Declined",
        };
        var message = new MimeMessage { Subject = $"{verb}: {summary}" };
        message.From.Add(from);
        var to = new MailboxAddress(organizer.Parameter("CN") ?? "", organizer.Address!);
        message.To.Add(to);

        var who = string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name;
        var text = $"{who} {verb.ToLowerInvariant()} the invitation: {summary}";
        if (!string.IsNullOrWhiteSpace(comment))
        {
            text += $"\n\n{comment.Trim()}";
        }

        var calendar = new TextPart("calendar") { Text = reply.Serialize() };
        calendar.ContentType.Parameters.Add("method", "REPLY");
        calendar.ContentType.Charset = "utf-8";
        message.Body = new MultipartAlternative { new TextPart("plain") { Text = text + "\n" }, calendar };
        return message;
    }

    private static ICalComponent NewCalendar(ICalComponent source)
    {
        var calendar = new ICalComponent { Name = "VCALENDAR" };
        calendar.Properties.Add(source.Property("PRODID")?.Clone() ?? new ContentLine { Name = "PRODID", Value = "-//Sockless Mail//EN" });
        calendar.Properties.Add(new ContentLine { Name = "VERSION", Value = "2.0" });
        if (source.Property("CALSCALE") is { } scale)
        {
            calendar.Properties.Add(scale.Clone());
        }

        return calendar;
    }

    /// <summary>Puts an occurrence in place of the stored one with the same key, or adds it.</summary>
    private static void Replace(ICalComponent target, ICalComponent occurrence)
    {
        var key = Key(occurrence);
        var index = target.Children.FindIndex(c => c.Name == "VEVENT" && Key(c) == key);
        if (index >= 0)
        {
            target.Children[index] = occurrence;
        }
        else
        {
            target.Children.Add(occurrence);
        }
    }

    /// <summary>The source's VTIMEZONE definitions the target's events use and does not have, before its events.</summary>
    private static void CopyTimeZones(ICalComponent source, ICalComponent target)
    {
        var used = target.ChildrenNamed("VEVENT")
            .SelectMany(e => e.Properties)
            .Select(p => p.Parameter("TZID"))
            .OfType<string>()
            .ToHashSet();
        var present = target.ChildrenNamed("VTIMEZONE").Select(z => z.Property("TZID")?.Value).ToHashSet();
        var index = 0;
        foreach (var zone in source.ChildrenNamed("VTIMEZONE"))
        {
            var id = zone.Property("TZID")?.Value;
            if (id is not null && used.Contains(id) && present.Add(id))
            {
                target.Children.Insert(index++, zone.Clone());
            }
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
