using SocklessMail.Api.AppApi;
using SocklessMail.Api.Data;

namespace SocklessMail.Api.Api;

public sealed record AppSettingsInput(bool? DomainAgeChecks, bool? Assistant);

/// <summary>Instance-wide settings for Nextcloud admins (access level ADMIN in info.xml, checked here again).</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").AddEndpointFilter<AdminOnlyFilter>();

        admin.MapGet("/settings", (AppSettings settings, CancellationToken ct) => settings.GetAsync(ct));

        // A setting left out keeps its value.
        admin.MapPut("/settings", async (AppSettingsInput input, AppSettings settings, CancellationToken ct) =>
        {
            var current = await settings.GetAsync(ct);
            return await settings.SaveAsync(
                new AppSettingsDto(input.DomainAgeChecks ?? current.DomainAgeChecks, input.Assistant ?? current.Assistant), ct);
        });
    }
}
