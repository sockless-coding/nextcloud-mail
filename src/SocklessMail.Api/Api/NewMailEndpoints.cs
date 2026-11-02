using System.Security.Claims;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Api;

/// <summary>
/// The count on the installed app's icon: unread INBOX mail that arrived since the user last
/// looked at the app, on any device. Kept with the cache (<see cref="CachedFolder.SeenUid"/>).
/// </summary>
public static class NewMailEndpoints
{
    public static void MapNewMailEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/new-mail");

        group.MapGet("", async (FolderCache cache, CancellationToken ct) =>
            Results.Ok(new { counts = await cache.NewInInboxesAsync(ct) }));

        group.MapPut("/seen", async (InboxesSeenInput input, ClaimsPrincipal user, FolderCache cache, MailPush push, CancellationToken ct) =>
        {
            // The query filter keeps it to the user's own folders, whatever ids are sent.
            if (await cache.MarkInboxesSeenAsync(input.AccountIds ?? [], ct))
            {
                await push.InboxesSeenAsync(user.GetNextcloudUserId());
            }

            return Results.NoContent();
        });
    }
}

/// <summary>The accounts whose inboxes the user just saw (those the count covers).</summary>
public sealed record InboxesSeenInput(IReadOnlyList<Guid>? AccountIds);
