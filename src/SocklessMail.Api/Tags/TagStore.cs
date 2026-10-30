using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Data;

namespace SocklessMail.Api.Tags;

public sealed record TagDto(Guid Id, string Name, string Color, string Keyword, int SortOrder);

/// <summary>A new tag (<see cref="Keyword"/> only to adopt a keyword found on the server) or a change to one.</summary>
public sealed record TagInput(string? Name, string? Color, string? Keyword);

public sealed record TagOrderInput(IReadOnlyList<Guid>? Ids);

/// <summary>
/// A keyword found on the user's messages that no tag has yet, with a suggested name and color;
/// or an Outlook category (<see cref="Outlook"/>, no count) of a Microsoft account.
/// </summary>
public sealed record TagSuggestionDto(string Keyword, string Name, string Color, int Count, bool Outlook = false);

public sealed class TagValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>The user's tags (names, colors, order). Which messages carry them is <see cref="TagIndex"/>'s business.</summary>
public sealed partial class TagStore(MailDbContext db, TimeProvider clock)
{
    public const int MaxTags = 200;
    public const int MaxNameLength = 50;

    /// <summary>Colors of new tags without one, in turn: the first of the dialog's palette.</summary>
    public const string DefaultColor = "#0082c9";

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    public async Task<IReadOnlyList<TagDto>> ListAsync(string userId, CancellationToken ct) =>
        (await db.Tags.AsNoTracking().Where(t => t.OwnerUserId == userId).OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<MailTag?> FindAsync(string userId, Guid id, CancellationToken ct) =>
        await db.Tags.AsNoTracking().SingleOrDefaultAsync(t => t.OwnerUserId == userId && t.Id == id, ct);

    public async Task<TagDto> CreateAsync(string userId, TagInput input, CancellationToken ct)
    {
        var name = ValidName(input.Name);
        var color = ValidColor(input.Color ?? DefaultColor);
        var existing = await db.Tags.Where(t => t.OwnerUserId == userId).ToListAsync(ct);
        if (existing.Count >= MaxTags)
        {
            throw new TagValidationException("name", $"At most {MaxTags} tags.");
        }

        CheckUniqueName(existing, name, null);
        string keyword;
        if (!string.IsNullOrWhiteSpace(input.Keyword))
        {
            keyword = input.Keyword.Trim();
            if (!Keywords.IsTagKeyword(keyword))
            {
                throw new TagValidationException("keyword", "This keyword cannot be a tag.");
            }

            if (existing.Any(t => t.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                throw new TagValidationException("keyword", "A tag already uses this keyword.");
            }
        }
        else
        {
            keyword = Keywords.ForName(name, existing.Select(t => t.Keyword));
        }

        var tag = new MailTag
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = userId,
            Name = name,
            Color = color,
            Keyword = keyword,
            SortOrder = existing.Count == 0 ? 0 : existing.Max(t => t.SortOrder) + 1,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        return ToDto(tag);
    }

    /// <summary>Renames or recolors; the keyword stays, so messages keep the tag.</summary>
    public async Task<TagDto?> UpdateAsync(string userId, Guid id, TagInput input, CancellationToken ct)
    {
        var existing = await db.Tags.Where(t => t.OwnerUserId == userId).ToListAsync(ct);
        if (existing.SingleOrDefault(t => t.Id == id) is not { } tag)
        {
            return null;
        }

        if (input.Name is not null)
        {
            var name = ValidName(input.Name);
            CheckUniqueName(existing, name, id);
            tag.Name = name;
        }

        if (input.Color is not null)
        {
            tag.Color = ValidColor(input.Color);
        }

        await db.SaveChangesAsync(ct);
        return ToDto(tag);
    }

    /// <summary>Puts the named tags first, in the given order; others keep their order after them.</summary>
    public async Task<IReadOnlyList<TagDto>> ReorderAsync(string userId, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var tags = await db.Tags.Where(t => t.OwnerUserId == userId).OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).ToListAsync(ct);
        var order = (ids ?? []).Distinct().ToList();
        var sorted = order.Select(i => tags.FirstOrDefault(t => t.Id == i)).OfType<MailTag>()
            .Concat(tags.Where(t => !order.Contains(t.Id)))
            .ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].SortOrder = i;
        }

        await db.SaveChangesAsync(ct);
        return sorted.Select(ToDto).ToList();
    }

    /// <summary>Deletes the tag; returns it (for removing its keyword from messages), or null when there is none.</summary>
    public async Task<MailTag?> DeleteAsync(string userId, Guid id, CancellationToken ct)
    {
        var tag = await db.Tags.SingleOrDefaultAsync(t => t.OwnerUserId == userId && t.Id == id, ct);
        if (tag is not null)
        {
            db.Tags.Remove(tag);
            await db.SaveChangesAsync(ct);
        }

        return tag;
    }

    /// <summary>
    /// Keywords on the user's cached and indexed messages that may be tags and have none yet, most
    /// used first. Thunderbird's five defaults come with their names and colors.
    /// </summary>
    public async Task<IReadOnlyList<TagSuggestionDto>> SuggestionsAsync(string userId, CancellationToken ct)
    {
        var known = (await db.Tags.Where(t => t.OwnerUserId == userId).Select(t => t.Keyword).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cached = await db.Messages.Where(m => m.OwnerUserId == userId && m.Keywords != null && m.Keywords != "")
            .GroupBy(m => m.Keywords!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var indexed = await db.TaggedMessages.Where(m => m.OwnerUserId == userId && m.Keywords != "")
            .GroupBy(m => m.Keywords!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // The index repeats cached messages; the larger count of the two is the better guess.
        foreach (var source in new[] { cached, indexed })
        {
            var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in source)
            {
                foreach (var keyword in Keywords.Parse(group.Key).Where(k => Keywords.IsTagKeyword(k) && !known.Contains(k)))
                {
                    found[keyword] = found.GetValueOrDefault(keyword) + group.Count;
                }
            }

            foreach (var (keyword, count) in found)
            {
                counts[keyword] = Math.Max(counts.GetValueOrDefault(keyword), count);
            }
        }

        return counts
            .OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(c => Keywords.WellKnown.TryGetValue(c.Key, out var known5)
                ? new TagSuggestionDto(c.Key, known5.Name, known5.Color, c.Value)
                : new TagSuggestionDto(c.Key, c.Key.TrimStart('$'), DefaultColor, c.Value))
            .ToList();
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new TagValidationException("name", "Enter a name.");
        }

        return trimmed.Length <= MaxNameLength ? trimmed : throw new TagValidationException("name", $"At most {MaxNameLength} characters.");
    }

    private static string ValidColor(string color) =>
        ColorPattern().IsMatch(color) ? color.ToLowerInvariant() : throw new TagValidationException("color", "Use a color like #1a2b3c.");

    private static void CheckUniqueName(IEnumerable<MailTag> tags, string name, Guid? self)
    {
        if (tags.Any(t => t.Id != self && t.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new TagValidationException("name", "A tag with this name exists already.");
        }
    }

    private static TagDto ToDto(MailTag t) => new(t.Id, t.Name, t.Color, t.Keyword, t.SortOrder);
}
