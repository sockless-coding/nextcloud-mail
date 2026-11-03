using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SocklessMail.Api.Localization;

/// <summary>
/// Translations of the server's own messages (validation and error texts), in the user's
/// Nextcloud language. They come from the same <c>l10n/&lt;lang&gt;.json</c> files as the
/// frontend's, which the build copies to <c>wwwroot/js/l10n/</c>.
/// <para>
/// A message is written in English with named placeholders, <c>L.T("Enter a port between {min} and
/// {max}.", ("min", 1), ("max", 65535))</c>; <c>tools/l10n.mjs extract</c> finds these calls (only
/// literal strings) for the translation template. The language is the request's
/// <c>X-Sockless-Language</c> header, set per request by <see cref="Use"/>; outside a request
/// (background work) messages stay English.
/// </para>
/// </summary>
public static partial class L
{
    public const string LanguageHeader = "X-Sockless-Language";

    private static readonly AsyncLocal<FrozenDictionary<string, string>?> Current = new();
    private static FrozenDictionary<string, FrozenDictionary<string, string>> languages =
        FrozenDictionary<string, FrozenDictionary<string, string>>.Empty;

    /// <summary>The languages that were loaded, e.g. "sv", "de_DE".</summary>
    public static IEnumerable<string> Languages => languages.Keys;

    /// <summary>
    /// Adds every <c>&lt;lang&gt;.json</c> of a directory to the known languages (a file replaces
    /// an earlier one of the same language); a missing directory adds none.
    /// </summary>
    public static void Load(string directory)
    {
        var loaded = new Dictionary<string, FrozenDictionary<string, string>>(languages, StringComparer.Ordinal);
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var language = Path.GetFileNameWithoutExtension(file);
                if (LanguagePattern().IsMatch(language))
                {
                    loaded[language] = Parse(File.ReadAllText(file));
                }
            }
        }
        languages = loaded.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The translations of a <c>l10n/&lt;lang&gt;.json</c> file; plural entries are left out.</summary>
    public static FrozenDictionary<string, string> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("translations", out var translations))
        {
            foreach (var entry in translations.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } text)
                {
                    result[entry.Name] = text;
                }
            }
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Sets the language for the rest of the current async flow: a Nextcloud language code such
    /// as "de_DE", falling back to "de", else English. Call it from an async method so the
    /// setting ends with it.
    /// </summary>
    public static void Use(string? language)
    {
        Current.Value = null;
        if (string.IsNullOrEmpty(language) || language.Length > 16 || !LanguagePattern().IsMatch(language))
        {
            return;
        }
        if (languages.TryGetValue(language, out var exact))
        {
            Current.Value = exact;
        }
        else if (languages.TryGetValue(language.Split('_', '-')[0], out var basic))
        {
            Current.Value = basic;
        }
    }

    /// <summary>
    /// The language of a request: the frontend's <c>X-Sockless-Language</c> header, or for pages the
    /// browser opens itself (the OAuth callback) the first <c>Accept-Language</c> tag as a Nextcloud
    /// code ("sv-SE" → "sv_SE").
    /// </summary>
    public static string? RequestLanguage(HttpRequest request)
    {
        if (request.Headers[LanguageHeader].ToString() is { Length: > 0 } header)
        {
            return header;
        }
        var first = request.Headers.AcceptLanguage.ToString().Split(',')[0].Split(';')[0].Trim();
        return first.Length > 0 ? first.Replace('-', '_') : null;
    }

    /// <summary>The message in the current language, with its <c>{name}</c> placeholders filled in.</summary>
    public static string T(string text, params (string Name, object? Value)[] values)
    {
        var translated = Current.Value is { } table && table.TryGetValue(text, out var found) ? found : text;
        return values.Length == 0 ? translated : Fill(translated, values);
    }

    private static string Fill(string text, (string Name, object? Value)[] values)
    {
        var result = new StringBuilder(text);
        foreach (var (name, value) in values)
        {
            var shown = value switch
            {
                null => "",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };
            result.Replace("{" + name + "}", shown);
        }
        return result.ToString();
    }

    [GeneratedRegex("^[a-z]{2,3}([_-][A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguagePattern();
}
