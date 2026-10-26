using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MX.Domain;
using MX.MailServer;
using MX.MailServer.Authentication;
using MX.MailServer.Smtp;
using MX.Persistence;
using MX.Security;
using MX.Tests.Infrastructure;

namespace MX.Tests.MailServer;

/// <summary>Authenticated submission (587 STARTTLS / 465 implicit TLS) end to end.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SubmissionTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Password = "Correct-Horse-Battery-9";
    private ServiceProvider _services = null!;
    private SmtpListener _listener = null!;
    private string _storeRoot = null!;
    private string _certPath = null!;

    public async Task InitializeAsync()
    {
        _storeRoot = Path.Combine(Path.GetTempPath(), "mx-sub-" + Guid.NewGuid().ToString("N"));
        _certPath = Path.Combine(Path.GetTempPath(), $"mx-sub-{Guid.NewGuid():N}.pfx");
        using (var key = RSA.Create(2048))
        using (var cert = new CertificateRequest("CN=mx.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
            await File.WriteAllBytesAsync(_certPath, cert.Export(X509ContentType.Pfx, "test"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Mx"] = db.AppConnectionString,
            ["MailServer:Hostname"] = "mx.test",
            ["MailServer:ListenAddress"] = "127.0.0.1",
            ["MailServer:SmtpPort"] = "-1",
            ["MailServer:SubmissionPort"] = "0",
            ["MailServer:SubmissionTlsPort"] = "0",
            ["MailServer:CertificatePath"] = _certPath,
            ["MailServer:CertificatePassword"] = "test",
            ["MailServer:MaxSubmissionsPerHour"] = "2",
            ["MailStore:Root"] = _storeRoot,
            ["Secrets:KeyEncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["Jwt:SigningKey"] = "unused-in-these-tests-0123456789-0123456789",
        }).Build();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddPersistence(config).AddMxSecurity(config).AddSmtpServer(config);
        services.Replace(ServiceDescriptor.Singleton<IMailDns, FakeDns>());
        _services = services.BuildServiceProvider();

        // Give the test mailboxes a client password.
        var hash = new Argon2PasswordHasher().Hash(Password);
        await using (var ctx = db.CreateContext(db.ExampleOrgId))
            await ctx.Mailboxes.Where(m => m.EmailAddress == "priya.shah@example.com" || m.EmailAddress == "elena.garcia@example.com" || m.EmailAddress == "marcus.chen@example.com" || m.EmailAddress == "jordan.lee@example.com")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.PasswordHash, hash));

        _listener = _services.GetRequiredService<SmtpListener>();
        await _listener.StartAsync(CancellationToken.None);
        await _listener.Started;
    }

    public async Task DisposeAsync()
    {
        await _listener.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
        if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, true);
        File.Delete(_certPath);
    }

    private static string Plain(string user, string pass) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{user}\0{pass}"));

    private async Task<SmtpTestClient> LoggedInAsync(string user)
    {
        var c = await SmtpTestClient.ConnectAsync(_listener.SubmissionEndpoint!.Port);
        await c.SendAsync("EHLO laptop");
        await c.SendAsync("STARTTLS");
        await c.UpgradeToTlsAsync();
        await c.SendAsync("EHLO laptop");
        Assert.StartsWith("235", await c.SendAsync($"AUTH PLAIN {Plain(user, Password)}"));
        return c;
    }

    [Fact]
    public async Task Auth_is_only_offered_and_accepted_over_tls()
    {
        await using var c = await SmtpTestClient.ConnectAsync(_listener.SubmissionEndpoint!.Port);
        var ehlo = await c.SendAsync("EHLO laptop");
        Assert.Contains("STARTTLS", ehlo);
        Assert.DoesNotContain("AUTH", ehlo);
        Assert.StartsWith("538", await c.SendAsync($"AUTH PLAIN {Plain("priya.shah@example.com", Password)}"));
        Assert.StartsWith("530", await c.SendAsync("MAIL FROM:<priya.shah@example.com>"));

        await c.SendAsync("STARTTLS");
        await c.UpgradeToTlsAsync();
        Assert.Contains("AUTH PLAIN LOGIN", await c.SendAsync("EHLO laptop"));
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_logged()
    {
        await using var c = await SmtpTestClient.ConnectAsync(_listener.SubmissionEndpoint!.Port);
        await c.SendAsync("EHLO laptop");
        await c.SendAsync("STARTTLS");
        await c.UpgradeToTlsAsync();
        await c.SendAsync("EHLO laptop");
        Assert.StartsWith("535 5.7.8", await c.SendAsync($"AUTH PLAIN {Plain("priya.shah@example.com", "wrong")}"));

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        Assert.True(await ctx.AuthenticationEvents.AnyAsync(e => e.AttemptedEmail == "priya.shah@example.com" && e.Outcome == AuthenticationOutcome.InvalidCredentials));
    }

    [Fact]
    public async Task Submits_mail_queues_it_and_files_a_sent_copy()
    {
        var subject = $"Submitted {Guid.NewGuid():N}";
        await using var c = await LoggedInAsync("priya.shah@example.com");
        Assert.StartsWith("553 5.7.1", await c.SendAsync("MAIL FROM:<jordan.lee@example.com>")); // someone else's address
        Assert.StartsWith("250", await c.SendAsync("MAIL FROM:<priya.shah@example.com>"));
        Assert.StartsWith("250", await c.SendAsync("RCPT TO:<partner@remote.test>"));     // relaying is allowed once authenticated
        Assert.StartsWith("250", await c.SendAsync("RCPT TO:<hidden@remote.test>"));
        await c.SendAsync("DATA");
        var reply = await c.SendAsync($"From: Priya <priya.shah@example.com>\r\nTo: partner@remote.test\r\nBcc: hidden@remote.test\r\nSubject: {subject}\r\n\r\nHi\r\n.");
        Assert.StartsWith("250 2.0.0", reply);

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var outbound = await ctx.OutboundMessages.Include(m => m.Recipients).SingleAsync(m => m.Subject == subject);
        Assert.Equal(OutboundSource.Submission, outbound.Source);
        Assert.Equal("priya.shah@example.com", outbound.EnvelopeFrom);
        Assert.Equal(2, outbound.Recipients.Count);
        var queued = await File.ReadAllTextAsync(Path.Combine(_storeRoot, outbound.StoragePath));
        Assert.DoesNotContain("Bcc:", queued);                       // Bcc never leaves the server
        Assert.Contains("with ESMTPSA", queued);
        Assert.DoesNotContain("127.0.0.1", queued);                  // client IP not disclosed

        var sent = await ctx.MailMessages.Include(m => m.Mailbox).SingleAsync(m => m.Subject == subject);
        Assert.Equal(MailFolder.Sent, sent.Folder);
        Assert.Equal("priya.shah@example.com", sent.Mailbox!.EmailAddress);
    }

    [Fact]
    public async Task Forged_from_header_is_rejected()
    {
        await using var c = await LoggedInAsync("elena.garcia@example.com");
        await c.SendAsync("MAIL FROM:<elena.garcia@example.com>");
        await c.SendAsync("RCPT TO:<partner@remote.test>");
        await c.SendAsync("DATA");
        Assert.StartsWith("550 5.7.1", await c.SendAsync("From: CEO <ceo@example.com>\r\nSubject: wire transfer\r\n\r\nPay now\r\n."));
    }

    [Fact]
    public async Task Alias_may_be_used_as_sender()
    {
        await using var c = await LoggedInAsync("jordan.lee@example.com");
        Assert.StartsWith("250", await c.SendAsync("MAIL FROM:<support@example.com>")); // alias of jordan.lee in seed data
    }

    [Fact]
    public async Task Auth_login_works_over_implicit_tls()
    {
        await using var c = await SmtpTestClient.ConnectImplicitTlsAsync(_listener.SubmissionTlsEndpoint!.Port);
        Assert.StartsWith("220", c.Greeting);
        var ehlo = await c.SendAsync("EHLO laptop");
        Assert.Contains("AUTH PLAIN LOGIN", ehlo);
        Assert.DoesNotContain("STARTTLS", ehlo);
        Assert.StartsWith("334 VXNlcm5hbWU6", await c.SendAsync("AUTH LOGIN"));
        Assert.StartsWith("334 UGFzc3dvcmQ6", await c.SendAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("priya.shah@example.com"))));
        Assert.StartsWith("235", await c.SendAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(Password))));
    }

    [Fact]
    public async Task Repeated_failures_from_one_ip_are_throttled()
    {
        for (var i = 0; i < 5; i++)
        {
            await using var c = await SmtpTestClient.ConnectImplicitTlsAsync(_listener.SubmissionTlsEndpoint!.Port);
            await c.SendAsync("EHLO laptop");
            Assert.StartsWith("535", await c.SendAsync($"AUTH PLAIN {Plain("marcus.chen@example.com", $"guess{i}")}"));
        }
        await using var blocked = await SmtpTestClient.ConnectImplicitTlsAsync(_listener.SubmissionTlsEndpoint!.Port);
        await blocked.SendAsync("EHLO laptop");
        // Even the correct password is refused while throttled.
        Assert.StartsWith("454 4.7.0", await blocked.SendAsync($"AUTH PLAIN {Plain("marcus.chen@example.com", Password)}"));
    }

    [Fact]
    public async Task Hourly_sending_limit_is_enforced()
    {
        // MaxSubmissionsPerHour = 2 in this test configuration; elena has not sent successfully yet.
        for (var i = 0; i < 3; i++)
        {
            await using var c = await LoggedInAsync("elena.garcia@example.com");
            await c.SendAsync("MAIL FROM:<elena.garcia@example.com>");
            await c.SendAsync("RCPT TO:<partner@remote.test>");
            await c.SendAsync("DATA");
            var reply = await c.SendAsync($"From: elena.garcia@example.com\r\nSubject: burst {i}\r\n\r\nx\r\n.");
            Assert.StartsWith(i < 2 ? "250" : "451 4.7.1", reply);
        }
    }
}
