using System.Net;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using SocklessMail.Api.Accounts;

namespace SocklessMail.Api.Mail;

public enum MailProtocol
{
    Imap,
    Smtp,
}

/// <summary>Where and how to connect; separate from <see cref="MailAccount"/> so unsaved settings can be tested.</summary>
public sealed record MailEndpoint(string Host, int Port, MailSecurity Security);

/// <summary>A failure classified for the user. <see cref="Code"/> is stable; the message is display text.</summary>
public sealed class MailConnectionException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Opens connected, authenticated MailKit clients. DNS is resolved here and every address is
/// checked against <see cref="ServerAddressPolicy"/> before connecting; MailKit then gets the
/// already connected socket, so the address it talks to is the one that was checked (no DNS
/// rebinding between check and connect), while TLS still validates the certificate against the
/// host name.
/// </summary>
public sealed class MailServerConnector(ServerAddressPolicy policy)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<ImapClient> ConnectImapAsync(MailEndpoint endpoint, string username, MailCredential credential, CancellationToken ct)
    {
        var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, endpoint, username, credential, ct);
        return client;
    }

    public async Task<SmtpClient> ConnectSmtpAsync(MailEndpoint endpoint, string username, MailCredential credential, CancellationToken ct)
    {
        var client = new SmtpClient();
        await ConnectAndAuthenticateAsync(client, endpoint, username, credential, ct);
        return client;
    }

    private async Task ConnectAndAuthenticateAsync(
        MailService client, MailEndpoint endpoint, string username, MailCredential credential, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        client.Timeout = (int)Timeout.TotalMilliseconds;

        System.Net.Security.SslPolicyErrors? certificateErrors = null;
        client.ServerCertificateValidationCallback = (_, _, _, errors) =>
        {
            certificateErrors = errors;
            return errors == System.Net.Security.SslPolicyErrors.None;
        };

        try
        {
            var socket = await ConnectSocketAsync(endpoint.Host, endpoint.Port, timeout.Token);
            await client.ConnectAsync(socket, endpoint.Host, endpoint.Port, ToSocketOptions(endpoint.Security), timeout.Token);

            if (client is SmtpClient smtp && !smtp.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            {
                // Relays that do not ask for authentication are valid; there is nothing to log in to.
                return;
            }

            await AuthenticateAsync(client, username, credential, timeout.Token);
        }
        catch (Exception e) when (e is not MailConnectionException && !ct.IsCancellationRequested)
        {
            client.Dispose();
            throw ConnectionErrors.Classify(e, endpoint, certificateErrors,
                timedOut: timeout.IsCancellationRequested || e is TimeoutException);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<Socket> ConnectSocketAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (SocketException e)
            {
                throw new MailConnectionException("host_not_found", $"The server name \"{host}\" could not be resolved.", e);
            }
        }

        var allowed = addresses.Where(policy.IsAllowed).ToArray();
        if (addresses.Length > 0 && allowed.Length == 0)
        {
            throw new MailConnectionException("address_not_allowed",
                $"\"{host}\" points to a private or reserved network address. An administrator can allow local mail servers with SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS.");
        }

        SocketException? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, port, ct);
                return socket;
            }
            catch (SocketException e)
            {
                last = e;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static Task AuthenticateAsync(MailService client, string username, MailCredential credential, CancellationToken ct) =>
        credential switch
        {
            PasswordCredential password => client.AuthenticateAsync(username, password.Password, ct),
            _ => throw new NotSupportedException($"Credential type {credential.GetType().Name} is not supported yet."),
        };

    private static SecureSocketOptions ToSocketOptions(MailSecurity security) => security switch
    {
        MailSecurity.SslTls => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };
}
