using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MimeKit;
using Org.BouncyCastle.Bcpg.OpenPgp;
using SocklessMail.Api.Contacts;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Crypto;

/// <param name="Created">No contact had the address: a new one was made.</param>
public sealed record SavedToContactDto(string ContactId, string Name, bool Created);

/// <summary>keys.openpgp.org's answer per address: "unpublished", "pending" (a confirmation mail was sent), "published" or "revoked".</summary>
public sealed record PublishResultDto(IReadOnlyDictionary<string, string> Addresses);

/// <summary>
/// Handing keys around: the Autocrypt header (autocrypt.org, Level 1) on outgoing mail and what
/// incoming headers bring, a key saved into a contact (vCard KEY), and an own key published on
/// keys.openpgp.org, which publishes an address only after its owner confirmed it by mail.
/// </summary>
public sealed class KeySharing(
    CryptoKeyStore store, NextcloudContacts contacts, ContactDirectory directory, IHttpClientFactory http, TimeProvider clock, ILogger<KeySharing> logger)
{
    public const string AutocryptHeader = "Autocrypt";

    // ---- Autocrypt ----

    /// <summary>
    /// Adds an Autocrypt header with the sender's OpenPGP public key when they have a usable one
    /// for the From address, so programs that speak Autocrypt can answer encrypted.
    /// </summary>
    public async Task AddAutocryptAsync(string userId, MimeMessage message, CancellationToken ct)
    {
        if (message.From.Mailboxes.FirstOrDefault() is not { } from)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var key = (await store.LoadAsync(userId, ct)).Keys
            .Where(k => k.Kind == CryptoKind.OpenPgp && k.IsOwn && k.AddressList.Contains(from.Address, StringComparer.OrdinalIgnoreCase) && KeyUsability.CanEncrypt(k, now))
            .OrderByDescending(k => k.CreatedAt)
            .FirstOrDefault();
        if (key is null)
        {
            return;
        }

        // Folded by hand: base64 with no spaces of its own that MimeKit could fold at.
        var data = Convert.ToBase64String(key.PublicData);
        var value = new StringBuilder($" addr={from.Address}; keydata=\r\n");
        for (var at = 0; at < data.Length; at += 76)
        {
            value.Append(' ').Append(data.AsSpan(at, Math.Min(76, data.Length - at))).Append("\r\n");
        }

        message.Headers.RemoveAll(AutocryptHeader);
        var header = new Header(AutocryptHeader, "");
        header.SetRawValue(Encoding.ASCII.GetBytes(value.ToString()));
        message.Headers.Add(header);
    }

    /// <summary>
    /// Keeps the key of an incoming Autocrypt header, trust on first use: only when its addr is
    /// the single From address, the message did not fail DMARC, and the user has no OpenPGP key for
    /// that address yet (a header never replaces a key the user has).
    /// </summary>
    public async Task<CryptoKey?> TakeAutocryptAsync(string userId, string? header, InternetAddressList? from, bool dmarcFailed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(header) || dmarcFailed || from?.Mailboxes.Count() != 1)
        {
            return null;
        }

        var sender = from.Mailboxes.Single().Address;
        if (ParseAutocrypt(header) is not { } parsed || !parsed.Address.Equals(sender, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var keys = (await store.LoadAsync(userId, ct)).Keys;
        if (keys.Any(k => k.Kind == CryptoKind.OpenPgp && k.AddressList.Contains(sender, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        return await store.RememberAsync(userId, CryptoKind.OpenPgp, parsed.KeyData, KeySources.Autocrypt, [sender], ct);
    }

    /// <summary>addr and keydata of an Autocrypt header; null when it lacks them or has a critical attribute it does not know.</summary>
    internal static (string Address, byte[] KeyData)? ParseAutocrypt(string header)
    {
        string? address = null;
        byte[]? keyData = null;
        foreach (var attribute in header.Split(';'))
        {
            var equals = attribute.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var name = attribute[..equals].Trim().ToLowerInvariant();
            var value = attribute[(equals + 1)..];
            switch (name)
            {
                case "addr":
                    address = value.Trim();
                    break;
                case "keydata":
                    try
                    {
                        keyData = Convert.FromBase64String(string.Concat(value.Where(c => !char.IsWhiteSpace(c))));
                    }
                    catch (FormatException)
                    {
                        return null;
                    }

                    break;
                case "prefer-encrypt":
                    break;
                default:
                    // Unknown attributes are ignorable only when they start with an underscore.
                    if (!name.StartsWith('_'))
                    {
                        return null;
                    }

                    break;
            }
        }

        return address is { Length: > 2 } && address.Contains('@') && keyData is { Length: > 0 } ? (address, keyData) : null;
    }

    // ---- Contacts ----

    /// <summary>
    /// Saves a key into the first writable contact with its address, or a new contact in the
    /// first writable address book when there is none.
    /// </summary>
    public async Task<SavedToContactDto> SaveToContactAsync(string userId, CryptoKey key, CancellationToken ct)
    {
        var address = key.AddressList.FirstOrDefault() ?? throw new MailboxValidationException("id", L.T("The key names no address."));
        var books = await directory.GetAsync(userId, contacts, ct);
        var certificate = key.Kind == CryptoKind.Smime;
        var existing = books.Where(b => b.Book.Writable)
            .SelectMany(b => b.Contacts)
            .FirstOrDefault(c => c.Card.Emails.Contains(address, StringComparer.OrdinalIgnoreCase));
        var created = false;
        string contactId, name;
        if (existing is not null)
        {
            (contactId, name) = (existing.Id, existing.Card.Name);
        }
        else
        {
            var book = books.FirstOrDefault(b => b.Book.Writable).Book
                       ?? throw new MailboxValidationException("id", L.T("You have no address book to save contacts in."));
            name = key.Name ?? address;
            contactId = await contacts.CreateAsync(userId, book.Id, name, address, ct);
            created = true;
        }

        await contacts.AddKeyAsync(userId, contactId, key.PublicData, certificate, ct);
        directory.Invalidate(userId);
        return new SavedToContactDto(contactId, name, created);
    }

    // ---- keys.openpgp.org ----

    /// <summary>
    /// Uploads an own OpenPGP public key to keys.openpgp.org and asks it to confirm each address
    /// that is not published yet; the key server mails the owner a link to do so.
    /// </summary>
    /// <exception cref="KeyServerException">The key server refused it or could not be reached.</exception>
    public async Task<PublishResultDto> PublishAsync(CryptoKey key, CancellationToken ct)
    {
        if (key.Kind != CryptoKind.OpenPgp || !key.IsOwn)
        {
            throw new MailboxValidationException("id", L.T("Only your own OpenPGP keys can be published."));
        }

        var client = http.CreateClient(PublicKeyFinder.HttpClientName);
        var armored = OpenPgpKeys.Armor(new PgpPublicKeyRing(key.PublicData));
        var uploaded = await PostAsync(client, "vks/v1/upload", new { keytext = armored }, ct);
        var waiting = uploaded.Status.Where(s => s.Value == "unpublished").Select(s => s.Key).ToList();
        if (waiting.Count == 0 || uploaded.Token is null)
        {
            return new PublishResultDto(uploaded.Status);
        }

        var verified = await PostAsync(client, "vks/v1/request-verify", new { token = uploaded.Token, addresses = waiting, locale = new[] { "en" } }, ct);
        return new PublishResultDto(verified.Status);
    }

    private sealed record VksAnswer(
        [property: JsonPropertyName("key_fpr")] string? Fingerprint,
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("status")] Dictionary<string, string>? StatusOrNull,
        [property: JsonPropertyName("error")] string? Error)
    {
        [JsonIgnore]
        public Dictionary<string, string> Status => StatusOrNull ?? [];
    }

    private async Task<VksAnswer> PostAsync(HttpClient client, string path, object body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(new Uri(PublicKeyFinder.KeyServer, path), body, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("keys.openpgp.org could not be reached ({ExceptionType})", e.GetType().Name);
            throw new KeyServerException(L.T("keys.openpgp.org could not be reached. Try again later."));
        }

        using (response)
        {
            VksAnswer? answer = null;
            try
            {
                answer = await response.Content.ReadFromJsonAsync<VksAnswer>(ct);
            }
            catch (JsonException)
            {
                // Answered with something else than its JSON (an error page).
            }

            if (!response.IsSuccessStatusCode || answer is null)
            {
                throw new KeyServerException(answer?.Error is { Length: > 0 } error
                    ? $"keys.openpgp.org refused the key: {error}"
                    : $"keys.openpgp.org refused the key (HTTP {(int)response.StatusCode}).");
            }

            return answer;
        }
    }
}

/// <summary>keys.openpgp.org refused a request or could not be reached; answered with 502.</summary>
public sealed class KeyServerException(string message) : Exception(message);
