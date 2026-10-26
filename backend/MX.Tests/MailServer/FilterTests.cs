using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using MX.Application;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Application.Security;
using MX.Domain;
using MX.MailServer;
using MX.MailServer.Authentication;
using MX.MailServer.Filtering;
using MX.MailServer.Smtp;
using MX.Persistence;
using MX.Security;
using MX.Tests.Infrastructure;

namespace MX.Tests.MailServer;

public sealed class ContentFilterTests
{
    private static readonly string[] Hosted = ["examplecorp.net", "example.com"];
    private static readonly SpfOutcome SpfPass = new(AuthCheckResult.Pass, "sender.test", "");
    private static readonly DkimOutcome NoDkim = new(AuthCheckResult.None, [], "");
    private static readonly DmarcOutcome NoDmarc = new(AuthCheckResult.None, DmarcPolicy.None, "", "");

    private static ContentFilter Filter(FakeDns? dns = null) =>
        new(dns ?? new FakeDns(), new AttachmentScanner(Options.Create(new FilterOptions())), Options.Create(new FilterOptions()), NullLogger<ContentFilter>.Instance);

    private static MimeMessage Message(string from = "Ana <ana@sender.test>", string subject = "Meeting notes", string text = "See you tomorrow.",
        string? html = null, params (string Name, byte[] Data)[] attachments)
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse("alice@examplecorp.net"));
        m.Subject = subject;
        m.MessageId = "abc@sender.test";
        m.Date = DateTimeOffset.UtcNow;
        var builder = new BodyBuilder { TextBody = text, HtmlBody = html };
        foreach (var (name, data) in attachments) builder.Attachments.Add(name, data);
        m.Body = builder.ToMessageBody();
        return m;
    }

    private static Task<FilterVerdict> Evaluate(ContentFilter f, MimeMessage m, string ip = "198.51.100.7", SpfOutcome? spf = null, DkimOutcome? dkim = null, DmarcOutcome? dmarc = null) =>
        f.EvaluateAsync(new FilterContext([], m, IPAddress.Parse(ip), "mail.sender.test", "ana@sender.test", spf ?? SpfPass, dkim ?? NoDkim, dmarc ?? NoDmarc, Hosted), default);

    [Fact]
    public async Task Clean_authenticated_mail_is_delivered()
    {
        var v = await Evaluate(Filter(), Message());
        Assert.Equal(FilterAction.Deliver, v.Action);
        Assert.Equal(0, v.Score);
    }

    [Theory]
    [InlineData("invoice.exe")]
    [InlineData("invoice.pdf.js")]
    [InlineData("setup.SCR")]
    public async Task Blocked_attachment_types_are_quarantined_as_malware(string fileName)
    {
        var v = await Evaluate(Filter(), Message(attachments: (fileName, [1, 2, 3])));
        Assert.Equal(FilterAction.Quarantine, v.Action);
        Assert.Equal(QuarantineReason.Malware, v.Reason);
    }

    [Fact]
    public async Task Executable_renamed_to_pdf_is_detected()
    {
        var v = await Evaluate(Filter(), Message(attachments: ("invoice.pdf", [(byte)'M', (byte)'Z', 0x90, 0, 3, 0])));
        Assert.Equal(QuarantineReason.Malware, v.Reason);
        Assert.Contains("disguised", v.Hits[0].Detail);
    }

    [Fact]
    public async Task Eicar_test_file_is_detected()
    {
        var eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        var v = await Evaluate(Filter(), Message(attachments: ("readme.txt", eicar)));
        Assert.Equal(QuarantineReason.Malware, v.Reason);
    }

    [Fact]
    public async Task Executable_inside_zip_is_detected_but_clean_zip_passes()
    {
        static byte[] Zip(string entryName)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var w = new StreamWriter(zip.CreateEntry(entryName).Open());
                w.Write("content");
            }
            return ms.ToArray();
        }
        Assert.Equal(QuarantineReason.Malware, (await Evaluate(Filter(), Message(attachments: ("docs.zip", Zip("docs/run.exe"))))).Reason);
        Assert.Equal(FilterAction.Deliver, (await Evaluate(Filter(), Message(attachments: ("docs.zip", Zip("docs/report.pdf"))))).Action);
    }

    [Fact]
    public async Task Macro_documents_are_held_as_policy()
    {
        var v = await Evaluate(Filter(), Message(attachments: ("budget.xlsm", [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])));
        Assert.Equal(QuarantineReason.Policy, v.Reason);
    }

    [Fact]
    public async Task Dnsbl_listing_scores_and_refusal_codes_are_ignored()
    {
        var dns = new FakeDns().AddA("7.100.51.198.zen.spamhaus.org", "127.0.0.2").AddA("8.100.51.198.zen.spamhaus.org", "127.255.255.254");
        var listed = await Evaluate(Filter(dns), Message(), ip: "198.51.100.7");
        Assert.Contains(listed.Hits, h => h.Rule == "DNSBL");
        Assert.Equal(FilterAction.Junk, listed.Action); // 6 points

        var refused = await Evaluate(Filter(dns), Message(), ip: "198.51.100.8");
        Assert.DoesNotContain(refused.Hits, h => h.Rule == "DNSBL");
        Assert.DoesNotContain((await Evaluate(Filter(dns), Message(), ip: "10.0.0.5")).Hits, h => h.Rule == "DNSBL"); // private range never queried
    }

    [Fact]
    public async Task Unauthenticated_mail_claiming_our_domain_is_phishing()
    {
        var none = new SpfOutcome(AuthCheckResult.SoftFail, "examplecorp.net", "");
        var v = await Evaluate(Filter(), Message(from: "CEO <ceo@examplecorp.net>", subject: "Urgent payment",
            text: "Urgent action required: update your payment today.", html: "<a href=\"http://203.0.113.9/pay\">https://examplecorp.net/pay</a>"), spf: none);
        Assert.Equal(FilterAction.Quarantine, v.Action);
        Assert.Equal(QuarantineReason.Phishing, v.Reason);
        Assert.Contains(v.Hits, h => h.Rule == "SPOOFED_LOCAL_DOMAIN");
        Assert.Contains(v.Hits, h => h.Rule == "LINK_TEXT_MISMATCH");
        Assert.Contains(v.Hits, h => h.Rule == "URL_IP_ADDRESS");
    }

    [Theory]
    [InlineData("exampIecorp.net", true)]
    [InlineData("examp1ecorp.net", true)]
    [InlineData("examplecorpp.net", true)]
    [InlineData("examplecorp.net", false)]
    [InlineData("google.com", false)]
    public void Lookalike_domains(string candidate, bool expected) =>
        Assert.Equal(expected, ContentFilter.IsLookalike(candidate.ToLowerInvariant() == candidate ? candidate : candidate, "examplecorp.net"));

    [Fact]
    public async Task Content_heuristics_alone_send_spam_to_junk()
    {
        var fail = new SpfOutcome(AuthCheckResult.Fail, "sender.test", "");
        var v = await Evaluate(Filter(), Message(subject: "YOU HAVE WON THE LOTTERY!!!", text: "Claim your prize at our casino, 100% free, act now"), spf: fail);
        Assert.Equal(FilterAction.Junk, v.Action);
        Assert.Contains(v.Hits, h => h.Rule == "SPAM_PHRASES");
    }

    [Fact]
    public async Task Spam_from_a_listed_ip_is_quarantined()
    {
        var dns = new FakeDns().AddA("7.100.51.198.zen.spamhaus.org", "127.0.0.4");
        var fail = new SpfOutcome(AuthCheckResult.Fail, "sender.test", "");
        var v = await Evaluate(Filter(dns), Message(subject: "YOU HAVE WON THE LOTTERY!!!", text: "Claim your prize, act now"), spf: fail);
        Assert.Equal(FilterAction.Quarantine, v.Action);
        Assert.Equal(QuarantineReason.Spam, v.Reason);
    }
}

