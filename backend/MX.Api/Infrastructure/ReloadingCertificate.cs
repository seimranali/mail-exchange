using System.Security.Cryptography.X509Certificates;

namespace MX.Api.Infrastructure;

/// <summary>
/// A PFX certificate re-read when the file changes (checked at most every 30 seconds), so the web
/// UI picks up ACME renewals without a restart. Null until the file exists.
/// </summary>
public sealed class ReloadingCertificate(string path, string? password)
{
    private readonly Lock _gate = new();
    private X509Certificate2? _current;
    private DateTime _loadedWriteTime;
    private DateTime _nextCheck;

    public X509Certificate2? Current
    {
        get
        {
            lock (_gate)
            {
                if (DateTime.UtcNow < _nextCheck) return _current;
                _nextCheck = DateTime.UtcNow.AddSeconds(30);
                try
                {
                    var writeTime = File.GetLastWriteTimeUtc(path);
                    if (!File.Exists(path) || writeTime == _loadedWriteTime) return _current;
                    _current = X509CertificateLoader.LoadPkcs12FromFile(path, password);
                    _loadedWriteTime = writeTime;
                }
                catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
                {
                    // Being rewritten or unreadable right now: keep serving the previous certificate.
                }
                return _current;
            }
        }
    }
}
