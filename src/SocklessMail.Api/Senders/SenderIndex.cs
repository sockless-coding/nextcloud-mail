namespace SocklessMail.Api.Senders;

/// <summary>Where a domain the user knows comes from.</summary>
public enum KnownSource
{
    /// <summary>The bundled list of brands phishing imitates most (Senders/Data/brands.txt).</summary>
    Brand,

    /// <summary>The user's own mail accounts.</summary>
    Account,

    /// <summary>The user's Nextcloud address books.</summary>
    Contact,

    /// <summary>People the user wrote to or answered.</summary>
    Correspondent,

    /// <summary>Marked "Trust this domain".</summary>
    Trusted,
}

/// <summary>
/// A registered domain someone may imitate. <see cref="Suffix"/> is null for a brand that owns its
/// name under many endings ("amazon.*"); <see cref="CommonWord"/> brands are only matched by
/// look-alikes, not by domains that contain the name.
/// </summary>
public sealed class KnownDomain
{
    public KnownDomain(string label, string? suffix, KnownSource source, bool commonWord = false)
    {
        Label = label;
        Suffix = suffix;
        Source = source;
        CommonWord = commonWord;
        LabelSkeleton = Skeletons.Of(SenderDomain.ToUnicode(label));
    }

    public string Label { get; }
    public string? Suffix { get; }
    public KnownSource Source { get; }
    public bool CommonWord { get; }
    public string LabelSkeleton { get; }

    /// <summary>How a warning names it, ending in <paramref name="suffix"/> when the brand owns every ending.</summary>
    public string Display(string suffix) => SenderDomain.ToUnicode($"{Label}.{Suffix ?? suffix}");

    public static KnownDomain Of(SenderDomain domain, KnownSource source) => new(domain.Label, domain.Suffix, source);
}

/// <summary>
/// The domains one user knows: the brand list, their own accounts, contacts and correspondents, and
/// the domains they trust. A sender from one of these is never a look-alike; a sender resembling one
/// may be.
/// </summary>
public sealed class SenderIndex
{
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _anySuffix = new(StringComparer.Ordinal);
    private readonly HashSet<string> _trusted = new(StringComparer.Ordinal);
    private readonly List<KnownDomain> _domains = [];

    public SenderIndex(IEnumerable<KnownDomain> domains, IEnumerable<string>? trusted = null)
    {
        foreach (var domain in domains)
        {
            if (domain.Suffix is null ? _anySuffix.Add(domain.Label) : _registered.Add($"{domain.Label}.{domain.Suffix}"))
            {
                _domains.Add(domain);
            }
        }

        foreach (var name in trusted ?? [])
        {
            if (SenderDomain.Parse(name) is { } domain)
            {
                _trusted.Add(domain.Registered);
                if (_registered.Add(domain.Registered))
                {
                    _domains.Add(new KnownDomain(domain.Label, domain.Suffix, KnownSource.Trusted));
                }
            }
        }
    }

    public IReadOnlyList<KnownDomain> Domains => _domains;

    public bool IsKnown(SenderDomain domain) => _registered.Contains(domain.Registered) || _anySuffix.Contains(domain.Label);

    public bool IsTrusted(SenderDomain domain) => _trusted.Contains(domain.Registered);

    /// <summary>The bundled brand list, read once.</summary>
    public static IReadOnlyList<KnownDomain> Brands { get; } = LoadBrands();

    private static List<KnownDomain> LoadBrands()
    {
        using var stream = typeof(SenderIndex).Assembly.GetManifestResourceStream("SocklessMail.Api.Senders.Data.brands.txt")
                           ?? throw new InvalidOperationException("The embedded resource brands.txt is missing.");
        using var reader = new StreamReader(stream);
        var brands = new List<KnownDomain>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            foreach (var entry in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var commonWord = entry.StartsWith('~');
                var name = entry.TrimStart('~');
                if (name.EndsWith(".*", StringComparison.Ordinal))
                {
                    brands.Add(new KnownDomain(name[..^2], null, KnownSource.Brand, commonWord));
                }
                else if (SenderDomain.Parse(name) is { } domain && domain.Registered == name)
                {
                    brands.Add(new KnownDomain(domain.Label, domain.Suffix, KnownSource.Brand, commonWord));
                }
                else
                {
                    throw new InvalidOperationException($"brands.txt: \"{entry}\" is not a registered domain.");
                }
            }
        }

        return brands;
    }
}