/// <summary>Filtering through the real SMTP server, quarantine storage and admin release.</summary>
[Collection(PostgresCollection.Name)]
public sealed class QuarantineFlowTests(PostgresFixture db) : IAsyncLifetime
{
    private ServiceProvider _services = null!;
    private SmtpListener _listener = null!;
    private string _storeRoot = null!;

    public async Task InitializeAsync()
    {
        _storeRoot = Path.Combine(Path.GetTempPath(), "mx-q-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Mx"] = db.AppConnectionString,
            ["MailServer:Hostname"] = "mx.test",
            ["MailServer:ListenAddress"] = "127.0.0.1",
            ["MailServer:SmtpPort"] = "0",
            ["MailServer:SubmissionPort"] = "-1",
            ["MailServer:SubmissionTlsPort"] = "-1",
            ["MailServer:DisableAuthenticationChecks"] = "true",
            ["MailStore:Root"] = _storeRoot,
            ["Secrets:KeyEncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["Jwt:SigningKey"] = "unused-in-these-tests-0123456789-0123456789",
        }).Build();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddApplication().AddPersistence(config).AddMxSecurity(config).AddSmtpServer(config);
        services.AddSingleton<IDnsVerifier>(new NoDns());
        services.AddSingleton<IMailServerProviderFactory>(new NoProviders());
        services.Replace(ServiceDescriptor.Singleton<IMailDns, FakeDns>());
        _services = services.BuildServiceProvider();
        _listener = _services.GetRequiredService<SmtpListener>();
        await _listener.StartAsync(default);
        await _listener.Started;
    }

    public async Task DisposeAsync()
    {
        await _listener.StopAsync(default);
        await _services.DisposeAsync();
        if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, true);
    }

    private async Task<string> SendAsync(string to, string subject, string bodyPart, string extraHeaders = "")
    {
        await using var c = await SmtpTestClient.ConnectAsync(_listener.LocalEndpoint!.Port);
        await c.SendAsync("EHLO mail.sender.test");
        await c.SendAsync("MAIL FROM:<ana@sender.test>");
        await c.SendAsync($"RCPT TO:<{to}>");
        await c.SendAsync("DATA");
        return await c.SendAsync($"From: ana@sender.test\r\nTo: {to}\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@sender.test>\r\nDate: {DateTimeOffset.UtcNow:r}\r\n{extraHeaders}MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n--b1\r\nContent-Type: text/plain\r\n\r\nHello\r\n{bodyPart}--b1--\r\n.");
    }

    private static string Attachment(string name, string base64) =>
        $"--b1\r\nContent-Type: application/octet-stream; name=\"{name}\"\r\nContent-Disposition: attachment; filename=\"{name}\"\r\nContent-Transfer-Encoding: base64\r\n\r\n{base64}\r\n";

    [Fact]
    public async Task Malware_is_quarantined_alerted_and_not_delivered()
    {
        var subject = $"Invoice {Guid.NewGuid():N}";
        Assert.StartsWith("250", await SendAsync("jordan.lee@example.com", subject, Attachment("invoice.exe", Convert.ToBase64String([0x4D, 0x5A, 0x90, 0]))));

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var record = await ctx.QuarantineRecords.SingleAsync(r => r.Subject == subject);
        Assert.Equal(QuarantineReason.Malware, record.Reason);
        Assert.Equal(QuarantineStatus.Held, record.Status);
        Assert.True(File.Exists(Path.Combine(_storeRoot, record.StorageRef)));
        Assert.False(await ctx.MailMessages.AnyAsync(m => m.Subject == subject));
        Assert.True(await ctx.SecurityAlerts.AnyAsync(a => a.Title == "Malware blocked" && a.Description.Contains("invoice.exe")));
    }

    [Fact]
    public async Task Policy_hold_can_be_released_to_the_inbox()
    {
        var subject = $"Budget {Guid.NewGuid():N}";
        Assert.StartsWith("250", await SendAsync("priya.shah@example.com", subject, Attachment("budget.xlsm", Convert.ToBase64String("not really a workbook"u8.ToArray()))));

        Guid recordId;
        string path;
        await using (var ctx = db.CreateContext(db.ExampleOrgId))
        {
            var record = await ctx.QuarantineRecords.SingleAsync(r => r.Subject == subject);
            Assert.Equal(QuarantineReason.Policy, record.Reason);
            (recordId, path) = (record.Id, record.StorageRef);
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(db.ExampleOrgId, null);
            await scope.ServiceProvider.GetRequiredService<SecurityService>().ReleaseQuarantineAsync(recordId, default);
        }

        await using var verify = db.CreateContext(db.ExampleOrgId);
        Assert.Equal(QuarantineStatus.Released, await verify.QuarantineRecords.Where(r => r.Id == recordId).Select(r => r.Status).SingleAsync());
        var delivered = await verify.MailMessages.Include(m => m.Mailbox).SingleAsync(m => m.Subject == subject);
        Assert.Equal(MailFolder.Inbox, delivered.Folder);
        Assert.Equal("priya.shah@example.com", delivered.Mailbox!.EmailAddress);
        Assert.False(File.Exists(Path.Combine(_storeRoot, path))); // quarantine copy removed
    }

    [Fact]
    public async Task Score_headers_are_added_and_forged_ones_removed()
    {
        var subject = $"Plain {Guid.NewGuid():N}";
        Assert.StartsWith("250", await SendAsync("jordan.lee@example.com", subject, "", extraHeaders: "X-MX-Spam-Score: -100\r\nX-MX-Spam-Rules: trust me\r\n"));
        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var msg = await ctx.MailMessages.SingleAsync(m => m.Subject == subject);
        var content = await File.ReadAllTextAsync(Path.Combine(_storeRoot, msg.StoragePath));
        Assert.DoesNotContain("-100", content);
        Assert.DoesNotContain("trust me", content);
        Assert.Contains("X-MX-Spam-Score: 0.0", content);
        Assert.Equal(0, msg.SpamScore);
    }

    private sealed class NoDns : IDnsVerifier
    {
        public Task<DnsCheckResult> CheckAsync(string domainName, string verificationToken, string? dkimSelector, CancellationToken ct) =>
            Task.FromResult(new DnsCheckResult(false, false, false, false, false));
    }

    private sealed class NoProviders : IMailServerProviderFactory
    {
        public IMailServerProvider Get(MailServerType serverType) => throw new NotSupportedException();
    }
}
