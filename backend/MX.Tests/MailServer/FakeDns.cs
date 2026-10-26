using System.Net;
using MX.MailServer.Authentication;

namespace MX.Tests.MailServer;

/// <summary>In-memory DNS for SPF/DKIM/DMARC tests.</summary>
public sealed class FakeDns : IMailDns
{
    public Dictionary<string, List<string>> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<IPAddress>> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Mx { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Queries { get; private set; }

    public FakeDns AddTxt(string name, string value) { Get(Txt, name).Add(value); return this; }
    public FakeDns AddA(string name, string ip) { Get(Addresses, name).Add(IPAddress.Parse(ip)); return this; }
    public FakeDns AddMx(string name, string host) { Get(Mx, name).Add(host); return this; }

    public Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct) => Lookup(Txt, name);
    public Task<IReadOnlyList<IPAddress>> AddressesAsync(string name, CancellationToken ct) => Lookup(Addresses, name);
    public Task<IReadOnlyList<string>> MxHostsAsync(string name, CancellationToken ct) => Lookup(Mx, name);

    private Task<IReadOnlyList<T>> Lookup<T>(Dictionary<string, List<T>> map, string name)
    {
        Queries++;
        if (Failing.Contains(name)) throw new DnsTemporaryException($"SERVFAIL {name}");
        return Task.FromResult<IReadOnlyList<T>>(map.TryGetValue(name, out var v) ? v : []);
    }

    private static List<T> Get<T>(Dictionary<string, List<T>> map, string name) =>
        map.TryGetValue(name, out var list) ? list : map[name] = [];
}
