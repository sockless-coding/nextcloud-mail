using System.Buffers.Text;
using System.Text;
using MimeKit;
using SocklessMail.Api.Bimi;
using SocklessMail.Api.Senders;

namespace SocklessMail.Api.Mailbox;

/// <summary>A folder. <see cref="Id"/> is the base64url of the IMAP full name, safe in URL paths.</summary>
public sealed record FolderDto(
    string Id,
    string? ParentId,
    string Name,
    string FullName,
    string? SpecialUse,
    bool Selectable,
    int? Total,
    int? Unread);

public sealed record AddressDto(string? Name, string Address)
{
    public static AddressDto[] From(InternetAddressList? list) =>
        list?.Mailboxes.Select(m => new AddressDto(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address)).ToArray() ?? [];
}

public sealed record MessageSummaryDto(
    string Id,
    string Subject,
    AddressDto[] From,
    AddressDto[] To,
    DateTimeOffset Date,
    bool Seen,
    bool Flagged,
    bool Answered,
    bool HasAttachments,
    long Size,
    string? Preview,
    BimiDto? Bimi,
    string[] Keywords);

/// <summary>
/// Newest first by date. <see cref="NextBefore"/> is the cursor for the next (older) page; null at
/// the end. It names the last message's position (a <see cref="UnifiedCursor"/>), so new mail
/// arriving between page loads does not shift or duplicate entries.
/// <see cref="Token"/> is what to pass to the changes endpoint to learn what changed afterwards.
/// </summary>
public sealed record MessagePage(
    IReadOnlyList<MessageSummaryDto> Messages, string? NextBefore, int Total, uint UidValidity, int? Unread, long Token);

public sealed record AttachmentDto(string PartId, string FileName, string ContentType, long? Size);

/// <summary>A part with calendar data: an iMIP invitation/reply/cancellation (with its method) or an .ics file.</summary>
public sealed record CalendarPartDto(string PartId, string? Method);

public sealed record MessageDto(
    string Id,
    string Subject,
    AddressDto[] From,
    AddressDto[] To,
    AddressDto[] Cc,
    AddressDto[] ReplyTo,
    DateTimeOffset Date,
    bool Seen,
    bool Flagged,
    string Html,
    bool HasRemoteContent,
    bool RemoteContentAllowed,
    AttachmentDto[] Attachments,
    BimiDto? Bimi,
    CalendarPartDto[] CalendarParts,
    SenderWarning? SenderWarning,
    string[] Keywords);

/// <summary>The message's header block exactly as the server has it, folded lines and encoded words included.</summary>
public sealed record MessageHeadersDto(string Headers);

/// <summary>
/// The raw message for reading. <see cref="Size"/> is the whole message in bytes; when it exceeds
/// <see cref="MailboxService.MaxSourceViewBytes"/>, <see cref="Text"/> holds only the start of it.
/// </summary>
public sealed record MessageSourceDto(string Text, long Size, bool Truncated);

/// <summary>Opaque ids for the URL: folder full names and "{uidvalidity}-{uid}" message ids.</summary>
public static class MailboxIds
{
    public static string FolderId(string fullName) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(fullName));

    public static string? FolderFullName(string folderId)
    {
        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(folderId));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string MessageId(uint uidValidity, uint uid) => $"{uidValidity}-{uid}";

    public static bool TryParseMessageId(string id, out uint uidValidity, out uint uid)
    {
        uidValidity = uid = 0;
        var parts = id.Split('-');
        return parts.Length == 2 && uint.TryParse(parts[0], out uidValidity) && uint.TryParse(parts[1], out uid) && uid > 0;
    }

    /// <summary>MailKit's part specifier ("1.2"); the root part of a non-multipart message has "", sent as "0".</summary>
    public static string PartId(string partSpecifier) => partSpecifier.Length == 0 ? "0" : partSpecifier;

    public static string PartSpecifier(string partId) => partId == "0" ? "" : partId;
}

/// <summary>The requested folder or message does not exist (any more); answered with 404.</summary>
public sealed class MailboxNotFoundException(string what) : Exception($"{what} was not found.");

/// <summary>
/// Matches of a folder search or filter, newest first, paged like <see cref="MessagePage"/>.
/// <see cref="Total"/> counts all matches; null when only the pages read so far were checked (the
/// attachments filter, see <see cref="MessageFilters.ImapQuery"/>).
/// </summary>
public sealed record SearchPage(IReadOnlyList<MessageSummaryDto> Messages, uint? NextBefore, int? Total, uint UidValidity);

/// <summary>A cached message matching a search across folders, with where it lives.</summary>
public sealed record SearchHit(Guid AccountId, string FolderId, string FolderFullName, MessageSummaryDto Message);

/// <summary>Newest first; <see cref="More"/> means there were more matches than returned.</summary>
public sealed record CachedSearchResult(IReadOnlyList<SearchHit> Hits, bool More);

/// <summary>
/// A unified or smart folder (<see cref="UnifiedView"/>): one list across all of a user's accounts,
/// newest first. <see cref="NextBefore"/> is the cursor for the next (older) page; <see cref="Token"/>
/// is what to pass to the changes endpoint (on the first page). Accounts that could not be read are in
/// <see cref="Errors"/>; their messages are shown as last cached. <see cref="Total"/> counts the
/// folders' messages, or a smart folder's matches (null when not every match was counted);
/// <see cref="Unread"/> is null for a smart folder.
/// </summary>
public sealed record UnifiedPage(
    IReadOnlyList<SearchHit> Messages, string? NextBefore, int? Total, int? Unread, string Token, IReadOnlyList<AccountError> Errors);

/// <summary>Like <see cref="Sync.FolderChanges"/>, across the folders of a unified or smart folder.</summary>
public sealed record UnifiedChanges(
    string Token, bool Reset, int Total, int? Unread,
    IReadOnlyList<SearchHit> Messages, IReadOnlyList<RemovedHit> Removed, IReadOnlyList<AccountError> Errors);

/// <summary>
/// A quick filter over a unified folder, newest first, from each mail server. <see cref="NextBefore"/>
/// holds where each folder stands; <see cref="Total"/> is null when not every match was counted.
/// </summary>
public sealed record UnifiedFilterPage(IReadOnlyList<SearchHit> Messages, string? NextBefore, int? Total, IReadOnlyList<AccountError> Errors);

public sealed record RemovedHit(Guid AccountId, string FolderId, string MessageId);

/// <summary>Why one account's part of a combined view is missing or stale; codes as the per-account endpoints use them.</summary>
public sealed record AccountError(Guid AccountId, string Error, string Message);

/// <summary>A message as plain text, with its sender, date and Message-ID header (without the angle brackets).</summary>
public sealed record MessageText(string Subject, MailboxAddress? From, DateTimeOffset Date, string Text, string? MessageId);
