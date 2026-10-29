using System.Xml;

namespace SocklessMail.Api.Bimi;

/// <summary>
/// Checks that a BIMI logo is a self-contained static SVG, as the SVG Tiny Portable/Secure profile
/// requires: no scripts, event handlers, foreign content, animation or references outside the
/// document. The browser only ever shows it through &lt;img&gt;, where none of that runs anyway, and
/// it is served with a sandboxing CSP for the case someone opens it directly; this is the third
/// line, and keeps logos from loading anything when shown.
/// </summary>
public static class SvgLogo
{
    /// <summary>The SVG Tiny PS limit.</summary>
    public const int MaxBytes = 32 * 1024;

    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    private static readonly HashSet<string> ForbiddenElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "iframe", "embed", "object", "image", "video", "audio", "canvas",
        "animate", "animateColor", "animateMotion", "animateTransform", "set", "handler", "listener",
    };

    public static bool IsSafe(byte[] svg)
    {
        if (svg.Length is 0 or > MaxBytes)
        {
            return false;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            MaxCharactersInDocument = MaxBytes * 4,
        };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(svg), settings);
            var root = true;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (root && (reader.LocalName != "svg" || reader.NamespaceURI != SvgNamespace))
                        {
                            return false;
                        }

                        root = false;
                        if (ForbiddenElements.Contains(reader.LocalName) || !AttributesSafe(reader))
                        {
                            return false;
                        }

                        break;
                    // Style sheets: only url(#local) references, like attribute values.
                    case XmlNodeType.Text or XmlNodeType.CDATA when !CssSafe(reader.Value):
                        return false;
                    case XmlNodeType.ProcessingInstruction when reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase):
                        return false;
                }
            }

            return !root;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool AttributesSafe(XmlReader reader)
    {
        for (var more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
        {
            var name = reader.LocalName;
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (name == "href" && !reader.Value.TrimStart().StartsWith('#'))
            {
                return false;
            }

            if (!CssSafe(reader.Value))
            {
                return false;
            }
        }

        reader.MoveToElement();
        return true;
    }

    /// <summary>No url() other than url(#id), and no @import.</summary>
    private static bool CssSafe(string value)
    {
        if (value.Contains("@import", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var at = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase); at >= 0; at = value.IndexOf("url(", at + 4, StringComparison.OrdinalIgnoreCase))
        {
            if (!value[(at + 4)..].TrimStart().TrimStart('"', '\'').StartsWith('#'))
            {
                return false;
            }
        }

        return true;
    }
}
