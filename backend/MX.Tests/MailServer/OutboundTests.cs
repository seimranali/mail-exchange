using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MX.Application;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Application.Mail;
using MX.Domain;
using MX.MailServer;
using MX.MailServer.Authentication;
using MX.MailServer.Outbound;
using MX.Persistence;
using MX.Security;
using MX.Tests.Infrastructure;

namespace MX.Tests.MailServer;

public sealed class OutboundUnitTests
{
    [Fact]
    public void Dot_stuffing_escapes_leading_dots_and_terminates()
    {
        var stuffed = Encoding.ASCII.GetString(SmtpDeliveryClient.DotStuff(Encoding.ASCII.GetBytes("a\r\n.b\r\n..c\r\nend")));
        Assert.Equal("a\r\n..b\r\n...c\r\nend\r\n.\r\n", stuffed);
    }

    [Fact]
    public void Retry_schedule_backs_off_and_caps()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), RetrySchedule.After(1));
        Assert.Equal(TimeSpan.FromMinutes(10), RetrySchedule.After(2));
        Assert.Equal(TimeSpan.FromHours(4), RetrySchedule.After(50));
    }

    [Fact]
    public void Key_protector_round_trips_and_binds_purpose()
    {
        var protector = new AesGcmKeyProtector(Options.Create(new SecretsOptions { KeyEncryptionKey = Convert.ToBase64String(new byte[32]) }));
        var secret = Encoding.UTF8.GetBytes("private key material");
        var protectedValue = protector.Protect(secret, "dkim:a");
        Assert.DoesNotContain("private", Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue)));
        Assert.Equal(secret, protector.Unprotect(protectedValue, "dkim:a"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(protectedValue, "dkim:b"));
        Assert.NotEqual(protectedValue, protector.Protect(secret, "dkim:a")); // random nonce
    }

    [Fact]
    public void Bounce_is_a_delivery_status_report()
    {
        var bounce = BounceBuilder.Build("mx.test", "sender@example.com", "Hi", "id@example.com",
            [("nobody@remote.test", "550 5.1.1 User unknown")], DateTimeOffset.UtcNow);
        var report = Assert.IsType<MultipartReport>(bounce.Body);
        Assert.Equal("delivery-status", report.ReportType);
        Assert.Equal("auto-replied", bounce.Headers["Auto-Submitted"]);
        Assert.EndsWith("@mx.test", bounce.MessageId);
        using var ms = new MemoryStream();
        bounce.WriteTo(ms);
        var text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("Final-Recipient: rfc822; nobody@remote.test", text);
        Assert.Contains("Status: 5.1.1", text);
    }
}

