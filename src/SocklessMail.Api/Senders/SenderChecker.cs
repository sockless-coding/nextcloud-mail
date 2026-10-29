using System.Text.RegularExpressions;

namespace SocklessMail.Api.Senders;

/// <summary>
/// One reason a sender looks suspicious. <see cref="Code"/> says what was found; the UI words it.
/// <list type="bullet">
/// <item>lookalike: <see cref="Domain"/> imitates <see cref="LooksLike"/>; <see cref="Detail"/> says how:
/// alphabet (letters from another alphabet), characters (rn for m, 1 for l, ...), typo (one letter off),
/// ending (the brand's name under another ending).</item>
/// <item>brandName: <see cref="Domain"/> contains the name of <see cref="LooksLike"/> without belonging to it.</item>
/// <item>mixedScripts: <see cref="Domain"/> mixes letters of different alphabets.</item>
/// <item>nameShowsDomain: the sender's name shows <see cref="LooksLike"/>, the address is from <see cref="Domain"/>.</item>
/// <item>dmarcFail: the receiving mail server could not verify the message comes from <see cref="Domain"/>.</item>
/// <item>replyTo: replies go to <see cref="Domain"/>, another domain than the sender's.</item>
/// </list>
/// <see cref="Field"/> is "from", or "replyTo" for a look-alike Reply-To address.
/// </summary>
public sealed record SenderReason(string Code, string Domain, string? LooksLike = null, string? Detail = null, string Field = "from");

/// <summary>
/// Why a sender looks suspicious. <see cref="Level"/> is "danger" for a domain built to deceive (look-alike
/// characters) or a failed DMARC check, otherwise "warning". <see cref="Domain"/> is how the sender's
/// domain reads; <see cref="AsciiDomain"/> how it is spelled for DNS, when that differs;
/// <see cref="Registered"/> what "Trust this domain" would trust (with its subdomains).
/// </summary>
public sealed record SenderWarning(string Domain, string? AsciiDomain, string Registered, string Level, IReadOnlyList<SenderReason> Reasons);

/// <summary>The checks themselves, on what the message list and the reader know about a sender.</summary>
public static partial class SenderChecker
{
    /// <summary>Endings that make a word in a sender's name read as a domain; plus every two-letter country ending.</summary>
    private static readonly HashSet<string> NameEndings =
    [
        "com", "net", "org", "info", "biz", "io", "app", "dev", "xyz", "online", "site", "top", "shop", "store", "email", "support", "help", "live",
    ];

    /// <summary>Checks a sender as the message list shows it; null when nothing is suspicious (or the domain is trusted).</summary>
    public static SenderWarning? Check(SenderIndex index, string? name, string address) =>
        Check(index, name, address, null, false);

    /// <summary>
    /// Checks the open message: also its Reply-To address and whether the receiving server failed its DMARC check
    /// (<see cref="Bimi.BimiSenders.FailedDmarc"/>). A Reply-To that merely differs is no reason by itself; it is
    /// mentioned along with others.
    /// </summary>
    public static SenderWarning? Check(SenderIndex index, string? name, string address, string? replyTo, bool dmarcFailed)
    {
        if (SenderDomain.OfAddress(address) is not { } sender || index.IsTrusted(sender))
        {
            return null;
        }

        var reasons = new List<SenderReason>();
        if (Lookalike(index, sender) is { } lookalike)
        {
            reasons.Add(lookalike);
        }
        else if (sender.Name.Split('.').Any(label => Skeletons.MixesScripts(SenderDomain.ToUnicode(label))))
        {
            reasons.Add(new SenderReason("mixedScripts", sender.Unicode));
        }

        if (NameDomain(name, sender) is { } shown)
        {
            reasons.Add(new SenderReason("nameShowsDomain", sender.Unicode, shown));
        }

        if (dmarcFailed)
        {
            reasons.Add(new SenderReason("dmarcFail", sender.Unicode));
        }

        var replyToReason = false;
        if (replyTo is not null && SenderDomain.OfAddress(replyTo) is { } reply && reply.Registered != sender.Registered && !index.IsTrusted(reply))
        {
            if (Lookalike(index, reply) is { } replyLookalike)
            {
                reasons.Add(replyLookalike with { Field = "replyTo" });
            }
            else if (reasons.Count > 0)
            {
                reasons.Add(new SenderReason("replyTo", reply.Unicode));
                replyToReason = true;
            }
        }

        if (reasons.Count == 0 || replyToReason && reasons.Count == 1)
        {
            return null;
        }

        var danger = reasons.Any(r => r.Code == "dmarcFail" || r is { Code: "lookalike", Detail: "alphabet" or "characters" });
        var unicode = sender.Unicode;
        return new SenderWarning(unicode, unicode == sender.Name ? null : sender.Name, sender.Registered, danger ? "danger" : "warning", reasons);
    }

