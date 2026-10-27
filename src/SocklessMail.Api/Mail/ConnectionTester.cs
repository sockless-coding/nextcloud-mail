using SocklessMail.Api.Accounts;

namespace SocklessMail.Api.Mail;

public sealed record ProtocolTestResult(bool Ok, string? ErrorCode, string? Message);

public sealed record ConnectionTestResult(ProtocolTestResult Imap, ProtocolTestResult Smtp)
{
    public bool Ok => Imap.Ok && Smtp.Ok;
}

/// <summary>"Test connection": IMAP login and SMTP authentication, both in parallel.</summary>
public sealed class ConnectionTester(MailServerConnector connector, ILogger<ConnectionTester> logger)
{
    public async Task<ConnectionTestResult> TestAsync(
        MailEndpoint imap, MailEndpoint smtp, string username, MailCredential credential, CancellationToken ct)
    {
        var imapTask = RunAsync(MailProtocol.Imap, imap, async () =>
        {
            using var client = await connector.ConnectImapAsync(imap, username, credential, ct);
            await client.DisconnectAsync(true, ct);
        });
        var smtpTask = RunAsync(MailProtocol.Smtp, smtp, async () =>
        {
            using var client = await connector.ConnectSmtpAsync(smtp, username, credential, ct);
            await client.DisconnectAsync(true, ct);
        });
        return new ConnectionTestResult(await imapTask, await smtpTask);
    }

    private async Task<ProtocolTestResult> RunAsync(MailProtocol protocol, MailEndpoint endpoint, Func<Task> test)
    {
        try
        {
            await test();
            return new ProtocolTestResult(true, null, null);
        }
        catch (MailConnectionException e)
        {
            // Host and port only; exception messages of the inner MailKit error can quote server responses.
            logger.LogInformation("{Protocol} connection test to {Host}:{Port} failed: {Code} ({ExceptionType})",
                protocol, endpoint.Host, endpoint.Port, e.Code, e.InnerException?.GetType().Name);
            return new ProtocolTestResult(false, e.Code, e.Message);
        }
    }
}
