using MimeKit;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Tags;

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
    ServerSettings Smtp,
    string? SenderName = null);

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

/// <summary>Whether the account's mail is grouped into conversations.</summary>
public sealed record ConversationsInput(bool Enabled);

/// <summary>How the account is shown; none of it reaches the mail server. Empty values are cleared.</summary>
public sealed record AppearanceInput(string? Color, string? BadgeText, string? BadgeIcon, bool IncludeInUnified = true)
{
    /// <summary>The icons the app offers (frontend accountIcons.js); anything else is refused.</summary>
    public static readonly IReadOnlySet<string> Icons = new HashSet<string>(StringComparer.Ordinal)
    {
        "briefcase", "home", "school", "heart", "star", "cart", "account", "accountGroup", "bank", "rocket",
        "airplane", "music", "gamepad", "palette", "code", "book", "leaf", "paw", "coffee", "cog",
        "shield", "flash", "earth", "email",
    };

    public const int MaxBadgeLength = 16;

    /// <returns>Errors keyed by JSON field; empty when valid.</returns>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (!string.IsNullOrEmpty(Color) && !System.Text.RegularExpressions.Regex.IsMatch(Color, "^#[0-9a-fA-F]{6}$"))
        {
            errors["color"] = ["Use a colour like #1a62d6."];
        }

        // Up to three characters as people see them (an emoji with modifiers is one), and a byte cap.
        if (!string.IsNullOrEmpty(BadgeText)
            && (BadgeText.Length > MaxBadgeLength || new System.Globalization.StringInfo(BadgeText.Trim()).LengthInTextElements > 3))
        {
            errors["badgeText"] = ["Use at most three characters."];
        }

        if (!string.IsNullOrEmpty(BadgeIcon) && !Icons.Contains(BadgeIcon))
        {
            errors["badgeIcon"] = ["Choose one of the offered icons."];
        }

        return errors;
    }
}

/// <summary>What the API returns. Never contains the credential.</summary>
public sealed record AccountDto(
    Guid Id,
    string DisplayName,
    string? SenderName,
    string EmailAddress,
    string Username,
    MailAuthMethod AuthMethod,
    ServerSettings Imap,
    ServerSettings Smtp,
    int SortOrder,
    TagStorage TagStorage,
    string? Color,
    string? BadgeText,
    string? BadgeIcon,
    bool IncludeInUnified,
    bool Conversations)
{
    public static AccountDto From(MailAccount a) => new(
        a.Id, a.DisplayName, a.SenderName, a.EmailAddress, a.Username, a.AuthMethod,
        new ServerSettings(a.ImapHost, a.ImapPort, a.ImapSecurity),
        new ServerSettings(a.SmtpHost, a.SmtpPort, a.SmtpSecurity),
        a.SortOrder,
        a.TagStorage,
        a.Color,
        a.BadgeText,
        a.BadgeIcon,
        a.IncludeInUnified,
        a.Conversations);
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

        Check(input.SenderName is null || input.SenderName.Length <= 200, "senderName", "Use at most 200 characters.");

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
