using SocklessMail.Api.Compose;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Scheduling;

/// <summary>A message to send at <see cref="SendAt"/>; the other fields as in <see cref="SendInput"/>.</summary>
public sealed record ScheduleInput(
    DateTimeOffset? SendAt,
    string? To,
    string? Cc,
    string? Bcc,
    string? Subject,
    string? Text,
    IReadOnlyList<string>? AttachmentIds,
    SendSource? Source,
    string? Html = null,
    SavedDraftRef? Draft = null)
{
    public SendInput ToSendInput() => new(To, Cc, Bcc, Subject, Text, AttachmentIds, Source, Html, Draft);
}

public sealed record RescheduleInput(DateTimeOffset? SendAt);

/// <param name="Error">Why the last attempt failed: for <see cref="ScheduledStatus.Failed"/>, or a
/// <see cref="ScheduledStatus.Scheduled"/> message that is retried at <see cref="DueAt"/>.</param>
public sealed record ScheduledMessageDto(
    Guid Id,
    Guid AccountId,
    DateTimeOffset SendAt,
    DateTimeOffset DueAt,
    ScheduledStatus Status,
    string Subject,
    AddressDto[] Recipients,
    string Preview,
    bool HasAttachments,
    long Size,
    int Attempts,
    string? ErrorCode,
    string? Error,
    DateTimeOffset CreatedAt)
{
    public static ScheduledMessageDto From(ScheduledMessage m) => new(
        m.Id, m.AccountId, m.SendAt, m.DueAt, m.Status, m.Subject, m.Recipients, m.Preview, m.HasAttachments, m.Size,
        m.Attempts, m.ErrorCode, m.Error, m.CreatedAt);
}

public sealed record ScheduledAttachmentDto(string FileName, string ContentType, long Size);

/// <summary>A scheduled message to read: <see cref="Html"/> is sanitized like received mail, without remote content.</summary>
public sealed record ScheduledMessageDetailDto(
    ScheduledMessageDto Message, AddressDto[] To, AddressDto[] Cc, AddressDto[] Bcc, string Html, ScheduledAttachmentDto[] Attachments);

/// <summary>
/// What "send now" did. Not sent: <see cref="Message"/> is the scheduled message as it stands
/// (failed, or waiting for a retry), with the reason.
/// </summary>
public sealed record SendNowResult(bool Sent, string? Warning, ScheduledMessageDto? Message);

/// <summary>A cancelled message, back in Drafts. <see cref="Draft"/> is null when the server did not say where.</summary>
public sealed record CancelScheduledResult(SavedDraftRef? Draft);
