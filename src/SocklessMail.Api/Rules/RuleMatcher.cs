using System.Text.RegularExpressions;
using MimeKit;

namespace SocklessMail.Api.Rules;

/// <summary>What rules look at in a message. <see cref="Header"/> gives every value of a header, decoded.</summary>
public sealed record RuleMessage(
    IReadOnlyList<MailboxAddress> From,
    IReadOnlyList<MailboxAddress> To,
    IReadOnlyList<MailboxAddress> Cc,
    string Subject,
    Func<string, IReadOnlyList<string>> Header,
    long Size,
    bool HasAttachments);

/// <summary>A rule ready to run: conditions parsed, regular expressions compiled.</summary>
public sealed class CompiledRule
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly (RuleCondition Condition, Regex? Regex)[] _conditions;

    public CompiledRule(MailRule rule, IReadOnlyList<RuleCondition> conditions, IReadOnlyList<RuleAction> actions)
    {
        Rule = rule;
        Actions = actions;
        _conditions = conditions.Select(c => (c, c.Op == RuleOps.Matches ? CreateRegex(c.Value ?? "") : null)).ToArray();
    }

    public MailRule Rule { get; }
    public IReadOnlyList<RuleAction> Actions { get; }

    /// <summary>Header names the conditions read, for fetching them.</summary>
    public IEnumerable<string> HeaderNames =>
        _conditions.Where(c => c.Condition.Field == RuleFields.Header).Select(c => c.Condition.Header!);

    /// <exception cref="ArgumentException">Not a valid pattern.</exception>
    public static Regex CreateRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    public bool Matches(RuleMessage message) =>
        Rule.MatchAll ? _conditions.All(c => Test(c, message)) : _conditions.Any(c => Test(c, message));

    private static bool Test((RuleCondition Condition, Regex? Regex) entry, RuleMessage message)
    {
        var condition = entry.Condition;
        switch (condition.Field)
        {
            case RuleFields.Size:
                var limit = long.Parse(condition.Value!);
                return condition.Op == RuleOps.GreaterThan ? message.Size > limit : message.Size < limit;
            case RuleFields.Attachment:
                return message.HasAttachments == (condition.Op == RuleOps.Has);
        }

        var candidates = condition.Field switch
        {
            RuleFields.From => Addresses(message.From),
            RuleFields.To => Addresses(message.To),
            RuleFields.Cc => Addresses(message.Cc),
            RuleFields.Recipient => Addresses(message.To.Concat(message.Cc)),
            RuleFields.Subject => [message.Subject],
            RuleFields.Header => message.Header(condition.Header!),
            _ => [],
        };

        // Negations hold when no value matches, so a missing header "does not contain" anything.
        return condition.Op switch
        {
            RuleOps.NotContains => !candidates.Any(c => Compare(RuleOps.Contains, c, condition.Value!, null)),
            RuleOps.IsNot => !candidates.Any(c => Compare(RuleOps.Is, c, condition.Value!, null)),
            _ => candidates.Any(c => Compare(condition.Op, c, condition.Value ?? "", entry.Regex)),
        };
    }

    /// <summary>An address is matched by its address, its name, or both as "Name &lt;address&gt;".</summary>
    private static IReadOnlyList<string> Addresses(IEnumerable<MailboxAddress> addresses) =>
        addresses.SelectMany(a => string.IsNullOrWhiteSpace(a.Name)
            ? new[] { a.Address }
            : [a.Address, a.Name, $"{a.Name} <{a.Address}>"]).ToList();

    private static bool Compare(string op, string candidate, string value, Regex? regex)
    {
        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        return op switch
        {
            RuleOps.Contains => candidate.Contains(value, ignoreCase),
            RuleOps.Is => candidate.Trim().Equals(value.Trim(), ignoreCase),
            RuleOps.StartsWith => candidate.TrimStart().StartsWith(value, ignoreCase),
            RuleOps.EndsWith => candidate.TrimEnd().EndsWith(value, ignoreCase),
            RuleOps.Matches => IsMatch(regex!, candidate),
            _ => false,
        };
    }

    private static bool IsMatch(Regex regex, string candidate)
    {
        try
        {
            return regex.IsMatch(candidate);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological pattern on this input: treated as no match rather than holding up the run.
            return false;
        }
    }
}
