using System.Text;

namespace SocklessMail.Api.Mailbox;

/// <summary>
/// The line of message text a list shows under the subject. MailKit fetches it with the list
/// fields: the server's PREVIEW extension (RFC 8970) where there is one, otherwise the first bytes
/// of the text part, converted from HTML when needed.
/// </summary>
public static class MessagePreview
{
    public const int MaxLength = 200;

    /// <summary>Whitespace collapsed, cut at <see cref="MaxLength"/>; "" when there is no text.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        // Some newsletters pad their preview with invisible characters; those go too.
        var result = new StringBuilder(MaxLength);
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = string.Concat(word.Where(c => !char.IsControl(c) && c is not ('​' or '‌' or '͏')));
            if (clean.Length == 0)
            {
                continue;
            }

            if (result.Length > 0)
            {
                result.Append(' ');
            }

            result.Append(clean);
            if (result.Length >= MaxLength)
            {
                return result.ToString(0, MaxLength).TrimEnd();
            }
        }

        return result.ToString();
    }
}
