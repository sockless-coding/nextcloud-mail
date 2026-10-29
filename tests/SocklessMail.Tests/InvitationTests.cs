using MimeKit;
using SocklessMail.Api.Calendar;

namespace SocklessMail.Tests;

/// <summary>Reading invitations and the text operations on calendar objects (no server needed).</summary>
public sealed class InvitationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>As Outlook sends it: Windows time zone id with its VTIMEZONE, X- properties, a long folded description.</summary>
    private const string Outlook = """
        BEGIN:VCALENDAR
        METHOD:REQUEST
        PRODID:Microsoft Exchange Server 2010
        VERSION:2.0
        BEGIN:VTIMEZONE
        TZID:W. Europe Standard Time
        BEGIN:STANDARD
        DTSTART:16010101T030000
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=10
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:16010101T020000
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=3
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        ORGANIZER;CN="Olga Organizer":mailto:olga@corp.example
        ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;CN=Alice:mailto:Alice@Example.test
        ATTENDEE;ROLE=OPT-PARTICIPANT;PARTSTAT=ACCEPTED;CN=Bob:mailto:bob@corp.example
        DESCRIPTION;LANGUAGE=en-US:Agenda:\n1. Numbers\, mostly\n2. Coffee
        UID:040000008200E00074C5B7101A82E008
        SUMMARY;LANGUAGE=en-US:Quarterly review
        DTSTART;TZID=W. Europe Standard Time:20261110T090000
        DTEND;TZID=W. Europe Standard Time:20261110T103000
        SEQUENCE:2
        DTSTAMP:20261029T100000Z
        LOCATION;LANGUAGE=en-US:Room 4
        X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE
        X-MICROSOFT-CDO-IMPORTANCE:1
        END:VEVENT
        END:VCALENDAR
        """;

    private static Invitation Parse(string text) => Invitation.Parse(text.Replace("\n", "\r\n"));

    [Fact]
    public void An_outlook_invitation_is_described()
    {
        var invitation = Parse(Outlook);
        var dto = Invitation.Describe(invitation.Events[0], invitation.Root);

        Assert.Equal("REQUEST", invitation.Method);
        Assert.Equal("Quarterly review", dto.Summary);
        Assert.Equal("Room 4", dto.Location);
        Assert.Equal("Agenda:\n1. Numbers, mostly\n2. Coffee", dto.Description);
        // 09:00 in Berlin in November is 08:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero), dto.Start!.Instant);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero), dto.End!.Instant);
        Assert.Equal(2, dto.Sequence);
        Assert.Equal(["NEEDS-ACTION", "ACCEPTED"], dto.Attendees.Select(a => a.Status));
        Assert.Equal("Olga Organizer", Invitation.Organizer(invitation.Events[0])!.Name);
    }

    [Theory]
    [InlineData("DTSTART;TZID=Europe/Stockholm:20260710T120000", "2026-07-10T10:00:00+00:00", null, null)]
    [InlineData("DTSTART;TZID=/Europe/Stockholm:20260710T120000", "2026-07-10T10:00:00+00:00", null, null)]
    [InlineData("DTSTART;TZID=\"(UTC+01:00) Amsterdam, Berlin\":20260110T120000", "2026-01-10T11:00:00+00:00", null, null)]
    [InlineData("DTSTART:20260710T120000Z", "2026-07-10T12:00:00+00:00", null, null)]
    [InlineData("DTSTART;VALUE=DATE:20260710", null, "2026-07-10", null)]
    [InlineData("DTSTART:20260710T120000", null, null, "2026-07-10T12:00:00")]
    [InlineData("DTSTART;TZID=Somewhere odd:20260710T120000", null, null, "2026-07-10T12:00:00")]
    public void Times_resolve_with_zone_names_of_every_kind(string line, string? instant, string? date, string? local)
    {
        var calendar = ICalComponent.Parse("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var time = ICalTime.Resolve(ContentLine.Parse(line), calendar)!;

        Assert.Equal(instant is null ? null : DateTimeOffset.Parse(instant), time.Instant);
        Assert.Equal(date, time.Date);
        Assert.Equal(local, time.Local);
    }

    [Fact]
    public void All_day_and_duration_ends_are_computed()
    {
        var invitation = Parse("""
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:a
            DTSTART;VALUE=DATE:20261231
            END:VEVENT
            BEGIN:VEVENT
            UID:b
            DTSTART:20261231T230000Z
            DURATION:PT1H30M
            END:VEVENT
            END:VCALENDAR
            """);

        Assert.Equal("2027-01-01", Invitation.Describe(invitation.Events[0], invitation.Root).End!.Date);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.Zero), Invitation.Describe(invitation.Events[1], invitation.Root).End!.Instant);
    }

    [Fact]
    public void Accepting_keeps_everything_and_only_sets_the_answer()
    {
        var invitation = Parse(Outlook);

        var stored = Invitation.ForAttendee(invitation.Events, invitation.Root, null, "alice@example.test", "Alice", "ACCEPTED", clientScheduling: true);

        Assert.DoesNotContain("METHOD:", stored);
        Assert.Contains("ATTENDEE;ROLE=REQ-PARTICIPANT;CN=Alice;PARTSTAT=ACCEPTED:mailto:Alice@Example.test", Unfold(stored));
        Assert.Contains("ORGANIZER;CN=\"Olga Organizer\";SCHEDULE-AGENT=CLIENT:mailto:olga@corp.example", Unfold(stored));
        Assert.Contains("X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE", stored);
        Assert.Contains("DESCRIPTION;LANGUAGE=en-US:Agenda:\\n1. Numbers\\, mostly\\n2. Coffee", Unfold(stored));
        Assert.Contains("TZID:W. Europe Standard Time", stored);
        Assert.All(stored.Split("\r\n"), l => Assert.True(System.Text.Encoding.UTF8.GetByteCount(l) <= 75));

        // Handing scheduling back to Nextcloud removes only that parameter.
        var handedBack = Invitation.SetClientScheduling(ICalComponent.Parse(stored), client: false).Serialize();
        Assert.Contains("ORGANIZER;CN=\"Olga Organizer\":mailto:olga@corp.example", Unfold(handedBack));
    }

    [Fact]
    public void An_attendee_missing_from_the_invitation_is_added()
    {
        var invitation = Parse(Outlook);

        var stored = Invitation.ForAttendee(invitation.Events, invitation.Root, null, "list-member@example.test", "Lisa", "TENTATIVE", clientScheduling: false);

        Assert.Contains("ATTENDEE;CN=Lisa;PARTSTAT=TENTATIVE:mailto:list-member@example.test", Unfold(stored));
    }

    [Fact]
    public void An_updated_occurrence_replaces_only_its_own_copy()
    {
        var existing = ICalComponent.Parse(Lf("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:weekly
            DTSTART:20261102T090000Z
            RRULE:FREQ=WEEKLY
            SUMMARY:Standup
            END:VEVENT
            BEGIN:VEVENT
            UID:weekly
            RECURRENCE-ID:20261109T090000Z
            DTSTART:20261109T100000Z
            SUMMARY:Standup (moved)
            END:VEVENT
            END:VCALENDAR
            """));
        var update = Parse("""
            BEGIN:VCALENDAR
            METHOD:REQUEST
            BEGIN:VEVENT
            UID:weekly
            RECURRENCE-ID:20261116T090000Z
            DTSTART:20261116T110000Z
            SUMMARY:Standup (later)
            ORGANIZER:mailto:olga@corp.example
            ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:alice@example.test
            END:VEVENT
            END:VCALENDAR
            """);

        var stored = ICalComponent.Parse(Invitation.ForAttendee(update.Events, update.Root, existing, "alice@example.test", null, "ACCEPTED", false));

        Assert.Equal(["Standup", "Standup (moved)", "Standup (later)"], stored.ChildrenNamed("VEVENT").Select(e => e.Property("SUMMARY")!.Text));
    }

    [Fact]
    public void Cancelling_an_occurrence_excludes_it_and_cancelling_all_deletes()
    {
        var existing = ICalComponent.Parse(Lf("""
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly
            DTSTART;TZID=Europe/Berlin:20261102T090000
            RRULE:FREQ=WEEKLY
            END:VEVENT
            BEGIN:VEVENT
            UID:weekly
            RECURRENCE-ID;TZID=Europe/Berlin:20261109T090000
            DTSTART;TZID=Europe/Berlin:20261109T100000
            END:VEVENT
            END:VCALENDAR
            """));
        var one = Parse("BEGIN:VCALENDAR\nMETHOD:CANCEL\nBEGIN:VEVENT\nUID:weekly\nRECURRENCE-ID;TZID=Europe/Berlin:20261109T090000\nEND:VEVENT\nEND:VCALENDAR");
        var all = Parse("BEGIN:VCALENDAR\nMETHOD:CANCEL\nBEGIN:VEVENT\nUID:weekly\nEND:VEVENT\nEND:VCALENDAR");

        var remaining = Invitation.Cancel(one.Events, existing)!;

        var events = remaining.ChildrenNamed("VEVENT").ToList();
        Assert.Single(events);
        Assert.Equal("EXDATE;TZID=Europe/Berlin:20261109T090000", events[0].Property("EXDATE")!.ToString());
        Assert.Null(Invitation.Cancel(all.Events, existing));
    }

    [Fact]
    public void A_reply_updates_that_attendee_only()
    {
        var existing = ICalComponent.Parse(Lf("""
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:meeting
            DTSTART:20261110T090000Z
            ORGANIZER:mailto:alice@example.test
            ATTENDEE;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:bob@corp.example
            ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:carol@corp.example
            END:VEVENT
            END:VCALENDAR
            """));
        var reply = Parse("BEGIN:VCALENDAR\nMETHOD:REPLY\nBEGIN:VEVENT\nUID:meeting\nATTENDEE;PARTSTAT=DECLINED:mailto:BOB@corp.example\nEND:VEVENT\nEND:VCALENDAR");

        var updated = Invitation.ApplyReply(reply.Events, existing)!;

        var attendees = updated.ChildrenNamed("VEVENT").Single().PropertiesNamed("ATTENDEE").Select(a => a.ToString()).ToList();
        Assert.Equal(["ATTENDEE;PARTSTAT=DECLINED:mailto:bob@corp.example", "ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:carol@corp.example"], attendees);
        // The same reply again changes nothing.
        Assert.Null(Invitation.ApplyReply(reply.Events, updated));
    }

    [Fact]
    public void The_reply_mail_is_an_imip_reply_to_the_organizer()
    {
        var invitation = Parse(Outlook);
        var me = Invitation.Attendee(invitation.Events[0], ["alice@example.test"]);

        var message = Invitation.BuildReply(invitation.Events, invitation.Root, new MailboxAddress("Alice A.", "alice@example.test"),
            me, "TENTATIVE", "Might be late", Now);

        Assert.Equal("Tentatively accepted: Quarterly review", message.Subject);
        Assert.Equal("olga@corp.example", ((MailboxAddress)message.To.Single()).Address);
        var calendar = message.BodyParts.OfType<TextPart>().Single(p => p.ContentType.IsMimeType("text", "calendar"));
        Assert.Equal("REPLY", calendar.ContentType.Parameters["method"]);
        var reply = ICalComponent.Parse(calendar.Text);
        var vevent = reply.ChildrenNamed("VEVENT").Single();
        Assert.Equal("REPLY", reply.Property("METHOD")!.Value);
        Assert.Equal("040000008200E00074C5B7101A82E008", vevent.Property("UID")!.Value);
        Assert.Equal("2", vevent.Property("SEQUENCE")!.Value);
        Assert.Equal("ATTENDEE;CN=Alice;PARTSTAT=TENTATIVE:mailto:Alice@Example.test", Assert.Single(vevent.PropertiesNamed("ATTENDEE")).ToString());
        Assert.Equal("Might be late", vevent.Property("COMMENT")!.Text);
        Assert.Equal("20261029T120000Z", vevent.Property("DTSTAMP")!.Value);
        // The time zone the times refer to travels with them.
        Assert.Single(reply.ChildrenNamed("VTIMEZONE"));
    }

    [Theory]
    [InlineData("not calendar data")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:no uid\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nEND:VTODO\r\nEND:VCALENDAR\r\n")]
    public void Unusable_data_is_refused(string text) =>
        Assert.Throws<FormatException>(() => Invitation.Parse(text));

    [Theory]
    [InlineData("040000008200E00074C5B7101A82E008", "040000008200E00074C5B7101A82E008.ics")]
    [InlineData("abc@google.com", "abc@google.com.ics")]
    public void Safe_uids_name_the_object(string uid, string expected) =>
        Assert.Equal(expected, NextcloudCalendars.ObjectName(uid));

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("{curly}")]
    public void Other_uids_get_a_random_name(string uid) =>
        Assert.Matches("^[0-9a-f-]{36}\\.ics$", NextcloudCalendars.ObjectName(uid));

    private static string Lf(string text) => text.Replace("\n", "\r\n");

    private static string Unfold(string text) => text.Replace("\r\n ", "");
}
