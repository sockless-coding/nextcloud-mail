using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Api.Security;

/// <summary>
/// Wraps the Data Protection key ring, which lives on the same volume as the database, with a
/// key that does not: SOCKLESS_MAIL_ENCRYPTION_KEY if the admin set it, otherwise APP_SECRET
/// (stored in Nextcloud's database). A copy of the data volume alone then cannot decrypt the
/// stored mail credentials.
/// </summary>
public static class KeyRingProtection
{
    public static IServiceCollection AddCredentialDataProtection(
        this IServiceCollection services, SocklessMailOptions options, AppApiOptions appApi)
    {
        var keys = KeyRingWrappingKeys.Create(options.EncryptionKey, appApi.AppSecret);
        services.AddSingleton(keys);
        services.AddDataProtection()
            // Keep the purpose chain stable regardless of the install path.
            .SetApplicationName("sockless_mail")
            .PersistKeysToFileSystem(new DirectoryInfo(options.KeyRingDirectory));
        services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new KeyRingXmlEncryptor(keys));
        return services;
    }
}

/// <summary>The wrapping keys by source; new keys are always wrapped with <see cref="PrimarySource"/>.</summary>
public sealed class KeyRingWrappingKeys
{
    public const string EnvironmentSource = "env";
    public const string AppSecretSource = "app_secret";

    private readonly Dictionary<string, byte[]> _keys;

    private KeyRingWrappingKeys(string primarySource, Dictionary<string, byte[]> keys)
    {
        PrimarySource = primarySource;
        _keys = keys;
    }

    public string PrimarySource { get; }

    public static KeyRingWrappingKeys Create(string? encryptionKey, string appSecret)
    {
        // APP_SECRET stays available so keys wrapped before the admin set the variable still open.
        var keys = new Dictionary<string, byte[]> { [AppSecretSource] = Derive(appSecret) };
        if (encryptionKey is null)
        {
            return new KeyRingWrappingKeys(AppSecretSource, keys);
        }

        keys[EnvironmentSource] = Derive(encryptionKey);
        return new KeyRingWrappingKeys(EnvironmentSource, keys);
    }

    public byte[] Primary => _keys[PrimarySource];

    public byte[] Get(string source) =>
        _keys.TryGetValue(source, out var key)
            ? key
            : throw new CryptographicException(source == EnvironmentSource
                ? "The key ring was protected with SOCKLESS_MAIL_ENCRYPTION_KEY, which is not set."
                : $"Unknown key ring protection '{source}'.");

    private static byte[] Derive(string secret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32,
            salt: Encoding.UTF8.GetBytes("sockless_mail key ring"), info: Encoding.UTF8.GetBytes("v1"));
}

/// <summary>AES-256-GCM over the serialized key element.</summary>
public sealed class KeyRingXmlEncryptor(KeyRingWrappingKeys keys) : IXmlEncryptor
{
    internal static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("sockless_mail.keyring.v1");

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(keys.Primary, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
        }

        CryptographicOperations.ZeroMemory(plaintext);
        var element = new XElement("socklessWrappedKey",
            new XAttribute("version", "1"),
            new XAttribute("source", keys.PrimarySource),
            Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]));
        return new EncryptedXmlInfo(element, typeof(KeyRingXmlDecryptor));
    }
}

/// <summary>Instantiated by Data Protection through its IServiceProvider constructor.</summary>
public sealed class KeyRingXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var keys = services.GetRequiredService<KeyRingWrappingKeys>();
        var source = (string?)encryptedElement.Attribute("source") ?? "";
        var data = Convert.FromBase64String(encryptedElement.Value);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        if (data.Length < nonceSize + tagSize)
        {
            throw new CryptographicException("Wrapped key ring element is truncated.");
        }

        var plaintext = new byte[data.Length - nonceSize - tagSize];
        using (var aes = new AesGcm(keys.Get(source), tagSize))
        {
            aes.Decrypt(data.AsSpan(0, nonceSize), data.AsSpan(nonceSize + tagSize), data.AsSpan(nonceSize, tagSize),
                plaintext, KeyRingXmlEncryptor.AssociatedData);
        }

        return XElement.Parse(Encoding.UTF8.GetString(plaintext));
    }
}
