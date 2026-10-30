using System.Security.Claims;
using SocklessMail.Api.Accounts;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Tags;

namespace SocklessMail.Api.Api;

/// <summary>The user's tags, shared by all accounts. Tagging messages is a mailbox action (see <see cref="MailboxEndpoints"/>).</summary>
public static class TagEndpoints
{
    public static void MapTagEndpoints(this RouteGroupBuilder api)
    {
        var tags = api.MapGroup("/tags");

        tags.MapGet("", (ClaimsPrincipal user, TagStore store, CancellationToken ct) => store.ListAsync(user.GetNextcloudUserId(), ct));

        tags.MapGet("/counts", async (ClaimsPrincipal user, TagStore store, TagFolders folders, CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            var all = await store.ListAsync(userId, ct);
            var tags = all.Select(t => new MailTag { Id = t.Id, OwnerUserId = userId, Name = t.Name, Color = t.Color, Keyword = t.Keyword }).ToList();
            return Results.Ok(await folders.CountsAsync(userId, tags, ct));
        });

        // A tag folder: across all accounts, or one account (accountId), one folder (folderId) or
        // each account's folder of a use (role: inbox, sent, …), optionally narrowed by words (q).
        tags.MapGet("/{tagId:guid}/messages", async (
            Guid tagId, Guid? accountId, string? folderId, string? role, string? q, string? before, int? limit,
            ClaimsPrincipal user, TagStore store, TagFolders folders, TagScanner scanner, CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            if (await store.FindAsync(userId, tagId, ct) is not { } tag)
            {
                return Results.NotFound();
            }

            // The index is as good as the last scan; one that is a while ago runs again in the background.
            scanner.RequestIfOlder(userId, TimeSpan.FromMinutes(5));
            try
            {
                return Results.Ok(await folders.GetPageAsync(userId, tag, new TagScope(accountId, folderId, role, q), before,
                    Math.Clamp(limit ?? MailboxService.DefaultPageSize, 1, TagFolders.MaxPageSize), ct));
            }
            catch (MailboxValidationException e)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
            }
            catch (MailboxNotFoundException)
            {
                return Results.NotFound();
            }
        });

