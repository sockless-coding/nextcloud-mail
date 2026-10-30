using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Tags;

/// <summary>
/// A tag: a name and a color, shared by all of a user's accounts. On the mail server a tag is the
/// IMAP keyword <see cref="Keyword"/>, which stays the same when the tag is renamed, so tagged
/// messages keep their tag.
/// </summary>
public sealed class MailTag
{
    public Guid Id { get; init; }
    public required string OwnerUserId { get; init; }
    public required string Name { get; set; }

    /// <summary>"#rrggbb".</summary>
    public required string Color { get; set; }

    public required string Keyword { get; init; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Where an account keeps which messages have which tags.</summary>
public enum TagStorage
{
    /// <summary>Not tagged yet; the server's keywords are read as tags.</summary>
    Unknown,

    /// <summary>IMAP keywords on the server, which other mail programs see as well.</summary>
    Keywords,

    /// <summary>
    /// The server keeps no keywords (Outlook.com, Exchange): the tag index (<see cref="TaggedMessage"/>)
    /// is the only record, and follows a message by its Message-ID.
    /// </summary>
    Local,

    /// <summary>
    /// Microsoft with Graph consent: tags are Outlook categories (same name), which Outlook shows
    /// too. The tag index works as for <see cref="Local"/> and follows the categories (see
    /// <see cref="OutlookCategories"/>).
    /// </summary>
    Graph,
}

public static class TagStorages
{
    /// <summary>The app keeps the tags in its index (with or without Outlook categories), not as IMAP keywords.</summary>
    public static bool IsAppKept(this TagStorage storage) => storage is TagStorage.Local or TagStorage.Graph;
}

/// <summary>
/// A message with tags, wherever it is in the account: the index the tag folders are read from,
/// so they need no search across folders. For accounts with <see cref="TagStorage.Local"/> it is
/// also the only record of the tags. The list fields are copied from the message so a tag folder
/// can show it without the folder's cache.
/// </summary>
public sealed class TaggedMessage
{
    public long Id { get; init; }
    public Guid AccountId { get; init; }
    public required string OwnerUserId { get; init; }

    /// <summary>The folder's cache key (<see cref="Sync.FolderCache.Key"/>).</summary>
    public required string FolderName { get; set; }

    public uint UidValidity { get; set; }

    /// <summary>0 while the message's place is unknown (moved by another mail program): found again by <see cref="MessageIdHeader"/>.</summary>
    public uint Uid { get; set; }

    /// <summary>The Message-ID header, without angle brackets; null when the message has none.</summary>
    public string? MessageIdHeader { get; set; }

    /// <summary>The message's keywords as <see cref="Keywords.Format"/> stores them.</summary>
    public required string Keywords { get; set; }

    public required string Subject { get; set; }
    public required AddressDto[] From { get; set; }
    public required AddressDto[] To { get; set; }
    public DateTimeOffset Date { get; set; }
    public bool Seen { get; set; }
    public bool Flagged { get; set; }
    public bool Answered { get; set; }
    public bool HasAttachments { get; set; }
    public long Size { get; set; }
    public string? Preview { get; set; }
    public string? BimiDomain { get; set; }
    public string? BimiSelector { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// What the tag scan (<see cref="TagScanner"/>) last saw of a folder, so an unchanged folder is
/// skipped: same UIDVALIDITY, HIGHESTMODSEQ (CONDSTORE) and tag keywords. Without CONDSTORE a
/// folder is searched on every round.
/// </summary>
public sealed class TagScanState
{
    public Guid AccountId { get; init; }
    public required string OwnerUserId { get; init; }
    public required string FolderName { get; init; }
    public uint UidValidity { get; set; }
    public ulong HighestModSeq { get; set; }

    /// <summary>The tag keywords searched for, sorted and joined; a new tag means a new search.</summary>
    public required string KeywordSet { get; set; }

    public DateTimeOffset ScannedAt { get; set; }
}
