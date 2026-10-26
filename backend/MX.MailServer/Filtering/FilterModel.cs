using System.Net;
using MimeKit;
using MX.Domain;
using MX.MailServer.Authentication;

namespace MX.MailServer.Filtering;

public sealed class FilterOptions
{
    public const string Section = "Filtering";

    public bool Enabled { get; set; } = true;
    /// <summary>Score at which mail is delivered to Junk instead of the Inbox.</summary>
    public decimal JunkThreshold { get; set; } = 5;
    /// <summary>Score at which mail is held in quarantine for an administrator.</summary>
    public decimal QuarantineThreshold { get; set; } = 10;
    /// <summary>Score at which mail is refused during the SMTP session (sender gets a 550).</summary>
    public decimal RejectThreshold { get; set; } = 20;

    /// <summary>
    /// DNS blocklists queried for the connecting IP. Spamhaus refuses queries arriving via large
    /// public resolvers (8.8.8.8, 1.1.1.1); run a local caching resolver on the mail host.
    /// </summary>
    public string[] DnsblZones { get; set; } = ["zen.spamhaus.org", "bl.spamcop.net"];
    public decimal DnsblScore { get; set; } = 6;

    public int QuarantineDays { get; set; } = 30;
    public long MaxScanBytes { get; set; } = 25L * 1024 * 1024;

    public string[] BlockedExtensions { get; set; } =
    [
        ".exe", ".scr", ".com", ".pif", ".bat", ".cmd", ".cpl", ".msi", ".msp", ".jar", ".js", ".jse", ".vbs", ".vbe",
        ".wsf", ".wsh", ".ps1", ".psm1", ".hta", ".lnk", ".reg", ".dll", ".sys", ".iso", ".img", ".vhd", ".vhdx", ".application", ".appx", ".msix",
    ];

    /// <summary>Macro-enabled Office formats: held as policy violations rather than malware.</summary>
    public string[] MacroExtensions { get; set; } = [".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".sldm"];
}

public enum FilterAction { Deliver, Junk, Quarantine, Reject }

public sealed record FilterHit(string Rule, decimal Score, string Detail);

public sealed record FilterVerdict(FilterAction Action, decimal Score, IReadOnlyList<FilterHit> Hits, QuarantineReason? Reason)
{
    public static readonly FilterVerdict Clean = new(FilterAction.Deliver, 0, [], null);

    /// <summary>Compact form for the X-MX-Spam-Rules header.</summary>
    public string RulesSummary => Hits.Count == 0 ? "none" : string.Join(", ", Hits.Select(h => $"{h.Rule}({h.Score:0.#})"));
}

/// <summary>Everything the filter may look at for one inbound message.</summary>
public sealed record FilterContext(
    byte[] Raw,
    MimeMessage? Message,
    IPAddress RemoteIp,
    string Helo,
    string EnvelopeFrom,
    SpfOutcome Spf,
    DkimOutcome Dkim,
    DmarcOutcome Dmarc,
    IReadOnlyCollection<string> HostedDomains);

/// <summary>Result of a malware scan. Pluggable so a signature engine can be added later.</summary>
public sealed record ScanResult(bool Infected, bool PolicyViolation, string? Detail, decimal SuspicionScore);

public interface IMalwareScanner
{
    ScanResult Scan(MimeMessage message);
}
