using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MX.MailServer.Acme;
using MX.MailServer.Tls;

namespace MX.Api.Controllers;

public sealed record MailCertificateDto(
    bool Issued, string? Hostname, IReadOnlyList<string> DnsNames, string? Issuer,
    DateTimeOffset? NotBefore, DateTimeOffset? NotAfter, DateTimeOffset? RenewsAt, bool AutoRenew, IReadOnlyList<int> Ports);

/// <summary>Installation-level facts the admin UI shows, e.g. the TLS certificate the mail server serves.</summary>
[ApiController]
[Route("api/system")]
[Authorize(Roles = RoleSets.Any)]
public sealed class SystemController(IConfiguration configuration, IMemoryCache cache, ILogger<SystemController> logger) : ControllerBase
{
    /// <summary>
    /// The certificate the mail host uses for SMTP/IMAP. It belongs to the server's host name and
    /// covers every hosted domain. Read from the same file the mail host obtained via ACME.
    /// </summary>
    [HttpGet("mail-certificate")]
    public MailCertificateDto MailCertificate() => cache.GetOrCreate("mail-certificate", entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
        var path = configuration["MailServer:CertificatePath"];
        var hostname = configuration["MailServer:Hostname"];
        int[] ports = [993, 465, 587, 143, 25];
        // The installer gives the API the same Acme:Enabled flag as the mail host (which does the renewing).
        var autoRenew = configuration.GetValue<bool>("Acme:Enabled");
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return new MailCertificateDto(false, hostname, [], null, null, null, null, autoRenew, ports);
        try
        {
            var cert = CertificateProvider.Load(path, configuration["MailServer:CertificatePassword"]);
            var renewBefore = configuration.GetValue("Acme:RenewBeforeDays", 30);
            return new MailCertificateDto(true, hostname, cert.DnsNames, cert.Leaf.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: true),
                cert.Leaf.NotBefore.ToUniversalTime(), cert.NotAfter, CertificateManager.RenewalTime(cert, renewBefore), autoRenew, ports);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read mail certificate at {Path}", path);
            return new MailCertificateDto(false, hostname, [], null, null, null, null, autoRenew, ports);
        }
    })!;
}
