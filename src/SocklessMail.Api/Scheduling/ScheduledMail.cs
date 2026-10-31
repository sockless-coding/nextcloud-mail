using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.IO;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Compose;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Scheduling;

/// <summary>
/// The current user's scheduled messages: scheduling, listing, rescheduling, sending now and
/// cancelling. Sending itself is <see cref="ScheduledSender"/>'s.
/// </summary>
public sealed class ScheduledMail(
    MailDbContext db, MailboxService mailbox, ScheduledMessageFiles files, ScheduledSender sender, ScheduledSenderOptions options,
    MailPush push, TimeProvider clock)
{
    public static readonly TimeSpan MaxLead = TimeSpan.FromDays(366);

    public async Task<ScheduledMessageDto> ScheduleAsync(MailAccount account, ScheduleInput input, CancellationToken ct)
    {
        var sendAt = CheckSendAt(input.SendAt);
        var id = Guid.NewGuid();
        // A continued draft is no original to mark (it is removed once scheduled).
        var original = input.Source is { Mode: not ReplyMode.Edit } source ? source : null;
        ScheduledMessage? row = null;
        await mailbox.ScheduleAsync(account, input.ToSendInput(), async (message, token) =>
        {
            var size = await files.SaveAsync(id, message, token);
            var now = clock.GetUtcNow();
            row = new ScheduledMessage
            {
                Id = id,
                OwnerUserId = account.OwnerUserId,
                AccountId = account.Id,
                SendAt = sendAt,
                DueAt = sendAt,
                Status = ScheduledStatus.Scheduled,
                Subject = message.Subject ?? "",
                Recipients = [.. message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
                    .Select(m => new AddressDto(string.IsNullOrEmpty(m.Name) ? null : m.Name, m.Address))],
                Preview = MessagePreview.Normalize(message.TextBody),
                HasAttachments = message.Attachments.Any(),
                Size = size,
                MessageIdHeader = message.MessageId ?? "",
                SourceFolderId = original?.FolderId,
                SourceMessageId = original?.MessageId,
                SourceMode = original?.Mode,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ScheduledMessages.Add(row);
            try
            {
                await db.SaveChangesAsync(token);
            }
            catch
            {
                files.Delete(id);
                throw;
            }
        }, ct);

        sender.Wake();
        await push.ScheduledChangedAsync(account.OwnerUserId);
        return ScheduledMessageDto.From(row!);
    }

    public async Task<IReadOnlyList<ScheduledMessageDto>> ListAsync(Guid? accountId, CancellationToken ct)
    {
        var query = db.ScheduledMessages.AsNoTracking();
        if (accountId is { } id)
        {
            query = query.Where(m => m.AccountId == id);
        }

        var rows = await query.OrderBy(m => m.SendAt).ToListAsync(ct);
        return rows.Select(ScheduledMessageDto.From).ToList();
    }

    public async Task<ScheduledMessageDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await FindAsync(id, ct);
        var message = await files.LoadAsync(id, ct);
        if (message is null)
        {
            return new ScheduledMessageDetailDto(ScheduledMessageDto.From(row), [], [], [], "", []);
        }

        var body = message.HtmlBody is { } html
            ? html
            : message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsPlain && !p.IsAttachment) is { } text ? MailboxService.PlainTextToHtml(text) : "";
        var attachments = message.Attachments.OfType<MimePart>().Select(part =>
        {
            using var measure = new MeasuringStream();
            part.Content?.DecodeTo(measure);
            return new ScheduledAttachmentDto(part.FileName ?? "attachment", part.ContentType.MimeType, measure.Length);
        }).ToArray();
        return new ScheduledMessageDetailDto(
            ScheduledMessageDto.From(row),
            AddressDto.From(message.To),
            AddressDto.From(message.Cc),
            AddressDto.From(message.Bcc),
            // Its own images are inside the message; nothing is fetched from elsewhere.
            MessageHtmlSanitizer.Sanitize(body, _ => null, null).Html,
            attachments);
    }

    /// <summary>A new time; a failed message is scheduled again with it.</summary>
    public async Task<ScheduledMessageDto> RescheduleAsync(Guid id, RescheduleInput input, CancellationToken ct)
    {
        var sendAt = CheckSendAt(input.SendAt);
        var row = await FindAsync(id, ct);
        var updated = await db.ScheduledMessages
            .Where(m => m.Id == id && m.Status != ScheduledStatus.Sending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(m => m.SendAt, sendAt)
                .SetProperty(m => m.DueAt, sendAt)
                .SetProperty(m => m.Status, ScheduledStatus.Scheduled)
                .SetProperty(m => m.Attempts, 0)
                .SetProperty(m => m.ErrorCode, (string?)null)
                .SetProperty(m => m.Error, (string?)null)
                .SetProperty(m => m.UpdatedAt, clock.GetUtcNow()), ct);
        if (updated == 0)
        {
            throw BeingSent();
        }

        sender.Wake();
        await push.ScheduledChangedAsync(row.OwnerUserId);
        await db.Entry(row).ReloadAsync(ct);
        return ScheduledMessageDto.From(row);
    }

    public async Task<SendNowResult> SendNowAsync(Guid id, CancellationToken ct)
    {
        var row = await FindAsync(id, ct);
        if (!await ScheduledSender.TryClaimAsync(db, id, clock.GetUtcNow(), ct))
        {
            throw BeingSent();
        }

        return await sender.SendClaimedAsync(row.Id, row.OwnerUserId, ct);
    }

    /// <summary>Not sent after all: the message goes back to Drafts, so nothing written is lost.</summary>
    public async Task<CancelScheduledResult> CancelAsync(Guid id, CancellationToken ct)
    {
        var row = await FindAsync(id, ct);
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == row.AccountId, ct)
                      ?? throw new MailboxNotFoundException("The scheduled message");

        // Claimed, so the sender cannot pick it up while it is being moved to Drafts.
        var previous = row.Status;
        if (!await ScheduledSender.TryClaimAsync(db, id, clock.GetUtcNow(), ct))
        {
            throw BeingSent();
        }

        SavedDraftRef? draft = null;
        if (await files.LoadAsync(id, ct) is { } message)
        {
            try
            {
                draft = await mailbox.RestoreDraftAsync(account, message, ct);
            }
            catch
            {
                await db.ScheduledMessages.Where(m => m.Id == id)
                    .ExecuteUpdateAsync(set => set.SetProperty(m => m.Status, previous), CancellationToken.None);
                throw;
            }
        }

        await db.ScheduledMessages.Where(m => m.Id == id).ExecuteDeleteAsync(CancellationToken.None);
        files.Delete(id);
        await push.ScheduledChangedAsync(row.OwnerUserId);
        return new CancelScheduledResult(draft);
    }

    private async Task<ScheduledMessage> FindAsync(Guid id, CancellationToken ct) =>
        await db.ScheduledMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct)
        ?? throw new MailboxNotFoundException("The scheduled message");

    private DateTimeOffset CheckSendAt(DateTimeOffset? value)
    {
        var now = clock.GetUtcNow();
        if (value is not { } sendAt)
        {
            throw new MailboxValidationException("sendAt", "Choose when to send the message.");
        }

        if (sendAt < now + options.MinimumLead)
        {
            throw new MailboxValidationException("sendAt", "Choose a time at least a minute from now.");
        }

        if (sendAt > now + MaxLead)
        {
            throw new MailboxValidationException("sendAt", "Choose a time within a year.");
        }

        return sendAt.ToUniversalTime();
    }

    private static MailboxConflictException BeingSent() =>
        new("scheduled_sending", "The message is being sent right now.");
}
