using System.Globalization;
using MailKit;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Bimi;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Tags;

/// <summary>A page of a tag folder; <see cref="Total"/> and <see cref="Unread"/> count the whole folder.</summary>
public sealed record TagPage(IReadOnlyList<SearchHit> Messages, string? NextBefore, int Total, int Unread);

public sealed record TagCountDto(Guid TagId, Guid AccountId, int Total, int Unread);

/// <summary>Where a tag folder looks: one account, one folder, or each account's folder of a use.</summary>
public sealed record TagScope(Guid? AccountId = null, string? FolderId = null, string? Role = null, string? Query = null);

/// <summary>
/// Tag folders, read from the tag index alone: no mail server is asked, so they open at once
/// however many folders the accounts have. Messages in Trash, Junk and Drafts are left out (unless
/// one folder is asked for), as are Gmail's virtual folders; a message in several folders (Gmail
/// labels) is shown once.
/// </summary>
public sealed class TagFolders(MailDbContext db, AccountService accounts, MailboxService mailbox, FolderRoles roles, ILogger<TagFolders> logger)
{
    public const int MaxPageSize = 500;

    private static readonly string[] HiddenUses = ["trash", "junk", "drafts", "all", "flagged"];

    private sealed record Candidate(long Id, Guid AccountId, string FolderName, string? MessageIdHeader, DateTimeOffset Date, bool Seen, string Keywords);

