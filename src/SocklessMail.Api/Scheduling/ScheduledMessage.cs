using SocklessMail.Api.Compose;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Scheduling;

public enum ScheduledStatus
{
    /// <summary>Waiting for <see cref="ScheduledMessage.DueAt"/>.</summary>
    Scheduled,

    /// <summary>Being delivered right now. Found at startup, it may or may not have gone out.</summary>
    Sending,

    /// <summary>Gave up; <see cref="ScheduledMessage.Error"/> says why. Waits for the user.</summary>
    Failed,
}

/// <summary>
/// A message to be sent later. The message itself was built when it was scheduled and is kept
/// encrypted on disk (<see cref="ScheduledMessageFiles"/>); this row holds what the list shows
/// and where sending stands. Removed once the message is sent.
/// </summary>
public sealed class ScheduledMessage
{
    public Guid Id { get; set; }
    public required string OwnerUserId { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>When the user wants it sent.</summary>
    public DateTimeOffset SendAt { get; set; }

    /// <summary>When the sender next tries: <see cref="SendAt"/>, or later after a failed attempt.</summary>
    public DateTimeOffset DueAt { get; set; }

    public ScheduledStatus Status { get; set; }

    /// <summary>Failed attempts since it was last scheduled.</summary>
    public int Attempts { get; set; }

    public string? ErrorCode { get; set; }
    public string? Error { get; set; }

    public string Subject { get; set; } = "";

    /// <summary>To, Cc and Bcc together, for the list.</summary>
    public AddressDto[] Recipients { get; set; } = [];

    public string Preview { get; set; } = "";
    public bool HasAttachments { get; set; }
    public long Size { get; set; }

    /// <summary>The Message-ID header, kept when it is sent.</summary>
    public string MessageIdHeader { get; set; } = "";

    /// <summary>The message replied to or forwarded, marked once this is sent.</summary>
    public string? SourceFolderId { get; set; }

    public string? SourceMessageId { get; set; }
    public ReplyMode? SourceMode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public SendSource? Source => SourceFolderId is { } folder && SourceMessageId is { } message && SourceMode is { } mode
        ? new SendSource(folder, message, mode, null)
        : null;
}
