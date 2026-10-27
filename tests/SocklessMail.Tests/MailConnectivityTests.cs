using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using MailKit.Security;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Mail;

namespace SocklessMail.Tests;

public sealed class MailConnectivityTests
{
    [Theory]
    [InlineData("93.184.215.14", true)]       // public
    [InlineData("2606:4700::1111", true)]     // public IPv6
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("100.64.0.1", false)]          // carrier-grade NAT
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]             // unique local
    [InlineData("::ffff:10.0.0.1", false)]     // IPv4-mapped private
    public void Local_addresses_need_the_admin_setting(string address, bool allowedByDefault)
    {
        var ip = IPAddress.Parse(address);
        Assert.Equal(allowedByDefault, new ServerAddressPolicy(allowLocal: false).IsAllowed(ip));
        Assert.True(new ServerAddressPolicy(allowLocal: true).IsAllowed(ip));
    }

    [Theory]
    [InlineData("169.254.169.254")]            // cloud metadata
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::")]
    [InlineData("::ffff:169.254.169.254")]
    public void Reserved_addresses_are_never_allowed(string address)
    {
        Assert.False(new ServerAddressPolicy(allowLocal: true).IsAllowed(IPAddress.Parse(address)));
    }

    private static readonly MailEndpoint Endpoint = new("mail.example.com", 993, MailSecurity.SslTls);

    public static TheoryData<Exception, string> Failures() => new()
    {
        { new AuthenticationException(), "auth_failed" },
        { new SocketException((int)SocketError.ConnectionRefused), "connection_refused" },
        { new SocketException((int)SocketError.HostNotFound), "host_not_found" },
        { new SocketException((int)SocketError.NetworkUnreachable), "host_unreachable" },
        { new NotSupportedException("The IMAP server does not support the STARTTLS extension."), "starttls_unsupported" },
        { new IOException("The server unexpectedly closed the connection."), "protocol_error" },
        { new InvalidOperationException(), "unknown" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Failures_are_classified(Exception error, string code)
    {
        Assert.Equal(code, ConnectionErrors.Classify(error, Endpoint, null, timedOut: false).Code);
    }

    [Fact]
    public void Timeout_wins_over_the_exception_type()
    {
        Assert.Equal("timeout", ConnectionErrors.Classify(new IOException(), Endpoint, null, timedOut: true).Code);
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, "different host name")]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, "not trusted")]
    public void Certificate_problems_are_explained(SslPolicyErrors errors, string expected)
    {
        var e = ConnectionErrors.Classify(new SslHandshakeException(), Endpoint, errors, timedOut: false);
        Assert.Equal("tls_failed", e.Code);
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Password_credential_does_not_print_its_password()
    {
        Assert.DoesNotContain("hunter2", new PasswordCredential("hunter2").ToString());
    }
}
