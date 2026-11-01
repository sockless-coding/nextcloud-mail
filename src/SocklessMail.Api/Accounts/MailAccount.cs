using SocklessMail.Api.Tags;

namespace SocklessMail.Api.Accounts;

public enum MailSecurity
{
    None,
    StartTls,
    SslTls,
}

public enum MailAuthMethod
{
    Password,
    OAuth2,
}

/// <summary>A mail account. Belongs to exactly one Nextcloud user and is never visible to another.</summary>
public sealed class MailAccount
{
    public Guid Id { get; set; }

    /// <summary>Set from the verified AppAPI user on creation, never from request data, never changed.</summary>
    public required string OwnerUserId { get; init; }

    public int SortOrder { get; set; }

    /// <summary>Label in the navigation, e.g. "Work".</summary>
    public required string DisplayName { get; set; }

    /// <summary>The name recipients see in From; null sends the address alone.</summary>
    public string? SenderName { get; set; }

    public required string EmailAddress { get; set; }

    public required string ImapHost { get; set; }
    public int ImapPort { get; set; }
    public MailSecurity ImapSecurity { get; set; }

    public required string SmtpHost { get; set; }
    public int SmtpPort { get; set; }
    public MailSecurity SmtpSecurity { get; set; }

    /// <summary>Login name for both IMAP and SMTP.</summary>
    public required string Username { get; set; }

    public MailAuthMethod AuthMethod { get; set; }

    /// <summary>A <see cref="MailCredential"/>, encrypted by <see cref="CredentialProtector"/>.</summary>
    public required string ProtectedCredential { get; set; }

    /// <summary>The account's colour in the rail and lists, "#rrggbb"; null picks one from the id.</summary>
    public string? Color { get; set; }

    /// <summary>A few characters (an emoji, too) shown for the account instead of its initials.</summary>
    public string? BadgeText { get; set; }

    /// <summary>An icon shown for the account instead of its initials (a name from the app's set).</summary>
    public string? BadgeIcon { get; set; }

    /// <summary>Whether the account's folders are part of the "All accounts" views.</summary>
    public bool IncludeInUnified { get; set; } = true;

    /// <summary>Whether the account's mail is grouped into conversations (threads).</summary>
    public bool Conversations { get; set; }

    /// <summary>Where the account's tags are kept; learnt from the server when a message is first tagged.</summary>
    public TagStorage TagStorage { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
