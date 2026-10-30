using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.OAuth;

namespace SocklessMail.Api.Tags;

/// <summary>Graph access tokens for accounts; tests replace it.</summary>
public interface IGraphTokens
{
    /// <returns>A token, or null when the account is not one with Graph (not a Microsoft sign-in).</returns>
    /// <exception cref="GraphConsentRequiredException">A Microsoft sign-in without the Graph consent.</exception>
    Task<string?> TokenAsync(MailAccount account, CancellationToken ct);
}

public sealed class MailLoginGraphTokens(MailLogins logins) : IGraphTokens
{
    public Task<string?> TokenAsync(MailAccount account, CancellationToken ct) => logins.GraphTokenAsync(account, ct);
}

/// <summary>
/// Tags of Microsoft accounts as Outlook categories (<see cref="TagStorage.Graph"/>): a tag is the
/// category of the same name, with the nearest of Outlook's colors. The tag index stays the app's
/// record, as for <see cref="TagStorage.Local"/>; changes the app makes go to Graph first (and
/// fail when Graph refuses them), and the tag scan takes in what Outlook changed
/// (<see cref="ReconcileAsync"/>). A Microsoft account whose sign-in predates the Graph consent
/// keeps its tags in the app alone until the user signs in again; then its tags are copied to
/// Outlook (<see cref="DecideAsync"/>).
/// </summary>
public sealed class OutlookCategories(MailDbContext db, TagIndex index, GraphClient graph, IGraphTokens tokens, TimeProvider clock,
    ILogger<OutlookCategories> logger)
{
    /// <summary>A token where the account can have Outlook categories now; null otherwise (not Microsoft, no consent).</summary>
    public async Task<string?> TryTokenAsync(MailAccount account, CancellationToken ct)
    {
        try
        {
            return await tokens.TokenAsync(account, ct);
        }
        catch (GraphConsentRequiredException)
        {
            logger.LogDebug("Account {AccountId} has no Graph consent; its tags stay in the app", account.Id);
            return null;
        }
        catch (MailConnectionException e)
        {
            logger.LogDebug("No Graph token for account {AccountId} ({Error})", account.Id, e.Code);
            return null;
        }
    }

    /// <summary>
    /// Where the account's tags go before a change: Outlook categories when it can have them, by
    /// moving app-kept tags there first; the account's storage otherwise (Unknown: the IMAP server
    /// decides, see <see cref="Mailbox.MailboxService.ChangeKeywordsAsync"/>).
    /// </summary>
    public async Task<TagStorage> DecideAsync(MailAccount account, CancellationToken ct)
    {
        var storage = await index.StorageAsync(account.Id, ct);
        if (storage is TagStorage.Keywords or TagStorage.Graph)
        {
            return storage;
        }

        if (await TryTokenAsync(account, ct) is not { } token)
        {
            return storage;
        }

        if (storage == TagStorage.Local)
        {
            await MigrateAsync(account, token, ct);
        }

        await index.SetStorageAsync(account.Id, TagStorage.Graph, ct);
        return TagStorage.Graph;
    }

    /// <summary>Adds and removes categories on every copy of the messages (by Message-ID); added ones get a master category first.</summary>
    /// <exception cref="GraphUnavailableException">Graph refused; nothing may have changed, or part.</exception>
    public async Task ApplyAsync(MailAccount account, IEnumerable<string> messageIds, IReadOnlyCollection<string> addKeywords,
        IReadOnlyCollection<string> removeKeywords, CancellationToken ct)
    {
        var token = await RequiredTokenAsync(account, ct);
        var tags = await TagsAsync(account, ct);
        var add = tags.Where(t => addKeywords.Contains(t.Keyword, StringComparer.OrdinalIgnoreCase)).ToList();
        var remove = tags.Where(t => removeKeywords.Contains(t.Keyword, StringComparer.OrdinalIgnoreCase)).Select(t => t.Name).ToList();
        await EnsureCategoriesAsync(token, add, ct);
        foreach (var id in messageIds.Distinct())
        {
            foreach (var message in await graph.MessagesByInternetIdAsync(token, id, ct))
            {
                var categories = message.Categories.Where(c => !remove.Contains(c, StringComparer.OrdinalIgnoreCase))
                    .Concat(add.Select(t => t.Name).Where(n => !message.Categories.Contains(n, StringComparer.OrdinalIgnoreCase)))
                    .ToList();
                if (!categories.SequenceEqual(message.Categories))
                {
                    await graph.SetMessageCategoriesAsync(token, message.Id, categories, ct);
                }
            }
        }
    }

    /// <summary>
    /// Takes in Outlook's categories: rows get the tags their message has in Outlook (tags of no
    /// category lose their rows), and categorised messages without a row get one without a place,
    /// for the scan to find by Message-ID. Rows without a Message-ID cannot be matched and stay.
    /// </summary>
    /// <returns>Folders (cache keys) whose tagged messages changed.</returns>
    public async Task<HashSet<string>> ReconcileAsync(MailAccount account, CancellationToken ct)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var token = await RequiredTokenAsync(account, ct);
        var tags = await TagsAsync(account, ct);
        var tagKeywords = tags.Select(t => t.Keyword).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inOutlook = new Dictionary<string, (HashSet<string> Keywords, GraphMessage Message)>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            foreach (var message in await graph.MessagesWithCategoryAsync(token, tag.Name, ct))
            {
                if (TagIndex.MessageIdOf(message.InternetMessageId) is not { } id)
                {
                    continue;
                }

                if (!inOutlook.TryGetValue(id, out var entry))
                {
                    inOutlook[id] = entry = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), message);
                }

                entry.Keywords.Add(tag.Keyword);
            }
        }

        var rows = await db.TaggedMessages.Where(m => m.AccountId == account.Id && m.MessageIdHeader != null).ToListAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var row in rows)
        {
            var wanted = inOutlook.GetValueOrDefault(row.MessageIdHeader!).Keywords ?? [];
            // Keywords of no tag (other programs' flags) are left as they are.
            var keywords = Keywords.Format(Keywords.Parse(row.Keywords).Where(k => !tagKeywords.Contains(k)).Concat(wanted.Order(StringComparer.Ordinal)));
            if (Keywords.Parse(keywords).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Keywords.Parse(row.Keywords)))
            {
                continue;
            }

            if (row.Uid != 0)
            {
                changed.Add(row.FolderName);
                await index.SetCachedKeywordsAsync(row.AccountId, row.FolderName, row.UidValidity, row.Uid, keywords, ct);
            }

            if (Keywords.HasTagKeyword(keywords))
            {
                row.Keywords = keywords;
                row.UpdatedAt = now;
            }
            else
            {
                db.TaggedMessages.Remove(row);
            }
        }

        var known = rows.Select(r => r.MessageIdHeader!).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, (keywords, message)) in inOutlook.Where(e => !known.Contains(e.Key)))
        {
            db.TaggedMessages.Add(new TaggedMessage
            {
                AccountId = account.Id,
                OwnerUserId = account.OwnerUserId,
                FolderName = "",
                MessageIdHeader = id,
                Keywords = Keywords.Format(keywords.Order(StringComparer.Ordinal)),
                Subject = message.Subject,
                From = message.From,
                To = message.To,
                Date = message.Date,
                Seen = message.Read,
                Flagged = message.Flagged,
                HasAttachments = message.HasAttachments,
                Preview = message.Preview is null ? null : Mailbox.MessagePreview.Normalize(message.Preview),
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Outlook cannot rename a category: the new one is made, messages move over, the old one goes.</summary>
    public async Task RenameAsync(MailAccount account, string oldName, string newName, string color, CancellationToken ct)
    {
        if (oldName.Equals(newName, StringComparison.Ordinal))
        {
            return;
        }

        var token = await RequiredTokenAsync(account, ct);
        var categories = await graph.CategoriesAsync(token, ct);
        if (!categories.Any(c => c.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            await graph.CreateCategoryAsync(token, newName, OutlookColors.Nearest(color), ct);
        }

        foreach (var message in await graph.MessagesWithCategoryAsync(token, oldName, ct))
        {
            var renamed = message.Categories
                .Select(c => c.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? newName : c)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            await graph.SetMessageCategoriesAsync(token, message.Id, renamed, ct);
        }

        // Case only: Outlook names compare without case, so the old one is the new one.
        if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)
            && categories.FirstOrDefault(c => c.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase)) is { } old)
        {
            await graph.DeleteCategoryAsync(token, old.Id, ct);
        }
    }

    public async Task RecolorAsync(MailAccount account, string name, string color, CancellationToken ct)
    {
        var token = await RequiredTokenAsync(account, ct);
        var preset = OutlookColors.Nearest(color);
        var category = (await graph.CategoriesAsync(token, ct)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (category is null)
        {
            await graph.CreateCategoryAsync(token, name, preset, ct);
        }
        else if (category.Color != preset)
        {
            await graph.SetCategoryColorAsync(token, category.Id, preset, ct);
        }
    }

    /// <summary>Takes the category off every message and out of the master list.</summary>
    public async Task DeleteAsync(MailAccount account, string name, CancellationToken ct)
    {
        var token = await RequiredTokenAsync(account, ct);
        foreach (var message in await graph.MessagesWithCategoryAsync(token, name, ct))
        {
            await graph.SetMessageCategoriesAsync(token, message.Id,
                message.Categories.Where(c => !c.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList(), ct);
        }

        if ((await graph.CategoriesAsync(token, ct)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } category)
        {
            await graph.DeleteCategoryAsync(token, category.Id, ct);
        }
    }

    /// <summary>Outlook categories no tag has yet, for adopting.</summary>
    public async Task<List<GraphCategory>> UnusedCategoriesAsync(MailAccount account, CancellationToken ct)
    {
        var token = await RequiredTokenAsync(account, ct);
        var names = (await TagsAsync(account, ct)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (await graph.CategoriesAsync(token, ct)).Where(c => !names.Contains(c.Name)).ToList();
    }

    /// <summary>App-kept tags of an account that just got Graph: copied to Outlook as categories.</summary>
    private async Task MigrateAsync(MailAccount account, string token, CancellationToken ct)
    {
        var tags = await TagsAsync(account, ct);
        await EnsureCategoriesAsync(token, tags, ct);
        var rows = await db.TaggedMessages.AsNoTracking()
            .Where(m => m.AccountId == account.Id && m.MessageIdHeader != null)
            .Select(m => new { m.MessageIdHeader, m.Keywords })
            .ToListAsync(ct);
        foreach (var group in rows.GroupBy(r => r.MessageIdHeader!))
        {
            var names = tags.Where(t => group.Any(r => Keywords.Has(r.Keywords, t.Keyword))).Select(t => t.Name).ToList();
            if (names.Count == 0)
            {
                continue;
            }

            foreach (var message in await graph.MessagesByInternetIdAsync(token, group.Key, ct))
            {
                var categories = message.Categories.Concat(names.Where(n => !message.Categories.Contains(n, StringComparer.OrdinalIgnoreCase))).ToList();
                if (categories.Count != message.Categories.Count)
                {
                    await graph.SetMessageCategoriesAsync(token, message.Id, categories, ct);
                }
            }
        }

        logger.LogInformation("Account {AccountId}: tags copied to Outlook categories", account.Id);
    }

    private async Task EnsureCategoriesAsync(string token, IReadOnlyCollection<MailTag> tags, CancellationToken ct)
    {
        if (tags.Count == 0)
        {
            return;
        }

        var existing = await graph.CategoriesAsync(token, ct);
        foreach (var tag in tags.Where(t => !existing.Any(c => c.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))))
        {
            await graph.CreateCategoryAsync(token, tag.Name, OutlookColors.Nearest(tag.Color), ct);
        }
    }

    private async Task<string> RequiredTokenAsync(MailAccount account, CancellationToken ct) =>
        await tokens.TokenAsync(account, ct) ?? throw new GraphConsentRequiredException();

    private Task<List<MailTag>> TagsAsync(MailAccount account, CancellationToken ct) =>
        db.Tags.AsNoTracking().Where(t => t.OwnerUserId == account.OwnerUserId).ToListAsync(ct);
}
