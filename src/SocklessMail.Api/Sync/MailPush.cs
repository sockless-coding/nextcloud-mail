using Microsoft.AspNetCore.SignalR;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Sync;

/// <summary>
/// The browser's push channel (SignalR at /api/hub). Only reachable through HaRP: AppAPI's PHP
/// proxy buffers responses, so in that mode the frontend keeps polling. Clients only listen; the
/// hub has no methods to call. SignalR's default user id is the NameIdentifier claim, which is
/// the Nextcloud user id, so <see cref="IHubClients.User"/> reaches every tab of that user.
/// </summary>
public sealed class MailHub(UserActivity activity) : Hub
{
    public const string FolderChanged = "folderChanged";

    /// <summary>A scheduled message was sent, failed, or changed otherwise.</summary>
    public const string ScheduledChanged = "scheduledChanged";

    public override Task OnConnectedAsync()
    {
        // An open tab keeps the user's IDLE watchers running although it no longer polls.
        activity.Connected(Context.User!.GetNextcloudUserId());
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        activity.Disconnected(Context.User!.GetNextcloudUserId());
        return Task.CompletedTask;
    }
}

/// <summary>
/// Tells a user's open tabs that a cached folder changed. Only a nudge: the client asks the
/// changes endpoint what changed, so merging stays in one place and nothing here can leak data.
/// </summary>
public sealed class MailPush(IHubContext<MailHub> hub, ILogger<MailPush> logger)
{
    public async Task FolderChangedAsync(string userId, Guid accountId, string fullName)
    {
        try
        {
            await hub.Clients.User(userId).SendAsync(
                MailHub.FolderChanged, new { accountId, folderId = MailboxIds.FolderId(fullName) });
        }
        catch (Exception e)
        {
            // The cache is already written; clients that miss this catch up on their next refresh.
            logger.LogWarning(e, "Could not push a folder change");
        }
    }

    public async Task ScheduledChangedAsync(string userId)
    {
        try
        {
            await hub.Clients.User(userId).SendAsync(MailHub.ScheduledChanged);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not push a scheduled message change");
        }
    }
}
