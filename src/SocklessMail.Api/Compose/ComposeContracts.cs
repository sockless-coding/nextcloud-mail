using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Compose;

public enum ReplyMode
{
    Reply,
    ReplyAll,
    Forward,

    /// <summary>Continue a saved draft: its fields, text and attachments as they are.</summary>
    Edit,
}

/// <summary>Where a reply, forward or continued draft comes from.</summary>
public sealed record DraftSource(string FolderId, string MessageId, ReplyMode Mode);

/// <summary>
/// A prefilled message for the composer. Address fields are RFC 5322 address lists as the user
/// would type them ("Ann &lt;ann@example.com&gt;, bob@example.com"). <see cref="Html"/> is the
/// same content for the rich-text editor (see <see cref="OutgoingHtml"/>); its images refer to
/// parts of the source by Content-ID, which <see cref="InlineImages"/> lists to show them.
/// </summary>
public sealed record DraftDto(
    string To, string Cc, string Bcc, string Subject, string Text, string Html, AttachmentDto[] ForwardedAttachments, DraftSource Source,
    InlineImageDto[] InlineImages);

/// <summary>An image in a draft's HTML (<c>src="cid:ContentId"</c>) and the part of the source holding it.</summary>
public sealed record InlineImageDto(string ContentId, string PartId);

/// <param name="ForwardPartIds">For a forward or a continued draft: which of the source's attachments to include.</param>
public sealed record SendSource(string FolderId, string MessageId, ReplyMode Mode, IReadOnlyList<string>? ForwardPartIds);

/// <summary>A saved draft (in the account's Drafts folder).</summary>
public sealed record SavedDraftRef(string FolderId, string MessageId);

/// <summary>
/// A message to send. With <see cref="Html"/>, it goes out as HTML with a plain-text alternative
/// made from it, and <see cref="Text"/> is ignored. Address fields as in <see cref="DraftDto"/>.
/// <see cref="Draft"/> is the saved draft of this message, removed once it is sent.
/// </summary>
public sealed record SendInput(
    string? To,
    string? Cc,
    string? Bcc,
    string? Subject,
    string? Text,
    IReadOnlyList<string>? AttachmentIds,
    SendSource? Source,
    string? Html = null,
    SavedDraftRef? Draft = null);

/// <summary>
/// A draft to save, built like <see cref="SendInput"/> but nothing is required and addresses may
/// still be incomplete. <see cref="Replaces"/> is the previously saved version, removed once the
/// new one is stored.
/// </summary>
public sealed record SaveDraftInput(
    string? To,
    string? Cc,
    string? Bcc,
    string? Subject,
    string? Text,
    string? Html,
    IReadOnlyList<string>? AttachmentIds,
    SendSource? Source,
    SavedDraftRef? Replaces);

/// <summary>
/// Where the draft was saved. <see cref="MessageId"/> is null when the server did not say
/// (no UIDPLUS) and the message could not be found by its Message-ID. For a continued draft
/// (source mode Edit) the saved copy becomes the source: <see cref="Source"/> and
/// <see cref="Attachments"/> replace the composer's source and attachments, and the uploads it
/// included are gone.
/// </summary>
public sealed record SaveDraftResult(string FolderId, string? MessageId, DraftSource? Source, AttachmentDto[]? Attachments);

/// <summary>
/// The message was sent. Saving the copy to Sent and flagging the original happen afterwards and
/// may fail on their own; <see cref="Warning"/> says what did not work, the send is never repeated.
/// </summary>
public sealed record SendResult(bool SavedToSent, string? SentFolderId, string? Warning);
