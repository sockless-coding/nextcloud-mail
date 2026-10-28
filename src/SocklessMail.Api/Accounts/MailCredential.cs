using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace SocklessMail.Api.Accounts;

/// <summary>
/// What an account authenticates with. Stored only in encrypted form;
/// <see cref="MailAccount.AuthMethod"/> records which subtype it is.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PasswordCredential), "password")]
[JsonDerivedType(typeof(OAuthCredential), "oauth")]
public abstract record MailCredential;

public sealed record PasswordCredential(string Password) : MailCredential
{
    // Keep the password out of logs and debugger displays.
    public override string ToString() => "PasswordCredential { Password = *** }";
}

/// <summary>An OAuth2 grant: the refresh token, from which access tokens are fetched as needed.</summary>
public sealed record OAuthCredential(string Provider, string RefreshToken) : MailCredential
{
    public override string ToString() => $"OAuthCredential {{ Provider = {Provider}, RefreshToken = *** }}";
}

/// <summary>
/// A current OAuth2 access token for one login (XOAUTH2). Never stored: resolved from an
/// <see cref="OAuthCredential"/> right before connecting (see <see cref="OAuth.MailLogins"/>).
/// </summary>
public sealed record AccessTokenCredential(string AccessToken) : MailCredential
{
    public override string ToString() => "AccessTokenCredential { AccessToken = *** }";
}

/// <summary>Thrown when a stored credential cannot be decrypted (key ring lost or key changed).</summary>
public sealed class CredentialUnavailableException(Exception inner)
    : Exception("The stored credential cannot be decrypted; the password has to be entered again.", inner);

public sealed class CredentialProtector(IDataProtectionProvider provider)
{
    // Bound to the account id: a ciphertext copied into another account's row does not decrypt.
    private IDataProtector For(Guid accountId) =>
        provider.CreateProtector("SocklessMail.Credentials.v1", accountId.ToString("N"));

    public string Protect(Guid accountId, MailCredential credential) =>
        For(accountId).Protect(JsonSerializer.Serialize(credential));

    public MailCredential Unprotect(Guid accountId, string protectedCredential)
    {
        try
        {
            return JsonSerializer.Deserialize<MailCredential>(For(accountId).Unprotect(protectedCredential))
                   ?? throw new CryptographicException("Empty credential.");
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            throw new CredentialUnavailableException(e);
        }
    }
}
