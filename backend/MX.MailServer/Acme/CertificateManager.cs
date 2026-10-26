using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MX.Application.Abstractions;
using MX.MailServer.Tls;

namespace MX.MailServer.Acme;

/// <summary>
/// Obtains and renews the mail server's TLS certificate from an ACME CA (Let's Encrypt), writes it
/// to MailServer:CertificatePath and hot-reloads it into the listeners.
/// </summary>
public sealed class CertificateManager(
    IOptions<AcmeOptions> acmeOptions,
    IOptions<MailServerOptions> serverOptions,
    CertificateProvider provider,
    Http01ChallengeServer challenges,
    IServiceProvider services,
    ILogger<CertificateManager> logger) : BackgroundService
{
    private const string AccountKeyPurpose = "acme-account-key";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = acmeOptions.Value;
        if (!o.Enabled) return;
        if (o.Domains.Length == 0 || string.IsNullOrWhiteSpace(serverOptions.Value.CertificatePath))
        {
            logger.LogError("ACME is enabled but Acme:Domains or MailServer:CertificatePath is not configured");
            return;
        }
        await challenges.Started;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(12);
            try
            {
                if (NeedsCertificate(out var reason))
                {
                    logger.LogInformation("Requesting certificate for {Domains}: {Reason}", string.Join(", ", o.Domains), reason);
                    await IssueAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Certificate issuance failed; retrying in 1 hour");
                delay = TimeSpan.FromHours(1);
            }
            await Task.Delay(delay, stoppingToken);
        }
    }

    public bool NeedsCertificate(out string reason)
    {
        var current = provider.Current;
        var o = acmeOptions.Value;
        if (current is null) { reason = "no certificate"; return true; }
        if (DateTimeOffset.UtcNow >= RenewalTime(current, o.RenewBeforeDays)) { reason = $"expires {current.NotAfter:u}"; return true; }
        var missing = o.Domains.Where(d => !current.DnsNames.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0) { reason = $"certificate does not cover {string.Join(", ", missing)}"; return true; }
        reason = "";
        return false;
    }

    /// <summary>
    /// Renew once a third of the lifetime remains (Let's Encrypt's guidance), but never later than
    /// RenewBeforeDays before expiry. Works for 90-day and short-lived (6-day) certificates alike.
    /// </summary>
    public static DateTimeOffset RenewalTime(TlsCertificate certificate, int renewBeforeDays)
    {
        var lifetime = certificate.NotAfter - certificate.Leaf.NotBefore.ToUniversalTime();
        var window = TimeSpan.FromTicks(Math.Min(lifetime.Ticks / 3, TimeSpan.FromDays(renewBeforeDays).Ticks));
        return certificate.NotAfter - window;
    }

    /// <summary>Runs one full ACME order and installs the resulting certificate.</summary>
    public async Task IssueAsync(CancellationToken ct)
    {
        var o = acmeOptions.Value;
        using var accountKey = LoadOrCreateAccountKey(o.AccountKeyPath);
        using var handler = new HttpClientHandler();
        if (o.AcceptInvalidServerCertificate) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MX-mail-server/1.0");

        var client = new AcmeClient(http, accountKey, new Uri(o.DirectoryUrl));
        await client.InitializeAsync(ct);
        await client.RegisterAccountAsync(o.Email, o.AcceptTermsOfService, ct);
        var (orderUrl, order) = await client.NewOrderAsync(o.Domains, ct);

        foreach (var authzUrl in order["authorizations"]!.AsArray().Select(a => a!.GetValue<string>()))
        {
            var authz = await client.GetAsync(authzUrl, ct);
            if (authz["status"]?.GetValue<string>() == "valid") continue;
            var challenge = authz["challenges"]!.AsArray().FirstOrDefault(c => c!["type"]!.GetValue<string>() == "http-01")
                ?? throw new AcmeException($"No http-01 challenge offered for {authz["identifier"]?["value"]}");
            var token = challenge["token"]!.GetValue<string>();
            challenges.Publish(token, client.KeyAuthorization(token));
            try
            {
                await client.RespondToChallengeAsync(challenge["url"]!.GetValue<string>(), ct);
                var result = await client.PollAsync(authzUrl, ct);
                if (result["status"]?.GetValue<string>() != "valid")
                    throw new AcmeException($"Validation failed for {authz["identifier"]?["value"]}: {result["challenges"]?.ToJsonString()}");
            }
            finally
            {
                challenges.Remove(token);
            }
        }

        // RSA keys keep compatibility with older mail clients.
        using var certKey = RSA.Create(2048);
        var request = new CertificateRequest($"CN={o.Domains[0]}", certKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var d in o.Domains) san.AddDnsName(d);
        request.CertificateExtensions.Add(san.Build());

        await client.FinalizeAsync(order["finalize"]!.GetValue<string>(), request.CreateSigningRequest(), ct);
        var final = await client.PollAsync(orderUrl, ct);
        if (final["status"]?.GetValue<string>() != "valid" || final["certificate"] is null)
            throw new AcmeException($"Order did not complete: {final.ToJsonString()}");
        var chainPem = await client.DownloadCertificateAsync(final["certificate"]!.GetValue<string>(), ct);

        Install(chainPem, certKey);
        logger.LogInformation("Installed certificate for {Domains}, expires {NotAfter:u}", string.Join(", ", o.Domains), provider.Current?.NotAfter);
    }

    /// <summary>Writes leaf + intermediates + key as PFX (atomically) and reloads it.</summary>
    private void Install(string chainPem, RSA key)
    {
        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(chainPem);
        if (chain.Count == 0) throw new AcmeException("CA returned an empty certificate chain");
        var leaf = chain[0].CopyWithPrivateKey(key);
        var bundle = new X509Certificate2Collection { leaf };
        for (var i = 1; i < chain.Count; i++) bundle.Add(chain[i]);

        var o = serverOptions.Value;
        var bytes = bundle.Export(X509ContentType.Pkcs12, o.CertificatePassword)!;
        var path = Path.GetFullPath(o.CertificatePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        provider.Reload();
    }

    /// <summary>The ACME account key is stored encrypted with the installation's key-encryption key when available.</summary>
    private ECDsa LoadOrCreateAccountKey(string path)
    {
        var protector = services.GetService<IKeyProtector>();
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            var stored = File.ReadAllText(path).Trim();
            var pkcs8 = protector is not null ? protector.Unprotect(stored, AccountKeyPurpose) : Convert.FromBase64String(stored);
            try
            {
                key.ImportPkcs8PrivateKey(pkcs8, out _);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
            return key;
        }

        var created = key.ExportPkcs8PrivateKey();
        try
        {
            var text = protector is not null ? protector.Protect(created, AccountKeyPurpose) : Convert.ToBase64String(created);
            if (protector is null) logger.LogWarning("No key protector available; ACME account key stored unencrypted at {Path}", path);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(created);
        }
        return key;
    }
}
