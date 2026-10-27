using System.Security.Claims;

namespace SocklessMail.Api.Data;

public interface ICurrentUser
{
    /// <summary>The verified Nextcloud user of the current request, or null outside a user request.</summary>
    string? UserId { get; }
}

internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string? UserId =>
        accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id ? id : null;
}
