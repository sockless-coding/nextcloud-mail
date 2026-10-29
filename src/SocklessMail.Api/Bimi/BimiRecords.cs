namespace SocklessMail.Api.Bimi;

/// <summary>A DMARC policy record (RFC 7489, with the DMARCbis psd and t tags), as far as BIMI cares.</summary>
public sealed record DmarcRecord(string? Policy, string? SubdomainPolicy, int? Percent, bool Testing, bool PublicSuffix)
{
    /// <summary>
    /// Whether mail failing DMARC is quarantined or rejected, for a domain the record was found at
    /// (<paramref name="atAuthorDomain"/>) or inherited from a parent. BIMI requires this: without
    /// enforcement anyone could send mail that shows the brand's logo.
    /// </summary>
    public bool Enforces(bool atAuthorDomain)
    {
        var policy = atAuthorDomain ? Policy : SubdomainPolicy ?? Policy;
        return policy is "quarantine" or "reject" && Percent is null or 100 && !Testing;
    }
}

/// <summary>A BIMI assertion record (<c>v=BIMI1; l=…; a=…</c>). An empty <see cref="Location"/> declines to show a logo.</summary>
public sealed record BimiRecord(string? Location);

/// <summary>Parses the DNS TXT records behind BIMI: DMARC policies and BIMI assertions.</summary>
public static class BimiRecords
{
    /// <summary>DMARCbis caps the tree walk at eight queries.</summary>
    private const int MaxWalk = 8;

    /// <summary>
    /// The domains to look up a DMARC policy at, starting with the author domain: the DMARCbis DNS
    /// tree walk (a domain of more than eight labels jumps to its last seven, then one label is
    /// removed per step, down to the top-level domain).
    /// </summary>
    public static IReadOnlyList<string> TreeWalk(string domain)
    {
        var labels = domain.Split('.');
        var result = new List<string> { domain };
        var count = labels.Length > MaxWalk ? MaxWalk - 1 : labels.Length - 1;
        for (; count >= 1 && result.Count < MaxWalk; count--)
        {
            result.Add(string.Join('.', labels[^count..]));
        }

        return result;
    }

    /// <summary>The DMARC record among the TXT strings at one name; null when there is none or more than one.</summary>
    public static DmarcRecord? ParseDmarc(IEnumerable<string> txt)
    {
        var records = txt.Select(Tags).Where(t => t is not null && t.GetValueOrDefault("v") == "DMARC1").ToList();
        if (records.Count != 1)
        {
            return null;
        }

        var tags = records[0]!;
        return new DmarcRecord(
            tags.GetValueOrDefault("p")?.ToLowerInvariant(),
            tags.GetValueOrDefault("sp")?.ToLowerInvariant(),
            int.TryParse(tags.GetValueOrDefault("pct"), out var pct) ? pct : null,
            tags.GetValueOrDefault("t")?.ToLowerInvariant() == "y",
            tags.GetValueOrDefault("psd")?.ToLowerInvariant() == "y");
    }

    /// <summary>The BIMI record among the TXT strings at one name; null when there is none or more than one.</summary>
    public static BimiRecord? ParseBimi(IEnumerable<string> txt)
    {
        var records = txt.Select(Tags).Where(t => t is not null && t.GetValueOrDefault("v") == "BIMI1").ToList();
        if (records.Count != 1)
        {
            return null;
        }

        // Early drafts allowed a comma-separated list of locations; the first one counts.
        var location = records[0]!.GetValueOrDefault("l")?.Split(',')[0].Trim();
        return new BimiRecord(string.IsNullOrEmpty(location) ? null : location);
    }

    /// <summary>The selector a BIMI-Selector header names (<c>v=BIMI1; s=brand;</c>); null when absent or malformed.</summary>
    public static string? ParseSelectorHeader(string? value)
    {
        if (value is null || Tags(value) is not { } tags || tags.GetValueOrDefault("v") != "BIMI1")
        {
            return null;
        }

        var selector = tags.GetValueOrDefault("s")?.ToLowerInvariant();
        return selector is not null && BimiNames.IsSelector(selector) ? selector : null;
    }

    /// <summary>"tag=value; tag=value" with the first tag being v; null when it is not such a list.</summary>
    private static Dictionary<string, string>? Tags(string record)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in record.Split(';'))
        {
            var equals = part.IndexOf('=');
            if (equals < 0)
            {
                if (part.Trim().Length == 0)
                {
                    continue;
                }

                return null;
            }

            var name = part[..equals].Trim();
            if (tags.Count == 0 && !name.Equals("v", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            tags.TryAdd(name, part[(equals + 1)..].Trim());
        }

        return tags.Count > 0 ? tags : null;
    }
}
