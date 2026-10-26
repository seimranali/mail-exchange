using System.Net;
using DnsClient;
using Microsoft.Extensions.Configuration;

namespace MX.Infrastructure;

/// <summary>
/// Builds the DNS client used for mail checks (SPF, DKIM, DMARC, MX, blocklists, domain verification).
/// When <c>Dns:Servers</c> is configured, those resolvers are queried directly instead of the
/// system stub: local stubs such as systemd-resolved have been seen returning corrupted cached
/// TXT answers, which would turn valid DKIM/SPF records into false failures.
/// </summary>
public static class MailDnsClientFactory
{
    public static LookupClient Create(IConfiguration configuration)
    {
        var servers = configuration.GetSection("Dns:Servers").Get<string[]>()?
            .Select(s => IPAddress.TryParse(s.Trim(), out var ip) ? new NameServer(ip) : null)
            .OfType<NameServer>()
            .ToArray() ?? [];

        var options = servers.Length > 0 ? new LookupClientOptions(servers) : new LookupClientOptions();
        options.Timeout = TimeSpan.FromSeconds(5);
        options.Retries = 2;
        options.UseCache = true;
        // A negative answer must not be remembered for long: a record added minutes later should be seen.
        options.MaximumCacheTimeout = TimeSpan.FromMinutes(5);
        options.UseTcpFallback = true; // long TXT records (2048-bit DKIM keys) may exceed UDP size
        return new LookupClient(options);
    }
}
