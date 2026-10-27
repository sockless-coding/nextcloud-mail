using System.Buffers.Text;
using System.Text;
using MimeKit;

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
    long Size);

/// <summary>
/// Newest first. <see cref="NextBefore"/> is the cursor for the next (older) page; null at the end.
/// UID-based, so new mail arriving between page loads does not shift or duplicate entries.
/// </summary>
public sealed record MessagePage(IReadOnlyList<MessageSummaryDto> Messages, uint? NextBefore, int Total, uint UidValidity);

public sealed record AttachmentDto(string PartId, string FileName, string ContentType, long? Size);

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
    AttachmentDto[] Attachments);

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
