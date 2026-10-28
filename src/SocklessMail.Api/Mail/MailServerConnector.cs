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

/// <summary>IMAP extensions to ignore even when a server announces them; lets tests exercise the fallbacks.</summary>
public sealed record ImapFeatures
{
    public ImapCapabilities Disabled { get; init; }
}

/// <summary>
/// Opens connected, authenticated MailKit clients. The TCP connection comes from
/// <see cref="PolicySocketConnector"/> (address policy enforced); MailKit then runs the protocol
/// on that socket, and TLS still validates the certificate against the host name.
/// </summary>
public sealed class MailServerConnector(PolicySocketConnector sockets, ImapFeatures? features = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<ImapClient> ConnectImapAsync(MailEndpoint endpoint, string username, MailCredential credential, CancellationToken ct)
    {
        var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, endpoint, username, credential, ct);
        try
        {
            client.Capabilities &= ~(features?.Disabled ?? ImapCapabilities.None);
            // Must happen before any folder is selected. With it, syncs learn about removed
            // messages from VANISHED responses instead of comparing UID lists.
            if (client.Capabilities.HasFlag(ImapCapabilities.QuickResync))
            {
                await client.EnableQuickResyncAsync(ct);
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }

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
            var socket = await sockets.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
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

    private static Task AuthenticateAsync(MailService client, string username, MailCredential credential, CancellationToken ct) =>
        credential switch
        {
            PasswordCredential password => client.AuthenticateAsync(username, password.Password, ct),
            AccessTokenCredential token => client.AuthenticateAsync(new SaslMechanismOAuth2(username, token.AccessToken), ct),
            _ => throw new NotSupportedException($"Credential type {credential.GetType().Name} is not supported yet."),
        };

    private static SecureSocketOptions ToSocketOptions(MailSecurity security) => security switch
    {
        MailSecurity.SslTls => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };
}
