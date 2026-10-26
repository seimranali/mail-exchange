using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MX.MailServer.Authentication;
using MX.MailServer.Smtp;

namespace MX.MailServer.Outbound;

public enum RecipientOutcome { Delivered, Deferred, Failed }

public sealed record RecipientResult(string Address, RecipientOutcome Outcome, string Response);

public sealed record DeliveryAttempt(IReadOnlyList<RecipientResult> Results, string? RemoteHost, bool UsedTls);

/// <summary>
/// Delivers one message to one recipient domain (RFC 5321 client): MX lookup, preference order,
/// opportunistic STARTTLS, per-recipient RCPT results, dot-stuffed DATA.
/// </summary>
public sealed class SmtpDeliveryClient(IMailDns dns, IOptions<OutboundOptions> options, IOptions<MailServerOptions> serverOptions, ILogger<SmtpDeliveryClient> logger)
{
    private readonly OutboundOptions _o = options.Value;

    public async Task<DeliveryAttempt> DeliverAsync(string domain, string envelopeFrom, IReadOnlyList<string> recipients, byte[] content, CancellationToken ct)
    {
        List<string> hosts;
        try
        {
            hosts = (await dns.MxHostsAsync(domain, ct)).ToList();
        }
        catch (DnsTemporaryException ex)
        {
            return AllRecipients(recipients, RecipientOutcome.Deferred, $"DNS lookup failed: {ex.Message}", null);
        }

        // RFC 7505 null MX: the domain explicitly accepts no mail.
        if (hosts is [""] or ["."]) return AllRecipients(recipients, RecipientOutcome.Failed, "Domain does not accept mail (null MX)", null);
        // RFC 5321 section 5.1: with no MX records, the domain itself is the implicit MX.
        if (hosts.Count == 0) hosts.Add(domain);

        var lastError = "No reachable mail server";
        foreach (var host in hosts.Take(_o.MaxMxHosts))
        {
            IReadOnlyList<IPAddress> addresses;
            try
            {
                addresses = await dns.AddressesAsync(host, ct);
            }
            catch (DnsTemporaryException ex)
            {
                lastError = $"{host}: {ex.Message}";
                continue;
            }
            foreach (var ip in addresses)
            {
                try
                {
                    return await TransactAsync(host, ip, envelopeFrom, recipients, content, ct);
                }
                catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or AuthenticationException or SmtpTemporaryException)
                {
                    lastError = $"{host} [{ip}]: {ex.Message}";
                    logger.LogInformation("Delivery to {Host} [{Ip}] failed: {Error}", host, ip, ex.Message);
                }
            }
        }
        return AllRecipients(recipients, RecipientOutcome.Deferred, lastError, null);
    }

    private async Task<DeliveryAttempt> TransactAsync(string host, IPAddress ip, string envelopeFrom, IReadOnlyList<string> recipients, byte[] content, CancellationToken ct)
    {
        using var tcp = new TcpClient(ip.AddressFamily);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(_o.ConnectTimeoutSeconds));
            try
            {
                await tcp.ConnectAsync(ip, _o.RemotePort, connectTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("connect timed out");
            }
        }

        Stream stream = tcp.GetStream();
        var conn = new Connection(stream, TimeSpan.FromSeconds(_o.CommandTimeoutSeconds));
        await conn.ExpectAsync(220, "greeting", ct);

        var ehlo = await conn.CommandAsync($"EHLO {serverOptions.Value.Hostname}", ct);
        var capabilities = ehlo.Code == 250 ? ehlo.Lines : (await conn.CommandAsync($"HELO {serverOptions.Value.Hostname}", ct)).Require(250, "HELO").Lines;

        var usedTls = false;
        if (_o.OpportunisticTls && capabilities.Any(c => c.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase)))
        {
            (await conn.CommandAsync("STARTTLS", ct)).Require(220, "STARTTLS");
            var ssl = new SslStream(stream, false, (_, _, _, errors) =>
            {
                // Opportunistic TLS between MTAs encrypts even when certificates are self-signed (RFC 3207 practice);
                // RequireTls demands a valid certificate.
                if (errors != SslPolicyErrors.None) logger.LogDebug("TLS certificate for {Host} has errors: {Errors}", host, errors);
                return !_o.RequireTls || errors == SslPolicyErrors.None;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);
            stream = ssl;
            conn = new Connection(ssl, TimeSpan.FromSeconds(_o.CommandTimeoutSeconds));
            usedTls = true;
            capabilities = (await conn.CommandAsync($"EHLO {serverOptions.Value.Hostname}", ct)).Require(250, "EHLO after STARTTLS").Lines;
        }
        else if (_o.RequireTls)
        {
            throw new SmtpTemporaryException("TLS required but not offered");
        }

        var sizeParam = capabilities.Any(c => c.StartsWith("SIZE", StringComparison.OrdinalIgnoreCase)) ? $" SIZE={content.Length}" : "";
        var bodyParam = capabilities.Any(c => c.Equals("8BITMIME", StringComparison.OrdinalIgnoreCase)) && content.Any(b => b > 127) ? " BODY=8BITMIME" : "";
        var mail = await conn.CommandAsync($"MAIL FROM:<{envelopeFrom}>{sizeParam}{bodyParam}", ct);
        if (mail.Code != 250) return AllRecipients(recipients, Classify(mail.Code), mail.Text, host, usedTls);

        var results = new List<RecipientResult>();
        var accepted = new List<string>();
        foreach (var rcpt in recipients)
        {
            var reply = await conn.CommandAsync($"RCPT TO:<{rcpt}>", ct);
            if (reply.Code is 250 or 251) accepted.Add(rcpt);
            else results.Add(new RecipientResult(rcpt, Classify(reply.Code), reply.Text));
        }

        if (accepted.Count > 0)
        {
            var data = await conn.CommandAsync("DATA", ct);
            if (data.Code != 354)
            {
                results.AddRange(accepted.Select(a => new RecipientResult(a, Classify(data.Code), data.Text)));
            }
            else
            {
                await conn.WriteRawAsync(DotStuff(content), ct);
                var final = await conn.ReadReplyAsync(ct);
                var outcome = final.Code == 250 ? RecipientOutcome.Delivered : Classify(final.Code);
                results.AddRange(accepted.Select(a => new RecipientResult(a, outcome, final.Text)));
            }
        }

        try
        {
            await conn.CommandAsync("QUIT", ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or SmtpTemporaryException)
        {
            // The transaction is already complete; a lost QUIT does not matter.
        }
        return new DeliveryAttempt(results, host, usedTls);
    }

    /// <summary>Escapes leading dots and appends the CRLF.CRLF terminator (RFC 5321 section 4.5.2).</summary>
    internal static byte[] DotStuff(byte[] content)
    {
        using var output = new MemoryStream(content.Length + 64);
        var atLineStart = true;
        foreach (var b in content)
        {
            if (atLineStart && b == '.') output.WriteByte((byte)'.');
            output.WriteByte(b);
            atLineStart = b == '\n';
        }
        if (!atLineStart) output.Write("\r\n"u8);
        output.Write(".\r\n"u8);
        return output.ToArray();
    }

    private static RecipientOutcome Classify(int code) => code is >= 500 and < 600 ? RecipientOutcome.Failed : RecipientOutcome.Deferred;

    private static DeliveryAttempt AllRecipients(IEnumerable<string> recipients, RecipientOutcome outcome, string response, string? host, bool tls = false) =>
        new(recipients.Select(r => new RecipientResult(r, outcome, response)).ToList(), host, tls);

    private sealed record Reply(int Code, IReadOnlyList<string> Lines)
    {
        public string Text => $"{Code} {string.Join(" ", Lines)}".Trim();

        public Reply Require(int code, string step) =>
            Code == code ? this : throw new SmtpTemporaryException($"{step} rejected: {Text}");
    }

    private sealed class Connection(Stream stream, TimeSpan timeout)
    {
        private readonly SmtpLineReader _reader = new(stream);

        public async Task<Reply> CommandAsync(string command, CancellationToken ct)
        {
            await WriteRawAsync(Encoding.ASCII.GetBytes(command + "\r\n"), ct);
            return await ReadReplyAsync(ct);
        }

        public async Task ExpectAsync(int code, string step, CancellationToken ct) => (await ReadReplyAsync(ct)).Require(code, step);

        public async Task WriteRawAsync(byte[] data, CancellationToken ct)
        {
            await stream.WriteAsync(data, ct);
            await stream.FlushAsync(ct);
        }

        public async Task<Reply> ReadReplyAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var lines = new List<string>();
            var code = 0;
            try
            {
                while (true)
                {
                    var line = await _reader.ReadLineAsync(4096, cts.Token) ?? throw new IOException("connection closed by remote server");
                    var text = Encoding.ASCII.GetString(line.Bytes);
                    if (text.Length < 3 || !int.TryParse(text.AsSpan(0, 3), out code)) throw new IOException($"malformed reply: {text}");
                    lines.Add(text.Length > 4 ? text[4..] : "");
                    if (text.Length < 4 || text[3] != '-') break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("remote server timed out");
            }
            // 421 means the server is shutting the channel: treat as a temporary connection failure.
            if (code == 421) throw new SmtpTemporaryException(string.Join(" ", lines));
            return new Reply(code, lines);
        }
    }
}

public sealed class SmtpTemporaryException(string message) : Exception(message);
