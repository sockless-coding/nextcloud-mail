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

    /// <summary>Unread mail arrived in an INBOX; carries what a desktop notification shows.</summary>
    public const string NewMail = "newMail";

    /// <summary>The user looked at the app somewhere: the new-mail counts went back to zero.</summary>
    public const string InboxesSeen = "inboxesSeen";

    /// <summary>A scheduled message was sent, failed, or changed otherwise.</summary>
    public const string ScheduledChanged = "scheduledChanged";

    /// <summary>An Assistant task ended; carries only its id.</summary>
    public const string AssistantTaskChanged = "assistantTaskChanged";

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
/// changes endpoint what changed, so merging stays in one place. The one exception is
/// <see cref="NewMailAsync"/>, which carries sender and subject for a notification; like
/// everything here it only reaches the owner's own connections.
/// </summary>
public sealed class MailPush(IHubContext<MailHub> hub, ILogger<MailPush> logger)
{
    /// <summary>Messages a <see cref="MailHub.NewMail"/> describes; the count covers the rest.</summary>
    public const int NewMailItems = 5;

    public async Task NewMailAsync(string userId, Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<CachedMessage> messages)
    {
        var newest = messages.OrderByDescending(m => m.Date).Take(NewMailItems).Select(m => new
        {
            id = MailboxIds.MessageId(uidValidity, m.Uid),
            from = m.From.FirstOrDefault(),
            subject = m.Subject,
            date = m.Date,
        });
        try
        {
            await hub.Clients.User(userId).SendAsync(
                MailHub.NewMail, new { accountId, folderId = MailboxIds.FolderId(fullName), count = messages.Count, messages = newest.ToList() });
        }
        catch (Exception e)
        {
            // Only a notification is lost; the mail itself is cached and shows up as usual.
            logger.LogWarning(e, "Could not push new mail");
        }
    }

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

    public async Task InboxesSeenAsync(string userId)
    {
        try
        {
            await hub.Clients.User(userId).SendAsync(MailHub.InboxesSeen);
        }
        catch (Exception e)
        {
            // Other tabs ask again on their next refresh.
            logger.LogWarning(e, "Could not push that the inboxes were seen");
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

    public async Task AssistantTaskChangedAsync(string userId, long taskId)
    {
        try
        {
            await hub.Clients.User(userId).SendAsync(MailHub.AssistantTaskChanged, new { id = taskId });
        }
        catch (Exception e)
        {
            // The browser also asks for the task while it waits.
            logger.LogWarning(e, "Could not push an Assistant task change");
        }
    }
}
