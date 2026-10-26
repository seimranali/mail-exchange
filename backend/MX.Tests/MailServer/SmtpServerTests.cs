using System.Net.Security;
using System.Net.Sockets;
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

/// <summary>Runs the real SMTP listener against the real database and talks to it over TCP.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SmtpServerTests(PostgresFixture db) : IAsyncLifetime
{
    private ServiceProvider _services = null!;
    private SmtpListener _listener = null!;
    private string _storeRoot = null!;
    private string _certPath = null!;
    private const long MaxSize = 64 * 1024;

    public async Task InitializeAsync()
    {
        _storeRoot = Path.Combine(Path.GetTempPath(), "mx-store-" + Guid.NewGuid().ToString("N"));
        _certPath = CreateSelfSignedPfx();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Mx"] = db.AppConnectionString,
            ["MailServer:Hostname"] = "mx.test",
            ["MailServer:SmtpPort"] = "0",
            ["MailServer:SubmissionPort"] = "-1",
            ["MailServer:SubmissionTlsPort"] = "-1",
            ["MailServer:ListenAddress"] = "127.0.0.1",
            ["MailServer:MaxMessageSizeBytes"] = MaxSize.ToString(),
            ["MailServer:CertificatePath"] = _certPath,
            ["MailServer:CertificatePassword"] = "test",
            ["MailServer:DisableAuthenticationChecks"] = "true",
            ["MailServer:AcceptUnverifiedDomains"] = "false",
            ["MailStore:Root"] = _storeRoot,
        }).Build();

        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddPersistence(config).AddMxSecurity(config).AddSmtpServer(config);
        services.Replace(ServiceDescriptor.Singleton<IMailDns, FakeDns>());
        _services = services.BuildServiceProvider();
        _listener = _services.GetRequiredService<SmtpListener>();
        await _listener.StartAsync(CancellationToken.None);
        await _listener.Started;
    }

    public async Task DisposeAsync()
    {
        await _listener.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
        if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, recursive: true);
        File.Delete(_certPath);
    }

    [Fact]
    public async Task Delivers_to_local_mailbox_with_trace_headers()
    {
        var subject = $"Hello {Guid.NewGuid():N}";
        await using var c = await SmtpTestClient.ConnectAsync(_listener.LocalEndpoint!.Port);
        Assert.StartsWith("220 mx.test", c.Greeting);
        var ehlo = await c.SendAsync("EHLO client.test");
        Assert.Contains("STARTTLS", ehlo);
        Assert.Contains($"SIZE {MaxSize}", ehlo);
        Assert.StartsWith("250", await c.SendAsync("MAIL FROM:<sender@remote.test>"));
        Assert.StartsWith("250", await c.SendAsync("RCPT TO:<Jordan.Lee@example.com>"));
        Assert.StartsWith("354", await c.SendAsync("DATA"));
        var reply = await c.SendAsync($"From: Sender <sender@remote.test>\r\nSubject: {subject}\r\nMessage-ID: <abc@remote.test>\r\n\r\nBody line\r\n..starts with dot\r\n.");
        Assert.StartsWith("250 2.0.0", reply);

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var msg = await ctx.MailMessages.Include(m => m.Mailbox).SingleAsync(m => m.Subject == subject);
        Assert.Equal("jordan.lee@example.com", msg.Mailbox!.EmailAddress);
        Assert.Equal(MailFolder.Inbox, msg.Folder);
        Assert.Equal("sender@remote.test", msg.EnvelopeFrom);
        Assert.Equal("127.0.0.1", msg.RemoteIp);

        var stored = await File.ReadAllTextAsync(Path.Combine(_storeRoot, msg.StoragePath));
        Assert.StartsWith("Return-Path: <sender@remote.test>\r\nAuthentication-Results: mx.test;", stored);
        Assert.Contains("Received: from client.test ([127.0.0.1])\r\n\tby mx.test with ESMTP", stored);
        Assert.Contains("\r\n.starts with dot\r\n", stored); // dot-unstuffed
        Assert.Equal(new FileInfo(Path.Combine(_storeRoot, msg.StoragePath)).Length, msg.SizeBytes);
    }

    [Fact]
    public async Task Alias_and_mailbox_in_one_transaction_produce_one_copy()
    {
        var subject = $"Alias {Guid.NewGuid():N}";
        await using var c = await SmtpTestClient.ConnectAsync(_listener.LocalEndpoint!.Port);
        await c.SendAsync("EHLO client.test");
        await c.SendAsync("MAIL FROM:<a@remote.test>");
        Assert.StartsWith("250", await c.SendAsync("RCPT TO:<support@example.com>"));
        Assert.StartsWith("250", await c.SendAsync("RCPT TO:<jordan.lee@example.com>"));
        await c.SendAsync("DATA");
        Assert.StartsWith("250", await c.SendAsync($"Subject: {subject}\r\n\r\nx\r\n."));

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        Assert.Equal(1, await ctx.MailMessages.CountAsync(m => m.Subject == subject));
    }

    [Theory]
    [InlineData("someone@gmail.com", "550 5.7.1")]         // not a local domain: no open relay
    [InlineData("nobody@example.com", "550 5.1.1")]        // unknown user
    [InlineData("fatima.khan@example.com", "550 5.2.1")]   // disabled mailbox (seed data)
    [InlineData("user@example.org", "550 5.7.1")]          // domain exists but is not verified
    public async Task Rejects_invalid_recipients(string rcpt, string expected)
    {
        await using var c = await SmtpTestClient.ConnectAsync(_listener.LocalEndpoint!.Port);
        await c.SendAsync("EHLO client.test");
        await c.SendAsync("MAIL FROM:<a@remote.test>");
        Assert.StartsWith(expected, await c.SendAsync($"RCPT TO:<{rcpt}>"));
    }

    [Fact]
    public async Task Enforces_command_order()
    {
        await using var c = await SmtpTestClient.ConnectAsync(_listener.LocalEndpoint!.Port);
        Assert.StartsWith("503", await c.SendAsync("MAIL FROM:<a@remote.test>"));
        await c.SendAsync("EHLO client.test");
        Assert.StartsWith("503", await c.SendAsync("RCPT TO:<jordan.lee@example.com>"));
        await c.SendAsync("MAIL FROM:<a@remote.test>");
        Assert.StartsWith("554", await c.SendAsync("DATA"));
        Assert.StartsWith("502", await c.SendAsync("AUTH PLAIN AGVhAGU="));
        Assert.StartsWith("221", await c.SendAsync("QUIT"));
    }

    [Fact]
    public async Task Rejects_oversized_messages_and_keeps_session_usable()
    {
        await using var c = await SmtpTestClient.ConnectAsync(_listener.LocalEndpoint!.Port);
        await c.SendAsync("EHLO client.test");
        Assert.StartsWith("552", await c.SendAsync($"MAIL FROM:<a@remote.test> SIZE={MaxSize + 1}"));
        await c.SendAsync("MAIL FROM:<a@remote.test>");
        await c.SendAsync("RCPT TO:<jordan.lee@example.com>");
        await c.SendAsync("DATA");
        var big = string.Join("\r\n", Enumerable.Repeat(new string('x', 900), 100));
        Assert.StartsWith("552 5.3.4", await c.SendAsync($"Subject: big\r\n\r\n{big}\r\n."));
        Assert.StartsWith("250", await c.SendAsync("NOOP"));
    }

    [Fact]
    public async Task Bare_lf_cannot_smuggle_a_second_message()
    {
        var smuggled = $"Smuggled {Guid.NewGuid():N}";
        await using var c = await SmtpTestClient.ConnectAsync(_listener.LocalEndpoint!.Port);
        await c.SendAsync("EHLO client.test");
        await c.SendAsync("MAIL FROM:<a@remote.test>");
        await c.SendAsync("RCPT TO:<jordan.lee@example.com>");
        await c.SendAsync("DATA");
        // "\n.\n" must NOT end the data; the following lines must not run as commands.
        await c.WriteRawAsync($"Subject: first\r\n\r\nbody\n.\nMAIL FROM:<evil@remote.test>\r\nRCPT TO:<jordan.lee@example.com>\r\nDATA\r\nSubject: {smuggled}\r\n\r\nx\r\n.\r\n");
        Assert.StartsWith("550 5.5.2", await c.ReadReplyAsync());

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        Assert.False(await ctx.MailMessages.AnyAsync(m => m.Subject == smuggled));
    }

    [Fact]
    public async Task Starttls_upgrades_and_rejects_pipelined_injection()
    {
        await using (var c = await SmtpTestClient.ConnectAsync(_listener.LocalEndpoint!.Port))
        {
            await c.SendAsync("EHLO client.test");
            Assert.StartsWith("220", await c.SendAsync("STARTTLS"));
            await c.UpgradeToTlsAsync();
            var ehlo = await c.SendAsync("EHLO client.test");
            Assert.DoesNotContain("STARTTLS", ehlo);
            Assert.StartsWith("250", await c.SendAsync("NOOP"));
        }

        await using (var c = await SmtpTestClient.ConnectAsync(_listener.LocalEndpoint!.Port))
        {
            await c.SendAsync("EHLO client.test");
            await c.WriteRawAsync("STARTTLS\r\nMAIL FROM:<injected@remote.test>\r\n");
            Assert.StartsWith("554 5.5.1", await c.ReadReplyAsync());
        }
    }

    [Fact]
    public void Forged_authentication_results_for_our_host_are_removed()
    {
        var raw = Encoding.ASCII.GetBytes("Authentication-Results: mx.test; spf=pass\r\n\tdkim=pass\r\nAuthentication-Results: other.example; spf=fail\r\nSubject: x\r\n\r\nbody\r\n");
        var cleaned = Encoding.ASCII.GetString(SmtpSession.StripForgedAuthResults(raw, "mx.test"));
        Assert.DoesNotContain("mx.test", cleaned);
        Assert.Contains("Authentication-Results: other.example; spf=fail", cleaned);
        Assert.EndsWith("Subject: x\r\n\r\nbody\r\n", cleaned);
    }

    private static string CreateSelfSignedPfx()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=mx.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(Path.GetTempPath(), $"mx-test-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, "test"));
        return path;
    }
}

