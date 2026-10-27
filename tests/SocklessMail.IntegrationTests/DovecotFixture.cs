using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Net.Imap;
using MailKit.Security;

[assembly: AssemblyFixture(typeof(SocklessMail.IntegrationTests.DovecotFixture))]

namespace SocklessMail.IntegrationTests;

/// <summary>
/// One Dovecot container for the test run: the server with CONDSTORE and QRESYNC (GreenMail has
/// neither). The official image accepts any user name with <see cref="Password"/> and creates the
/// mailbox on first login, so every test uses a fresh user. Plain-text login on the unencrypted
/// port is switched on for the tests.
/// </summary>
public sealed class DovecotFixture : IAsyncLifetime
{
    public const string Password = "dovecot-password";
    private const int ImapPort = 31143;

    private readonly IContainer _container = new ContainerBuilder("dovecot/dovecot:2.4.2")
        .WithEnvironment("USER_PASSWORD", Password)
        .WithResourceMapping(Encoding.UTF8.GetBytes("auth_allow_cleartext = yes\n"), "/etc/dovecot/conf.d/cleartext.conf")
        .WithPortBinding(ImapPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ImapPort))
        .Build();

    public string Host => _container.Hostname;
    public int Imap => _container.GetMappedPublicPort(ImapPort);

    public static string NewUser() => $"u{Guid.NewGuid():N}@example.test";

    public async Task<ImapClient> ConnectAsync(string user)
    {
        var client = new ImapClient();
        await client.ConnectAsync(Host, Imap, SecureSocketOptions.None);
        await client.AuthenticateAsync(user, Password);
        return client;
    }

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