    /// <summary>The known domain <paramref name="domain"/> imitates, best match first: look-alike characters, another ending, a typo, the name within.</summary>
    internal static SenderReason? Lookalike(SenderIndex index, SenderDomain domain)
    {
        if (index.IsKnown(domain))
        {
            return null;
        }

        var skeleton = Skeletons.Of(domain.UnicodeLabel);
        var tokens = domain.Label.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var subdomain = domain.Subdomain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        (int Rank, SenderReason Reason)? best = null;

        void Consider(int rank, KnownDomain known, string? detail, string code = "lookalike")
        {
            if (best is null || rank < best.Value.Rank)
            {
                best = (rank, new SenderReason(code, domain.Unicode, known.Display(known.Suffix is null && code == "brandName" ? "com" : domain.Suffix), detail));
            }
        }

        var characters = domain.IsInternational ? "alphabet" : "characters";
        foreach (var known in index.Domains)
        {
            var sameEnding = known.Suffix is null || known.Suffix == domain.Suffix;
            if (skeleton == known.LabelSkeleton)
            {
                if (sameEnding)
                {
                    // Not known, so the label is spelled differently: it only looks the same.
                    Consider(0, known, characters);
                }
                else if (known.Source == KnownSource.Brand && known.Label.Length >= 5)
                {
                    Consider(domain.Label == known.Label ? 1 : 0, known, domain.Label == known.Label ? "ending" : characters);
                }
            }
            else if (sameEnding && known.Label.Length >= 5 && OneEditApart(skeleton, known.LabelSkeleton))
            {
                Consider(2, known, "typo");
            }
            else if (known is { Source: KnownSource.Brand, CommonWord: false, Label.Length: >= 4 }
                     && (tokens.Length > 1 && tokens.Contains(known.Label) || subdomain.Contains(known.Label)))
            {
                Consider(3, known, null, "brandName");
            }
        }

        return best?.Reason;
    }

    /// <summary>A domain the sender's name shows that is not the address's own, as in "service@paypal.com" &lt;x@example.net&gt;.</summary>
    internal static string? NameDomain(string? name, SenderDomain sender)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (Match match in DomainLike().Matches(name))
        {
            var text = match.Value.Trim('.', '-');
            var isAddress = text.Contains('@');
            if (SenderDomain.Parse(isAddress ? text[(text.LastIndexOf('@') + 1)..] : text) is not { } shown
                || !isAddress && !(shown.Suffix.Length == 2 || NameEndings.Contains(shown.Suffix.Split('.')[^1])))
            {
                continue;
            }

            if (shown.Registered != sender.Registered)
            {
                return shown.Unicode;
            }
        }

        return null;
    }

    /// <summary>One insertion, deletion, substitution or swap of neighbours apart.</summary>
    internal static bool OneEditApart(string a, string b)
    {
        if (a == b || Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
        {
            start++;
        }

        var endA = a.Length;
        var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }

        var restA = endA - start;
        var restB = endB - start;
        return restA <= 1 && restB <= 1
               || restA == 2 && restB == 2 && a[start] == b[start + 1] && a[start + 1] == b[start];
    }

    [GeneratedRegex(@"[\p{L}\p{N}._%+-]*@?[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+")]
    private static partial Regex DomainLike();
}
