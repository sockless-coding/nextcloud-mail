using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using SocklessMail.Api.Security;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

public sealed class KeyRingTests
{
    private const string EncryptionKey = "an-admin-chosen-key-of-sufficient-length-0123";
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> WithKey(string key) => new() { ["SOCKLESS_MAIL_ENCRYPTION_KEY"] = key };

    [Fact]
    public void Wrapping_round_trips_and_records_its_source()
    {
        var keys = KeyRingWrappingKeys.Create(EncryptionKey, TestApp.Secret);
        var element = new XElement("key", new XElement("masterKey", "secret material"));

        var wrapped = new KeyRingXmlEncryptor(keys).Encrypt(element);

        Assert.Equal("env", (string?)wrapped.EncryptedElement.Attribute("source"));
        Assert.DoesNotContain("secret material", wrapped.EncryptedElement.ToString());
        var services = new ServiceCollection().AddSingleton(keys).BuildServiceProvider();
        Assert.Equal(element.ToString(), new KeyRingXmlDecryptor(services).Decrypt(wrapped.EncryptedElement).ToString());
    }

    [Fact]
    public void A_different_key_cannot_unwrap()
    {
        var wrapped = new KeyRingXmlEncryptor(KeyRingWrappingKeys.Create(EncryptionKey, TestApp.Secret))
            .Encrypt(new XElement("key")).EncryptedElement;
        var other = KeyRingWrappingKeys.Create(EncryptionKey + "x", TestApp.Secret);
        var services = new ServiceCollection().AddSingleton(other).BuildServiceProvider();

        Assert.ThrowsAny<CryptographicException>(() => { _ = new KeyRingXmlDecryptor(services).Decrypt(wrapped); });
    }

    [Fact]
    public async Task Credentials_survive_a_restart_with_the_same_key()
    {
        using var first = new TestApp(settings: WithKey(EncryptionKey));
        var id = await CreateAsync(first.ClientAs("alice"));

        using var second = new TestApp(first.StorageDirectory, WithKey(EncryptionKey));
        var result = await JsonAsync(await second.ClientAs("alice").PostAsync($"/api/accounts/{id}/test", null, Ct));

        Assert.Equal("address_not_allowed", result.GetProperty("imap").GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Credentials_are_unavailable_when_the_key_is_removed()
    {
        using var first = new TestApp(settings: WithKey(EncryptionKey));
        var id = await CreateAsync(first.ClientAs("alice"));

        // The volume without the admin's key: the app starts, but cannot open the credentials.
        using var second = new TestApp(first.StorageDirectory);
        var result = await JsonAsync(await second.ClientAs("alice").PostAsync($"/api/accounts/{id}/test", null, Ct));

        Assert.Equal("credential_unavailable", result.GetProperty("imap").GetProperty("errorCode").GetString());
    }

    [Fact]
    public void Short_encryption_key_is_refused_at_startup()
    {
        using var app = new TestApp(settings: WithKey("too-short"));
        var e = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("SOCKLESS_MAIL_ENCRYPTION_KEY", e.ToString());
    }
}
