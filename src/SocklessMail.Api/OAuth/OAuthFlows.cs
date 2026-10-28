using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace SocklessMail.Api.OAuth;

public enum OAuthFlowStatus
{
    Pending,
    Done,
    Failed,
}

/// <summary>
/// One sign-in in progress. <see cref="State"/> goes to the provider and comes back on the
/// callback, which is how the callback finds the flow (it may arrive without a Nextcloud user);
/// <see cref="Id"/> is what the user's browser polls. Both are random and never logged.
/// </summary>
public sealed class OAuthFlow
{
    public required string Id { get; init; }
    public required string State { get; init; }
    public required string UserId { get; init; }
    public required string ProviderId { get; init; }
    public required string CodeVerifier { get; init; }
    public required string RedirectUri { get; init; }

    /// <summary>The account to sign in again; null for a new account.</summary>
    public Guid? AccountId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public volatile OAuthFlowStatus Status;
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? RefreshToken { get; set; }
    public string? Error { get; set; }

    /// <summary>Set once the callback started exchanging the code, so a replayed callback does nothing.</summary>
    public int Claimed;
}

/// <summary>
/// Sign-ins in progress, in memory: a restart only means signing in again. A flow lives for
/// <see cref="Lifetime"/>; a finished one is handed out once (<see cref="Take"/>).
/// </summary>
public sealed class OAuthFlows(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Per user, so one user cannot crowd out the others.</summary>
    public const int MaxFlowsPerUser = 10;

    private readonly ConcurrentDictionary<string, OAuthFlow> _byId = new();
    private readonly ConcurrentDictionary<string, OAuthFlow> _byState = new();

    public OAuthFlow? Start(string userId, string providerId, string redirectUri, Guid? accountId)
    {
        Prune();
        if (_byId.Values.Count(f => f.UserId == userId) >= MaxFlowsPerUser)
        {
            return null;
        }

        var flow = new OAuthFlow
        {
            Id = Random(),
            State = Random(),
            UserId = userId,
            ProviderId = providerId,
            CodeVerifier = Random(),
            RedirectUri = redirectUri,
            AccountId = accountId,
            CreatedAt = clock.GetUtcNow(),
        };
        _byId[flow.Id] = flow;
        _byState[flow.State] = flow;
        return flow;
    }

    public OAuthFlow? FindByState(string? state) =>
        state is { Length: > 0 } && _byState.TryGetValue(state, out var flow) && IsLive(flow) ? flow : null;

    /// <summary>The user's flow with this id; another user's looks like a missing one.</summary>
    public OAuthFlow? Get(string userId, string flowId) =>
        _byId.TryGetValue(flowId, out var flow) && flow.UserId == userId && IsLive(flow) ? flow : null;

    /// <summary>Removes and returns the user's finished flow, so its grant is used at most once.</summary>
    public OAuthFlow? Take(string userId, string flowId)
    {
        if (Get(userId, flowId) is not { Status: OAuthFlowStatus.Done } flow || !_byId.TryRemove(flow.Id, out _))
        {
            return null;
        }

        _byState.TryRemove(flow.State, out _);
        return flow;
    }

    /// <summary>PKCE S256 challenge of the flow's verifier.</summary>
    public static string Challenge(OAuthFlow flow) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(flow.CodeVerifier)));

    private bool IsLive(OAuthFlow flow) => clock.GetUtcNow() - flow.CreatedAt < Lifetime;

    private void Prune()
    {
        foreach (var flow in _byId.Values.Where(f => !IsLive(f)))
        {
            _byId.TryRemove(flow.Id, out _);
            _byState.TryRemove(flow.State, out _);
        }
    }

    /// <summary>256 random bits, base64url (43 characters; also a valid PKCE verifier).</summary>
    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
}