        // Keywords on messages, then Outlook categories of Microsoft accounts (one per name).
        tags.MapGet("/suggestions", async (ClaimsPrincipal user, TagStore store, MailDbContext db, OutlookCategories outlook, ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            var suggestions = (await store.SuggestionsAsync(userId, ct)).ToList();
            var taken = (await store.ListAsync(userId, ct)).Select(t => t.Keyword).Concat(suggestions.Select(s => s.Keyword)).ToList();
            foreach (var account in await GraphAccountsAsync(db, userId, ct))
            {
                try
                {
                    foreach (var category in await outlook.UnusedCategoriesAsync(account, ct))
                    {
                        if (suggestions.Any(s => s.Name.Equals(category.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var keyword = Keywords.ForName(category.Name, taken);
                        taken.Add(keyword);
                        suggestions.Add(new TagSuggestionDto(keyword, category.Name.Trim(), OutlookColors.ToHex(category.Color) ?? TagStore.DefaultColor, 0, Outlook: true));
                    }
                }
                catch (Exception e) when (e is GraphUnavailableException or GraphConsentRequiredException)
                {
                    loggers.CreateLogger(typeof(TagEndpoints)).LogInformation("Outlook categories of account {AccountId} unavailable", account.Id);
                }
            }

            return suggestions;
        });

        // A new tag's keyword may be on messages already (adopted, or set by another program): scan for it.
        tags.MapPost("", (TagInput input, ClaimsPrincipal user, TagStore store, TagScanner scanner, CancellationToken ct) =>
            Validated(async () =>
            {
                var tag = await store.CreateAsync(user.GetNextcloudUserId(), input, ct);
                scanner.Request(user.GetNextcloudUserId());
                return Results.Ok(tag);
            }));

        tags.MapPut("/order", async (TagOrderInput input, ClaimsPrincipal user, TagStore store, CancellationToken ct) =>
            Results.Ok(await store.ReorderAsync(user.GetNextcloudUserId(), input.Ids, ct)));

        // Outlook categories follow a rename (or the rename is undone) and, best effort, a new color.
        tags.MapPut("/{tagId:guid}", (Guid tagId, TagInput input, ClaimsPrincipal user, TagStore store, MailDbContext db, OutlookCategories outlook,
            ILoggerFactory loggers, CancellationToken ct) =>
            Validated(async () =>
            {
                var userId = user.GetNextcloudUserId();
                if (await store.FindAsync(userId, tagId, ct) is not { } before)
                {
                    return Results.NotFound();
                }

                var tag = (await store.UpdateAsync(userId, tagId, input, ct))!;
                foreach (var account in await GraphAccountsAsync(db, userId, ct))
                {
                    try
                    {
                        if (tag.Name != before.Name)
                        {
                            await outlook.RenameAsync(account, before.Name, tag.Name, tag.Color, ct);
                        }
                        else if (tag.Color != before.Color)
                        {
                            await outlook.RecolorAsync(account, tag.Name, tag.Color, ct);
                        }
                    }
                    catch (Exception e) when (e is GraphUnavailableException or GraphConsentRequiredException)
                    {
                        loggers.CreateLogger(typeof(TagEndpoints)).LogInformation("Outlook categories of account {AccountId} not updated ({ExceptionType})",
                            account.Id, e.GetType().Name);
                        if (tag.Name != before.Name)
                        {
                            // A name Outlook does not know would take the tag off its messages at the next scan.
                            await store.UpdateAsync(userId, tagId, new TagInput(before.Name, before.Color, null), ct);
                            return Results.Json(new { error = "graph_unavailable", message = $"Outlook could not rename the category for {account.DisplayName}: {e.Message}" },
                                statusCode: StatusCodes.Status502BadGateway);
                        }
                    }
                }

                return Results.Ok(tag);
            }));

        // With fromMessages, the tag comes off every message the index knows first. Folders that
        // fail are reported and the tag is kept, so the user can try again.
        tags.MapDelete("/{tagId:guid}", async (
            Guid tagId, bool? fromMessages, ClaimsPrincipal user, TagStore store, TagIndex index, AccountService accounts, MailboxService mailbox,
            TagScanner scanner, MailDbContext db, OutlookCategories outlook, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            if (await store.FindAsync(userId, tagId, ct) is not { } tag)
            {
                return Results.NotFound();
            }

            if (fromMessages == true)
            {
                // A scan first, so messages beyond the cache are known too.
                await scanner.ScanUserAsync(userId, ct);
                var failed = 0;
                foreach (var (accountId, folderName, uidValidity, uids) in await index.PlacesAsync(userId, tag.Keyword, ct))
                {
                    if (await accounts.GetAsync(userId, accountId, ct) is not { } account)
                    {
                        continue;
                    }

                    foreach (var chunk in uids.Chunk(MailboxService.MaxMessagesPerAction))
                    {
                        try
                        {
                            await mailbox.ChangeKeywordsAsync(account, MailboxIds.FolderId(folderName),
                                chunk.Select(u => MailboxIds.MessageId(uidValidity, u)).ToList(), [], [tag.Keyword], ct);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            loggers.CreateLogger(typeof(TagEndpoints)).LogWarning(e, "Removing a tag from messages of account {AccountId} failed", accountId);
                            failed += chunk.Length;
                        }
                    }
                }

                if (failed > 0)
                {
                    return Results.Json(new { error = "tag_removal_failed", message = $"The tag could not be removed from {failed} messages. Try again later." },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                // Outlook categories also on messages the index has no place for; the category goes too.
                foreach (var account in await GraphAccountsAsync(db, userId, ct))
                {
                    try
                    {
                        await outlook.DeleteAsync(account, tag.Name, ct);
                    }
                    catch (Exception e) when (e is GraphUnavailableException or GraphConsentRequiredException)
                    {
                        return Results.Json(new { error = "graph_unavailable", message = $"Outlook could not remove the category for {account.DisplayName}: {e.Message}" },
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                }

                await index.RemoveFromLostAsync(userId, tag.Keyword, ct);
            }

            await store.DeleteAsync(userId, tagId, ct);
            return Results.NoContent();
        });
    }

    private static Task<List<MailAccount>> GraphAccountsAsync(MailDbContext db, string userId, CancellationToken ct) =>
        db.Accounts.AsNoTracking().Where(a => a.OwnerUserId == userId && a.TagStorage == TagStorage.Graph).ToListAsync(ct);

    private static async Task<IResult> Validated(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TagValidationException e)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
        }
    }
}
