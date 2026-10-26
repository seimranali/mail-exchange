using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MX.MailServer.Tls;

/// <summary>A loaded server certificate plus its intermediate chain, ready for SslStream.</summary>
public sealed record TlsCertificate(X509Certificate2 Leaf, SslStreamCertificateContext Context)
{
    public DateTimeOffset NotAfter => Leaf.NotAfter.ToUniversalTime();

    public IReadOnlyList<string> DnsNames =>
        Leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
}

/// <summary>
/// Holds the current TLS certificate. Listeners read <see cref="Current"/> for every new connection,
/// so a renewed certificate takes effect without restarting the mail server.
/// </summary>
public sealed class CertificateProvider
{
    private readonly MailServerOptions _options;
    private readonly ILogger<CertificateProvider> _logger;
    private volatile TlsCertificate? _current;

    public CertificateProvider(IOptions<MailServerOptions> options, ILogger<CertificateProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        Reload();
    }

    public TlsCertificate? Current => _current;

    /// <summary>(Re)loads the PFX at MailServer:CertificatePath, including intermediates. Keeps the old one on failure.</summary>
    public void Reload()
    {
        var path = _options.CertificatePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            _current = Load(path, _options.CertificatePassword);
            _logger.LogInformation("TLS certificate loaded: {Subject}, expires {NotAfter:u}", _current.Leaf.Subject, _current.NotAfter);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            _logger.LogError(ex, "Could not load TLS certificate from {Path}; keeping the previous one", path);
        }
    }

    public static TlsCertificate Load(string path, string? password)
    {
        var all = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password);
        var leaf = all.FirstOrDefault(c => c.HasPrivateKey) ?? throw new System.Security.Cryptography.CryptographicException("PFX contains no private key");
        var intermediates = new X509Certificate2Collection(all.Where(c => !ReferenceEquals(c, leaf)).ToArray());
        return new TlsCertificate(leaf, SslStreamCertificateContext.Create(leaf, intermediates, offline: true));
    }
}
