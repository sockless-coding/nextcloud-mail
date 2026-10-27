using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Compose;

public enum ReplyMode
{
    Reply,
    ReplyAll,
    Forward,
}

/// <summary>Where a reply or forward comes from.</summary>
public sealed record DraftSource(string FolderId, string MessageId, ReplyMode Mode);

/// <summary>
/// A prefilled message for the composer. Address fields are RFC 5322 address lists as the user
/// would type them ("Ann &lt;ann@example.com&gt;, bob@example.com").
/// </summary>
public sealed record DraftDto(string To, string Cc, string Subject, string Text, AttachmentDto[] ForwardedAttachments, DraftSource Source);

/// <param name="ForwardPartIds">For a forward: which of the original's attachments to include.</param>
public sealed record SendSource(string FolderId, string MessageId, ReplyMode Mode, IReadOnlyList<string>? ForwardPartIds);

/// <summary>A message to send. Plain text for now; address fields as in <see cref="DraftDto"/>.</summary>
public sealed record SendInput(
    string? To,
    string? Cc,
    string? Bcc,
    string? Subject,
    string? Text,
    IReadOnlyList<string>? AttachmentIds,
    SendSource? Source);

/// <summary>
/// The message was sent. Saving the copy to Sent and flagging the original happen afterwards and
/// may fail on their own; <see cref="Warning"/> says what did not work, the send is never repeated.
/// </summary>
public sealed record SendResult(bool SavedToSent, string? SentFolderId, string? Warning);
