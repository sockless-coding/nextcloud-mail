using System.Text;

namespace SocklessMail.Api.Calendar;

/// <summary>
/// One iCalendar content line: NAME;PARAM=value;…:value. Values stay as written (escaped);
/// parameters keep their quotes. Only what the app changes is rewritten, so properties it does
/// not know (X-…, time zones, alarms) pass through unchanged.
/// </summary>
public sealed class ContentLine
{
    public required string Name { get; set; }

    /// <summary>Parameter names upper-cased; values as written, quotes included.</summary>
    public List<KeyValuePair<string, string>> Parameters { get; init; } = [];

    public required string Value { get; set; }

    public static ContentLine? Parse(string line)
    {
        var colon = -1;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }

        if (colon <= 0)
        {
            return null;
        }

        var head = SplitOutsideQuotes(line[..colon], ';');
        var parameters = new List<KeyValuePair<string, string>>();
        foreach (var parameter in head.Skip(1))
        {
            var equals = parameter.IndexOf('=');
            if (equals > 0)
            {
                parameters.Add(new(parameter[..equals].Trim().ToUpperInvariant(), parameter[(equals + 1)..]));
            }
        }

        return new ContentLine { Name = head[0].Trim().ToUpperInvariant(), Parameters = parameters, Value = line[(colon + 1)..] };
    }

    /// <summary>The parameter's value without quotes, or null.</summary>
    public string? Parameter(string name)
    {
        var raw = Parameters.FirstOrDefault(p => p.Key == name).Value;
        return raw is null ? null : raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
    }

    public void SetParameter(string name, string value)
    {
        RemoveParameter(name);
        var needsQuotes = value.IndexOfAny([':', ';', ',']) >= 0;
        Parameters.Add(new(name, needsQuotes ? $"\"{value.Replace("\"", "")}\"" : value));
    }

    public void RemoveParameter(string name) => Parameters.RemoveAll(p => p.Key == name);

    /// <summary>The value with text escapes resolved (for SUMMARY, LOCATION, …).</summary>
    public string Text => Unescape(Value);

    /// <summary>For ORGANIZER/ATTENDEE: the address without "mailto:", or null when it is no email address.</summary>
    public string? Address
    {
        get
        {
            var value = Value.Trim();
            if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                value = value[7..];
            }

            return value.Contains('@') && !value.Contains(':') ? value : null;
        }
    }

    public ContentLine Clone() => new() { Name = Name, Parameters = [.. Parameters], Value = Value };

    public override string ToString()
    {
        var builder = new StringBuilder(Name);
        foreach (var (key, value) in Parameters)
        {
            builder.Append(';').Append(key).Append('=').Append(value);
        }

        return builder.Append(':').Append(Value).ToString();
    }

    public static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    public static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                result.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                result.Append(value[i]);
            }
        }

        return result.ToString();
    }

    private static List<string> SplitOutsideQuotes(string text, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }

            if (c == separator && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString());
        return parts;
    }
}

/// <summary>A component (VCALENDAR, VEVENT, VTIMEZONE, VALARM, …) with its properties and children, in order.</summary>
public sealed class ICalComponent
{
    public required string Name { get; init; }

    public List<ContentLine> Properties { get; init; } = [];

    public List<ICalComponent> Children { get; init; } = [];

    /// <summary>The first VCALENDAR in the text.</summary>
    /// <exception cref="FormatException">There is none, or BEGIN/END do not match.</exception>
    public static ICalComponent Parse(string text)
    {
        var unfolded = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n ", "").Replace("\n\t", "");
        var stack = new Stack<ICalComponent>();
        foreach (var raw in unfolded.Split('\n'))
        {
            if (raw.Length == 0 || ContentLine.Parse(raw) is not { } line)
            {
                continue;
            }

            if (line.Name == "BEGIN")
            {
                var component = new ICalComponent { Name = line.Value.Trim().ToUpperInvariant() };
                if (stack.Count > 0)
                {
                    stack.Peek().Children.Add(component);
                }
                else if (component.Name != "VCALENDAR")
                {
                    continue;
                }

                stack.Push(component);
            }
            else if (line.Name == "END" && stack.Count > 0)
            {
                var done = stack.Pop();
                if (!string.Equals(done.Name, line.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException("Unbalanced BEGIN/END in the calendar data.");
                }

                if (stack.Count == 0)
                {
                    return done;
                }
            }
            else if (stack.Count > 0)
            {
                stack.Peek().Properties.Add(line);
            }
        }

        throw new FormatException("No complete VCALENDAR in the calendar data.");
    }

    public ContentLine? Property(string name) => Properties.FirstOrDefault(p => p.Name == name);

    public IEnumerable<ContentLine> PropertiesNamed(string name) => Properties.Where(p => p.Name == name);

    public void RemoveProperties(string name) => Properties.RemoveAll(p => p.Name == name);

    public IEnumerable<ICalComponent> ChildrenNamed(string name) => Children.Where(c => c.Name == name);

    public ICalComponent Clone() =>
        new() { Name = Name, Properties = [.. Properties.Select(p => p.Clone())], Children = [.. Children.Select(c => c.Clone())] };

    /// <summary>CRLF line ends, lines folded at 75 octets as RFC 5545 requires.</summary>
    public string Serialize()
    {
        var builder = new StringBuilder();
        Write(builder);
        return builder.ToString();
    }

    private void Write(StringBuilder builder)
    {
        Fold(builder, "BEGIN:" + Name);
        foreach (var property in Properties)
        {
            Fold(builder, property.ToString());
        }

        foreach (var child in Children)
        {
            child.Write(builder);
        }

        Fold(builder, "END:" + Name);
    }

    private static void Fold(StringBuilder builder, string line)
    {
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > 75)
            {
                builder.Append("\r\n ");
                bytes = 1;
            }

            builder.Append(rune.ToString());
            bytes += size;
        }

        builder.Append("\r\n");
    }
}
