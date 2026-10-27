using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace SocklessMail.Api.Accounts;

/// <summary>
/// What an account authenticates with. Stored only in encrypted form. OAuth2 adds a second
/// subtype (access/refresh token) later; <see cref="MailAccount.AuthMethod"/> records which.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PasswordCredential), "password")]
public abstract record MailCredential;

public sealed record PasswordCredential(string Password) : MailCredential
{
    // Keep the password out of logs and debugger displays.
    public override string ToString() => "PasswordCredential { Password = *** }";
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
