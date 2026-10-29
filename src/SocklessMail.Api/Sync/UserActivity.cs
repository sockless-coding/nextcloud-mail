using System.Collections.Concurrent;
using System.Security.Claims;

namespace SocklessMail.Api.Sync;

/// <summary>
/// When each user last used the app (any /api request, polling included). IDLE watchers run only
/// for recently active users, so a server with many accounts does not hold a connection per
/// account around the clock. A user with an open push connection (<see cref="MailHub"/>) counts
/// as active until it closes.
/// </summary>
public sealed class UserActivity(WatcherOptions options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();
    private readonly Dictionary<string, int> _connections = [];

    /// <summary>Raised when a user who was not active within the window becomes active.</summary>
    public event Action? Activated;

    private TimeSpan Window => options.ActiveWindow;

    public void Touch(string userId)
    {
        var now = clock.GetUtcNow();
        var previous = _lastSeen.GetValueOrDefault(userId);
        _lastSeen[userId] = now;
        if (now - previous > Window)
        {
            Activated?.Invoke();
        }
    }

    public void Connected(string userId)
    {
        lock (_connections)
        {
            _connections[userId] = _connections.GetValueOrDefault(userId) + 1;
        }

        Touch(userId);
    }

    /// <summary>The window starts when the user's last connection closes.</summary>
    public void Disconnected(string userId)
    {
        lock (_connections)
        {
            if (_connections.GetValueOrDefault(userId) <= 1)
            {
                _connections.Remove(userId);
            }
            else
            {
                _connections[userId]--;
            }
        }

        Touch(userId);
    }

    /// <summary>Users active within the window, most recent first; forgets the others.</summary>
    public IReadOnlyList<string> ActiveUsers()
    {
        var now = clock.GetUtcNow();
        lock (_connections)
        {
            foreach (var user in _connections.Keys)
            {
                _lastSeen[user] = now;
            }
        }

        var cutoff = now - Window;
        foreach (var (user, seen) in _lastSeen)
        {
            if (seen < cutoff)
            {
                _lastSeen.TryRemove(new KeyValuePair<string, DateTimeOffset>(user, seen));
            }
        }

        return _lastSeen.Where(e => e.Value >= cutoff).OrderByDescending(e => e.Value).Select(e => e.Key).ToList();
    }
}

public static class UserActivityMiddleware
{
    /// <summary>Records activity for authenticated /api requests; place after authentication.</summary>
    public static IApplicationBuilder UseUserActivity(this IApplicationBuilder app)
    {
        var activity = app.ApplicationServices.GetRequiredService<UserActivity>();
        return app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api")
                && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId)
            {
                activity.Touch(userId);
            }

            return next(context);
        });
    }
}
