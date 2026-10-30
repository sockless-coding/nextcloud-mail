using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Rules;

/// <summary>
/// An account's rules: validation, and saving the whole list at once (so reordering is one
/// change). Callers pass an account already looked up for the current user.
/// </summary>
public sealed class RuleStore(MailDbContext db, TimeProvider clock)
{
    public const int MaxRules = 100;
    public const int MaxConditions = 20;
    public const int MaxActions = 10;
    public const int MaxNameLength = 100;
    public const int MaxValueLength = 500;
    public const int MaxHeaderNameLength = 100;
    public const int MaxTestMatches = 50;

    public async Task<RuleListDto> GetAsync(MailAccount account, CancellationToken ct)
    {
        var rules = await db.Rules.AsNoTracking().Where(r => r.AccountId == account.Id).OrderBy(r => r.SortOrder).ToListAsync(ct);
        return new RuleListDto(rules.Select(ToDto).ToList());
    }

    /// <summary>
    /// Replaces the account's rules. A rule keeps its id (and its last error, unless its conditions
    /// or actions changed) when the input names it. The cursor is dropped when no rule is enabled
    /// any more, and created (not yet positioned) when one is again.
    /// </summary>
    /// <returns>The saved list, and whether the account's cursor still has to be positioned.</returns>
    public async Task<(RuleListDto Rules, bool NeedsCursor)> SaveAsync(MailAccount account, IReadOnlyList<RuleDto>? input, CancellationToken ct)
    {
        var rules = input ?? [];
        if (rules.Count > MaxRules)
        {
            throw new RuleValidationException("rules", $"At most {MaxRules} rules per account.");
        }

        var validated = rules.Select((rule, i) => Validate(rule, $"rules[{i}]")).ToList();
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.Rules.Where(r => r.AccountId == account.Id).ToDictionaryAsync(r => r.Id, ct);
        db.Rules.RemoveRange(existing.Values);
        await db.SaveChangesAsync(ct);

        var kept = new HashSet<Guid>();
        for (var i = 0; i < rules.Count; i++)
        {
            var (name, conditions, actions) = validated[i];
            var conditionsJson = JsonSerializer.Serialize(conditions, JsonSerializerOptions.Web);
            var actionsJson = JsonSerializer.Serialize(actions, JsonSerializerOptions.Web);
            var previous = rules[i].Id is { } id && kept.Add(id) ? existing.GetValueOrDefault(id) : null;
            var unchanged = previous is not null && previous.Conditions == conditionsJson && previous.Actions == actionsJson;
            db.Rules.Add(new MailRule
            {
                Id = previous?.Id ?? Guid.CreateVersion7(),
                AccountId = account.Id,
                OwnerUserId = account.OwnerUserId,
                SortOrder = i,
                Name = name,
                Enabled = rules[i].Enabled,
                MatchAll = rules[i].MatchAll,
                Stop = rules[i].Stop,
                Conditions = conditionsJson,
                Actions = actionsJson,
                LastError = unchanged ? previous!.LastError : null,
                LastErrorAt = unchanged ? previous!.LastErrorAt : null,
                MatchCount = previous?.MatchCount ?? 0,
                LastMatchedAt = previous?.LastMatchedAt,
                UpdatedAt = previous is not null && unchanged && previous.Name == name && previous.Enabled == rules[i].Enabled
                            && previous.MatchAll == rules[i].MatchAll && previous.Stop == rules[i].Stop
                    ? previous.UpdatedAt
                    : now,
            });
        }

        var cursor = await db.RuleCursors.SingleOrDefaultAsync(c => c.AccountId == account.Id, ct);
        var anyEnabled = rules.Any(r => r.Enabled);
        if (!anyEnabled && cursor is not null)
        {
            db.RuleCursors.Remove(cursor);
        }
        else if (anyEnabled && cursor is null)
        {
            cursor = new RuleCursor { AccountId = account.Id, OwnerUserId = account.OwnerUserId, UpdatedAt = now };
            db.RuleCursors.Add(cursor);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (await GetAsync(account, ct), cursor is { UidValidity: 0 } && anyEnabled);
    }

    /// <summary>The account's enabled rules in order, tracked (so errors can be recorded on them).</summary>
    public async Task<List<CompiledRule>> EnabledAsync(Guid accountId, CancellationToken ct)
    {
        var rules = await db.Rules.Where(r => r.AccountId == accountId && r.Enabled).OrderBy(r => r.SortOrder).ToListAsync(ct);
        var compiled = new List<CompiledRule>(rules.Count);
        foreach (var rule in rules)
        {
            try
            {
                compiled.Add(Compile(rule));
            }
            catch (Exception e) when (e is JsonException or RuleValidationException or ArgumentException)
            {
                // Saved rules were validated; one that no longer passes (a newer, stricter version) is skipped.
                rule.LastError = "This rule is not valid. Edit and save it again.";
                rule.LastErrorAt = clock.GetUtcNow();
            }
        }

        return compiled;
    }

    public static CompiledRule Compile(MailRule rule) => new(
        rule,
        JsonSerializer.Deserialize<RuleCondition[]>(rule.Conditions, JsonSerializerOptions.Web) ?? [],
        JsonSerializer.Deserialize<RuleAction[]>(rule.Actions, JsonSerializerOptions.Web) ?? []);

    /// <summary>A rule as entered, checked and normalised but not saved (for trying it out).</summary>
    public static CompiledRule Draft(MailAccount account, RuleDto? input)
    {
        var (name, conditions, actions) = Validate(input ?? throw new RuleValidationException("rule", "Give a rule."), "rule");
        var rule = new MailRule
        {
            OwnerUserId = account.OwnerUserId,
            Name = name,
            Enabled = true,
            MatchAll = input.MatchAll,
            Conditions = "",
            Actions = "",
        };
        return new CompiledRule(rule, conditions, actions);
    }

    public static RuleDto ToDto(MailRule rule) => new(
        rule.Id,
        rule.Name,
        rule.Enabled,
        rule.MatchAll,
        rule.Stop,
        JsonSerializer.Deserialize<RuleCondition[]>(rule.Conditions, JsonSerializerOptions.Web) ?? [],
        JsonSerializer.Deserialize<RuleAction[]>(rule.Actions, JsonSerializerOptions.Web) ?? [],
        rule.LastError,
        rule.LastErrorAt,
        rule.MatchCount,
        rule.LastMatchedAt);

    private static (string Name, List<RuleCondition> Conditions, List<RuleAction> Actions) Validate(RuleDto rule, string path)
    {
        var name = rule.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new RuleValidationException($"{path}.name", "Give the rule a name.");
        }

        if (name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            throw new RuleValidationException($"{path}.name", $"Use at most {MaxNameLength} characters.");
        }

        var conditions = rule.Conditions ?? [];
        if (conditions.Count == 0)
        {
            throw new RuleValidationException($"{path}.conditions", "Add at least one condition.");
        }

        if (conditions.Count > MaxConditions)
        {
            throw new RuleValidationException($"{path}.conditions", $"At most {MaxConditions} conditions.");
        }

        var actions = rule.Actions ?? [];
        if (actions.Count == 0)
        {
            throw new RuleValidationException($"{path}.actions", "Add at least one action.");
        }

        if (actions.Count > MaxActions)
        {
            throw new RuleValidationException($"{path}.actions", $"At most {MaxActions} actions.");
        }

        return (name,
            conditions.Select((c, i) => ValidateCondition(c, $"{path}.conditions[{i}]")).ToList(),
            ValidateActions(actions, $"{path}.actions"));
    }

