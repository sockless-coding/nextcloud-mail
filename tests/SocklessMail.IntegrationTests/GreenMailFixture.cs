using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

[assembly: AssemblyFixture(typeof(SocklessMail.IntegrationTests.GreenMailFixture))]

namespace SocklessMail.IntegrationTests;

/// <summary>
/// One GreenMail container for the test run. Plain ports 3143 (IMAP) and 3025 (SMTP), TLS ports
/// 3993 (IMAPS) and 3465 (SMTPS) with GreenMail's self-signed certificate.
/// Note: GreenMail turns authentication OFF whenever -Dgreenmail.auth.disabled is present, even
/// with "=false", so the flag must be absent.
/// </summary>
public sealed class GreenMailFixture : IAsyncLifetime
{
    public const string AliceAddress = "alice@example.test";
    public const string AlicePassword = "alice-password";
    public const string ReaderAddress = "reader@example.test";
    public const string ReaderPassword = "reader-password";
    public const string ActionsPassword = "actions-password";

    private readonly IContainer _container = new ContainerBuilder("greenmail/standalone:2.1.15")
        .WithEnvironment("GREENMAIL_OPTS",
            $"-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.users=alice:{AlicePassword}@example.test,reader:{ReaderPassword}@example.test,actions:{ActionsPassword}@example.test,notrash:{ActionsPassword}@example.test,composer:{ActionsPassword}@example.test,nosent:{ActionsPassword}@example.test,rcpt1:{ActionsPassword}@example.test,rcpt2:{ActionsPassword}@example.test,rcpt3:{ActionsPassword}@example.test,watcher:{ActionsPassword}@example.test,searcher:{ActionsPassword}@example.test,filters:{ActionsPassword}@example.test,drafter:{ActionsPassword}@example.test,oauth:{ActionsPassword}@example.test,filer:{ActionsPassword}@example.test,invitee:{ActionsPassword}@example.test,scheduler:{ActionsPassword}@example.test -Dgreenmail.users.login=email")
        .WithPortBinding(3143, true)
        .WithPortBinding(3025, true)
        .WithPortBinding(3993, true)
        .WithPortBinding(3465, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilInternalTcpPortIsAvailable(3143)
            .UntilInternalTcpPortIsAvailable(3025)
            .UntilInternalTcpPortIsAvailable(3993)
            .UntilInternalTcpPortIsAvailable(3465))
        .Build();

    public string Host => _container.Hostname;
    public int Imap => _container.GetMappedPublicPort(3143);
    public int Smtp => _container.GetMappedPublicPort(3025);
    public int Imaps => _container.GetMappedPublicPort(3993);
    public int Smtps => _container.GetMappedPublicPort(3465);

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
