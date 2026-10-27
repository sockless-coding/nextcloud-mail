using System.Security.Claims;

namespace SocklessMail.Api.Data;

public interface ICurrentUser
{
    /// <summary>The verified Nextcloud user of the current request, or null outside a user request.</summary>
    string? UserId { get; }
}

internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private string? _actingFor;

    public string? UserId => _actingFor ??
        (accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id ? id : null);

    /// <summary>For background work on a user's data; must happen before the scope creates a DbContext.</summary>
    public void ActFor(string userId) => _actingFor = userId;
}

public static class CurrentUserScopes
{
    /// <summary>
    /// A service scope whose <see cref="ICurrentUser"/> is <paramref name="userId"/>, for background
    /// work (mailbox sync) outside a request. The DbContext's query filters then apply as usual.
    /// </summary>
    public static AsyncServiceScope CreateUserScope(this IServiceScopeFactory scopes, string userId)
    {
        var scope = scopes.CreateAsyncScope();
        ((HttpContextCurrentUser)scope.ServiceProvider.GetRequiredService<ICurrentUser>()).ActFor(userId);
        return scope;
    }
}
