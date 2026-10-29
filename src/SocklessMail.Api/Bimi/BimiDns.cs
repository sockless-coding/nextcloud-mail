using DnsClient;

namespace SocklessMail.Api.Bimi;

/// <summary>TXT lookups for BIMI and DMARC records; an interface so tests can answer without DNS.</summary>
public interface IBimiDns
{
    /// <returns>
    /// The TXT records at <paramref name="name"/>, each record's strings joined; empty when the name
    /// has none or does not exist.
    /// </returns>
    /// <exception cref="BimiDnsException">The lookup failed (timeout, SERVFAIL); try again later.</exception>
    Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct);
}

public sealed class BimiDnsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Uses the resolvers of the container (/etc/resolv.conf).</summary>
public sealed class SystemBimiDns : IBimiDns
{
    private readonly LookupClient _client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(5),
        Retries = 1,
        UseCache = true,
        ThrowDnsErrors = false,
    });

    public async Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct)
    {
        IDnsQueryResponse response;
        try
        {
            response = await _client.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
        }
        catch (DnsResponseException e)
        {
            throw new BimiDnsException($"DNS lookup failed: {e.Code}", e);
        }

        if (response.HasError && response.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
        {
            throw new BimiDnsException($"DNS lookup failed: {response.Header.ResponseCode}");
        }

        return response.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
    }
}
