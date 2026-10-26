using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MimeKit;
using MX.Domain;
using MX.MailServer.Authentication;
using MX.MailServer.Delivery;
using MX.MailServer.Filtering;
using MX.MailServer.Submission;

namespace MX.MailServer.Smtp;

/// <summary>Collaborators a session needs; one instance shared by all sessions.</summary>
public sealed record SmtpSessionServices(
    MailServerOptions Options,
    MX.MailServer.Tls.TlsCertificate? Certificate,
    IRecipientDirectory Directory,
    ILocalDelivery Delivery,
    SpfEvaluator Spf,
    DkimChecker Dkim,
    DmarcEvaluator Dmarc,
    IMailboxAuthenticator Authenticator,
    ISubmissionHandler Submission,
    IContentFilter Filter,
    IQuarantine Quarantine,
    ILogger Logger);

public enum SmtpMode
{
    /// <summary>Port 25: mail from other servers, local recipients only, no AUTH.</summary>
    Inbound,
    /// <summary>Ports 587/465: mail from our own users' clients, AUTH required, any recipient.</summary>
    Submission,
}

/// <summary>
/// One SMTP conversation (RFC 5321). In <see cref="SmtpMode.Inbound"/> it accepts mail only for
/// local recipients and never relays; in <see cref="SmtpMode.Submission"/> it relays only for
/// authenticated users sending as their own addresses (RFC 6409).
/// </summary>
public sealed partial class SmtpSession(Stream stream, IPAddress remoteIp, SmtpSessionServices services, SmtpMode mode = SmtpMode.Inbound, bool implicitTls = false)
{
    private const int MaxCommandLength = 2048;
    private const int MaxDataLineLength = 64 * 1024;
    private const int MaxReceivedHeaders = 50;

    private static readonly Encoding Ascii = Encoding.ASCII;

