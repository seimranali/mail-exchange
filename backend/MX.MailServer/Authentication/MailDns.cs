using System.Net;
using DnsClient;
using DnsClient.Protocol;

namespace MX.MailServer.Authentication;

/// <summary>DNS queries needed by SPF, DKIM and DMARC. Abstracted so the evaluators can be tested offline.</summary>
public interface IMailDns
{
    Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<IPAddress>> AddressesAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<string>> MxHostsAsync(string name, CancellationToken ct);
}

/// <summary>Thrown for transient DNS failures (SERVFAIL, timeouts), which map to temperror.</summary>
public sealed class DnsTemporaryException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class DnsClientMailDns(ILookupClient client) : IMailDns
{
    public async Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct)
    {
        var result = await QueryAsync(name, QueryType.TXT, ct);
        // A TXT record may be split into several strings that must be concatenated.
        return result.Answers.OfType<TxtRecord>().Select(r => string.Concat(r.Text)).ToList();
    }

    public async Task<IReadOnlyList<IPAddress>> AddressesAsync(string name, CancellationToken ct)
    {
        var a = await QueryAsync(name, QueryType.A, ct);
        var aaaa = await QueryAsync(name, QueryType.AAAA, ct);
        return a.Answers.OfType<ARecord>().Select(r => r.Address)
            .Concat(aaaa.Answers.OfType<AaaaRecord>().Select(r => r.Address))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> MxHostsAsync(string name, CancellationToken ct)
    {
        var result = await QueryAsync(name, QueryType.MX, ct);
        return result.Answers.OfType<MxRecord>().OrderBy(r => r.Preference).Select(r => r.Exchange.Value.TrimEnd('.')).ToList();
    }

    private async Task<IDnsQueryResponse> QueryAsync(string name, QueryType type, CancellationToken ct)
    {
        IDnsQueryResponse response;
        try
        {
            response = await client.QueryAsync(name, type, cancellationToken: ct);
        }
        catch (DnsResponseException ex)
        {
            throw new DnsTemporaryException($"DNS {type} lookup for {name} failed", ex);
        }
        if (response.HasError && response.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
            throw new DnsTemporaryException($"DNS {type} lookup for {name} returned {response.Header.ResponseCode}");
        return response;
    }
}
