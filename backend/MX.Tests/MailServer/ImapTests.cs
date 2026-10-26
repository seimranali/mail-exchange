using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MimeKit;
using MX.Domain;
using MX.MailServer;
using MX.MailServer.Authentication;
using MX.MailServer.Delivery;
using MX.MailServer.Imap;
using MX.Persistence;
using MX.Security;
using MX.Tests.Infrastructure;
using MailFolder = MX.Domain.MailFolder;

namespace MX.Tests.MailServer;

public sealed class ImapSyntaxTests
{
    [Fact]
    public void Tokenizer_keeps_bracketed_sections_in_one_atom()
    {
        var tokens = ImapTokenizer.Tokenize([("A1 UID FETCH 1:* (UID BODY.PEEK[HEADER.FIELDS (From Subject)]<0.512>) \"quoted \\\"x\\\"\"", null)]);
        Assert.Equal(["A1", "UID", "FETCH", "1:*"], tokens.Take(4).Cast<ImapAtom>().Select(a => a.Value));
        Assert.IsType<ImapListOpen>(tokens[4]);
        Assert.Equal("BODY.PEEK[HEADER.FIELDS (From Subject)]<0.512>", ((ImapAtom)tokens[6]).Value);
        Assert.Equal("quoted \"x\"", ((ImapString)tokens[^1]).Value);
    }

    [Fact]
    public void Literal_markers_are_detected()
    {
        Assert.Equal((12, false), ImapTokenizer.LiteralMarker("A1 APPEND INBOX {12}"));
        Assert.Equal((5, true), ImapTokenizer.LiteralMarker("A1 LOGIN {5+}"));
        Assert.Null(ImapTokenizer.LiteralMarker("A1 NOOP"));
    }

    [Theory]
    [InlineData("1:3", 2, 10, true)]
    [InlineData("1:3", 4, 10, false)]
    [InlineData("5,7:*", 9, 10, true)]
    [InlineData("*", 10, 10, true)]
    [InlineData("*:4", 6, 10, true)] // reversed range
    public void Sequence_sets(string set, long value, long max, bool expected) =>
        Assert.Equal(expected, SequenceSet.Parse(set).Contains(value, max));

    [Fact]
    public void Imap_date_time_parses_numeric_offsets()
    {
        Assert.True(ImapFormat.TryParseDateTime("\"17-Jul-1996 02:44:25 -0700\"", out var d));
        Assert.Equal(TimeSpan.FromHours(-7), d.Offset);
        Assert.Equal("\"17-Jul-1996 02:44:25 -0700\"", ImapFormat.FormatDate(d));
    }
}

