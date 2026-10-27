using System.Security.Claims;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Api.Api;

public static class MeEndpoints
{
    public sealed record MeResponse(string UserId, string? DisplayName);

    public static void MapMeEndpoints(this RouteGroupBuilder api)
    {
        // The user id comes from the verified AppAPI header; the display name is a round trip to
        // Nextcloud as that user, which exercises the outgoing authentication as well.
        api.MapGet("/me", async (ClaimsPrincipal principal, NextcloudClient nextcloud, ILogger<MeResponse> logger, CancellationToken ct) =>
        {
            var userId = principal.GetNextcloudUserId();
            string? displayName = null;
            try
            {
                var user = await nextcloud.OcsAsync(HttpMethod.Get, "cloud/user", userId: userId, ct: ct);
                if (user.ValueKind == System.Text.Json.JsonValueKind.Object
                    && user.TryGetProperty("displayname", out var name))
                {
                    displayName = name.GetString();
                }
            }
            catch (Exception e) when (e is NextcloudApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "Could not fetch the display name from Nextcloud");
            }

            return new MeResponse(userId, displayName);
        });
    }
}
