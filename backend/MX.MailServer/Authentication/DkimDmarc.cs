using MimeKit;
using MimeKit.Cryptography;
using MX.Domain;
using Org.BouncyCastle.Crypto;

namespace MX.MailServer.Authentication;

public sealed record DkimOutcome(AuthCheckResult Result, IReadOnlyList<string> PassingDomains, string Reason);

/// <summary>Verifies DKIM-Signature headers (RFC 6376) using MimeKit's verifier and our DNS lookups.</summary>
public sealed class DkimChecker(IMailDns dns)
{
    private const int MaxSignatures = 5;

    public async Task<DkimOutcome> CheckAsync(MimeMessage message, CancellationToken ct)
    {
        var signatures = message.Headers.Where(h => h.Id == HeaderId.DkimSignature).Take(MaxSignatures).ToList();
        if (signatures.Count == 0) return new DkimOutcome(AuthCheckResult.None, [], "no signature");

        var verifier = new DkimVerifier(new Locator(dns));
        var passing = new List<string>();
        var sawTempError = false;
        foreach (var header in signatures)
        {
            var domain = TagValue(header.Value, "d");
            try
            {
                if (await verifier.VerifyAsync(message, header, ct) && domain is not null) passing.Add(domain.ToLowerInvariant());
            }
            catch (DnsTemporaryException)
            {
                sawTempError = true;
            }
            catch (Exception ex) when (ex is FormatException or ParseException or NotSupportedException or ArgumentException)
            {
                // Malformed signature or key: counts as a failed signature.
            }
        }

        if (passing.Count > 0) return new DkimOutcome(AuthCheckResult.Pass, passing, $"valid signature from {string.Join(", ", passing)}");
        return sawTempError
            ? new DkimOutcome(AuthCheckResult.TempError, [], "key lookup failed temporarily")
            : new DkimOutcome(AuthCheckResult.Fail, [], "no valid signature");
    }

    internal static string? TagValue(string headerValue, string tag)
    {
        foreach (var part in headerValue.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))
                return string.Concat(kv[1].Where(c => !char.IsWhiteSpace(c)));
        }
        return null;
    }

    private sealed class Locator(IMailDns dns) : DkimPublicKeyLocatorBase
    {
        public override AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector, CancellationToken cancellationToken = default) =>
            LocatePublicKeyAsync(methods, domain, selector, cancellationToken).GetAwaiter().GetResult();

        public override async Task<AsymmetricKeyParameter> LocatePublicKeyAsync(string methods, string domain, string selector, CancellationToken cancellationToken = default)
        {
            var records = await dns.TxtAsync($"{selector}._domainkey.{domain}", cancellationToken);
            var record = records.FirstOrDefault(r => r.Contains("p=", StringComparison.Ordinal))
                ?? throw new FormatException($"no DKIM key at {selector}._domainkey.{domain}");
            return GetPublicKey(record);
        }
    }
}

public enum DmarcPolicy { None, Quarantine, Reject }

public sealed record DmarcOutcome(AuthCheckResult Result, DmarcPolicy Policy, string FromDomain, string Reason);

/// <summary>DMARC (RFC 7489): alignment of SPF / DKIM with the From domain and the published policy.</summary>
public sealed class DmarcEvaluator(IMailDns dns)
{
    public async Task<DmarcOutcome> EvaluateAsync(string? fromDomain, SpfOutcome spf, DkimOutcome dkim, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fromDomain)) return new DmarcOutcome(AuthCheckResult.None, DmarcPolicy.None, "", "no From domain");
        fromDomain = fromDomain.ToLowerInvariant();
        var orgDomain = OrganizationalDomain.Of(fromDomain);

        IReadOnlyDictionary<string, string>? record;
        var usedOrgRecord = false;
        try
        {
            record = await LookupAsync(fromDomain, ct);
            if (record is null && orgDomain != fromDomain)
            {
                record = await LookupAsync(orgDomain, ct);
                usedOrgRecord = record is not null;
            }
        }
        catch (DnsTemporaryException ex)
        {
            return new DmarcOutcome(AuthCheckResult.TempError, DmarcPolicy.None, fromDomain, ex.Message);
        }
        if (record is null) return new DmarcOutcome(AuthCheckResult.None, DmarcPolicy.None, fromDomain, "no DMARC record");

        var policyTag = usedOrgRecord && record.TryGetValue("sp", out var sp) ? sp : record.GetValueOrDefault("p", "none");
        var policy = policyTag.ToLowerInvariant() switch { "reject" => DmarcPolicy.Reject, "quarantine" => DmarcPolicy.Quarantine, _ => DmarcPolicy.None };
        var strictSpf = record.GetValueOrDefault("aspf", "r").Equals("s", StringComparison.OrdinalIgnoreCase);
        var strictDkim = record.GetValueOrDefault("adkim", "r").Equals("s", StringComparison.OrdinalIgnoreCase);

        var spfAligned = spf.Result == AuthCheckResult.Pass && Aligned(spf.Domain, fromDomain, strictSpf);
        var dkimAligned = dkim.PassingDomains.Any(d => Aligned(d, fromDomain, strictDkim));

        return spfAligned || dkimAligned
            ? new DmarcOutcome(AuthCheckResult.Pass, policy, fromDomain, spfAligned ? "aligned SPF pass" : "aligned DKIM pass")
            : new DmarcOutcome(AuthCheckResult.Fail, policy, fromDomain, "no aligned SPF or DKIM pass");
    }

    private static bool Aligned(string authenticated, string from, bool strict) =>
        strict
            ? authenticated.Equals(from, StringComparison.OrdinalIgnoreCase)
            : OrganizationalDomain.Of(authenticated.ToLowerInvariant()) == OrganizationalDomain.Of(from);

    private async Task<IReadOnlyDictionary<string, string>?> LookupAsync(string domain, CancellationToken ct)
    {
        var txt = (await dns.TxtAsync($"_dmarc.{domain}", ct))
            .Where(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (txt.Count != 1) return null; // none, or ambiguous multiple records (treated as no policy)
        return txt[0].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0].Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim());
    }
}

/// <summary>
/// Approximates the organizational domain without the full Public Suffix List: the registrable
/// domain is the last two labels, or three under common two-level public suffixes.
/// </summary>
public static class OrganizationalDomain
{
    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "com.au", "net.au", "org.au", "co.nz", "co.in", "co.jp", "co.za",
        "com.br", "com.cn", "com.mx", "com.pk", "net.pk", "org.pk", "edu.pk", "gov.pk", "com.sa", "com.tr", "com.sg", "com.my",
    };

    public static string Of(string domain)
    {
        var labels = domain.TrimEnd('.').ToLowerInvariant().Split('.');
        if (labels.Length <= 2) return string.Join('.', labels);
        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        return TwoLevelSuffixes.Contains(lastTwo) ? $"{labels[^3]}.{lastTwo}" : lastTwo;
    }
}
