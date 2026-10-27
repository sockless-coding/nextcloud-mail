using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Mail;

namespace SocklessMail.IntegrationTests;

/// <summary>"Test connection" against a real IMAP/SMTP server.</summary>
public sealed class ConnectionTestTests(GreenMailFixture mail)
{
    private static readonly ConnectionTester Tester =
        new(new MailServerConnector(new PolicySocketConnector(new ServerAddressPolicy(allowLocal: true))), NullLogger<ConnectionTester>.Instance);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private MailEndpoint PlainImap => new(mail.Host, mail.Imap, MailSecurity.None);
    private MailEndpoint PlainSmtp => new(mail.Host, mail.Smtp, MailSecurity.None);

    private Task<ConnectionTestResult> Test(MailEndpoint imap, MailEndpoint smtp, string password = GreenMailFixture.AlicePassword) =>
        Tester.TestAsync(imap, smtp, GreenMailFixture.AliceAddress, new PasswordCredential(password), Ct);

    [Fact]
    public async Task Valid_settings_pass_for_imap_and_smtp()
    {
        var result = await Test(PlainImap, PlainSmtp);

        Assert.True(result.Imap.Ok, result.Imap.Message);
        Assert.True(result.Smtp.Ok, result.Smtp.Message);
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_bad_credentials()
    {
        var result = await Test(PlainImap, PlainSmtp, "wrong");

        Assert.Equal("auth_failed", result.Imap.ErrorCode);
        Assert.Equal("auth_failed", result.Smtp.ErrorCode);
    }

    [Fact]
    public async Task Untrusted_certificate_is_reported_as_tls_problem()
    {
        var result = await Test(
            new MailEndpoint(mail.Host, mail.Imaps, MailSecurity.SslTls),
            new MailEndpoint(mail.Host, mail.Smtps, MailSecurity.SslTls));

        Assert.Equal("tls_failed", result.Imap.ErrorCode);
        Assert.Equal("tls_failed", result.Smtp.ErrorCode);
        Assert.Contains("certificate", result.Imap.Message);
    }

    [Fact]
    public async Task Ssl_against_a_plain_port_explains_the_port_or_security_setting()
    {
        var result = await Test(new MailEndpoint(mail.Host, mail.Imap, MailSecurity.SslTls), PlainSmtp);

        Assert.False(result.Imap.Ok);
        Assert.Contains(result.Imap.ErrorCode, new[] { "tls_failed", "protocol_error", "timeout" });
        Assert.True(result.Smtp.Ok, result.Smtp.Message);
    }

    [Fact]
    public async Task Closed_port_is_reported_as_refused()
    {
        var closed = FreeLocalPort();
        var result = await Test(new MailEndpoint("127.0.0.1", closed, MailSecurity.None), PlainSmtp);

        Assert.Equal("connection_refused", result.Imap.ErrorCode);
    }

    [Fact]
    public async Task Unknown_host_is_reported_as_not_found()
    {
        var result = await Test(new MailEndpoint("no-such-host.invalid", 993, MailSecurity.SslTls), PlainSmtp);

        Assert.Equal("host_not_found", result.Imap.ErrorCode);
    }

    [Fact]
    public async Task Local_server_is_refused_unless_the_admin_allows_it()
    {
        var strict = new ConnectionTester(new MailServerConnector(new PolicySocketConnector(new ServerAddressPolicy(allowLocal: false))), NullLogger<ConnectionTester>.Instance);
        var result = await strict.TestAsync(PlainImap, PlainSmtp, GreenMailFixture.AliceAddress,
            new PasswordCredential(GreenMailFixture.AlicePassword), Ct);

        Assert.Equal("address_not_allowed", result.Imap.ErrorCode);
        Assert.Equal("address_not_allowed", result.Smtp.ErrorCode);
    }

    private static int FreeLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