/// <summary>End-to-end outbound delivery against the real database and a fake remote MTA.</summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboundDeliveryTests(PostgresFixture db) : IAsyncLifetime
{
    private ServiceProvider _services = null!;
    private FakeRemoteSmtpServer _remote = null!;
    private readonly FakeDns _dns = new();
    private string _storeRoot = null!;

    public async Task InitializeAsync()
    {
        _remote = new FakeRemoteSmtpServer();
        _dns.AddMx("remote.test", "mx.remote.test").AddA("mx.remote.test", "127.0.0.1").AddMx("nullmx.test", "");
        _storeRoot = Path.Combine(Path.GetTempPath(), "mx-out-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Mx"] = db.AppConnectionString,
            ["MailServer:Hostname"] = "mx.test",
            ["MailServer:SmtpPort"] = "0",
            ["MailServer:DisableAuthenticationChecks"] = "true",
            ["MailStore:Root"] = _storeRoot,
            ["Outbound:RemotePort"] = _remote.Port.ToString(),
            ["Outbound:OpportunisticTls"] = "false",
            ["Secrets:KeyEncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["Jwt:SigningKey"] = "unused-in-these-tests-0123456789-0123456789",
        }).Build();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddApplication().AddPersistence(config).AddMxSecurity(config).AddSmtpServer(config).AddOutboundDelivery();
        services.AddSingleton<IDnsVerifier, NoDns>();
        services.Replace(ServiceDescriptor.Singleton<IMailDns>(_dns));
        _services = services.BuildServiceProvider();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _remote.DisposeAsync();
        if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, true);
    }

    private async Task<Guid> SendAsync(string from, string to, string subject)
    {
        await using var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(db.ExampleOrgId, null);
        return await scope.ServiceProvider.GetRequiredService<IOutboundMailer>().EnqueueAsync(
            new OutboundEnvelope(from, "Tester", [to], subject, "Hello\r\n.leading dot line\r\n", OutboundSource.TestMessage, null), default);
    }

    private Task<int> DeliverPassAsync() => _services.GetRequiredService<OutboundDeliveryWorker>().RunOnceAsync(default);

    private async Task<Domain.Entities.OutboundRecipient> RecipientAsync(Guid messageId)
    {
        await using var ctx = db.CreateContext(db.ExampleOrgId);
        return await ctx.OutboundRecipients.Include(r => r.OutboundMessage).SingleAsync(r => r.OutboundMessageId == messageId);
    }

    private async Task<DkimKeyDto> EnsureDkimKeyAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(db.ExampleOrgId, null);
        var appDb = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var domainId = await appDb.OrganizationDomains.Where(d => d.DomainName == "example.com").Select(d => d.Id).SingleAsync();
        var service = scope.ServiceProvider.GetRequiredService<DkimKeyService>();
        return await service.GetActiveAsync(domainId, default) ?? await service.RotateAsync(domainId, default);
    }

    [Fact]
    public async Task Delivers_dkim_signed_message_that_verifies()
    {
        var key = await EnsureDkimKeyAsync();
        _dns.AddTxt(key.DnsName, key.DnsValue);
        var subject = $"Signed {Guid.NewGuid():N}";

        var id = await SendAsync("jordan.lee@example.com", "friend@remote.test", subject);
        await DeliverPassAsync();

        var rcpt = await RecipientAsync(id);
        Assert.Equal(DeliveryStatus.Delivered, rcpt.Status);
        Assert.True(rcpt.OutboundMessage!.DkimSigned);
        Assert.Equal("mx.remote.test", rcpt.RemoteHost);
        Assert.False(File.Exists(Path.Combine(_storeRoot, rcpt.OutboundMessage.StoragePath))); // cleaned up

        var tx = Assert.Single(_remote.Transactions, t => t.Data.Contains(subject));
        Assert.Equal("jordan.lee@example.com", tx.MailFrom);
        Assert.Matches(@"Message-Id: <[0-9a-f]{32}@example\.com>", tx.Data); // sender's domain, not the machine name
        Assert.Contains("\r\n.leading dot line\r\n", tx.Data); // dot-stuffing round-trips

        // Verify the signature exactly as a receiving server would.
        var received = MimeMessage.Load(new MemoryStream(Encoding.Latin1.GetBytes(tx.Data)));
        var dkim = await new DkimChecker(_dns).CheckAsync(received, default);
        Assert.Equal(AuthCheckResult.Pass, dkim.Result);
        Assert.Contains("example.com", dkim.PassingDomains);
    }

    [Fact]
    public async Task Temporary_failure_defers_then_retries_successfully()
    {
        _remote.RcptReply = _ => "451 4.7.1 Greylisted, try later";
        var id = await SendAsync("jordan.lee@example.com", "busy@remote.test", $"Defer {Guid.NewGuid():N}");
        await DeliverPassAsync();

        var rcpt = await RecipientAsync(id);
        Assert.Equal(DeliveryStatus.Deferred, rcpt.Status);
        Assert.Equal(1, rcpt.Attempts);
        Assert.Contains("451", rcpt.LastResponse);
        Assert.InRange(rcpt.NextAttemptAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6));

        _remote.RcptReply = _ => "250 OK";
        await using (var ctx = db.CreateContext(db.ExampleOrgId))
            await ctx.OutboundRecipients.Where(r => r.Id == rcpt.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await DeliverPassAsync();

        rcpt = await RecipientAsync(id);
        Assert.Equal(DeliveryStatus.Delivered, rcpt.Status);
        Assert.Equal(2, rcpt.Attempts);
    }

    [Fact]
    public async Task Permanent_failure_bounces_to_local_sender_and_never_bounces_a_bounce()
    {
        _remote.RcptReply = r => r.StartsWith("ghost") ? "550 5.1.1 No such user" : "250 OK";
        var subject = $"Bounce me {Guid.NewGuid():N}";
        var id = await SendAsync("jordan.lee@example.com", "ghost@remote.test", subject);
        await DeliverPassAsync(); // remote rejects -> Failed, bounce queued
        await DeliverPassAsync(); // bounce delivered locally

        var rcpt = await RecipientAsync(id);
        Assert.Equal(DeliveryStatus.Failed, rcpt.Status);
        Assert.Contains("550 5.1.1", rcpt.LastResponse);

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var bounce = await ctx.OutboundMessages.Include(m => m.Recipients).SingleAsync(m => m.Source == OutboundSource.Bounce && m.Subject!.Contains(subject));
        Assert.Equal("", bounce.EnvelopeFrom); // null reverse-path
        Assert.Equal(DeliveryStatus.Delivered, Assert.Single(bounce.Recipients).Status);
        Assert.True(await ctx.MailMessages.AnyAsync(m => m.Subject!.Contains("Undelivered Mail Returned to Sender") && m.Subject.Contains(subject)));
    }

    [Fact]
    public async Task Mail_to_hosted_domains_is_delivered_locally()
    {
        var subject = $"Internal {Guid.NewGuid():N}";
        var before = _remote.Transactions.Count;
        var id = await SendAsync("jordan.lee@example.com", "priya.shah@example.com", subject);
        await DeliverPassAsync();

        Assert.Equal(DeliveryStatus.Delivered, (await RecipientAsync(id)).Status);
        Assert.Equal(before, _remote.Transactions.Count); // never left the server
        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var msg = await ctx.MailMessages.Include(m => m.Mailbox).SingleAsync(m => m.Subject == subject);
        Assert.Equal("priya.shah@example.com", msg.Mailbox!.EmailAddress);
    }

    [Fact]
    public async Task Null_mx_domain_fails_permanently()
    {
        var id = await SendAsync("jordan.lee@example.com", "user@nullmx.test", $"NullMx {Guid.NewGuid():N}");
        await DeliverPassAsync();
        var rcpt = await RecipientAsync(id);
        Assert.Equal(DeliveryStatus.Failed, rcpt.Status);
        Assert.Contains("null MX", rcpt.LastResponse);
    }

    [Fact]
    public async Task Cannot_send_as_a_domain_the_tenant_does_not_own()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => SendAsync("ceo@remote.test", "victim@remote.test", "spoof"));
    }

    [Fact]
    public async Task Dkim_private_key_is_stored_encrypted()
    {
        await EnsureDkimKeyAsync();
        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var stored = await ctx.DkimKeys.FirstAsync(k => k.IsActive);
        // PKCS#8 RSA keys begin "MII..." in base64; the stored value must not be a usable key.
        Assert.DoesNotContain("PRIVATE KEY", stored.EncryptedPrivateKey);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
        {
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.EncryptedPrivateKey), out _);
        });
    }

    private sealed class NoDns : IDnsVerifier
    {
        public Task<DnsCheckResult> CheckAsync(string domainName, string verificationToken, string? dkimSelector, CancellationToken ct) =>
            Task.FromResult(new DnsCheckResult(false, false, false, false, false));
    }
}
