using MimeKit;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.Accounts;

public sealed record ServerSettings(string Host, int Port, MailSecurity Security)
{
    public MailEndpoint ToEndpoint() => new(Host.Trim(), Port, Security);
}

/// <summary>Create/update/test request. On update and test, an empty password keeps the stored one.</summary>
public sealed record AccountInput(
    string DisplayName,
    string EmailAddress,
    string Username,
    string? Password,
    ServerSettings Imap,
    ServerSettings Smtp);

public sealed record ConnectionTestInput(
    string DisplayName,
    string EmailAddress,
    string Username,
    string? Password,
    ServerSettings Imap,
    ServerSettings Smtp,
    Guid? AccountId)
{
    public AccountInput ToAccountInput() => new(DisplayName, EmailAddress, Username, Password, Imap, Smtp);
}

public sealed record ReorderInput(IReadOnlyList<Guid> AccountIds);

/// <summary>What the API returns. Never contains the credential.</summary>
public sealed record AccountDto(
    Guid Id,
    string DisplayName,
    string EmailAddress,
    string Username,
    MailAuthMethod AuthMethod,
    ServerSettings Imap,
    ServerSettings Smtp,
    int SortOrder)
{
    public static AccountDto From(MailAccount a) => new(
        a.Id, a.DisplayName, a.EmailAddress, a.Username, a.AuthMethod,
        new ServerSettings(a.ImapHost, a.ImapPort, a.ImapSecurity),
        new ServerSettings(a.SmtpHost, a.SmtpPort, a.SmtpSecurity),
        a.SortOrder);
}

public static class AccountValidation
{
    public const int MaxPasswordLength = 1024;

    /// <returns>Errors keyed by JSON field path; empty when valid.</returns>
    public static Dictionary<string, string[]> Validate(AccountInput? input, bool passwordRequired)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null)
        {
            errors[""] = ["A request body is required."];
            return errors;
        }

        void Check(bool ok, string field, string message)
        {
            if (!ok && !errors.ContainsKey(field))
            {
                errors[field] = [message];
            }
        }

        Check(!string.IsNullOrWhiteSpace(input.DisplayName), "displayName", "Enter a name for the account.");
        Check(input.DisplayName is null || input.DisplayName.Length <= 200, "displayName", "Use at most 200 characters.");

        Check(IsEmailAddress(input.EmailAddress), "emailAddress", "Enter a valid email address.");

        Check(!string.IsNullOrWhiteSpace(input.Username), "username", "Enter the login name.");
        Check(input.Username is null || input.Username.Length <= 320, "username", "Use at most 320 characters.");

        if (passwordRequired)
        {
            Check(!string.IsNullOrEmpty(input.Password), "password", "Enter the password.");
        }

        Check(input.Password is null || input.Password.Length <= MaxPasswordLength, "password", "The password is too long.");

        ValidateServer(input.Imap, "imap", Check);
        ValidateServer(input.Smtp, "smtp", Check);
        return errors;
    }

    private static void ValidateServer(ServerSettings? server, string prefix, Action<bool, string, string> check)
    {
        if (server is null)
        {
            check(false, prefix, "Server settings are required.");
            return;
        }

        var host = server.Host?.Trim() ?? "";
        check(host.Length is > 0 and <= 253, $"{prefix}.host", "Enter the server name.");
        check(!host.Contains("://") && !host.Any(char.IsWhiteSpace) && !host.Contains('/'), $"{prefix}.host",
            "Enter only the server name, without protocol or path.");
        check(server.Port is > 0 and <= 65535, $"{prefix}.port", "Enter a port between 1 and 65535.");
        check(Enum.IsDefined(server.Security), $"{prefix}.security", "Choose a security setting.");
    }

    private static bool IsEmailAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 320
        && MailboxAddress.TryParse(value, out var mailbox)
        && mailbox.Address == value.Trim()
        && mailbox.Address.Contains('@');
}