/// <summary>IMAP end to end, using MailKit (a widely used client library) against the real server and database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ImapServerTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Password = "Imap-Test-Password-42";
    private ServiceProvider _services = null!;
    private ImapListener _listener = null!;
    private string _storeRoot = null!;
    private string _certPath = null!;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    public async Task InitializeAsync()
    {
        _storeRoot = Path.Combine(Path.GetTempPath(), "mx-imap-" + Guid.NewGuid().ToString("N"));
        _certPath = Path.Combine(Path.GetTempPath(), $"mx-imap-{Guid.NewGuid():N}.pfx");
        using (var key = RSA.Create(2048))
        using (var cert = new CertificateRequest("CN=mx.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
            await File.WriteAllBytesAsync(_certPath, cert.Export(X509ContentType.Pfx, "test"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Mx"] = db.AppConnectionString,
            ["MailServer:Hostname"] = "mx.test",
            ["MailServer:ListenAddress"] = "127.0.0.1",
            ["MailServer:ImapPort"] = "0",
            ["MailServer:ImapTlsPort"] = "0",
            ["MailServer:ImapIdlePollSeconds"] = "1",
            ["MailServer:CertificatePath"] = _certPath,
            ["MailServer:CertificatePassword"] = "test",
            ["MailStore:Root"] = _storeRoot,
            ["Secrets:KeyEncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["Jwt:SigningKey"] = "unused-in-these-tests-0123456789-0123456789",
        }).Build();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddPersistence(config).AddMxSecurity(config).AddSmtpServer(config).AddImapServer();
        services.Replace(ServiceDescriptor.Singleton<IMailDns, FakeDns>());
        _services = services.BuildServiceProvider();

        var hash = new Argon2PasswordHasher().Hash(Password);
        await using (var ctx = db.CreateContext(db.ExampleOrgId))
            await ctx.Mailboxes.Where(m => m.EmailAddress == "noah.williams@example.com" || m.EmailAddress == "yuki.tanaka@example.com")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.PasswordHash, hash));

        _listener = _services.GetRequiredService<ImapListener>();
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

    /// <summary>Delivers a message into a mailbox the way the SMTP server would.</summary>
    private async Task DeliverAsync(string to, string subject, string body = "Hello there", bool multipart = false)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ana Sender", "ana@remote.test"));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        if (multipart)
        {
            var builder = new BodyBuilder { TextBody = body, HtmlBody = $"<p>{body}</p>" };
            builder.Attachments.Add("report.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 fake"), new ContentType("application", "pdf"));
            message.Body = builder.ToMessageBody();
        }
        else message.Body = new TextPart("plain") { Text = body };
        using var ms = new MemoryStream();
        var format = FormatOptions.Default.Clone();
        format.NewLineFormat = NewLineFormat.Dos;
        await message.WriteToAsync(format, ms);

        var (_, recipient) = await _services.GetRequiredService<IRecipientDirectory>().ResolveAsync(to, default);
        var none = new SpfOutcome(AuthCheckResult.None, "", "");
        await _services.GetRequiredService<ILocalDelivery>().DeliverAsync(
            new ReceivedMessage(ms.ToArray(), "ana@remote.test", "127.0.0.1", "test", true, none, new DkimOutcome(AuthCheckResult.None, [], ""), new DmarcOutcome(AuthCheckResult.None, DmarcPolicy.None, "", "")),
            [recipient!], default);
    }

    private async Task<ImapClient> ConnectAsync(string user = "noah.williams@example.com")
    {
        var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _listener.TlsEndpoint!.Port, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync(user, Password);
        return client;
    }

    [Fact]
    public async Task Login_requires_tls_and_starttls_works()
    {
        using (var raw = new System.Net.Sockets.TcpClient())
        {
            await raw.ConnectAsync("127.0.0.1", _listener.Endpoint!.Port);
            var reader = new StreamReader(raw.GetStream());
            var writer = new StreamWriter(raw.GetStream()) { NewLine = "\r\n", AutoFlush = true };
            var greeting = await reader.ReadLineAsync();
            Assert.Contains("LOGINDISABLED", greeting);
            Assert.Contains("STARTTLS", greeting);
            await writer.WriteLineAsync($"a1 LOGIN noah.williams@example.com {Password}");
            Assert.StartsWith("a1 NO [PRIVACYREQUIRED]", await reader.ReadLineAsync());
        }

        using var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _listener.Endpoint!.Port, SecureSocketOptions.StartTls);
        await client.AuthenticateAsync("noah.williams@example.com", Password);
        Assert.True(client.IsAuthenticated);
        Assert.True(client.IsSecure);
    }

    [Fact]
    public async Task Wrong_password_fails()
    {
        using var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _listener.TlsEndpoint!.Port, SecureSocketOptions.SslOnConnect);
        await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync("noah.williams@example.com", "nope"));
    }

    [Fact]
    public async Task Lists_special_use_folders_and_reads_messages()
    {
        var subject = $"Quarterly report {_tag}";
        await DeliverAsync("noah.williams@example.com", subject, "Numbers inside", multipart: true);
        using var client = await ConnectAsync();

        Assert.NotNull(client.GetFolder(SpecialFolder.Sent));
        Assert.NotNull(client.GetFolder(SpecialFolder.Trash)!);
        Assert.NotNull(client.GetFolder(SpecialFolder.Drafts));

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        await using (var ctx = db.CreateContext(db.ExampleOrgId))
            Assert.Equal(await ctx.MailMessages.CountAsync(m => m.Mailbox!.EmailAddress == "noah.williams@example.com" && m.Folder == MailFolder.Inbox), inbox.Count);

        var uid = (await inbox.SearchAsync(SearchQuery.SubjectContains(_tag))).Single();
        var summary = (await inbox.FetchAsync([uid], MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.Flags | MessageSummaryItems.Size)).Single();
        Assert.NotNull(summary.Envelope);
        Assert.Equal(subject, summary.Envelope.Subject);
        Assert.Equal("ana@remote.test", summary.Envelope.From.Mailboxes.Single().Address);
        Assert.Equal("Ana Sender", summary.Envelope.From.Mailboxes.Single().Name);
        Assert.False(summary.Flags!.Value.HasFlag(MessageFlags.Seen));
        Assert.Contains(summary.Attachments, a => a.FileName == "report.pdf");
        Assert.NotNull(summary.TextBody);
        var textBody = summary.TextBody!;

        var message = await inbox.GetMessageAsync(uid);
        Assert.Equal(subject, message.Subject);
        Assert.Contains("Numbers inside", message.TextBody);
        Assert.Equal(summary.Size, (uint)new FileInfo(Path.Combine(_storeRoot, (await StoragePathAsync(subject)))).Length);

        // MailKit reads with BODY.PEEK[], which must leave \Seen untouched.
        await using (var ctx = db.CreateContext(db.ExampleOrgId))
            Assert.False(await ctx.MailMessages.Where(m => m.Subject == subject).Select(m => m.IsRead).SingleAsync());

        // Partial fetch of a single body part (the text part of the alternative).
        var textPart = textBody;
        var part = (TextPart)await inbox.GetBodyPartAsync(uid, textPart);
        Assert.Contains("Numbers inside", part.Text);
    }

    private async Task<string> StoragePathAsync(string subject)
    {
        await using var ctx = db.CreateContext(db.ExampleOrgId);
        return await ctx.MailMessages.Where(m => m.Subject == subject).Select(m => m.StoragePath).SingleAsync();
    }

    [Fact]
    public async Task Flags_search_and_store()
    {
        var subject = $"Flag me {_tag}";
        await DeliverAsync("noah.williams@example.com", subject);
        using var client = await ConnectAsync();
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var uid = (await inbox.SearchAsync(SearchQuery.SubjectContains(_tag).And(SearchQuery.NotSeen))).Single();

        await inbox.AddFlagsAsync(uid, MessageFlags.Flagged | MessageFlags.Seen, silent: true);
        Assert.Contains(uid, await inbox.SearchAsync(SearchQuery.Flagged));
        Assert.DoesNotContain(uid, await inbox.SearchAsync(SearchQuery.NotSeen));
        await inbox.RemoveFlagsAsync(uid, MessageFlags.Flagged, silent: true);

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var row = await ctx.MailMessages.SingleAsync(m => m.Subject == subject);
        Assert.True(row.IsRead);
        Assert.False(row.IsFlagged);
    }

    [Fact]
    public async Task Move_copy_and_expunge()
    {
        var moveSubject = $"Move me {_tag}";
        var copySubject = $"Copy me {_tag}";
        await DeliverAsync("noah.williams@example.com", moveSubject);
        await DeliverAsync("noah.williams@example.com", copySubject);
        using var client = await ConnectAsync();
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);

        var moveUid = (await inbox.SearchAsync(SearchQuery.SubjectContains(moveSubject))).Single();
        var before = inbox.Count;
        var moved = await inbox.MoveToAsync(moveUid, client.GetFolder(SpecialFolder.Archive)!);
        Assert.NotNull(moved);
        Assert.Equal(before - 1, inbox.Count);

        var copyUid = (await inbox.SearchAsync(SearchQuery.SubjectContains(copySubject))).Single();
        var copied = await inbox.CopyToAsync(copyUid, client.GetFolder(SpecialFolder.Trash)!);
        Assert.NotNull(copied);

        await inbox.AddFlagsAsync(copyUid, MessageFlags.Deleted, silent: true);
        await inbox.ExpungeAsync();
        Assert.Empty(await inbox.SearchAsync(SearchQuery.SubjectContains(copySubject)));

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        Assert.Equal(MailFolder.Archive, await ctx.MailMessages.Where(m => m.Subject == moveSubject).Select(m => m.Folder).SingleAsync());
        Assert.Equal(MailFolder.Trash, await ctx.MailMessages.Where(m => m.Subject == copySubject).Select(m => m.Folder).SingleAsync());
    }

    [Fact]
    public async Task Append_saves_a_draft_with_appenduid()
    {
        var subject = $"Draft {_tag}";
        var draft = new MimeMessage();
        draft.From.Add(MailboxAddress.Parse("noah.williams@example.com"));
        draft.To.Add(MailboxAddress.Parse("someone@remote.test"));
        draft.Subject = subject;
        draft.Body = new TextPart("plain") { Text = "unfinished thoughts" };

        using var client = await ConnectAsync();
        var drafts = client.GetFolder(SpecialFolder.Drafts)!;
        var uid = await drafts.AppendAsync(draft, MessageFlags.Draft | MessageFlags.Seen);
        Assert.NotNull(uid);

        await drafts.OpenAsync(FolderAccess.ReadOnly);
        var fetched = await drafts.GetMessageAsync(uid!.Value);
        Assert.Equal(subject, fetched.Subject);

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var row = await ctx.MailMessages.SingleAsync(m => m.Subject == subject);
        Assert.Equal(MailFolder.Drafts, row.Folder);
        Assert.True(row.IsDraft);
        Assert.Equal((long)uid.Value.Id, row.Uid);
    }

    [Fact]
    public async Task Idle_reports_new_mail()
    {
        using var client = await ConnectAsync();
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        var initial = inbox.Count;
        var arrived = new TaskCompletionSource();
        inbox.CountChanged += (_, _) => { if (inbox.Count > initial) arrived.TrySetResult(); };

        using var done = new CancellationTokenSource();
        var idle = client.IdleAsync(done.Token);
        await Task.Delay(300);
        await DeliverAsync("noah.williams@example.com", $"Live {_tag}");
        var completed = await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await done.CancelAsync();
        await idle;
        Assert.Same(arrived.Task, completed);
    }

    [Fact]
    public async Task Colleague_in_same_organization_cannot_see_the_mailbox()
    {
        var subject = $"Private {_tag}";
        await DeliverAsync("noah.williams@example.com", subject);
        using var client = await ConnectAsync("yuki.tanaka@example.com");
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        Assert.Empty(await inbox.SearchAsync(SearchQuery.SubjectContains(subject)));
        Assert.Empty(await inbox.SearchAsync(SearchQuery.FromContains("ana@remote.test").And(SearchQuery.SubjectContains(_tag))));
    }

    [Fact]
    public async Task Raw_append_with_synchronizing_literal()
    {
        using var raw = new System.Net.Sockets.TcpClient();
        await raw.ConnectAsync("127.0.0.1", _listener.TlsEndpoint!.Port);
        using var ssl = new SslStream(raw.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("mx.test");
        var reader = new StreamReader(ssl, Encoding.ASCII);
        async Task Send(string s) { await ssl.WriteAsync(Encoding.ASCII.GetBytes(s)); await ssl.FlushAsync(); }
        await reader.ReadLineAsync(); // greeting
        await Send($"a1 LOGIN \"noah.williams@example.com\" \"{Password}\"\r\n");
        Assert.StartsWith("a1 OK", await reader.ReadLineAsync());

        var body = $"Subject: Raw {_tag}\r\n\r\nraw body\r\n";
        await Send($"a2 APPEND Archive (\\Seen) {{{body.Length}}}\r\n");
        Assert.StartsWith("+", await reader.ReadLineAsync());
        await Send(body + "\r\n");
        var reply = await reader.ReadLineAsync();
        var appendUid = System.Text.RegularExpressions.Regex.Match(reply!, @"^a2 OK \[APPENDUID \d+ (\d+)\]");
        Assert.True(appendUid.Success, reply);

        // A non-PEEK BODY[] fetch returns exactly the stored bytes and implicitly sets \Seen.
        await Send("a3 SELECT Archive\r\n");
        while (!(await reader.ReadLineAsync())!.StartsWith("a3 ")) { }
        await Send($"a4 STORE {appendUid.Groups[1].Value} -FLAGS.SILENT (\\Seen)\r\n");
        await Send($"a5 UID FETCH {appendUid.Groups[1].Value} (BODY[])\r\n");
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null && !line.StartsWith("a5 ")) lines.Add(line);
        Assert.StartsWith("a5 OK", line);
        var fetch = string.Join("\n", lines);
        Assert.Contains($"BODY[] {{{body.Length}}}", fetch);
        Assert.Contains(@"FLAGS (\Seen)", fetch);
    }
}
