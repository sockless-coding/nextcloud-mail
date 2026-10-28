using System.Collections.Concurrent;
using System.Text.Json;

namespace SocklessMail.Api.AppApi;

/// <summary>Remembered answers, so admin pages do not ask Nextcloud on every request.</summary>
public sealed class AdminCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    public ConcurrentDictionary<string, (bool IsAdmin, DateTimeOffset At)> Entries { get; } = new();
}

/// <summary>
/// Whether a user is a Nextcloud admin (member of the "admin" group), as Nextcloud tells it for
/// that user. AppAPI and HaRP already forward /api/admin only for admins (access level ADMIN in
/// info.xml); this check keeps the admin endpoints closed if a route is misregistered. Failures
/// to ask count as "no".
/// </summary>
public sealed class NextcloudAdmins(NextcloudClient nextcloud, AdminCache cache, TimeProvider clock, ILogger<NextcloudAdmins> logger)
{
    public async Task<bool> IsAdminAsync(string userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (cache.Entries.TryGetValue(userId, out var cached) && now - cached.At < AdminCache.Lifetime)
        {
            return cached.IsAdmin;
        }

        JsonElement user;
        try
        {
            user = await nextcloud.OcsAsync(HttpMethod.Get, "cloud/user", userId: userId, ct: ct);
        }
        catch (Exception e) when (e is NextcloudApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Could not ask Nextcloud whether the user is an admin");
            return false;
        }

        return Remember(userId, user);
    }

    /// <summary>Reads (and remembers) the answer from an OCS cloud/user response fetched as <paramref name="userId"/>.</summary>
    public bool Remember(string userId, JsonElement user)
    {
        var isAdmin = user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("groups", out var groups)
            && groups.ValueKind == JsonValueKind.Array
            && groups.EnumerateArray().Any(g => g.ValueKind == JsonValueKind.String && g.GetString() == "admin");
        cache.Entries[userId] = (isAdmin, clock.GetUtcNow());
        return isAdmin;
    }
}

/// <summary>Answers 403 unless the request's user is a Nextcloud admin.</summary>
public sealed class AdminOnlyFilter(NextcloudAdmins admins) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        return await admins.IsAdminAsync(http.User.GetNextcloudUserId(), http.RequestAborted)
            ? await next(context)
            : Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
    }
}
