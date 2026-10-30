using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.OAuth;

/// <summary>
/// Turns an account's stored credential into what a connection logs in with. A password is used
/// as is. An OAuth grant becomes a current access token: kept in memory until shortly before it
/// expires, then refreshed (one refresh per account at a time). A refresh token the provider
/// rotates is saved back to the account.
/// <para>Failures surface as <see cref="MailConnectionException"/>, so every caller reports them
/// like a mail server error: oauth_signin_required (sign in again), oauth_not_configured,
/// oauth_unavailable.</para>
/// </summary>
public sealed class MailLogins(
    CredentialProtector credentials, IServiceScopeFactory scopes, OAuthTokens tokens, TimeProvider clock, ILogger<MailLogins> logger)
{
    /// <summary>Tokens are refreshed this long before they expire, so a connection never starts with one about to lapse.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private sealed record CachedToken(string ProtectedCredential, string AccessToken, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<Guid, CachedToken> _tokens = new();
    private readonly ConcurrentDictionary<Guid, CachedToken> _graphTokens = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();

    /// <exception cref="CredentialUnavailableException">The stored credential cannot be decrypted.</exception>
    /// <exception cref="MailConnectionException">An OAuth grant could not be turned into an access token.</exception>
    public async Task<MailCredential> ForConnectionAsync(MailAccount account, CancellationToken ct)
    {
        var credential = credentials.Unprotect(account.Id, account.ProtectedCredential);
        if (credential is not OAuthCredential grant)
        {
            return credential;
        }

        // The cache entry belongs to this exact stored credential: signing in again replaces it.
        if (Current(account) is { } cached)
        {
            return new AccessTokenCredential(cached);
        }

        var gate = _refreshLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Current(account) is { } refreshed)
            {
                return new AccessTokenCredential(refreshed);
            }

            return new AccessTokenCredential(await RefreshAsync(account, grant, ct));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A Microsoft Graph access token for an account signed in with a provider that has Graph
    /// scopes (<see cref="OAuthProvider.GraphScope"/>); null for any other account. Redeemed from
    /// the account's refresh token, cached like the mail token.
    /// </summary>
    /// <exception cref="GraphConsentRequiredException">The sign-in predates the Graph consent (or it was refused).</exception>
    /// <exception cref="MailConnectionException">As for <see cref="ForConnectionAsync"/>.</exception>
    public async Task<string?> GraphTokenAsync(MailAccount account, CancellationToken ct)
    {
        if (credentials.Unprotect(account.Id, account.ProtectedCredential) is not OAuthCredential grant)
        {
            return null;
        }

        if (_graphTokens.TryGetValue(account.Id, out var cached) && cached.ProtectedCredential == account.ProtectedCredential
            && cached.ExpiresAt - Margin > clock.GetUtcNow())
        {
            return cached.AccessToken;
        }

        var gate = _refreshLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var client = await scope.ServiceProvider.GetRequiredService<OAuthClientStore>().GetAsync(grant.Provider, ct);
            if (client?.Provider.GraphScope is not { } graphScope)
            {
                return null;
            }

            OAuthTokenResponse response;
            try
            {
                response = await tokens.RefreshAsync(client, grant.RefreshToken, ct, $"{graphScope} offline_access");
            }
            catch (OAuthConsentRequiredException)
            {
                throw new GraphConsentRequiredException();
            }
            catch (OAuthGrantRevokedException)
            {
                throw new MailConnectionException("oauth_signin_required",
                    $"The sign-in with {client.Provider.Name} has expired or was revoked. Sign in again in the account settings.");
            }
            catch (OAuthUnavailableException e)
            {
                throw new MailConnectionException("oauth_unavailable", e.Message, e);
            }

            var stored = account.ProtectedCredential;
            if (response.RefreshToken is { Length: > 0 } rotated && rotated != grant.RefreshToken)
            {
                stored = credentials.Protect(account.Id, grant with { RefreshToken = rotated });
                await SaveRotatedAsync(scope.ServiceProvider.GetRequiredService<MailDbContext>(), account, stored, ct);
            }

            _graphTokens[account.Id] = new CachedToken(stored, response.AccessToken, clock.GetUtcNow() + response.ExpiresIn);
            return response.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drops the cached access tokens (account changed, signed in again, or deleted).</summary>
    public void Forget(Guid accountId)
    {
        _tokens.TryRemove(accountId, out _);
        _graphTokens.TryRemove(accountId, out _);
    }

    private string? Current(MailAccount account) =>
        _tokens.TryGetValue(account.Id, out var cached)
        && cached.ProtectedCredential == account.ProtectedCredential
        && cached.ExpiresAt - Margin > clock.GetUtcNow()
            ? cached.AccessToken
            : null;

    private async Task<string> RefreshAsync(MailAccount account, OAuthCredential grant, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var client = await scope.ServiceProvider.GetRequiredService<OAuthClientStore>().GetAsync(grant.Provider, ct)
                     ?? throw new MailConnectionException("oauth_not_configured",
                         "Signing in with this provider is not set up (any more). Ask your administrator to configure it.");

        OAuthTokenResponse response;
        try
        {
            response = await tokens.RefreshAsync(client, grant.RefreshToken, ct);
        }
        catch (OAuthGrantRevokedException)
        {
            logger.LogInformation("OAuth grant of account {AccountId} was rejected; the user has to sign in again", account.Id);
            throw new MailConnectionException("oauth_signin_required",
                $"The sign-in with {client.Provider.Name} has expired or was revoked. Sign in again in the account settings.");
        }
        catch (OAuthUnavailableException e)
        {
            throw new MailConnectionException("oauth_unavailable", e.Message, e);
        }

        var stored = account.ProtectedCredential;
        if (response.RefreshToken is { Length: > 0 } rotated && rotated != grant.RefreshToken)
        {
            stored = credentials.Protect(account.Id, grant with { RefreshToken = rotated });
            await SaveRotatedAsync(scope.ServiceProvider.GetRequiredService<MailDbContext>(), account, stored, ct);
        }

        _tokens[account.Id] = new CachedToken(stored, response.AccessToken, clock.GetUtcNow() + response.ExpiresIn);
        return response.AccessToken;
    }

    /// <summary>
    /// Saves a rotated refresh token, unless the account's credential changed meanwhile (the user
    /// signed in again). The caller's account object is updated too, so its next lookup hits the cache.
    /// </summary>
    private static async Task SaveRotatedAsync(MailDbContext db, MailAccount account, string stored, CancellationToken ct)
    {
        var previous = account.ProtectedCredential;
        var saved = await db.Accounts.IgnoreQueryFilters()
            .Where(a => a.Id == account.Id && a.ProtectedCredential == previous)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ProtectedCredential, stored), ct);
        if (saved > 0)
        {
            account.ProtectedCredential = stored;
        }
    }
}

/// <summary>The account's sign-in lacks the consent to Microsoft Graph: signing in again asks for it.</summary>
public sealed class GraphConsentRequiredException() : Exception("Sign in again to allow tags as Outlook categories.");
