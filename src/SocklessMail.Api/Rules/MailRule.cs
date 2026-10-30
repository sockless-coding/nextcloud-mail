using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Rules;

/// <summary>
/// One of an account's rules for new INBOX mail. Rules run in <see cref="SortOrder"/>; conditions
/// and actions are kept as JSON (<see cref="RuleCondition"/>, <see cref="RuleAction"/>).
/// Deleted with the account.
/// </summary>
public sealed class MailRule
{
    public Guid Id { get; init; }
    public Guid AccountId { get; init; }
    public required string OwnerUserId { get; init; }
    public int SortOrder { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; }

    /// <summary>All conditions must match; otherwise any one.</summary>
    public bool MatchAll { get; set; }

    /// <summary>Later rules are not tried on a message this rule matched.</summary>
    public bool Stop { get; set; }

    public required string Conditions { get; set; }
    public required string Actions { get; set; }

    /// <summary>Why the rule's actions last failed (a folder gone, the server refused); cleared when the rule is edited.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>Messages the rule matched so far (new mail and runs on existing messages); kept when it is edited.</summary>
    public int MatchCount { get; set; }

    public DateTimeOffset? LastMatchedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// How far an account's INBOX has been run through the rules: every UID up to <see cref="LastUid"/>
/// was. Exists only while the account has an enabled rule, so enabling rules again later starts at
/// the newest mail instead of working through everything that arrived meanwhile.
/// </summary>
public sealed class RuleCursor
{
    public Guid AccountId { get; init; }
    public required string OwnerUserId { get; init; }

    /// <summary>0 until the first run sets it from the INBOX (then <see cref="LastUid"/> is UIDNEXT - 1).</summary>
    public uint UidValidity { get; set; }

    public uint LastUid { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <param name="Field">One of <see cref="RuleFields"/>.</param>
/// <param name="Header">The header name, for <see cref="RuleFields.Header"/> only.</param>
/// <param name="Op">One of <see cref="RuleOps"/>, depending on the field.</param>
/// <param name="Value">Text to compare; bytes for <see cref="RuleFields.Size"/>; none for <see cref="RuleFields.Attachment"/>.</param>
public sealed record RuleCondition(string Field, string? Header, string Op, string? Value);

/// <param name="Type">One of <see cref="RuleActionTypes"/>.</param>
/// <param name="FolderId">The target of <see cref="RuleActionTypes.Move"/> and <see cref="RuleActionTypes.Copy"/>.</param>
/// <param name="TagId">The tag <see cref="RuleActionTypes.Tag"/> adds (one of the user's, see <see cref="Tags.MailTag"/>).</param>
public sealed record RuleAction(string Type, string? FolderId, Guid? TagId = null);

public static class RuleFields
{
    public const string From = "from";
    public const string To = "to";
    public const string Cc = "cc";

    /// <summary>To or Cc.</summary>
    public const string Recipient = "recipient";

    public const string Subject = "subject";
    public const string Header = "header";
    public const string Size = "size";
    public const string Attachment = "attachment";

    public static readonly string[] Text = [From, To, Cc, Recipient, Subject, Header];
}

public static class RuleOps
{
    public const string Contains = "contains";
    public const string NotContains = "notContains";
    public const string Is = "is";
    public const string IsNot = "isNot";
    public const string StartsWith = "startsWith";
    public const string EndsWith = "endsWith";
    public const string Matches = "matches";

    public const string GreaterThan = "greaterThan";
    public const string LessThan = "lessThan";

    public const string Has = "has";
    public const string HasNot = "hasNot";

    public static readonly string[] Text = [Contains, NotContains, Is, IsNot, StartsWith, EndsWith, Matches];
    public static readonly string[] Size = [GreaterThan, LessThan];
    public static readonly string[] Attachment = [Has, HasNot];
}

public static class RuleActionTypes
{
    public const string Move = "move";
    public const string Copy = "copy";
    public const string MarkRead = "markRead";
    public const string Flag = "flag";
    public const string Trash = "trash";
    public const string Tag = "tag";

    public static readonly string[] All = [Move, Copy, MarkRead, Flag, Trash, Tag];
}

public sealed record RuleDto(
    Guid? Id,
    string? Name,
    bool Enabled,
    bool MatchAll,
    bool Stop,
    IReadOnlyList<RuleCondition>? Conditions,
    IReadOnlyList<RuleAction>? Actions,
    string? Error = null,
    DateTimeOffset? ErrorAt = null,
    int MatchCount = 0,
    DateTimeOffset? LastMatchedAt = null);

public sealed record RuleListDto(IReadOnlyList<RuleDto> Rules);

public sealed record RuleListInput(IReadOnlyList<RuleDto>? Rules);

public sealed record RuleTestInput(RuleDto? Rule);

public sealed record RuleTestMatch(string Id, string Subject, AddressDto[] From, DateTimeOffset Date);

/// <summary>
/// Matches among a folder's newest <paramref name="Scanned"/> messages: at most
/// <see cref="RuleStore.MaxTestMatches"/> listed, all of them in <paramref name="MessageIds"/>.
/// </summary>
public sealed record RuleTestResult(int Scanned, int Matched, IReadOnlyList<RuleTestMatch> Matches, IReadOnlyList<string> MessageIds);

public sealed record RuleRunInput(string? FolderId, IReadOnlyList<string>? MessageIds);

public sealed record RuleRunResult(int Applied);

public sealed class RuleValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}