/// <summary>Minimal SMTP client for tests: sends lines, reads (multi-line) replies.</summary>
internal sealed class SmtpTestClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private Stream _stream;
    private StreamReader _reader;
    public string Greeting { get; private set; } = "";

    private SmtpTestClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new StreamReader(_stream, Encoding.ASCII);
    }

    public static async Task<SmtpTestClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var client = new SmtpTestClient(tcp);
        client.Greeting = await client.ReadReplyAsync();
        return client;
    }

    /// <summary>Connects to an implicit-TLS port (465): TLS handshake before the greeting.</summary>
    public static async Task<SmtpTestClient> ConnectImplicitTlsAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var client = new SmtpTestClient(tcp);
        await client.UpgradeToTlsAsync();
        client.Greeting = await client.ReadReplyAsync();
        return client;
    }

    public async Task<string> SendAsync(string command)
    {
        await WriteRawAsync(command + "\r\n");
        return await ReadReplyAsync();
    }

    public async Task WriteRawAsync(string data)
    {
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(data));
        await _stream.FlushAsync();
    }

    public async Task<string> ReadReplyAsync()
    {
        var sb = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var line = await _reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Connection closed");
            sb.AppendLine(line);
            if (line.Length < 4 || line[3] == ' ') return sb.ToString();
        }
    }

    public async Task UpgradeToTlsAsync()
    {
        var ssl = new SslStream(_stream, false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("mx.test");
        _stream = ssl;
        _reader = new StreamReader(ssl, Encoding.ASCII);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}