    private static RuleCondition ValidateCondition(RuleCondition? condition, string path)
    {
        if (condition is null)
        {
            throw new RuleValidationException(path, "Not a condition.");
        }

        switch (condition.Field)
        {
            case RuleFields.Size:
                if (!RuleOps.Size.Contains(condition.Op))
                {
                    throw new RuleValidationException($"{path}.op", "Choose larger or smaller than.");
                }

                if (!long.TryParse(condition.Value?.Trim(), out var bytes) || bytes < 0)
                {
                    throw new RuleValidationException($"{path}.value", "Enter a size.");
                }

                return new RuleCondition(RuleFields.Size, null, condition.Op, bytes.ToString());

            case RuleFields.Attachment:
                if (!RuleOps.Attachment.Contains(condition.Op))
                {
                    throw new RuleValidationException($"{path}.op", "Choose has or has no attachments.");
                }

                return new RuleCondition(RuleFields.Attachment, null, condition.Op, null);
        }

        if (!RuleFields.Text.Contains(condition.Field))
        {
            throw new RuleValidationException($"{path}.field", "Choose what to look at.");
        }

        string? header = null;
        if (condition.Field == RuleFields.Header)
        {
            header = condition.Header?.Trim() ?? "";
            // RFC 5322 field names: printable US-ASCII except the colon.
            if (header.Length is 0 or > MaxHeaderNameLength || header.Any(c => c is < '!' or > '~' or ':'))
            {
                throw new RuleValidationException($"{path}.header", "Enter a header name, such as List-Id.");
            }
        }

        if (!RuleOps.Text.Contains(condition.Op))
        {
            throw new RuleValidationException($"{path}.op", "Choose how to compare.");
        }

        var value = condition.Value ?? "";
        if (value.Trim().Length == 0)
        {
            throw new RuleValidationException($"{path}.value", "Enter a value.");
        }

        if (value.Length > MaxValueLength)
        {
            throw new RuleValidationException($"{path}.value", $"Use at most {MaxValueLength} characters.");
        }

        if (condition.Op == RuleOps.Matches)
        {
            try
            {
                CompiledRule.CreateRegex(value);
            }
            catch (ArgumentException)
            {
                throw new RuleValidationException($"{path}.value", "This is not a valid regular expression.");
            }
        }

        return new RuleCondition(condition.Field, header, condition.Op, value);
    }

    private static List<RuleAction> ValidateActions(IReadOnlyList<RuleAction?> actions, string path)
    {
        var result = new List<RuleAction>(actions.Count);
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var at = $"{path}[{i}]";
            if (action is null || !RuleActionTypes.All.Contains(action.Type))
            {
                throw new RuleValidationException(at, "Choose an action.");
            }

            string? folderId = null;
            if (action.Type is RuleActionTypes.Move or RuleActionTypes.Copy)
            {
                folderId = action.FolderId;
                var fullName = folderId is { Length: > 0 and <= 4000 } ? MailboxIds.FolderFullName(folderId) : null;
                if (string.IsNullOrEmpty(fullName))
                {
                    throw new RuleValidationException($"{at}.folderId", "Choose a folder.");
                }

                if (MailboxService.IsInbox(fullName))
                {
                    throw new RuleValidationException($"{at}.folderId", "Rules act on new mail in the inbox: choose another folder.");
                }
            }

            var duplicate = result.Any(a => a.Type == action.Type && (action.Type != RuleActionTypes.Copy || a.FolderId == folderId));
            if (duplicate)
            {
                throw new RuleValidationException(at, "This action is already in the rule.");
            }

            result.Add(new RuleAction(action.Type, folderId));
        }

        if (result.Count(a => a.Type is RuleActionTypes.Move or RuleActionTypes.Trash) > 1)
        {
            throw new RuleValidationException(path, "A rule can move a message to one place only.");
        }

        return result;
    }
}
