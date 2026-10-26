using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MX.Domain;
using MX.MailServer.Authentication;

namespace MX.MailServer.Filtering;

public interface IContentFilter
{
    Task<FilterVerdict> EvaluateAsync(FilterContext context, CancellationToken ct);
}

/// <summary>
/// Scores inbound mail with transparent, named rules (authentication results, DNS blocklists,
/// header sanity, phishing indicators, content patterns) plus the attachment scanner.
/// </summary>
public sealed partial class ContentFilter(IMailDns dns, IMalwareScanner scanner, IOptions<FilterOptions> options, ILogger<ContentFilter> logger) : IContentFilter
{
    private static readonly string[] SpamPhrases =
    [
        "viagra", "cialis", "casino", "lottery", "you have won", "you've won", "claim your prize", "bitcoin giveaway", "crypto giveaway",
        "100% free", "act now", "limited time offer", "risk-free", "work from home and earn", "weight loss", "nigerian prince", "inheritance fund",
    ];

    private static readonly string[] PhishingPhrases =
    [
        "verify your account", "confirm your password", "your password will expire", "your mailbox is full", "unusual sign-in activity",
        "account will be suspended", "update your payment", "urgent action required", "validate your mailbox", "re-activate your account",
    ];

    private static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at",
    };

    public async Task<FilterVerdict> EvaluateAsync(FilterContext ctx, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled) return FilterVerdict.Clean;

        var hits = new List<FilterHit>();
        var phishing = false;
        void Hit(string rule, decimal score, string detail, bool isPhishing = false)
        {
            hits.Add(new FilterHit(rule, score, detail));
            phishing |= isPhishing;
        }

        // 1. Malware / policy: decided independently of the score.
        if (ctx.Message is not null)
        {
            var scan = scanner.Scan(ctx.Message);
            if (scan.Infected)
                return new FilterVerdict(FilterAction.Quarantine, 100, [new FilterHit("MALWARE", 100, scan.Detail ?? "malware")], QuarantineReason.Malware);
            if (scan.PolicyViolation)
                return new FilterVerdict(FilterAction.Quarantine, o.QuarantineThreshold, [new FilterHit("POLICY_ATTACHMENT", o.QuarantineThreshold, scan.Detail ?? "policy")], QuarantineReason.Policy);
            if (scan.SuspicionScore > 0) Hit("SUSPICIOUS_ARCHIVE", scan.SuspicionScore, scan.Detail ?? "suspicious archive");
        }

        // 2. Sender authentication.
        switch (ctx.Spf.Result)
        {
            case AuthCheckResult.Fail: Hit("SPF_FAIL", 3, $"SPF fail for {ctx.Spf.Domain}"); break;
            case AuthCheckResult.SoftFail: Hit("SPF_SOFTFAIL", 1, $"SPF softfail for {ctx.Spf.Domain}"); break;
            case AuthCheckResult.PermError: Hit("SPF_PERMERROR", 0.5m, "Broken SPF record"); break;
        }
        if (ctx.Dkim.Result == AuthCheckResult.Fail) Hit("DKIM_INVALID", 1.5m, "DKIM signature did not verify");
        if (ctx.Dmarc.Result == AuthCheckResult.Fail) Hit("DMARC_FAIL", 2, $"DMARC fail for {ctx.Dmarc.FromDomain}");

        // 3. Reputation of the connecting IP.
        foreach (var zone in await ListedZonesAsync(ctx.RemoteIp, ct)) { Hit("DNSBL", o.DnsblScore, $"{ctx.RemoteIp} listed on {zone}"); break; }

        // 4. Protocol / header sanity.
        if (IPAddress.TryParse(ctx.Helo.Trim('[', ']'), out _) || !ctx.Helo.Contains('.')) Hit("HELO_NOT_FQDN", 1, $"HELO '{ctx.Helo}' is not a host name");
        var m = ctx.Message;
        if (m is not null)
        {
            if (string.IsNullOrEmpty(m.Headers[HeaderId.MessageId])) Hit("MISSING_MESSAGE_ID", 1, "No Message-ID header");
            if (!m.Headers.Contains(HeaderId.Date)) Hit("MISSING_DATE", 1, "No Date header");
            else if (m.Date > DateTimeOffset.UtcNow.AddDays(2)) Hit("DATE_IN_FUTURE", 1.5m, "Date header is in the future");

            var from = m.From.Mailboxes.FirstOrDefault();
            var fromDomain = from?.Domain?.ToLowerInvariant() ?? "";

            // Phishing: mail claiming to come from one of our own domains without authenticating as it.
            if (ctx.HostedDomains.Contains(fromDomain) && ctx.Dmarc.Result != AuthCheckResult.Pass && ctx.Spf.Result != AuthCheckResult.Pass && ctx.Dkim.Result != AuthCheckResult.Pass)
                Hit("SPOOFED_LOCAL_DOMAIN", 8, $"Claims to be from {fromDomain} but is not authenticated", isPhishing: true);

            // Phishing: look-alike of a hosted domain (exampIecorp.net, examp1e…).
            var lookalike = ctx.HostedDomains.FirstOrDefault(h => h != fromDomain && IsLookalike(fromDomain, h));
            if (lookalike is not null) Hit("LOOKALIKE_DOMAIN", 6, $"{fromDomain} imitates {lookalike}", isPhishing: true);

            // Phishing: display name shows a different address than the real one.
            if (from?.Name is { Length: > 0 } displayName && EmailInText().Match(displayName) is { Success: true } shown
                && !shown.Value.Equals(from.Address, StringComparison.OrdinalIgnoreCase))
                Hit("DISPLAY_NAME_SPOOF", 2.5m, $"Display name shows {shown.Value} but sender is {from.Address}", isPhishing: true);

            var subject = m.Subject ?? "";
            var letters = subject.Where(char.IsLetter).ToList();
            if (letters.Count >= 10 && letters.All(char.IsUpper)) Hit("SUBJECT_ALL_CAPS", 1.5m, "Subject is all capitals");
            if (subject.Count(c => c == '!') >= 3) Hit("SUBJECT_EXCLAMATIONS", 1, "Subject has many exclamation marks");

            var text = (m.TextBody ?? "") + "\n" + (m.HtmlBody ?? "") + "\n" + subject;
            var spamPhrases = SpamPhrases.Where(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            if (spamPhrases.Count > 0) Hit("SPAM_PHRASES", spamPhrases.Count, string.Join(", ", spamPhrases));
            var phishPhrases = PhishingPhrases.Where(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (phishPhrases.Count > 0) Hit("PHISHING_PHRASES", 1.5m * phishPhrases.Count, string.Join(", ", phishPhrases), isPhishing: true);

            if (m.HtmlBody is not null && m.TextBody is null) Hit("HTML_ONLY", 0.8m, "HTML without a text alternative");
            CheckLinks(m.HtmlBody, m.TextBody, Hit);
        }

        var score = hits.Sum(h => h.Score);
        var action = score >= o.RejectThreshold ? FilterAction.Reject
            : score >= o.QuarantineThreshold ? FilterAction.Quarantine
            : score >= o.JunkThreshold ? FilterAction.Junk
            : FilterAction.Deliver;
        QuarantineReason? reason = action == FilterAction.Quarantine ? (phishing ? QuarantineReason.Phishing : QuarantineReason.Spam) : null;
        return new FilterVerdict(action, score, hits, reason);
    }

    private static void CheckLinks(string? html, string? text, Action<string, decimal, string, bool> hit)
    {
        var ipLinks = false;
        var shortener = false;
        var mismatch = (string?)null;

        if (html is not null)
        {
            foreach (Match a in Anchor().Matches(html))
            {
                if (!Uri.TryCreate(a.Groups["href"].Value, UriKind.Absolute, out var href) || href.Scheme is not ("http" or "https")) continue;
                ipLinks |= href.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6;
                shortener |= Shorteners.Contains(href.Host);
                // Link text that itself looks like a URL/domain but points elsewhere.
                var shown = StripTags().Replace(a.Groups["text"].Value, "").Trim();
                if (mismatch is null && DomainInText().Match(shown) is { Success: true } shownDomain
                    && !SameSite(shownDomain.Groups["domain"].Value, href.Host))
                    mismatch = $"link shows {shownDomain.Groups["domain"].Value} but goes to {href.Host}";
            }
        }
        foreach (Match u in BareUrl().Matches(text ?? ""))
        {
            if (!Uri.TryCreate(u.Value, UriKind.Absolute, out var url)) continue;
            ipLinks |= url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6;
            shortener |= Shorteners.Contains(url.Host);
        }

        if (ipLinks) hit("URL_IP_ADDRESS", 2, "Link points to a raw IP address", true);
        if (mismatch is not null) hit("LINK_TEXT_MISMATCH", 3, mismatch, true);
        if (shortener) hit("URL_SHORTENER", 0.5m, "Link uses a URL shortener", false);
    }

    private static bool SameSite(string a, string b) =>
        OrganizationalDomain.Of(a.TrimStart('.').ToLowerInvariant()) == OrganizationalDomain.Of(b.ToLowerInvariant());

    /// <summary>Domains within edit distance 1–2 (or using digit/letter swaps) of a hosted domain.</summary>
    internal static bool IsLookalike(string candidate, string hosted)
    {
        if (candidate.Length == 0 || candidate == hosted) return false;
        static string Normalize(string s) => s.Replace('0', 'o').Replace('1', 'l').Replace('I', 'l').Replace("rn", "m").Replace("vv", "w").ToLowerInvariant();
        if (Normalize(candidate) == Normalize(hosted)) return true;
        var distance = Levenshtein(candidate, hosted);
        return distance is > 0 and <= 2 && hosted.Length >= 6;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private async Task<List<string>> ListedZonesAsync(IPAddress ip, CancellationToken ct)
    {
        var listed = new List<string>();
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || IsPrivate(ip)) return listed;
        var reversed = string.Join('.', ip.GetAddressBytes().Reverse());
        foreach (var zone in options.Value.DnsblZones)
        {
            try
            {
                var answers = await dns.AddressesAsync($"{reversed}.{zone}", ct);
                foreach (var a in answers.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                {
                    var b = a.GetAddressBytes();
                    if (b[0] != 127) continue;
                    // 127.255.255.x: the list refused our query (e.g. via a public resolver) — not a listing.
                    if (b[1] == 255 && b[2] == 255) { logger.LogWarning("DNSBL {Zone} refused the query ({Code}); use a local resolver", zone, a); continue; }
                    listed.Add(zone);
                    break;
                }
            }
            catch (DnsTemporaryException ex)
            {
                logger.LogDebug(ex, "DNSBL {Zone} lookup failed", zone);
            }
        }
        return listed;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailInText();

    [GeneratedRegex(@"<a\s[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex StripTags();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?<domain>[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.[a-z]{2,})(?:[/:?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainInText();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();
}