    public async Task<TagPage> GetPageAsync(string userId, MailTag tag, TagScope scope, string? before, int limit, CancellationToken ct)
    {
        var cursor = ParseCursor(before);
        var all = await CandidatesAsync(userId, TagIndex.Like(tag.Keyword), scope, ct);
        var shown = all.OrderByDescending(c => c.Date).ThenByDescending(c => c.Id).ToList();
        var after = cursor is { } c0
            ? shown.Where(c => c.Date < c0.Date || (c.Date == c0.Date && c.Id < c0.Id)).ToList()
            : shown;
        var page = after.Take(limit).ToList();
        var ids = page.Select(c => c.Id).ToList();
        var rows = (await db.TaggedMessages.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct)).ToDictionary(m => m.Id);
        var conversations = await ConversationsAsync(rows.Values, ct);
        return new TagPage(
            page.Where(c => rows.ContainsKey(c.Id)).Select(c => Hit(rows[c.Id], conversations)).ToList(),
            after.Count > limit ? $"{page[^1].Date.UtcTicks}.{page[^1].Id}" : null,
            shown.Count,
            shown.Count(c => !c.Seen));
    }

    /// <summary>Messages and unread messages per tag and account, as the tag folders count them.</summary>
    public async Task<IReadOnlyList<TagCountDto>> CountsAsync(string userId, IReadOnlyList<MailTag> tags, CancellationToken ct)
    {
        if (tags.Count == 0)
        {
            return [];
        }

        var all = await CandidatesAsync(userId, null, new TagScope(), ct);
        return tags
            .SelectMany(tag => all.Where(c => Keywords.Has(c.Keywords, tag.Keyword))
                .GroupBy(c => c.AccountId)
                .Select(g => new TagCountDto(tag.Id, g.Key, g.Count(), g.Count(c => !c.Seen))))
            .ToList();
    }

    /// <param name="likeKeyword">A keyword as <see cref="Keywords.Like"/> gives it; null for every tagged message.</param>
    private async Task<List<Candidate>> CandidatesAsync(string userId, string? likeKeyword, TagScope scope, CancellationToken ct)
    {
        var query = db.TaggedMessages.AsNoTracking().Where(m => m.OwnerUserId == userId && m.Uid != 0);
        if (likeKeyword is not null)
        {
            query = query.Where(m => EF.Functions.Like(m.Keywords, $"% {likeKeyword} %", "\\"));
        }

        if (scope.AccountId is { } accountId)
        {
            query = query.Where(m => m.AccountId == accountId);
        }

        if (scope.FolderId is not null)
        {
            var name = MailboxIds.FolderFullName(scope.FolderId) is { } full ? Sync.FolderCache.Key(full) : throw new MailboxNotFoundException("The folder");
            query = query.Where(m => m.FolderName == name);
        }

        List<Candidate> candidates;
        var terms = string.IsNullOrWhiteSpace(scope.Query) ? [] : SearchTerms.Parse(scope.Query);
        if (terms.Count == 0)
        {
            candidates = await query
                .Select(m => new Candidate(m.Id, m.AccountId, m.FolderName, m.MessageIdHeader, m.Date, m.Seen, m.Keywords))
                .ToListAsync(ct);
        }
        else
        {
            // Words match subject, names and addresses, as the cache search does; the addresses are
            // stored as JSON, so this filter runs here rather than in SQL.
            var rows = await query.Select(m => new { m.Id, m.AccountId, m.FolderName, m.MessageIdHeader, m.Date, m.Seen, m.Keywords, m.Subject, m.From, m.To })
                .ToListAsync(ct);
            candidates = rows
                .Where(m =>
                {
                    var text = SearchTerms.Normalize(string.Join('\n', m.From.Concat(m.To).Select(a => $"{a.Name} {a.Address}").Prepend(m.Subject)));
                    return terms.All(t => text.Contains(t, StringComparison.Ordinal));
                })
                .Select(m => new Candidate(m.Id, m.AccountId, m.FolderName, m.MessageIdHeader, m.Date, m.Seen, m.Keywords))
                .ToList();
        }

        // Per account: the folders hidden from tag folders, or the one folder of the asked use.
        var places = new Dictionary<Guid, (HashSet<string> Hidden, string? Only)>();
        foreach (var account in candidates.Select(c => c.AccountId).Distinct())
        {
            var folderRoles = await RolesAsync(userId, account, ct);
            string Name(string folderId) => Sync.FolderCache.Key(MailboxIds.FolderFullName(folderId) ?? "");
            places[account] = (
                scope.FolderId is null
                    ? HiddenUses.Where(folderRoles.ContainsKey).Select(u => Name(folderRoles[u])).ToHashSet(StringComparer.Ordinal)
                    : [],
                scope.Role is null ? null : folderRoles.TryGetValue(scope.Role, out var only) ? Name(only) : "");
        }

        return candidates
            .Where(c => !places[c.AccountId].Hidden.Contains(c.FolderName)
                        && (places[c.AccountId].Only is not { } only || c.FolderName == only))
            .OrderBy(c => c.Id)
            .DistinctBy(c => c.MessageIdHeader is null ? $"{c.Id}" : $"{c.AccountId}/{c.MessageIdHeader}")
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, string>> RolesAsync(string userId, Guid accountId, CancellationToken ct)
    {
        if (await accounts.GetAsync(userId, accountId, ct) is not { } account)
        {
            return new Dictionary<string, string>();
        }

        if (roles.Find(account) is { } known)
        {
            return known;
        }

        try
        {
            return await mailbox.FolderRolesAsync(account, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unreachable server: the last known map, or none (then nothing is hidden).
            logger.LogDebug("Folder roles of account {AccountId} unknown ({ExceptionType})", accountId, e.GetType().Name);
            return roles.Find(account, anyAge: true) ?? new Dictionary<string, string>();
        }
    }

    /// <summary>The conversation of each tagged message, by account and Message-ID (<see cref="Sync.ConversationLink"/>).</summary>
    private async Task<Dictionary<(Guid, string), Guid>> ConversationsAsync(IEnumerable<TaggedMessage> messages, CancellationToken ct)
    {
        var found = new Dictionary<(Guid, string), Guid>();
        foreach (var account in messages.Where(m => m.MessageIdHeader is not null).GroupBy(m => m.AccountId))
        {
            var headers = account.Select(m => m.MessageIdHeader!).Distinct().ToList();
            var links = await db.ConversationLinks.AsNoTracking()
                .Where(l => l.AccountId == account.Key && headers.Contains(l.MessageIdHeader))
                .Select(l => new { l.MessageIdHeader, l.ConversationId })
                .ToListAsync(ct);
            foreach (var link in links)
            {
                found[(account.Key, link.MessageIdHeader)] = link.ConversationId;
            }
        }

        return found;
    }

    private static SearchHit Hit(TaggedMessage m, IReadOnlyDictionary<(Guid, string), Guid>? conversations = null) => new(
        m.AccountId,
        MailboxIds.FolderId(m.FolderName),
        m.FolderName,
        new MessageSummaryDto(
            MailboxIds.MessageId(m.UidValidity, m.Uid), m.Subject, m.From, m.To, m.Date, m.Seen, m.Flagged, m.Answered, m.HasAttachments,
            m.Size, m.Preview, string.IsNullOrEmpty(m.BimiDomain) ? null : new BimiDto(m.BimiDomain, m.BimiSelector), Keywords.ForClient(m.Keywords),
            m.MessageIdHeader is { } header && conversations?.TryGetValue((m.AccountId, header), out var id) == true ? id : null));

    private static (DateTimeOffset Date, long Id)? ParseCursor(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var parts = value.Split('.');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }

        throw new MailboxValidationException("before", "The cursor is not valid.");
    }
}
