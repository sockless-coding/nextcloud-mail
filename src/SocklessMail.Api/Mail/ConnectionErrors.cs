using System.Net.Security;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace SocklessMail.Api.Mail;

/// <summary>
/// Turns MailKit and socket exceptions into codes the UI can explain. Messages name the host and
/// port the user entered, never credentials, and do not echo server responses verbatim.
/// </summary>
public static class ConnectionErrors
{
    public static MailConnectionException Classify(Exception e, MailEndpoint endpoint, SslPolicyErrors? certificateErrors, bool timedOut)
    {
        var where = $"{endpoint.Host}:{endpoint.Port}";
        return e switch
        {
            _ when timedOut => new("timeout",
                $"{where} did not answer in time. Check the host name, port and firewall.", e),

            AuthenticationException => new("auth_failed",
                "The server rejected the username or password.", e),

            SslHandshakeException => new("tls_failed", TlsMessage(where, certificateErrors), e),

            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => new("connection_refused",
                $"{where} refused the connection. Check the port.", e),

            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => new("host_not_found",
                $"The server name \"{endpoint.Host}\" could not be resolved.", e),

            SocketException => new("host_unreachable",
                $"{where} could not be reached. Check the host name and your network.", e),

            NotSupportedException when e.Message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase) => new("starttls_unsupported",
                $"{where} does not support STARTTLS. Choose SSL/TLS or a different port.", e),

            NotSupportedException => new("auth_unsupported",
                "The server does not offer a supported way to log in.", e),

            ImapProtocolException or SmtpProtocolException or ProtocolException or IOException => new("protocol_error",
                $"{where} answered with something unexpected. The port or security setting is probably wrong (typical: 993 with SSL/TLS or 143 with STARTTLS for IMAP, 465 with SSL/TLS or 587 with STARTTLS for SMTP).", e),

            ImapCommandException or SmtpCommandException => new("server_error",
                $"{where} reported an error. Try again later or check the account settings.", e),

            _ => new("unknown", $"Connecting to {where} failed unexpectedly.", e),
        };
    }

    private static string TlsMessage(string where, SslPolicyErrors? errors)
    {
        if (errors is null or SslPolicyErrors.None)
        {
            return $"The secure connection to {where} failed. The port or security setting may be wrong.";
        }

        var problems = new List<string>();
        if (errors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            problems.Add("it was issued for a different host name");
        }

        if (errors.Value.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            problems.Add("it is not trusted (self-signed, expired or from an unknown authority)");
        }

        if (errors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            problems.Add("the server sent none");
        }

        return $"The certificate of {where} was rejected: {string.Join("; ", problems)}.";
    }
}
