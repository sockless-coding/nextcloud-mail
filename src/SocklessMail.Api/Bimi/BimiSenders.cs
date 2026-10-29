using MailKit;
using MimeKit;
using MimeKit.Cryptography;

namespace SocklessMail.Api.Bimi;

/// <summary>The sender domain and selector whose BIMI logo a message may show.</summary>
public sealed record BimiDto(string Domain, string? Selector);

/// <summary>
/// Decides from a message's headers whether it may show its sender's BIMI logo: only when the
/// receiving mail server says it passed DMARC for the From domain, or, when it states no DMARC
/// result, that it verified a DKIM signature or SPF aligned with that domain. That verdict is read from the
/// topmost Authentication-Results header, which the server closest to the mailbox adds, so a
/// sender's own forged header further down is never read. A server that adds none would leave a
/// forged header on top; <see cref="SocklessMailOptions.BimiAuthServIds"/> closes that gap by
/// naming the servers whose headers count. The logo itself is looked up later (<see cref="BimiResolver"/>),
/// which also checks that the domain enforces DMARC.
/// </summary>
public sealed class BimiSenders(SocklessMailOptions options)
{
    /// <summary>The headers a message list fetches along with the envelope.</summary>
    public static readonly string[] Headers = ["Authentication-Results", "BIMI-Selector"];

    /// <returns>The sender, or null when the message must not show a logo.</returns>
    public BimiDto? Find(IMessageSummary summary) => Find(summary.Envelope?.From, summary.Headers);

    public BimiDto? Find(InternetAddressList? from, HeaderList? headers)
    {
        // DMARC is undefined for several authors, and the logo would claim only one of them.
        if (from?.Mailboxes.ToList() is not [var author] || BimiNames.DomainOf(author.Address) is not { } domain || headers is null)
        {
            return null;
        }

        if (TrustedResults(headers) is not { } results || !PassedDmarc(results, domain))
        {
            return null;
        }

        var selector = BimiRecords.ParseSelectorHeader(headers[Headers[1]]);
        return new BimiDto(domain, selector is null or BimiNames.DefaultSelector ? null : selector);
    }

    /// <summary>
    /// Whether the same trusted header says the message failed DMARC for its From domain: the domain
    /// publishes a policy and the message is not one of its own. For the suspicious-sender warning.
    /// </summary>
    public bool FailedDmarc(InternetAddressList? from, HeaderList? headers) =>
        from?.Mailboxes.FirstOrDefault() is { } author && BimiNames.DomainOf(author.Address) is { } domain
        && headers is not null && TrustedResults(headers) is { } results
        && results.Results.Any(r => Is(r.Method, "dmarc") && Is(r.Result, "fail")
            && r.Properties.Where(p => Is(p.PropertyType, "header") && Is(p.Property, "from")).All(p => BimiNames.NormalizeDomain(p.Value) == domain));

    private AuthenticationResults? TrustedResults(HeaderList headers)
    {
        foreach (var header in headers.Where(h => h.Id == HeaderId.AuthenticationResults))
        {
            var parsed = AuthenticationResults.TryParse(header.RawValue, out var results);
            if (options.BimiAuthServIds.Count == 0)
            {
                // Only the topmost header: skipping one that does not parse would reach the sender's.
                return parsed ? results : null;
            }

            if (parsed && results!.AuthenticationServiceIdentifier is { } id && options.BimiAuthServIds.Contains(id))
            {
                return results;
            }
        }

        return null;
    }

    /// <summary>
    /// The server's DMARC verdict when it gives one. Some servers only verify DKIM and SPF (amavis
    /// writes no dmarc= result); then a pass that is aligned with the From domain is what DMARC
    /// itself would count. Whether the domain enforces DMARC is checked with its logo.
    /// </summary>
    private static bool PassedDmarc(AuthenticationResults results, string domain)
    {
        var dmarc = results.Results.Where(r => Is(r.Method, "dmarc")).ToList();
        if (dmarc.Count > 0)
        {
            // header.from names the domain DMARC was evaluated for; it must be the one we show.
            return dmarc.Any(r => Is(r.Result, "pass")
                && r.Properties.Where(p => Is(p.PropertyType, "header") && Is(p.Property, "from")).All(p => BimiNames.NormalizeDomain(p.Value) == domain));
        }

        return results.Results.Any(r => Is(r.Result, "pass") && (
            Is(r.Method, "dkim") && Property(r, "header", "d") is { } signer && Aligned(BimiNames.NormalizeDomain(signer), domain)
            || Is(r.Method, "spf") && Property(r, "smtp", "mailfrom") is { } mailFrom
                && Aligned(mailFrom.Contains('@') ? BimiNames.DomainOf(mailFrom) : BimiNames.NormalizeDomain(mailFrom), domain)));
    }

    /// <summary>
    /// The same domain, or a parent of the From domain (relaxed alignment, as far as it can be told
    /// without the public suffix list). Never a subdomain of it: under a public suffix such as a
    /// dynamic DNS provider, whoever owns a subdomain must not pass for its parent.
    /// </summary>
    private static bool Aligned(string? authenticated, string domain) =>
        authenticated is not null && (authenticated == domain || domain.EndsWith("." + authenticated, StringComparison.Ordinal));

    private static string? Property(AuthenticationMethodResult result, string type, string name) =>
        result.Properties.FirstOrDefault(p => Is(p.PropertyType, type) && Is(p.Property, name))?.Value;

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
