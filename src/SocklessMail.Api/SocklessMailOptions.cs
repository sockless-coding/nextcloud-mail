namespace SocklessMail.Api;

/// <summary>Settings of this app (declared in info.xml's environment-variables), as opposed to the AppAPI contract.</summary>
public sealed class SocklessMailOptions
{
    public const int MinEncryptionKeyLength = 32;

    /// <summary>Directory for the database and the Data Protection key ring.</summary>
    public required string StorageDirectory { get; init; }

    /// <summary>SOCKLESS_MAIL_ENCRYPTION_KEY; null means the key ring is wrapped with APP_SECRET.</summary>
    public string? EncryptionKey { get; init; }

    /// <summary>SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS: allow mail servers on private and loopback addresses.</summary>
    public bool AllowLocalServers { get; init; }

    /// <summary>
    /// SOCKLESS_MAIL_BIMI_AUTHSERV_IDS: comma-separated authserv-ids (the host name at the start of
    /// an Authentication-Results header) whose DMARC verdicts count for BIMI logos. Empty means the
    /// topmost header counts, whoever added it (see <see cref="Bimi.BimiSenders"/>).
    /// </summary>
    public IReadOnlySet<string> BimiAuthServIds { get; init; } = new HashSet<string>();

    public string DatabasePath =>Path.Combine(StorageDirectory, "sockless_mail.db");
    public string KeyRingDirectory => Path.Combine(StorageDirectory, "keys");

    public static SocklessMailOptions FromConfiguration(IConfiguration config, string contentRoot)
    {
        var storage = config["APP_PERSISTENT_STORAGE"];
        if (string.IsNullOrEmpty(storage))
        {
            throw new InvalidOperationException(
                "APP_PERSISTENT_STORAGE is not set. AppAPI injects it when deploying; for a local run see README, \"Local development\".");
        }

        var key = config["SOCKLESS_MAIL_ENCRYPTION_KEY"];
        if (!string.IsNullOrEmpty(key) && key.Length < MinEncryptionKeyLength)
        {
            throw new InvalidOperationException(
                $"SOCKLESS_MAIL_ENCRYPTION_KEY must be at least {MinEncryptionKeyLength} characters long.");
        }

        return new SocklessMailOptions
        {
            StorageDirectory = Path.GetFullPath(storage, contentRoot),
            EncryptionKey = string.IsNullOrEmpty(key) ? null : key,
            AllowLocalServers = bool.TryParse(config["SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS"], out var allow) && allow,
            BimiAuthServIds = (config["SOCKLESS_MAIL_BIMI_AUTHSERV_IDS"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }
}
