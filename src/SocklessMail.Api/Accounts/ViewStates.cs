using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Data;

namespace SocklessMail.Api.Accounts;

/// <summary>A user's choice where the folder pane is remembered, and the view they last had open.</summary>
public sealed class ViewSettings
{
    public required string OwnerUserId { get; init; }

    /// <summary><see cref="ViewStates.Server"/> or <see cref="ViewStates.Browser"/>.</summary>
    public required string Storage { get; set; }

    /// <summary>A <see cref="ViewLocation"/> as JSON; null in browser storage.</summary>
    public string? LastView { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An account's folder pane: the folders shown expanded and the folder last open. Deleted with the account.</summary>
public sealed class AccountViewState
{
    public required Guid AccountId { get; init; }

    public required string OwnerUserId { get; init; }

    /// <summary>Folder ids as a JSON array.</summary>
    public required string Expanded { get; set; }

    public string? LastFolderId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An account's folder, or a unified view (<see cref="Unified"/> set, the others null).</summary>
public sealed record ViewLocation(Guid? AccountId, string? FolderId, string? Unified);

public sealed record AccountViewDto(IReadOnlyList<string>? Expanded, string? LastFolderId);

public sealed record ViewStateDto(ViewLocation? LastView, IReadOnlyDictionary<Guid, AccountViewDto>? Accounts);

/// <summary>State is null in browser storage, and before the browser first saved any.</summary>
public sealed record ViewStateResponse(string Storage, ViewStateDto? State);

public sealed class ViewStateValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// What the folder pane remembers across sessions: on the server by default, so every device the
/// user signs in on opens where they left off, or only in the browser when the user prefers that
/// (then nothing but the choice itself is kept here). Each save replaces the whole state.
/// </summary>
public sealed partial class ViewStates(MailDbContext db, TimeProvider clock)
{
    public const string Server = "server";
    public const string Browser = "browser";

    public const int MaxExpanded = 2000;

    // Folder ids are base64url of the full IMAP name (MailboxIds.FolderId); names are capped at 1000 characters.
    private const int MaxFolderIdLength = 4000;

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex FolderIdPattern();

    [GeneratedRegex("^[a-z]{1,32}$")]
    private static partial Regex UnifiedPattern();

    public async Task<ViewStateResponse> GetAsync(string userId, CancellationToken ct)
    {
        var settings = await db.ViewSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OwnerUserId == userId, ct);
        if (settings?.Storage == Browser)
        {
            return new ViewStateResponse(Browser, null);
        }

        var accounts = await db.AccountViewStates.AsNoTracking().Where(s => s.OwnerUserId == userId).ToListAsync(ct);
        if (settings is null && accounts.Count == 0)
        {
            return new ViewStateResponse(Server, null);
        }

        return new ViewStateResponse(Server, new ViewStateDto(
            settings?.LastView is { } lastView ? JsonSerializer.Deserialize<ViewLocation>(lastView, JsonSerializerOptions.Web) : null,
            accounts.ToDictionary(
                s => s.AccountId,
                s => new AccountViewDto(JsonSerializer.Deserialize<string[]>(s.Expanded, JsonSerializerOptions.Web) ?? [], s.LastFolderId))));
    }

    /// <summary>
    /// Switching to browser storage forgets what was kept here. Accounts the user does not have
    /// (any more: deleted in another tab) are skipped.
    /// </summary>
    public async Task<ViewStateResponse> SaveAsync(string userId, string storage, ViewStateDto? state, CancellationToken ct)
    {
        if (storage is not (Server or Browser))
        {
            throw new ViewStateValidationException("storage", "Choose server or browser.");
        }

        if (storage == Server && state is not null)
        {
            Validate(state);
        }

        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = await db.ViewSettings.SingleOrDefaultAsync(s => s.OwnerUserId == userId, ct);
        if (settings is null)
        {
            settings = new ViewSettings { OwnerUserId = userId, Storage = storage };
            db.Add(settings);
        }

        settings.Storage = storage;
        settings.UpdatedAt = now;
        await db.AccountViewStates.Where(s => s.OwnerUserId == userId).ExecuteDeleteAsync(ct);
        if (storage == Server && state is not null)
        {
            settings.LastView = state.LastView is { } lastView ? JsonSerializer.Serialize(lastView, JsonSerializerOptions.Web) : null;
            var owned = await db.Accounts.Where(a => a.OwnerUserId == userId).Select(a => a.Id).ToListAsync(ct);
            foreach (var (accountId, account) in state.Accounts ?? new Dictionary<Guid, AccountViewDto>())
            {
                if (owned.Contains(accountId))
                {
                    db.Add(new AccountViewState
                    {
                        AccountId = accountId,
                        OwnerUserId = userId,
                        Expanded = JsonSerializer.Serialize((account.Expanded ?? []).Distinct(), JsonSerializerOptions.Web),
                        LastFolderId = account.LastFolderId,
                        UpdatedAt = now,
                    });
                }
            }
        }
        else
        {
            settings.LastView = null;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(userId, ct);
    }

    private static void Validate(ViewStateDto state)
    {
        if (state.LastView is { } view)
        {
            var valid = view.Unified is { } unified
                ? UnifiedPattern().IsMatch(unified) && view.AccountId is null && view.FolderId is null
                : view.AccountId is not null && IsFolderId(view.FolderId);
            if (!valid)
            {
                throw new ViewStateValidationException("lastView", "Give an account and folder, or a unified view.");
            }
        }

        foreach (var account in (state.Accounts ?? new Dictionary<Guid, AccountViewDto>()).Values)
        {
            var expanded = account.Expanded ?? [];
            if (expanded.Count > MaxExpanded)
            {
                throw new ViewStateValidationException("accounts", $"At most {MaxExpanded} expanded folders per account.");
            }

            if (!expanded.All(IsFolderId) || (account.LastFolderId is not null && !IsFolderId(account.LastFolderId)))
            {
                throw new ViewStateValidationException("accounts", "Not a folder id.");
            }
        }
    }

    private static bool IsFolderId(string? id) => id is { Length: > 0 and <= MaxFolderIdLength } && FolderIdPattern().IsMatch(id);
}
