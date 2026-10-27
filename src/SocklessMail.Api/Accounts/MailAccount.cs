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

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
