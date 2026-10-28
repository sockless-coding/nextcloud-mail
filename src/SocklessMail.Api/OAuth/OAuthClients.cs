using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Data;

namespace SocklessMail.Api.OAuth;

/// <summary>
/// The app's registration with one provider, entered by a Nextcloud admin. Instance-wide, so not
/// user-scoped. The client secret is encrypted with the app's key ring like account passwords.
/// </summary>
public sealed class OAuthClient
{
    public required string ProviderId { get; init; }
    public required string ClientId { get; set; }
    public required string ProtectedClientSecret { get; set; }

    /// <summary>Microsoft: "common", "organizations", "consumers" or a tenant id; null for other providers.</summary>
    public string? Tenant { get; set; }

    /// <summary>The callback URL registered with the provider; providers require an exact match.</summary>
    public required string RedirectUri { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An admin's change. An empty secret keeps the stored one.</summary>
public sealed record OAuthClientInput(string? ClientId, string? ClientSecret, string? Tenant, string? RedirectUri);

/// <summary>What admins see; never the secret.</summary>
public sealed record OAuthClientDto(
    string Provider, string Name, bool Configured, string? ClientId, bool HasClientSecret, bool UsesTenant, string? Tenant, string? RedirectUri);

/// <summary>A configured provider with the client secret decrypted, for talking to the provider.</summary>
public sealed record OAuthClientSettings(OAuthProvider Provider, string ClientId, string ClientSecret, string? Tenant, string RedirectUri);

public sealed class OAuthClientStore(MailDbContext db, IDataProtectionProvider protection, OAuthProviderCatalog catalog, TimeProvider clock)
{
    private IDataProtector Protector(string providerId) =>
        protection.CreateProtector("SocklessMail.OAuthClientSecret.v1", providerId);

    public async Task<IReadOnlyList<OAuthClientDto>> ListAsync(CancellationToken ct)
    {
        var clients = await db.OAuthClients.AsNoTracking().ToDictionaryAsync(c => c.ProviderId, ct);
        return catalog.All.Select(p => clients.GetValueOrDefault(p.Id) is { } c
                ? new OAuthClientDto(p.Id, p.Name, true, c.ClientId, true, p.UsesTenant, c.Tenant, c.RedirectUri)
                : new OAuthClientDto(p.Id, p.Name, false, null, false, p.UsesTenant, null, null))
            .ToList();
    }

    /// <summary>The provider's settings when an admin configured it; null otherwise (or when the secret no longer decrypts).</summary>
    public async Task<OAuthClientSettings?> GetAsync(string providerId, CancellationToken ct)
    {
        if (catalog.Find(providerId) is not { } provider
            || await db.OAuthClients.AsNoTracking().SingleOrDefaultAsync(c => c.ProviderId == providerId, ct) is not { } client)
        {
            return null;
        }

        try
        {
            return new OAuthClientSettings(provider, client.ClientId, Protector(providerId).Unprotect(client.ProtectedClientSecret), client.Tenant, client.RedirectUri);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Saves (or, with an empty client id, removes) a provider's settings.</summary>
    /// <exception cref="Mailbox.MailboxValidationException">Invalid input.</exception>
    public async Task SaveAsync(OAuthProvider provider, OAuthClientInput input, CancellationToken ct)
    {
        var existing = await db.OAuthClients.SingleOrDefaultAsync(c => c.ProviderId == provider.Id, ct);
        var clientId = input.ClientId?.Trim() ?? "";
        if (clientId.Length == 0)
        {
            if (existing is not null)
            {
                db.OAuthClients.Remove(existing);
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        Require(clientId.Length <= 500, "clientId", "Use at most 500 characters.");
        var secret = input.ClientSecret?.Trim() ?? "";
        Require(secret.Length > 0 || existing is not null, "clientSecret", "Enter the client secret.");
        Require(secret.Length <= 2000, "clientSecret", "Use at most 2000 characters.");
        var redirect = input.RedirectUri?.Trim() ?? "";
        Require(IsRedirectUri(redirect), "redirectUri", "Enter the full callback URL, starting with https://.");
        var tenant = provider.UsesTenant && !string.IsNullOrWhiteSpace(input.Tenant) ? input.Tenant.Trim() : null;
        Require(tenant is null || (tenant.Length <= 100 && tenant.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')),
            "tenant", "Enter \"common\", \"organizations\", \"consumers\" or a tenant id.");

        var protectedSecret = secret.Length > 0 ? Protector(provider.Id).Protect(secret) : existing!.ProtectedClientSecret;
        if (existing is null)
        {
            db.OAuthClients.Add(new OAuthClient
            {
                ProviderId = provider.Id,
                ClientId = clientId,
                ProtectedClientSecret = protectedSecret,
                Tenant = tenant,
                RedirectUri = redirect,
                UpdatedAt = clock.GetUtcNow(),
            });
        }
        else
        {
            existing.ClientId = clientId;
            existing.ProtectedClientSecret = protectedSecret;
            existing.Tenant = tenant;
            existing.RedirectUri = redirect;
            existing.UpdatedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>https, or http for localhost (development); no fragment.</summary>
    private static bool IsRedirectUri(string value) =>
        value.Length <= 2000
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && uri.Fragment.Length == 0;

    private static void Require(bool ok, string field, string message)
    {
        if (!ok)
        {
            throw new Mailbox.MailboxValidationException(field, message);
        }
    }
}
