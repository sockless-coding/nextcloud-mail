namespace SocklessMail.Api.AppApi;

/// <summary>
/// The environment AppAPI injects into every ExApp container (see README, "Runtime contract").
/// Read through IConfiguration so tests and local runs can supply the same keys.
/// </summary>
public sealed class AppApiOptions
{
    public const string ExpectedAppId = "sockless_mail";

    /// <summary>Fixed by HaRP's FRP client configuration; the tunnel forwards to exactly this path.</summary>
    public const string HarpSocketPath = "/tmp/exapp.sock";

    public required string AppId { get; init; }
    public required string AppVersion { get; init; }
    public required string AppSecret { get; init; }
    public required Uri NextcloudUrl { get; init; }
    public string AppHost { get; init; } = "0.0.0.0";
    public int AppPort { get; init; }
    public string? PersistentStorage { get; init; }

    /// <summary>
    /// True when deployed through a HaRP Docker daemon. The value of HP_SHARED_KEY is a daemon
    /// credential that this app never needs, so only its presence is kept.
    /// </summary>
    public bool IsHarp { get; init; }

    public static AppApiOptions FromConfiguration(IConfiguration config)
    {
        string Required(string key) =>
            config[key] is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException(
                    $"Environment variable {key} is not set. AppAPI injects it when deploying; for a local run see README, \"Local development\".");

        var appId = Required("APP_ID");
        if (appId != ExpectedAppId)
        {
            throw new InvalidOperationException($"APP_ID is '{appId}', expected '{ExpectedAppId}'.");
        }

        var portText = config["APP_PORT"];
        var port = 0;
        if (!string.IsNullOrEmpty(portText) && !int.TryParse(portText, out port))
        {
            throw new InvalidOperationException($"APP_PORT '{portText}' is not a number.");
        }

        return new AppApiOptions
        {
            AppId = appId,
            AppVersion = Required("APP_VERSION"),
            AppSecret = Required("APP_SECRET"),
            NextcloudUrl = new Uri(Required("NEXTCLOUD_URL").TrimEnd('/') + "/"),
            AppHost = config["APP_HOST"] is { Length: > 0 } host ? host : "0.0.0.0",
            AppPort = port,
            PersistentStorage = config["APP_PERSISTENT_STORAGE"] is { Length: > 0 } storage ? storage : null,
            IsHarp = !string.IsNullOrEmpty(config["HP_SHARED_KEY"]),
        };
    }
}
