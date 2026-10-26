using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MX.MailServer;
using MX.MailServer.Acme;
using MX.MailServer.Smtp;
using MX.MailServer.Tls;
using MX.Persistence;
using MX.Security;
using MX.Tests.Infrastructure;

namespace MX.Tests.MailServer;

/// <summary>
/// Full ACME issuance against Pebble, Let's Encrypt's official test CA, running in Docker.
/// Pebble is told to treat challenges as valid (it cannot reach our test machine on port 80),
/// so this exercises the protocol: JWS, nonces, account, order, finalize, chain download, install.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AcmeTests(PostgresFixture db) : IAsyncLifetime
{
    private IContainer _pebble = null!;
    private string _dir = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _pebble = new ContainerBuilder("ghcr.io/letsencrypt/pebble:2.7.0")
            .WithEnvironment("PEBBLE_VA_ALWAYS_VALID", "1")
            .WithEnvironment("PEBBLE_VA_NOSLEEP", "1")
            .WithPortBinding(14000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening on"))
            .Build();
        await _pebble.StartAsync();

        _dir = Path.Combine(Path.GetTempPath(), "mx-acme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Mx"] = db.AppConnectionString,
            ["MailServer:Hostname"] = "mail.example.test",
            ["MailServer:ListenAddress"] = "127.0.0.1",
            ["MailServer:SmtpPort"] = "-1",
            ["MailServer:SubmissionPort"] = "-1",
            ["MailServer:SubmissionTlsPort"] = "0",
            ["MailServer:CertificatePath"] = Path.Combine(_dir, "mail.pfx"),
            ["MailStore:Root"] = Path.Combine(_dir, "store"),
            ["Acme:Enabled"] = "true",
            ["Acme:DirectoryUrl"] = $"https://{_pebble.Hostname}:{_pebble.GetMappedPublicPort(14000)}/dir",
            ["Acme:AcceptTermsOfService"] = "true",
            ["Acme:Email"] = "admin@example.test",
            ["Acme:Domains:0"] = "mail.example.test",
            ["Acme:HttpPort"] = "0",
            ["Acme:ListenAddress"] = "127.0.0.1",
            ["Acme:AccountKeyPath"] = Path.Combine(_dir, "account.key"),
            ["Acme:AcceptInvalidServerCertificate"] = "true",
            ["Secrets:KeyEncryptionKey"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray()),
            ["Jwt:SigningKey"] = "unused-in-these-tests-0123456789-0123456789",
        }).Build();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddPersistence(config).AddMxSecurity(config).AddSmtpServer(config).AddAcmeCertificates();
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _pebble.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Issues_installs_and_serves_a_certificate()
    {
        var provider = _services.GetRequiredService<CertificateProvider>();
        var manager = _services.GetRequiredService<CertificateManager>();
        var challenges = _services.GetRequiredService<Http01ChallengeServer>();
        await challenges.StartAsync(default);
        Assert.Null(provider.Current);
        Assert.True(manager.NeedsCertificate(out var reason));
        Assert.Equal("no certificate", reason);

        await manager.IssueAsync(default);

        var cert = provider.Current;
        Assert.NotNull(cert);
        Assert.Contains("mail.example.test", cert.DnsNames);
        Assert.Contains("Pebble", cert.Leaf.Issuer);
        Assert.True(cert.Leaf.HasPrivateKey);
        // Pebble issues ~6-day certificates (like Let's Encrypt's short-lived profile): renewal is
        // scheduled at two-thirds of the lifetime, not at a fixed 30 days.
        Assert.False(manager.NeedsCertificate(out var after), $"{after}; NotAfter={cert.NotAfter:u}");
        var renewAt = CertificateManager.RenewalTime(cert, 30);
        Assert.InRange(renewAt, DateTimeOffset.UtcNow.AddDays(2), cert.NotAfter.AddDays(-1));
        Assert.True(File.Exists(Path.Combine(_dir, "mail.pfx")));

        // The account key on disk is encrypted, not a usable PKCS#8 key.
        var stored = await File.ReadAllTextAsync(Path.Combine(_dir, "account.key"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
        {
            using var ec = System.Security.Cryptography.ECDsa.Create();
            ec.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
        });

        // A second order reuses the same ACME account (existing key -> 200 from newAccount).
        await manager.IssueAsync(default);
        Assert.NotEqual(cert.Leaf.Thumbprint, provider.Current!.Leaf.Thumbprint);

        // The listener picks up the certificate for new connections without a restart.
        var listener = _services.GetRequiredService<SmtpListener>();
        await listener.StartAsync(default);
        await listener.Started;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", listener.SubmissionTlsEndpoint!.Port);
        X509Certificate? served = null;
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, c, _, _) => { served = c; return true; });
        await ssl.AuthenticateAsClientAsync("mail.example.test");
        Assert.Equal(provider.Current!.Leaf.Thumbprint, new X509Certificate2(served!).Thumbprint);
        await listener.StopAsync(default);
        await challenges.StopAsync(default);
    }

    [Fact]
    public async Task Http01_responder_serves_published_tokens_only()
    {
        var challenges = _services.GetRequiredService<Http01ChallengeServer>();
        await challenges.StartAsync(default);
        await challenges.Started;
        challenges.Publish("tok123", "tok123.thumb");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{challenges.Port}") };

        Assert.Equal("tok123.thumb", await http.GetStringAsync("/.well-known/acme-challenge/tok123"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/.well-known/acme-challenge/other")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/")).StatusCode);
        challenges.Remove("tok123");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/.well-known/acme-challenge/tok123")).StatusCode);
        await challenges.StopAsync(default);
    }
}
