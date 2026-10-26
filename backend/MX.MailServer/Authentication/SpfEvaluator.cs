using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MX.Domain;

namespace MX.MailServer.Authentication;

public sealed record SpfOutcome(AuthCheckResult Result, string Domain, string Reason);

/// <summary>
/// SPF check_host() per RFC 7208: all, include, a, mx, ip4, ip6, exists, ptr (treated as
/// non-matching, as the RFC discourages it), redirect, macros, the 10-lookup limit and the
/// 2-void-lookup limit.
/// </summary>
public sealed partial class SpfEvaluator(IMailDns dns)
{
    private const int MaxDnsLookups = 10;
    private const int MaxVoidLookups = 2;
    private const int MaxMxHosts = 10;

    private sealed class State
    {
        public int Lookups;
        public int VoidLookups;
    }

    private sealed class SpfPermError(string message) : Exception(message);

    /// <param name="sender">MAIL FROM address, or postmaster@helo for null senders.</param>
    public async Task<SpfOutcome> CheckAsync(IPAddress ip, string sender, string helo, CancellationToken ct)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var domain = sender.Contains('@') ? sender[(sender.LastIndexOf('@') + 1)..] : helo;
        try
        {
            var (result, reason) = await CheckHostAsync(ip, domain, sender, helo, new State(), ct);
            return new SpfOutcome(result, domain, reason);
        }
        catch (SpfPermError ex)
        {
            return new SpfOutcome(AuthCheckResult.PermError, domain, ex.Message);
        }
        catch (DnsTemporaryException ex)
        {
            return new SpfOutcome(AuthCheckResult.TempError, domain, ex.Message);
        }
    }

    private async Task<(AuthCheckResult, string)> CheckHostAsync(IPAddress ip, string domain, string sender, string helo, State state, CancellationToken ct)
    {
        if (!IsValidDomain(domain)) return (AuthCheckResult.None, $"invalid domain '{domain}'");

        var records = (await dns.TxtAsync(domain, ct))
            .Where(t => t.Equals("v=spf1", StringComparison.OrdinalIgnoreCase) || t.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (records.Count == 0) return (AuthCheckResult.None, $"no SPF record for {domain}");
        if (records.Count > 1) throw new SpfPermError($"multiple SPF records for {domain}");

        string? redirect = null;
        foreach (var term in records[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var modifier = ModifierPattern().Match(term);
            if (modifier.Success)
            {
                if (modifier.Groups[1].Value.Equals("redirect", StringComparison.OrdinalIgnoreCase))
                {
                    if (redirect is not null) throw new SpfPermError("duplicate redirect modifier");
                    redirect = modifier.Groups[2].Value;
                }
                continue; // exp= and unknown modifiers are ignored
            }

            var qualifier = term[0] switch { '+' or '-' or '~' or '?' => term[0], _ => '+' };
            var mechanism = term[0] is '+' or '-' or '~' or '?' ? term[1..] : term;

            if (await MatchesAsync(ip, mechanism, domain, sender, helo, state, ct))
            {
                var result = qualifier switch
                {
                    '-' => AuthCheckResult.Fail,
                    '~' => AuthCheckResult.SoftFail,
                    '?' => AuthCheckResult.Neutral,
                    _ => AuthCheckResult.Pass,
                };
                return (result, $"matched '{term}' in {domain}");
            }
        }

        if (redirect is not null)
        {
            CountLookup(state);
            var target = Expand(redirect, ip, domain, sender, helo);
            var (result, reason) = await CheckHostAsync(ip, target, sender, helo, state, ct);
            if (result == AuthCheckResult.None) throw new SpfPermError($"redirect target {target} has no SPF record");
            return (result, reason);
        }
        return (AuthCheckResult.Neutral, $"no mechanism matched in {domain}");
    }

    private async Task<bool> MatchesAsync(IPAddress ip, string mechanism, string domain, string sender, string helo, State state, CancellationToken ct)
    {
        var colon = mechanism.IndexOf(':');
        var name = (colon >= 0 ? mechanism[..colon] : mechanism.Split('/')[0]).ToLowerInvariant();
        var arg = colon >= 0 ? mechanism[(colon + 1)..] : mechanism[name.Length..];

        switch (name)
        {
            case "all":
                return true;

            case "include":
            {
                CountLookup(state);
                var target = Expand(arg, ip, domain, sender, helo);
                var (result, _) = await CheckHostAsync(ip, target, sender, helo, state, ct);
                return result switch
                {
                    AuthCheckResult.Pass => true,
                    AuthCheckResult.Fail or AuthCheckResult.SoftFail or AuthCheckResult.Neutral => false,
                    AuthCheckResult.TempError => throw new DnsTemporaryException($"include:{target} temperror"),
                    _ => throw new SpfPermError($"include:{target} returned {result}"),
                };
            }

            case "a":
            {
                CountLookup(state);
                var (spec, v4, v6) = SplitCidr(arg);
                var target = spec.Length == 0 ? domain : Expand(spec, ip, domain, sender, helo);
                var addresses = await dns.AddressesAsync(target, ct);
                CountVoid(state, addresses.Count);
                return addresses.Any(a => InNetwork(ip, a, v4, v6));
            }

            case "mx":
            {
                CountLookup(state);
                var (spec, v4, v6) = SplitCidr(arg);
                var target = spec.Length == 0 ? domain : Expand(spec, ip, domain, sender, helo);
                var hosts = await dns.MxHostsAsync(target, ct);
                CountVoid(state, hosts.Count);
                if (hosts.Count > MaxMxHosts) throw new SpfPermError($"more than {MaxMxHosts} MX hosts for {target}");
                foreach (var host in hosts)
                {
                    var addresses = await dns.AddressesAsync(host, ct);
                    if (addresses.Any(a => InNetwork(ip, a, v4, v6))) return true;
                }
                return false;
            }

            case "ip4":
            case "ip6":
            {
                var slash = arg.IndexOf('/');
                var addressText = slash >= 0 ? arg[..slash] : arg;
                if (!IPAddress.TryParse(addressText, out var network)) throw new SpfPermError($"invalid {name} address '{arg}'");
                var expected = name == "ip4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                if (network.AddressFamily != expected) throw new SpfPermError($"invalid {name} address '{arg}'");
                var maxPrefix = expected == AddressFamily.InterNetwork ? 32 : 128;
                var prefix = slash >= 0 && int.TryParse(arg[(slash + 1)..], out var p) ? p : maxPrefix;
                if (prefix < 0 || prefix > maxPrefix) throw new SpfPermError($"invalid prefix in '{arg}'");
                return ip.AddressFamily == expected && new IPNetwork(Mask(network, prefix), prefix).Contains(ip);
            }

            case "exists":
            {
                CountLookup(state);
                var target = Expand(arg, ip, domain, sender, helo);
                var addresses = await dns.AddressesAsync(target, ct);
                CountVoid(state, addresses.Count);
                return addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
            }

            case "ptr":
                CountLookup(state);
                return false;

            default:
                throw new SpfPermError($"unknown mechanism '{mechanism}'");
        }
    }

    private static void CountLookup(State state)
    {
        if (++state.Lookups > MaxDnsLookups) throw new SpfPermError("too many DNS lookups (limit 10)");
    }

    private static void CountVoid(State state, int answers)
    {
        if (answers == 0 && ++state.VoidLookups > MaxVoidLookups) throw new SpfPermError("too many void DNS lookups (limit 2)");
    }

    /// <summary>Splits "domain/24//64" into the domain-spec and the IPv4/IPv6 prefix lengths.</summary>
    private static (string Spec, int V4, int V6) SplitCidr(string arg)
    {
        int v4 = 32, v6 = 128;
        var dual = arg.IndexOf("//", StringComparison.Ordinal);
        if (dual >= 0)
        {
            if (!int.TryParse(arg[(dual + 2)..], out v6) || v6 is < 0 or > 128) throw new SpfPermError($"invalid ip6 prefix in '{arg}'");
            arg = arg[..dual];
        }
        var single = arg.LastIndexOf('/');
        if (single >= 0)
        {
            if (!int.TryParse(arg[(single + 1)..], out v4) || v4 is < 0 or > 32) throw new SpfPermError($"invalid ip4 prefix in '{arg}'");
            arg = arg[..single];
        }
        return (arg, v4, v6);
    }

    private static bool InNetwork(IPAddress ip, IPAddress candidate, int v4Prefix, int v6Prefix)
    {
        if (candidate.AddressFamily != ip.AddressFamily) return false;
        var prefix = ip.AddressFamily == AddressFamily.InterNetwork ? v4Prefix : v6Prefix;
        return new IPNetwork(Mask(candidate, prefix), prefix).Contains(ip);
    }

    /// <summary>IPNetwork requires the base address to have no bits set beyond the prefix.</summary>
    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
        return new IPAddress(bytes);
    }

    /// <summary>RFC 7208 section 7 macro expansion (letters s l o d i h, digit and r transformers, delimiters).</summary>
    internal static string Expand(string spec, IPAddress ip, string domain, string sender, string helo)
    {
        if (!spec.Contains('%')) return spec;
        var at = sender.LastIndexOf('@');
        var local = at > 0 ? sender[..at] : "postmaster";
        var senderDomain = at >= 0 ? sender[(at + 1)..] : helo;
        var sb = new StringBuilder();
        for (var i = 0; i < spec.Length; i++)
        {
            if (spec[i] != '%') { sb.Append(spec[i]); continue; }
            if (i + 1 >= spec.Length) throw new SpfPermError("dangling % in macro");
            var next = spec[++i];
            if (next == '%') { sb.Append('%'); continue; }
            if (next == '_') { sb.Append(' '); continue; }
            if (next == '-') { sb.Append("%20"); continue; }
            if (next != '{') throw new SpfPermError($"invalid macro in '{spec}'");
            var end = spec.IndexOf('}', i);
            if (end < 0) throw new SpfPermError($"unterminated macro in '{spec}'");
            var body = MacroPattern().Match(spec[(i + 1)..end]);
            if (!body.Success) throw new SpfPermError($"invalid macro body in '{spec}'");
            i = end;

            var value = char.ToLowerInvariant(body.Groups[1].Value[0]) switch
            {
                's' => sender,
                'l' => local,
                'o' => senderDomain,
                'd' => domain,
                'h' => helo,
                'i' => ip.AddressFamily == AddressFamily.InterNetwork
                    ? ip.ToString()
                    : string.Join('.', Convert.ToHexString(ip.GetAddressBytes()).ToLowerInvariant().ToCharArray()),
                _ => throw new SpfPermError($"unsupported macro letter '{body.Groups[1].Value}'"),
            };
            var delimiters = body.Groups[4].Success && body.Groups[4].Value.Length > 0 ? body.Groups[4].Value.ToCharArray() : ['.'];
            var parts = value.Split(delimiters);
            if (body.Groups[3].Success && body.Groups[3].Value.Length > 0) Array.Reverse(parts);
            if (body.Groups[2].Success && body.Groups[2].Value.Length > 0)
            {
                var keep = int.Parse(body.Groups[2].Value);
                if (keep == 0) throw new SpfPermError("macro digit transformer cannot be zero");
                parts = parts[Math.Max(0, parts.Length - keep)..];
            }
            sb.Append(string.Join('.', parts));
        }
        return sb.ToString();
    }

    private static bool IsValidDomain(string domain) =>
        domain.Length is > 0 and <= 253 && domain.Contains('.') && domain.Split('.').All(l => l.Length is > 0 and <= 63);

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9\-_.]*)=(.*)$")]
    private static partial Regex ModifierPattern();

    [GeneratedRegex(@"^([slodiphcrtvSLODIPHCRTV])(\d*)(r?)([.\-+,/_=]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex MacroPattern();
}
