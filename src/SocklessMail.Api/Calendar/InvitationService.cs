using MimeKit;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Calendar;

/// <summary>The event as it is in the user's calendar.</summary>
public sealed record ExistingDto(
    string CalendarId, string CalendarName, string? Color, bool Writable,
    string? MyStatus, int Sequence, bool Outdated, string? ReplierStatus);

/// <summary>
/// A message's calendar data and what the user can do with it. <see cref="Event"/> describes
/// the first event (of <see cref="EventCount"/> in a plain .ics file).
/// </summary>
public sealed record InvitationDto(
    string Method,
    string Uid,
    EventDto Event,
    int EventCount,
    PersonDto? Organizer,
    bool IsOrganizer,
    PersonDto? Me,
    bool Rsvp,
    PersonDto? Replier,
    ExistingDto? Existing);

public sealed record RespondInput(string? Response, string? CalendarId, bool? SendReply, string? Comment);

public sealed record ImportInput(string? CalendarId);

public sealed record ImportResult(int Added, int Updated, int Skipped, InvitationDto Invitation);

/// <summary>
/// Invitations in mail, against the user's Nextcloud calendars. The app is the scheduling agent
/// for what it writes: objects are stored with SCHEDULE-AGENT=CLIENT (Nextcloud would otherwise
/// mail the organizer itself, from its own address), the reply goes out from the mail account,
/// and the object is then stored once more without it, so later changes made in the Calendar
/// app are scheduled by Nextcloud as usual. That second write changes no participation status,
/// so Nextcloud sends nothing for it.
/// </summary>
public sealed class InvitationService(
    MailboxService mailbox, NextcloudCalendars calendars, TimeProvider clock, ILogger<InvitationService> logger)
{
    private static readonly Dictionary<string, string> Responses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["accepted"] = "ACCEPTED",
        ["tentative"] = "TENTATIVE",
        ["declined"] = "DECLINED",
    };

    private sealed record Loaded(Invitation Invitation, IReadOnlyList<CalendarInfo> Calendars);

    public Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string userId, CancellationToken ct) => calendars.ListAsync(userId, ct);

    public async Task<InvitationDto> GetAsync(string userId, MailAccount account, string folderId, string messageId, string partId, CancellationToken ct)
    {
        var loaded = await LoadAsync(userId, account, folderId, messageId, partId, ct);
        return await DescribeAsync(userId, account, loaded, ct);
    }

    public async Task<InvitationDto> RespondAsync(
        string userId, MailAccount account, string folderId, string messageId, string partId, RespondInput input, CancellationToken ct)
    {
        if (!Responses.TryGetValue(input.Response ?? "", out var partStatus))
        {
            throw new MailboxValidationException("response", "Answer accepted, tentative or declined.");
        }

        if (input.Comment is { Length: > 1000 })
        {
            throw new MailboxValidationException("comment", "The comment may be at most 1000 characters.");
        }

        var loaded = await LoadAsync(userId, account, folderId, messageId, partId, ct);
        var invitation = loaded.Invitation;
        if (invitation.Method != "REQUEST")
        {
            throw new MailboxValidationException("response", "This is no invitation to answer.");
        }

        var uid = invitation.Uids[0];
        var events = EventsOf(invitation, uid);
        var main = Invitation.MainEvent(events)!;
        var addresses = Addresses(account);
        var me = Invitation.Attendee(main, addresses);
        var address = me?.Address ?? account.EmailAddress;
        var existing = await calendars.FindAsync(userId, loaded.Calendars, uid, ct);

        // A declined invitation is not added; one already in the calendar keeps the answer.
        string? stored = null;
        CalendarInfo? target = null;
        string? etag = null;
        if (existing is { Calendar.Writable: true } || (existing is null && partStatus != "DECLINED"))
        {
            target = existing?.Calendar ?? WritableCalendar(loaded, input.CalendarId);
            var name = existing?.Name ?? NextcloudCalendars.ObjectName(uid);
            stored = Invitation.ForAttendee(events, invitation.Root, existing is null ? null : ICalComponent.Parse(existing.Text),
                address, me?.Parameter("CN") ?? account.SenderName, partStatus, clientScheduling: true);
            etag = await calendars.PutAsync(userId, target.Id, name, stored, existing?.ETag, create: existing is null, ct);
        }

        if (input.SendReply != false && Invitation.Organizer(main) is not null)
        {
            var reply = Invitation.BuildReply(events, invitation.Root, new MailboxAddress(account.SenderName, account.EmailAddress),
                me, partStatus, input.Comment, clock.GetUtcNow());
            try
            {
                await mailbox.SendGeneratedAsync(account, reply, ct);
            }
            catch (Exception e) when (e is MailConnectionException or MailboxValidationException)
            {
                throw new CalendarException("reply_failed",
                    (stored is null ? "" : "Your answer is saved in the calendar, but ") + $"the reply to the organizer could not be sent: {e.Message}",
                    StatusCodes.Status502BadGateway);
            }
        }

        if (stored is not null)
        {
            await HandBackAsync(userId, target!.Id, existing?.Name ?? NextcloudCalendars.ObjectName(uid), stored, etag, ct);
        }

        logger.LogInformation("Answered an invitation ({Response}) from account {AccountId}", partStatus, account.Id);
        return await DescribeAsync(userId, account, loaded, ct);
    }

    /// <summary>Adds the events of a plain .ics or published calendar to a calendar, updating ones already there.</summary>
    public async Task<ImportResult> ImportAsync(
        string userId, MailAccount account, string folderId, string messageId, string partId, ImportInput input, CancellationToken ct)
    {
        var loaded = await LoadAsync(userId, account, folderId, messageId, partId, ct);
        var invitation = loaded.Invitation;
        if (invitation.Method is "CANCEL" or "REPLY" or "COUNTER" or "DECLINECOUNTER" or "REFRESH")
        {
            throw new MailboxValidationException("calendarId", "This calendar message cannot be added as an event.");
        }

        int added = 0, updated = 0, skipped = 0;
        foreach (var uid in invitation.Uids)
        {
            var existing = await calendars.FindAsync(userId, loaded.Calendars, uid, ct);
            if (existing is { Calendar.Writable: false })
            {
                skipped++;
                continue;
            }

            var target = existing?.Calendar ?? WritableCalendar(loaded, input.CalendarId);
            var name = existing?.Name ?? NextcloudCalendars.ObjectName(uid);
            var text = Invitation.ForAttendee(EventsOf(invitation, uid), invitation.Root,
                existing is null ? null : ICalComponent.Parse(existing.Text), account.EmailAddress, null, null, clientScheduling: true);
            var etag = await calendars.PutAsync(userId, target.Id, name, text, existing?.ETag, create: existing is null, ct);
            await HandBackAsync(userId, target.Id, name, text, etag, ct);
            if (existing is null)
            {
                added++;
            }
            else
            {
                updated++;
            }
        }

        return new ImportResult(added, updated, skipped, await DescribeAsync(userId, account, loaded, ct));
    }

    /// <summary>
    /// A cancellation removes the event (or the cancelled occurrences); an attendee's reply
    /// updates their status in the organizer's event. Both only touch an event already there.
    /// </summary>
    public async Task<InvitationDto> ApplyAsync(
        string userId, MailAccount account, string folderId, string messageId, string partId, CancellationToken ct)
    {
        var loaded = await LoadAsync(userId, account, folderId, messageId, partId, ct);
        var invitation = loaded.Invitation;
        var uid = invitation.Uids[0];
        var events = EventsOf(invitation, uid);
        var existing = await calendars.FindAsync(userId, loaded.Calendars, uid, ct)
                       ?? throw new CalendarException("not_in_calendar", "The event is not in your calendar.", StatusCodes.Status404NotFound);
        if (!existing.Calendar.Writable)
        {
            throw new CalendarException("read_only", "This calendar is read-only.", StatusCodes.Status403Forbidden);
        }

        var current = ICalComponent.Parse(existing.Text);
        switch (invitation.Method)
        {
            case "CANCEL":
                // Silenced first: removing an event as an attendee makes Nextcloud decline it to the organizer.
                var silenced = Invitation.SetClientScheduling(current.Clone(), client: true).Serialize();
                var etag = await calendars.PutAsync(userId, existing.Calendar.Id, existing.Name, silenced, existing.ETag, create: false, ct);
                if (Invitation.Cancel(events, current) is { } remaining)
                {
                    var text = Invitation.SetClientScheduling(remaining, client: true).Serialize();
                    etag = await calendars.PutAsync(userId, existing.Calendar.Id, existing.Name, text, etag, create: false, ct);
                    await HandBackAsync(userId, existing.Calendar.Id, existing.Name, text, etag, ct);
                }
                else
                {
                    await calendars.DeleteAsync(userId, existing.Calendar.Id, existing.Name, etag, ct);
                }

                break;
            case "REPLY":
                if (Invitation.ApplyReply(events, current) is { } updated)
                {
                    await calendars.PutAsync(userId, existing.Calendar.Id, existing.Name, updated.Serialize(), existing.ETag, create: false, ct);
                }

                break;
            default:
                throw new MailboxValidationException("partId", "Only cancellations and replies change an event this way.");
        }

        return await DescribeAsync(userId, account, loaded, ct);
    }

    /// <summary>
    /// Stores the object again without SCHEDULE-AGENT=CLIENT. Best effort: if it fails, the event
    /// is in the calendar and only later answers from the Calendar app are not sent by Nextcloud.
    /// </summary>
    private async Task HandBackAsync(string userId, string calendarId, string name, string stored, string? etag, CancellationToken ct)
    {
        try
        {
            var text = Invitation.SetClientScheduling(ICalComponent.Parse(stored), client: false).Serialize();
            await calendars.PutAsync(userId, calendarId, name, text, etag, create: false, ct);
        }
        catch (CalendarException e)
        {
            logger.LogWarning("Could not hand scheduling back to Nextcloud: {Code}", e.Code);
        }
    }

    private async Task<Loaded> LoadAsync(string userId, MailAccount account, string folderId, string messageId, string partId, CancellationToken ct)
    {
        var text = await mailbox.GetCalendarTextAsync(account, folderId, messageId, partId, ct);
        Invitation invitation;
        try
        {
            invitation = Invitation.Parse(text);
        }
        catch (FormatException e)
        {
            throw new CalendarException("invalid_data", $"The calendar data in this message cannot be read: {e.Message}", StatusCodes.Status422UnprocessableEntity);
        }

        return new Loaded(invitation, await calendars.ListAsync(userId, ct));
    }

    private async Task<InvitationDto> DescribeAsync(string userId, MailAccount account, Loaded loaded, CancellationToken ct)
    {
        var invitation = loaded.Invitation;
        var uid = invitation.Uids[0];
        var events = EventsOf(invitation, uid);
        var main = Invitation.MainEvent(events)!;
        var addresses = Addresses(account);
        var organizer = Invitation.Organizer(main);
        var me = Invitation.Attendee(main, addresses);
        var replier = invitation.Method == "REPLY"
            ? main.PropertiesNamed("ATTENDEE").FirstOrDefault(a => a.Address is not null) is { } r
                ? new PersonDto(r.Address!, r.Parameter("CN"), r.Parameter("PARTSTAT")?.ToUpperInvariant())
                : null
            : null;

        ExistingDto? existingDto = null;
        if (await calendars.FindAsync(userId, loaded.Calendars, uid, ct) is { } existing)
        {
            var stored = ICalComponent.Parse(existing.Text).ChildrenNamed("VEVENT").ToList();
            var key = Invitation.Key(main);
            var match = stored.FirstOrDefault(e => Invitation.Key(e) == key) ?? Invitation.MainEvent(stored);
            var sequence = match is null ? 0 : Invitation.Sequence(match);
            existingDto = new ExistingDto(
                existing.Calendar.Id, existing.Calendar.Name, existing.Calendar.Color, existing.Calendar.Writable,
                match is null ? null : Invitation.Attendee(match, addresses)?.Parameter("PARTSTAT")?.ToUpperInvariant(),
                sequence,
                invitation.Method is "REQUEST" or "CANCEL" && Invitation.Sequence(main) < sequence,
                replier is null || match is null ? null : Invitation.Attendee(match, [replier.Address])?.Parameter("PARTSTAT")?.ToUpperInvariant());
        }

        return new InvitationDto(
            invitation.Method,
            uid,
            Invitation.Describe(main, invitation.Root),
            invitation.Uids.Count,
            organizer,
            organizer is not null && addresses.Contains(organizer.Address, StringComparer.OrdinalIgnoreCase),
            me is null ? null : new PersonDto(me.Address!, me.Parameter("CN"), me.Parameter("PARTSTAT")?.ToUpperInvariant()),
            me is null || !string.Equals(me.Parameter("RSVP"), "FALSE", StringComparison.OrdinalIgnoreCase),
            replier,
            existingDto);
    }

    private static CalendarInfo WritableCalendar(Loaded loaded, string? calendarId) =>
        loaded.Calendars.FirstOrDefault(c => c.Id == calendarId && c.Writable)
        ?? throw new MailboxValidationException("calendarId", "Choose a calendar you can add events to.");

    private static List<ICalComponent> EventsOf(Invitation invitation, string uid) =>
        [.. invitation.Events.Where(e => Invitation.Uid(e) == uid)];

    /// <summary>The account's own addresses: its email address, and its login when that is one too.</summary>
    private static string[] Addresses(MailAccount account) =>
        account.Username.Contains('@') && !string.Equals(account.Username, account.EmailAddress, StringComparison.OrdinalIgnoreCase)
            ? [account.EmailAddress, account.Username]
            : [account.EmailAddress];
}
