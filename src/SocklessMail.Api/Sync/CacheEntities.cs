using MailKit;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Sync;

/// <summary>
/// Sync state of one IMAP folder. The cache holds a contiguous window of the folder: every message
/// with a UID from <see cref="WindowStart"/> to <see cref="LastUid"/> that still exists on the
/// server has a <see cref="CachedMessage"/> row. The window starts with the newest page and grows
/// downwards as older pages are requested; new mail extends it upwards.
/// </summary>
public sealed class CachedFolder
{
    public Guid Id { get; init; }
    public Guid AccountId { get; init; }

    /// <summary>Copied from the account, for the per-user query filter.</summary>
    public required string OwnerUserId { get; init; }

    public required string FullName { get; init; }

    public uint UidValidity { get; set; }

    /// <summary>HIGHESTMODSEQ at the last sync; 0 when the server has no CONDSTORE (or no persistent mod-sequences).</summary>
    public ulong HighestModSeq { get; set; }

    /// <summary>Lowest UID of the cached window; null before the first sync. 1 means the window reaches the oldest message.</summary>
    public uint? WindowStart { get; set; }

    /// <summary>Every UID up to this one has been seen; anything above is new.</summary>
    public uint LastUid { get; set; }

    public uint? UidNext { get; set; }
    public int Total { get; set; }
    public int? Unread { get; set; }

    /// <summary>
    /// Bumped by every sync or local change that changes rows. Rows and removals carry the value
    /// of the change that touched them last, so "everything after token N" is a simple query.
    /// </summary>
    public long ChangeSeq { get; set; }

    /// <summary>Removals up to this change were pruned (or the window was rebuilt); older tokens must reload.</summary>
    public long ResetSeq { get; set; }

    public DateTimeOffset? SyncedAt { get; set; }
}

/// <summary>The list fields of one message. Bodies are never cached.</summary>
public sealed class CachedMessage
{
    public Guid FolderId { get; init; }
    public uint Uid { get; init; }
    public required string OwnerUserId { get; init; }

    public required string Subject { get; init; }
    public required AddressDto[] From { get; init; }
    public required AddressDto[] To { get; init; }
    public DateTimeOffset Date { get; init; }
    public bool HasAttachments { get; init; }
    public long Size { get; init; }

    /// <summary>System flags only (\Seen, \Flagged, \Answered, \Deleted, \Draft); keywords are not kept.</summary>
    public MessageFlags Flags { get; set; }

    public long ChangeSeq { get; set; }

    /// <summary>Subject, names and addresses in <see cref="SearchTerms.Normalize"/>d form, for searching the cache.</summary>
    public required string SearchText { get; init; }
}

/// <summary>A message that disappeared from a cached folder, so polling clients can drop it too. Pruned after a day.</summary>
public sealed class RemovedMessage
{
    public long Id { get; init; }
    public Guid FolderId { get; init; }
    public uint Uid { get; init; }
    public required string OwnerUserId { get; init; }
    public long ChangeSeq { get; init; }
    public DateTimeOffset RemovedAt { get; init; }
}
