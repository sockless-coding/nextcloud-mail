using MailKit;

namespace SocklessMail.Api.Mailbox;

/// <summary>Special-use detection (RFC 6154, with common names as fallback) and display order.</summary>
public static class FolderOrdering
{
    private static readonly string[] SpecialOrder = ["inbox", "drafts", "sent", "archive", "junk", "trash"];

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Drafts"] = "drafts",
        ["Sent"] = "sent", ["Sent Items"] = "sent", ["Sent Messages"] = "sent", ["Sent Mail"] = "sent",
        ["Archive"] = "archive", ["Archives"] = "archive",
        ["Junk"] = "junk", ["Spam"] = "junk", ["Junk E-mail"] = "junk", ["Junk Email"] = "junk",
        ["Trash"] = "trash", ["Deleted Items"] = "trash", ["Deleted Messages"] = "trash", ["Bin"] = "trash",
    };

    public static string? SpecialUse(IMailFolder folder)
    {
        if (folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return "inbox";
        }

        var a = folder.Attributes;
        if (a.HasFlag(FolderAttributes.Drafts)) return "drafts";
        if (a.HasFlag(FolderAttributes.Sent)) return "sent";
        if (a.HasFlag(FolderAttributes.Archive)) return "archive";
        if (a.HasFlag(FolderAttributes.Junk)) return "junk";
        if (a.HasFlag(FolderAttributes.Trash)) return "trash";
        if (a.HasFlag(FolderAttributes.All)) return "all";
        if (a.HasFlag(FolderAttributes.Flagged)) return "flagged";

        // Name fallback only for top-level folders (or children of INBOX, as on Courier/Dovecot setups).
        var parent = folder.ParentFolder?.FullName ?? "";
        return (parent.Length == 0 || parent.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
               && KnownNames.TryGetValue(folder.Name, out var use)
            ? use
            : null;
    }

    /// <summary>Depth-first: special folders first in a fixed order, then the rest by name, children after their parent.</summary>
    public static IReadOnlyList<FolderDto> Order(IReadOnlyList<FolderDto> folders)
    {
        var known = folders.Select(f => f.Id).ToHashSet();
        var children = folders
            .GroupBy(f => f.ParentId is not null && known.Contains(f.ParentId) ? f.ParentId : "")
            .ToDictionary(g => g.Key, g => g.OrderBy(Rank).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList());

        var result = new List<FolderDto>(folders.Count);
        void Visit(string parentId)
        {
            foreach (var folder in children.GetValueOrDefault(parentId) ?? [])
            {
                result.Add(folder.ParentId is not null && !known.Contains(folder.ParentId) ? folder with { ParentId = null } : folder);
                Visit(folder.Id);
            }
        }

        Visit("");
        return result;
    }

    private static int Rank(FolderDto folder) =>
        folder.SpecialUse is { } use && Array.IndexOf(SpecialOrder, use) is var i and >= 0 ? i : SpecialOrder.Length;
}
