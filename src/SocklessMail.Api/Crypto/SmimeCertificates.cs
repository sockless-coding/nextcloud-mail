using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Security.Certificates;
using Org.BouncyCastle.X509;

namespace SocklessMail.Api.Crypto;

/// <summary>An S/MIME certificate read from a file, with the PKCS#12 file it came from when that held its private key.</summary>
public sealed record ParsedCertificate(X509Certificate Certificate, byte[]? Pkcs12)
{
    public string Fingerprint => SmimeCertificates.Fingerprint(Certificate);
}

/// <summary>Reading S/MIME certificates and PKCS#12 files (BouncyCastle).</summary>
public static class SmimeCertificates
{
    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetEncoded()));

    public static bool LooksLikePkcs12(byte[] data) =>
        // A DER SEQUENCE whose first element is the version INTEGER 3 (RFC 7292); certificates start with a SEQUENCE of a SEQUENCE.
        data.Length > 8 && data[0] == 0x30 && FirstElementIsVersion3(data);

    private static bool FirstElementIsVersion3(byte[] data)
    {
        var at = 1;
        if ((data[at] & 0x80) != 0)
        {
            at += 1 + (data[at] & 0x7f);
        }
        else
        {
            at++;
        }

        return at + 2 < data.Length && data[at] == 0x02 && data[at + 1] == 0x01 && data[at + 2] == 0x03;
    }

    /// <summary>
    /// The certificates with a private key in a PKCS#12 file (normally one), each carrying the
    /// whole file. Thrown <see cref="CryptographicException"/>: wrong password.
    /// </summary>
    /// <exception cref="FormatException">The file is not PKCS#12 or holds no private key.</exception>
    public static IReadOnlyList<ParsedCertificate> ParsePkcs12(byte[] data, string password)
    {
        var store = Open(data, password);
        var found = store.Aliases
            .Where(store.IsKeyEntry)
            .Select(alias => store.GetCertificate(alias)?.Certificate)
            .OfType<X509Certificate>()
            .Select(c => new ParsedCertificate(c, data))
            .ToList();
        return found.Count > 0 ? found : throw new FormatException("The PKCS#12 file holds no private key.");
    }

    /// <exception cref="CryptographicException">The password is wrong.</exception>
    /// <exception cref="FormatException">The file is not PKCS#12.</exception>
    public static Pkcs12Store Open(byte[] data, string password)
    {
        var store = new Pkcs12StoreBuilder().Build();
        try
        {
            store.Load(new MemoryStream(data), password.ToCharArray());
            return store;
        }
        catch (IOException e) when (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("MAC", StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The password is wrong.", e);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidCastException or Org.BouncyCastle.Crypto.CryptoException or InvalidOperationException)
        {
            throw new FormatException("This is not a PKCS#12 file.", e);
        }
    }

    /// <summary>Whether <paramref name="password"/> opens the PKCS#12 file.</summary>
    public static bool Opens(byte[] pkcs12, string password)
    {
        try
        {
            Open(pkcs12, password);
            return true;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Certificates in PEM or DER (.pem, .crt, .cer).</summary>
    /// <exception cref="FormatException">There is no certificate in the data.</exception>
    public static IReadOnlyList<ParsedCertificate> ParseCertificates(byte[] data)
    {
        try
        {
            var certificates = new X509CertificateParser().ReadCertificates(data);
            return certificates.Count > 0
                ? certificates.Select(c => new ParsedCertificate(c, null)).ToList()
                : throw new FormatException("This is not a certificate.");
        }
        catch (Exception e) when (e is CertificateException or IOException or ArgumentException or InvalidCastException)
        {
            throw new FormatException("This is not a certificate.", e);
        }
    }

    /// <summary>The certificate's e-mail addresses (subject alternative names, then the subject's E=), lower case, and its common name.</summary>
    public static (IReadOnlyList<string> Addresses, string? Name) Identities(X509Certificate certificate)
    {
        var addresses = new List<string>();
        void Add(string? address)
        {
            var value = address?.Trim().ToLowerInvariant();
            if (value is { Length: > 2 } && value.Contains('@') && !addresses.Contains(value))
            {
                addresses.Add(value);
            }
        }

        try
        {
            foreach (var name in certificate.GetSubjectAlternativeNames() ?? [])
            {
                if (name.Count >= 2 && name[0] is int type && type == GeneralName.Rfc822Name)
                {
                    Add(name[1] as string);
                }
            }
        }
        catch (CertificateParsingException)
        {
            // A malformed extension; the subject may still name the address.
        }

        foreach (var email in certificate.SubjectDN.GetValueList(X509Name.EmailAddress))
        {
            Add(email);
        }

        var commonName = certificate.SubjectDN.GetValueList(X509Name.CN).FirstOrDefault();
        return (addresses, string.IsNullOrWhiteSpace(commonName) ? null : commonName.Trim());
    }

    public static string Pem(X509Certificate certificate) =>
        "-----BEGIN CERTIFICATE-----\n"
        + Convert.ToBase64String(certificate.GetEncoded(), Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n")
        + "\n-----END CERTIFICATE-----\n";

    public static X509Certificate Read(byte[] der) => new X509CertificateParser().ReadCertificate(der);
}
