using MailKit;
using MailKit.Search;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Mailbox;

/// <summary>The quick filters of the message list.</summary>
public enum MessageFilter
{
    None,
    Unread,
    Flagged,
    Attachments,
}

public static class MessageFilters
{
    /// <exception cref="MailboxValidationException">Not one of unread, flagged, attachments.</exception>
    public static MessageFilter Parse(string? value) => value switch
    {
        null or "" => MessageFilter.None,
        "unread" => MessageFilter.Unread,
        "flagged" => MessageFilter.Flagged,
        "attachments" => MessageFilter.Attachments,
        _ => throw new MailboxValidationException("filter", "Use unread, flagged or attachments."),
    };

    /// <summary>
    /// The IMAP search for the filter. IMAP cannot search for attachments, so that one finds
    /// candidates by their Content-Type header (mixed, and signed messages that wrap a mixed part),
    /// and <see cref="NeedsStructureCheck"/> tells the caller to keep only those whose BODYSTRUCTURE
    /// has an attachment.
    /// </summary>
    public static SearchQuery? ImapQuery(MessageFilter filter) => filter switch
    {
        MessageFilter.Unread => SearchQuery.NotSeen,
        MessageFilter.Flagged => SearchQuery.Flagged,
        MessageFilter.Attachments => SearchQuery.HeaderContains("Content-Type", "multipart/mixed")
            .Or(SearchQuery.HeaderContains("Content-Type", "multipart/signed")),
        _ => null,
    };

    public static bool NeedsStructureCheck(MessageFilter filter) => filter == MessageFilter.Attachments;

    /// <summary>The filter on cached rows.</summary>
    public static IQueryable<CachedMessage> Apply(IQueryable<CachedMessage> messages, MessageFilter filter) => filter switch
    {
        MessageFilter.Unread => messages.Where(m => (m.Flags & MessageFlags.Seen) == 0),
        MessageFilter.Flagged => messages.Where(m => (m.Flags & MessageFlags.Flagged) != 0),
        MessageFilter.Attachments => messages.Where(m => m.HasAttachments),
        _ => messages,
    };
}
