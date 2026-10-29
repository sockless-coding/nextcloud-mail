using System.Collections.Concurrent;
using SocklessMail.Api.Accounts;

namespace SocklessMail.Api.Mailbox;

/// <summary>
/// Which folder of an account has each special use ("sent" → its folder id), for the unified
/// folders. Remembered for <see cref="MaxAge"/> so a unified page does not LIST every account; each
/// folder list the app loads refreshes it, creating or renaming a folder drops it, and editing the
/// account makes it stale.
/// </summary>
public sealed class FolderRoles(TimeProvider clock)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private sealed record Entry(DateTimeOffset AccountVersion, DateTimeOffset At, IReadOnlyDictionary<string, string> Roles);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    /// <param name="anyAge">Also an outdated map, as a fallback while the server cannot be asked.</param>
    public IReadOnlyDictionary<string, string>? Find(MailAccount account, bool anyAge = false) =>
        _entries.TryGetValue(account.Id, out var entry)
        && entry.AccountVersion == account.UpdatedAt
        && (anyAge || clock.GetUtcNow() - entry.At < MaxAge)
            ? entry.Roles
            : null;

    /// <summary>
    /// Takes the first selectable folder of each use in display order, as the folder pane does.
    /// The inbox is always INBOX.
    /// </summary>
    public IReadOnlyDictionary<string, string> Remember(MailAccount account, IEnumerable<FolderDto> ordered)
    {
        var roles = new Dictionary<string, string> { ["inbox"] = MailboxIds.FolderId("INBOX") };
        foreach (var folder in ordered)
        {
            if (folder.Selectable && folder.SpecialUse is { } use)
            {
                roles.TryAdd(use, folder.Id);
            }
        }

        _entries[account.Id] = new Entry(account.UpdatedAt, clock.GetUtcNow(), roles);
        return roles;
    }

    public void Forget(Guid accountId) => _entries.TryRemove(accountId, out _);
}
