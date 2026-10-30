using SocklessMail.Api.Accounts;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.OAuth;

/// <summary>
/// A mail provider that signs in with OAuth2 (authorization code with PKCE) and accepts the
/// access token over IMAP and SMTP with XOAUTH2. Endpoint URLs may contain "{tenant}", replaced
/// by the tenant the admin configured (Microsoft); <see cref="DefaultTenant"/> otherwise.
/// <para>
/// <see cref="GraphScope"/> (Microsoft): Microsoft Graph permissions the user consents to at
/// sign-in as well, for tags as Outlook categories. A token is for one resource only, so the
/// sign-in's tokens stay IMAP/SMTP ones (<see cref="Scope"/>) and Graph tokens are redeemed from
/// the same refresh token separately (see <see cref="MailLogins.GraphTokenAsync"/>).
/// </para>
/// </summary>
public sealed record OAuthProvider(
    string Id,
    string Name,
    string AuthorizeUrl,
    string TokenUrl,
    string Scope,
    IReadOnlyDictionary<string, string> AuthorizeParameters,
    ServerSettings Imap,
    ServerSettings Smtp,
    string? DefaultTenant = null,
    string? GraphScope = null)
{
    public bool UsesTenant => DefaultTenant is not null;

    /// <summary>What the user is asked to consent to: the mail scopes and any Graph ones.</summary>
    public string ConsentScope => GraphScope is null ? Scope : $"{Scope} {GraphScope}";

    public Uri AuthorizeEndpoint(string? tenant) => new(WithTenant(AuthorizeUrl, tenant));

    public Uri TokenEndpoint(string? tenant) => new(WithTenant(TokenUrl, tenant));

    public MailEndpoint ImapEndpoint => Imap.ToEndpoint();

    public MailEndpoint SmtpEndpoint => Smtp.ToEndpoint();

    private string WithTenant(string url, string? tenant) =>
        url.Replace("{tenant}", Uri.EscapeDataString(string.IsNullOrWhiteSpace(tenant) ? DefaultTenant ?? "" : tenant.Trim()));
}

/// <summary>The providers the app supports. Tests replace it with one pointing at a local server.</summary>
public sealed class OAuthProviderCatalog(IReadOnlyList<OAuthProvider> providers)
{
    public static OAuthProviderCatalog Default { get; } = new(
    [
        new OAuthProvider(
            "google",
            "Google",
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            "https://mail.google.com/ openid email profile",
            // offline + consent: Google only returns a refresh token on a consented offline request.
            new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" },
            new ServerSettings("imap.gmail.com", 993, MailSecurity.SslTls),
            new ServerSettings("smtp.gmail.com", 465, MailSecurity.SslTls)),
        new OAuthProvider(
            "microsoft",
            "Microsoft",
            "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
            "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
            "openid email profile offline_access https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send",
            new Dictionary<string, string> { ["prompt"] = "select_account" },
            new ServerSettings("outlook.office365.com", 993, MailSecurity.SslTls),
            new ServerSettings("smtp.office365.com", 587, MailSecurity.StartTls),
            DefaultTenant: "common",
            GraphScope: "https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/MailboxSettings.ReadWrite"),
    ]);

    public IReadOnlyList<OAuthProvider> All { get; } = providers;

    public OAuthProvider? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}