    private Stream _stream = stream;
    private readonly SmtpLineReader _reader = new(stream);
    private readonly string _sessionId = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..6]);
    private readonly MailServerOptions _options = services.Options;

    private string? _helo;
    private bool _tls = implicitTls;
    private int _errors;
    private AuthenticatedMailbox? _user;

    // Transaction state
    private string? _mailFrom;
    private readonly List<LocalRecipient> _recipients = [];
    private readonly List<string> _submissionRecipients = [];

    private int RecipientCount => mode == SmtpMode.Submission ? _submissionRecipients.Count : _recipients.Count;
    private bool AuthAvailable => mode == SmtpMode.Submission && (_tls || _options.AllowPlaintextAuth);

    private void ResetTransaction()
    {
        _mailFrom = null;
        _recipients.Clear();
        _submissionRecipients.Clear();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = services.Logger.BeginScope(new Dictionary<string, object> { ["SmtpSession"] = _sessionId, ["RemoteIp"] = remoteIp.ToString() });
        await ReplyAsync($"220 {_options.Hostname} ESMTP MX ready", ct);

        while (!ct.IsCancellationRequested)
        {
            SmtpLine? line;
            try
            {
                line = await ReadAsync(MaxCommandLength, ct);
            }
            catch (SmtpLineTooLongException)
            {
                if (!await ErrorAsync("500 5.5.6 Line too long", ct)) return;
                continue;
            }
            if (line is null) return; // client disconnected or timed out

            var text = Ascii.GetString(line.Value.Bytes);
            var space = text.IndexOf(' ');
            var verb = (space < 0 ? text : text[..space]).ToUpperInvariant();
            var arg = space < 0 ? "" : text[(space + 1)..].Trim();

            switch (verb)
            {
                case "EHLO": await EhloAsync(arg, extended: true, ct); break;
                case "HELO": await EhloAsync(arg, extended: false, ct); break;
                case "MAIL": await MailAsync(arg, ct); break;
                case "RCPT": await RcptAsync(arg, ct); break;
                case "DATA": await DataAsync(ct); break;
                case "RSET":
                    ResetTransaction();
                    await ReplyAsync("250 2.0.0 OK", ct);
                    break;
                case "NOOP": await ReplyAsync("250 2.0.0 OK", ct); break;
                case "VRFY": await ReplyAsync("252 2.5.2 Cannot verify user; try sending a message", ct); break;
                case "HELP": await ReplyAsync("214 2.0.0 See RFC 5321", ct); break;
                case "STARTTLS":
                    if (!await StartTlsAsync(ct)) return;
                    break;
                case "QUIT":
                    await ReplyAsync($"221 2.0.0 {_options.Hostname} closing connection", ct);
                    return;
                case "AUTH":
                    if (mode != SmtpMode.Submission)
                    {
                        if (!await ErrorAsync("502 5.5.1 AUTH not available on this port; use submission", ct)) return;
                    }
                    else if (!await AuthAsync(arg, ct)) return;
                    break;
                default:
                    if (!await ErrorAsync("500 5.5.2 Command not recognized", ct)) return;
                    break;
            }
        }
    }

    private async Task EhloAsync(string arg, bool extended, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.Length > 255)
        {
            await ErrorAsync("501 5.5.4 Syntax: EHLO hostname", ct);
            return;
        }
        _helo = arg;
        ResetTransaction();
        if (!extended)
        {
            await ReplyAsync($"250 {_options.Hostname}", ct);
            return;
        }
        var lines = new List<string>
        {
            $"{_options.Hostname} greets {arg} [{remoteIp}]",
            "PIPELINING",
            $"SIZE {_options.MaxMessageSizeBytes}",
            "8BITMIME",
            "SMTPUTF8",
            "ENHANCEDSTATUSCODES",
        };
        if (services.Certificate is not null && !_tls) lines.Add("STARTTLS");
        // RFC 8314 / 4954: never offer AUTH where the password would travel unencrypted.
        if (AuthAvailable && _user is null) lines.Add("AUTH PLAIN LOGIN");
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++) sb.Append("250").Append(i == lines.Count - 1 ? ' ' : '-').Append(lines[i]).Append("\r\n");
        await WriteAsync(sb.ToString(), ct);
    }

    private async Task<bool> StartTlsAsync(CancellationToken ct)
    {
        if (services.Certificate is null || _tls)
        {
            return await ErrorAsync("502 5.5.1 STARTTLS not available", ct);
        }
        // Defence against STARTTLS command injection: plaintext pipelined after STARTTLS must not
        // be processed as if it arrived encrypted.
        if (_reader.HasBufferedData)
        {
            await ReplyAsync("554 5.5.1 Pipelining not allowed after STARTTLS", ct);
            return false;
        }
        await ReplyAsync("220 2.0.0 Ready to start TLS", ct);
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = services.Certificate!.Context,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
            }, ct);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            services.Logger.LogInformation("TLS handshake failed: {Message}", ex.Message);
            return false;
        }
        _stream = ssl;
        _reader.Reset(ssl);
        _tls = true;
        _helo = null; // RFC 3207: the client must EHLO again
        _user = null; // and nothing learned before TLS is trusted
        ResetTransaction();
        return true;
    }

    /// <summary>AUTH PLAIN (RFC 4616) and AUTH LOGIN. Returns false when the session should be dropped.</summary>
    private async Task<bool> AuthAsync(string arg, CancellationToken ct)
    {
        if (!AuthAvailable) return await ErrorAsync("538 5.7.11 Encryption required for requested authentication mechanism", ct);
        if (_helo is null) return await ErrorAsync("503 5.5.1 Send EHLO first", ct);
        if (_user is not null) return await ErrorAsync("503 5.5.1 Already authenticated", ct);
        if (_mailFrom is not null) return await ErrorAsync("503 5.5.1 AUTH not permitted during a mail transaction", ct);

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var mechanism = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        string? username, password;
        switch (mechanism)
        {
            case "PLAIN":
            {
                var payload = parts.Length > 1 ? parts[1] : await ChallengeAsync("", ct);
                if (payload is null) return await ErrorAsync("501 5.7.0 Authentication cancelled", ct);
                var decoded = DecodeBase64(payload)?.Split('\0');
                // authzid \0 authcid \0 password; acting as another identity (authzid) is not allowed.
                if (decoded is not { Length: 3 } || (decoded[0].Length > 0 && !decoded[0].Equals(decoded[1], StringComparison.OrdinalIgnoreCase)))
                    return await ErrorAsync("501 5.5.2 Malformed AUTH PLAIN response", ct);
                (username, password) = (decoded[1], decoded[2]);
                break;
            }
            case "LOGIN":
            {
                var userB64 = parts.Length > 1 ? parts[1] : await ChallengeAsync("VXNlcm5hbWU6", ct); // "Username:"
                if (userB64 is null) return await ErrorAsync("501 5.7.0 Authentication cancelled", ct);
                var passB64 = await ChallengeAsync("UGFzc3dvcmQ6", ct); // "Password:"
                if (passB64 is null) return await ErrorAsync("501 5.7.0 Authentication cancelled", ct);
                (username, password) = (DecodeBase64(userB64), DecodeBase64(passB64));
                if (username is null || password is null) return await ErrorAsync("501 5.5.2 Malformed AUTH LOGIN response", ct);
                break;
            }
            default:
                return await ErrorAsync("504 5.5.4 Unrecognized authentication mechanism", ct);
        }

        var (outcome, user) = await services.Authenticator.AuthenticateAsync(username, password, remoteIp.ToString(), "SMTP submission", ct);
        switch (outcome)
        {
            case AuthOutcome.Success:
                _user = user;
                await ReplyAsync("235 2.7.0 Authentication successful", ct);
                return true;
            case AuthOutcome.Throttled:
                await ReplyAsync("454 4.7.0 Too many failed logins; try again later", ct);
                return false;
            default:
                return await ErrorAsync("535 5.7.8 Authentication credentials invalid", ct);
        }
    }

    /// <summary>Sends a 334 challenge; returns the client's line, or null if it cancelled with "*".</summary>
    private async Task<string?> ChallengeAsync(string challenge, CancellationToken ct)
    {
        await ReplyAsync($"334 {challenge}", ct);
        var line = await ReadAsync(MaxCommandLength, ct);
        if (line is null) return null;
        var text = Ascii.GetString(line.Value.Bytes).Trim();
        return text == "*" ? null : text;
    }

    private static string? DecodeBase64(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task MailAsync(string arg, CancellationToken ct)
    {
        if (_helo is null) { await ErrorAsync("503 5.5.1 Send EHLO first", ct); return; }
        if (_mailFrom is not null) { await ErrorAsync("503 5.5.1 Sender already specified", ct); return; }

        if (mode == SmtpMode.Submission && _user is null) { await ErrorAsync("530 5.7.0 Authentication required", ct); return; }

        var m = MailFromPattern().Match(arg);
        if (!m.Success) { await ErrorAsync("501 5.5.4 Syntax: MAIL FROM:<address>", ct); return; }
        var address = m.Groups[1].Value.Trim();
        if (address.Length > 0 && !IsPlausibleAddress(address)) { await ErrorAsync("553 5.1.7 Invalid sender address", ct); return; }
        if (mode == SmtpMode.Submission && !_user!.AllowedSenders.Contains(address))
        {
            await ErrorAsync($"553 5.7.1 Not authorized to send as <{address}>", ct);
            return;
        }

        foreach (var param in m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = param.Split('=', 2);
            if (kv[0].Equals("SIZE", StringComparison.OrdinalIgnoreCase) && kv.Length == 2 && long.TryParse(kv[1], out var size) && size > _options.MaxMessageSizeBytes)
            {
                await ReplyAsync($"552 5.3.4 Message size exceeds limit of {_options.MaxMessageSizeBytes} bytes", ct);
                return;
            }
        }
        _mailFrom = address;
        await ReplyAsync("250 2.1.0 Sender OK", ct);
    }

    private async Task RcptAsync(string arg, CancellationToken ct)
    {
        if (_mailFrom is null) { await ErrorAsync("503 5.5.1 Send MAIL FROM first", ct); return; }
        var m = RcptToPattern().Match(arg);
        if (!m.Success) { await ErrorAsync("501 5.5.4 Syntax: RCPT TO:<address>", ct); return; }
        if (RecipientCount >= _options.MaxRecipients) { await ReplyAsync("452 4.5.3 Too many recipients", ct); return; }

        var address = m.Groups[1].Value.Trim();
        if (!IsPlausibleAddress(address)) { await ErrorAsync("553 5.1.3 Invalid recipient address", ct); return; }

        if (mode == SmtpMode.Submission)
        {
            // Authenticated users may send anywhere; the outbound queue resolves local vs remote.
            if (!_submissionRecipients.Contains(address, StringComparer.OrdinalIgnoreCase)) _submissionRecipients.Add(address);
            await ReplyAsync("250 2.1.5 Recipient OK", ct);
            return;
        }

        RecipientStatus status;
        LocalRecipient? recipient;
        try
        {
            (status, recipient) = await services.Directory.ResolveAsync(address, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.Logger.LogError(ex, "Recipient lookup failed for {Recipient}", address);
            await ReplyAsync("451 4.3.0 Temporary lookup failure, try again later", ct);
            return;
        }

        switch (status)
        {
            case RecipientStatus.Accepted:
                _recipients.Add(recipient!);
                await ReplyAsync("250 2.1.5 Recipient OK", ct);
                break;
            case RecipientStatus.NotLocal:
                await ErrorAsync("550 5.7.1 Relaying denied", ct);
                break;
            case RecipientStatus.UnknownUser:
                await ErrorAsync("550 5.1.1 No such user here", ct);
                break;
            case RecipientStatus.Disabled:
                await ErrorAsync("550 5.2.1 Mailbox disabled", ct);
                break;
            case RecipientStatus.OverQuota:
                await ReplyAsync("452 4.2.2 Mailbox full", ct);
                break;
        }
    }

    private async Task DataAsync(CancellationToken ct)
    {
        if (_mailFrom is null) { await ErrorAsync("503 5.5.1 Send MAIL FROM first", ct); return; }
        if (RecipientCount == 0) { await ErrorAsync("554 5.5.1 No valid recipients", ct); return; }
        await ReplyAsync("354 End data with <CR><LF>.<CR><LF>", ct);

        using var body = new MemoryStream();
        var oversize = false;
        var bareLf = false;
        var previousCrLf = true;
        while (true)
        {
            SmtpLine? read;
            try
            {
                read = await ReadAsync(MaxDataLineLength, ct);
            }
            catch (SmtpLineTooLongException)
            {
                oversize = true;
                continue;
            }
            if (read is null) return; // connection lost mid-message: nothing is delivered

            var line = read.Value;
            // End of data is only CRLF "." CRLF. A "." line reached through a bare LF is ignored as a
            // terminator, which defeats SMTP smuggling between servers that disagree on line endings.
            if (line.Bytes is [(byte)'.'] && line.EndedWithCrLf && previousCrLf) break;
            if (!line.EndedWithCrLf) bareLf = true;
            previousCrLf = line.EndedWithCrLf;

            var content = line.Bytes.AsSpan();
            if (content.Length > 0 && content[0] == '.') content = content[1..]; // dot-unstuffing
            if (body.Length + content.Length + 2 > _options.MaxMessageSizeBytes)
            {
                oversize = true;
                continue; // keep reading to the terminator so the session stays in sync
            }
            if (!oversize)
            {
                body.Write(content);
                body.Write("\r\n"u8);
            }
        }

        try
        {
            if (oversize) { await ReplyAsync($"552 5.3.4 Message exceeds size limit of {_options.MaxMessageSizeBytes} bytes", ct); return; }
            if (bareLf) { await ReplyAsync("550 5.5.2 Bare LF line endings are not allowed; use CRLF", ct); return; }
            if (mode == SmtpMode.Submission) await SubmitAsync(body.ToArray(), ct);
            else await AcceptMessageAsync(body.ToArray(), ct);
        }
        finally
        {
            ResetTransaction();
        }
    }

    private async Task AcceptMessageAsync(byte[] raw, CancellationToken ct)
    {
        MimeMessage? parsed = null;
        try
        {
            using var ms = new MemoryStream(raw, writable: false);
            parsed = await MimeMessage.LoadAsync(ms, ct);
        }
        catch (FormatException)
        {
            // Unparseable content is still deliverable; DKIM/DMARC simply cannot pass.
        }

        if (parsed is not null && parsed.Headers.Count(h => h.Id == HeaderId.Received) > MaxReceivedHeaders)
        {
            await ReplyAsync("554 5.4.6 Mail loop detected", ct);
            return;
        }

        var sender = _mailFrom!.Length > 0 ? _mailFrom : $"postmaster@{_helo}";
        SpfOutcome spf;
        DkimOutcome dkim;
        DmarcOutcome dmarc;
        if (_options.DisableAuthenticationChecks)
        {
            spf = new SpfOutcome(AuthCheckResult.None, sender, "checks disabled");
            dkim = new DkimOutcome(AuthCheckResult.None, [], "checks disabled");
            dmarc = new DmarcOutcome(AuthCheckResult.None, DmarcPolicy.None, "", "checks disabled");
        }
        else
        {
            spf = await services.Spf.CheckAsync(remoteIp, sender, _helo!, ct);
            dkim = parsed is null ? new DkimOutcome(AuthCheckResult.None, [], "unparseable message") : await services.Dkim.CheckAsync(parsed, ct);
            var fromDomain = parsed?.From.Mailboxes.FirstOrDefault()?.Domain;
            dmarc = await services.Dmarc.EvaluateAsync(fromDomain, spf, dkim, ct);
        }

        if (dmarc.Result == AuthCheckResult.Fail && dmarc.Policy == DmarcPolicy.Reject && _options.EnforceDmarcReject)
        {
            services.Logger.LogInformation("Rejected message from {Sender}: DMARC fail with p=reject for {Domain}", sender, dmarc.FromDomain);
            await ReplyAsync($"550 5.7.1 Rejected by DMARC policy of {dmarc.FromDomain}", ct);
            return;
        }

        FilterVerdict verdict;
        try
        {
            var hosted = await services.Directory.GetHostedDomainsAsync(ct);
            verdict = await services.Filter.EvaluateAsync(new FilterContext(raw, parsed, remoteIp, _helo!, sender, spf, dkim, dmarc, hosted), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A filter failure must not lose mail: deliver unscored and log loudly.
            services.Logger.LogError(ex, "Content filter failed; delivering without filtering");
            verdict = FilterVerdict.Clean;
        }
        if (verdict.Action == FilterAction.Reject)
        {
            services.Logger.LogInformation("Rejected message from {Sender} with spam score {Score}: {Rules}", sender, verdict.Score, verdict.RulesSummary);
            await ReplyAsync("550 5.7.1 Message rejected as spam", ct);
            return;
        }

        var traceHeaders = BuildTraceHeaders(spf, dkim, dmarc)
            + $"X-MX-Spam-Score: {verdict.Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}\r\n"
            + $"X-MX-Spam-Rules: {Truncate(verdict.RulesSummary, 900)}\r\n";
        var content = Concat(Ascii.GetBytes(traceHeaders), StripForgedAuthResults(raw, _options.Hostname));

        if (verdict.Action == FilterAction.Quarantine)
        {
            try
            {
                await services.Quarantine.HoldAsync(content, parsed, _mailFrom, _recipients, verdict, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                services.Logger.LogError(ex, "Quarantine failed");
                await ReplyAsync("451 4.3.0 Temporary failure storing message, try again later", ct);
                return;
            }
            // Accepted from the sender's point of view; the message waits for an administrator.
            await ReplyAsync($"250 2.0.0 OK queued as {_sessionId}", ct);
            return;
        }
        var received = new ReceivedMessage(content, _mailFrom, remoteIp.ToString(), _helo!, _tls, spf, dkim, dmarc, verdict.Score, verdict.Action == FilterAction.Junk);

        try
        {
            await services.Delivery.DeliverAsync(received, _recipients, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.Logger.LogError(ex, "Local delivery failed");
            await ReplyAsync("451 4.3.0 Temporary failure storing message, try again later", ct);
            return;
        }
        await ReplyAsync($"250 2.0.0 OK queued as {_sessionId}", ct);
    }

    private async Task SubmitAsync(byte[] raw, CancellationToken ct)
    {
        // The client's IP is deliberately left out of the Received header for submissions: it
        // would reveal users' home/office addresses to every recipient.
        var date = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture);
        var received = $"Received: from {Sanitize(_helo!)} (authenticated sender)\r\n\tby {_options.Hostname} with ESMTPSA id {_sessionId};\r\n\t{date}\r\n";
        var content = Concat(Ascii.GetBytes(received), StripForgedAuthResults(raw, _options.Hostname));
        try
        {
            var result = await services.Submission.SubmitAsync(_user!, _mailFrom!, _submissionRecipients.ToList(), content, ct);
            await ReplyAsync(result.Reply, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.Logger.LogError(ex, "Submission from {User} failed", _user!.Address);
            await ReplyAsync("451 4.3.0 Temporary failure queuing message, try again later", ct);
        }
    }

    private string BuildTraceHeaders(SpfOutcome spf, DkimOutcome dkim, DmarcOutcome dmarc)
    {
        var host = _options.Hostname;
        var protocol = _tls ? "ESMTPS" : "ESMTP";
        var forClause = _recipients.Count == 1 ? $"\r\n\tfor <{_recipients[0].Address}>" : "";
        var date = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture);
        var dkimPart = dkim.PassingDomains.Count > 0 ? $" header.d={dkim.PassingDomains[0]}" : "";
        var dmarcPart = dmarc.FromDomain.Length > 0 ? $" header.from={dmarc.FromDomain}" : "";
        return
            $"Return-Path: <{_mailFrom}>\r\n" +
            $"Authentication-Results: {host};\r\n" +
            $"\tspf={Token(spf.Result)} smtp.mailfrom={spf.Domain};\r\n" +
            $"\tdkim={Token(dkim.Result)}{dkimPart};\r\n" +
            $"\tdmarc={Token(dmarc.Result)}{dmarcPart}\r\n" +
            $"Received: from {Sanitize(_helo!)} ([{remoteIp}])\r\n\tby {host} with {protocol} id {_sessionId}{forClause};\r\n\t{date}\r\n";
    }

    private static string Token(AuthCheckResult r) => r.ToString().ToLowerInvariant();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Sanitize(string value) => new(value.Where(c => c > 32 && c < 127 && c != '(' && c != ')').Take(255).ToArray());

    /// <summary>
    /// Removes incoming Authentication-Results headers that claim to come from this server, so a
    /// sender cannot forge "pass" results that our users' clients would trust (RFC 8601 section 5).
    /// </summary>
    internal static byte[] StripForgedAuthResults(byte[] raw, string hostname)
    {
        var text = Encoding.Latin1.GetString(raw);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) return raw;
        var headerBlock = text[..(headerEnd + 2)];
        var fields = new List<string>();
        var current = new StringBuilder();
        foreach (var line in headerBlock.Split("\r\n"))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) { current.Append("\r\n").Append(line); continue; }
            if (current.Length > 0) fields.Add(current.ToString());
            current.Clear().Append(line);
        }
        if (current.Length > 0) fields.Add(current.ToString());

        var kept = fields.Where(f =>
        {
            // Our own filter headers are only ever added by us.
            if (f.StartsWith("X-MX-Spam-", StringComparison.OrdinalIgnoreCase)) return false;
            if (!f.StartsWith("Authentication-Results:", StringComparison.OrdinalIgnoreCase)) return true;
            var authServId = f["Authentication-Results:".Length..].TrimStart().Split([';', ' ', '\t', '\r'], 2)[0];
            return !authServId.Equals(hostname, StringComparison.OrdinalIgnoreCase);
        }).Where(f => f.Length > 0);

        var rebuilt = string.Join("\r\n", kept) + "\r\n" + text[(headerEnd + 2)..];
        return rebuilt.Length == text.Length ? raw : Encoding.Latin1.GetBytes(rebuilt);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private static bool IsPlausibleAddress(string address) =>
        address.Length <= 320 && address.IndexOf('@') is var at && at > 0 && at < address.Length - 1
        && !address.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' || char.IsControl(c));

    private async Task<SmtpLine?> ReadAsync(int maxLength, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));
        try
        {
            return await _reader.ReadLineAsync(maxLength, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await ReplyAsync("421 4.4.2 Idle timeout, closing connection", CancellationToken.None);
            return null;
        }
    }

    /// <summary>Sends an error reply; returns false when the session should be dropped.</summary>
    private async Task<bool> ErrorAsync(string reply, CancellationToken ct)
    {
        if (++_errors >= _options.MaxErrorsPerSession)
        {
            await ReplyAsync("421 4.7.0 Too many errors, closing connection", ct);
            return false;
        }
        await ReplyAsync(reply, ct);
        return true;
    }

    private Task ReplyAsync(string line, CancellationToken ct) => WriteAsync(line + "\r\n", ct);

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        try
        {
            await _stream.WriteAsync(Ascii.GetBytes(text), ct);
            await _stream.FlushAsync(ct);
        }
        catch (IOException)
        {
            // Client went away; the read loop ends on the next read.
        }
    }

    [GeneratedRegex(@"^FROM:\s*<([^<>]*)>(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex MailFromPattern();

    [GeneratedRegex(@"^TO:\s*<([^<>]+)>(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RcptToPattern();
}
