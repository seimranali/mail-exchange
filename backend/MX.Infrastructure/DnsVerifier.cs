using DnsClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MX.Application.Abstractions;

namespace MX.Infrastructure;

/// <summary>Checks ownership (TXT "mx-verification=token"), MX, SPF, DKIM and DMARC records via public DNS.</summary>
public sealed class DnsVerifier(ILookupClient dns, IConfiguration configuration, ILogger<DnsVerifier> logger) : IDnsVerifier
{
    public async Task<DnsCheckResult> CheckAsync(string domainName, string verificationToken, string? activeSelector, CancellationToken ct)
    {
        var dkimSelector = activeSelector ?? configuration["Mail:DkimSelector"] ?? "mx";
        try
        {
            var txt = await TxtAsync(domainName, ct);
            var mx = await dns.QueryAsync(domainName, QueryType.MX, cancellationToken: ct);
            var dkim = await TxtAsync($"{dkimSelector}._domainkey.{domainName}", ct);
            var dmarc = await TxtAsync($"_dmarc.{domainName}", ct);

            return new DnsCheckResult(
                OwnershipVerified: txt.Any(t => t == $"mx-verification={verificationToken}"),
                Mx: mx.Answers.MxRecords().Any(),
                Spf: txt.Any(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)),
                Dkim: dkim.Any(t => t.Contains("v=DKIM1", StringComparison.OrdinalIgnoreCase)),
                Dmarc: dmarc.Any(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)));
        }
        catch (DnsResponseException ex)
        {
            logger.LogWarning(ex, "DNS lookup failed for {Domain}", domainName);
            return new DnsCheckResult(false, false, false, false, false);
        }
    }

    private async Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct)
    {
        var result = await dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
        return result.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ILookupClient>(sp => MailDnsClientFactory.Create(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IDnsVerifier, DnsVerifier>();
        return services;
    }
}
