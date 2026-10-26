using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MX.MailServer.Acme;
using MX.MailServer.Authentication;
using MX.MailServer.Delivery;
using MX.MailServer.Filtering;
using MX.MailServer.Submission;
using MX.MailServer.Tls;

namespace MX.MailServer.Smtp;

/// <summary>
/// Accepts TCP connections on the SMTP ports and runs a session per connection, with connection
/// limits: port 25 (inbound MX), 587 (submission + STARTTLS) and 465 (submission over implicit TLS).
/// The TLS certificate is read per connection, so renewals apply without a restart.
/// </summary>
public sealed class SmtpListener(
    IOptions<MailServerOptions> options,
    IOptions<AcmeOptions> acme,
    CertificateProvider certificates,
    IRecipientDirectory directory,
    ILocalDelivery delivery,
    SpfEvaluator spf,
    DkimChecker dkim,
    DmarcEvaluator dmarc,
    IMailboxAuthenticator authenticator,
    ISubmissionHandler submission,
    IContentFilter filter,
    IQuarantine quarantine,
    ILogger<SmtpListener> logger) : BackgroundService
{
    private sealed record Endpoint(TcpListener Listener, SmtpMode Mode, bool ImplicitTls);

    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private readonly List<Endpoint> _endpoints = [];
    private SemaphoreSlim? _slots;

    /// <summary>Bound inbound endpoint (useful when configured with port 0 in tests).</summary>
    public IPEndPoint? LocalEndpoint => Find(SmtpMode.Inbound, false);
    public IPEndPoint? SubmissionEndpoint => Find(SmtpMode.Submission, false);
    public IPEndPoint? SubmissionTlsEndpoint => Find(SmtpMode.Submission, true);

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Started => _started.Task;

    private IPEndPoint? Find(SmtpMode mode, bool implicitTls) =>
        _endpoints.FirstOrDefault(e => e.Mode == mode && e.ImplicitTls == implicitTls)?.Listener.LocalEndpoint as IPEndPoint;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var services = new SmtpSessionServices(o, null, directory, delivery, spf, dkim, dmarc, authenticator, submission, filter, quarantine, logger);
        _slots = new SemaphoreSlim(o.MaxConnections);
        var address = IPAddress.Parse(o.ListenAddress);

        // Port semantics: -1 = disabled, 0 = any free port (tests), otherwise the given port.
        if (o.SmtpPort >= 0) Bind(address, o.SmtpPort, SmtpMode.Inbound, implicitTls: false);
        if (o.SubmissionPort >= 0) Bind(address, o.SubmissionPort, SmtpMode.Submission, implicitTls: false);
        if (o.SubmissionTlsPort >= 0)
        {
            // Bind when a certificate exists now or ACME will obtain one shortly.
            if (certificates.Current is not null || acme.Value.Enabled) Bind(address, o.SubmissionTlsPort, SmtpMode.Submission, implicitTls: true);
            else logger.LogWarning("Implicit-TLS submission port {Port} disabled: no certificate configured", o.SubmissionTlsPort);
        }

        foreach (var e in _endpoints)
            logger.LogInformation("SMTP {Mode}{Tls} listening on {Endpoint} as {Hostname}", e.Mode, e.ImplicitTls ? " (implicit TLS)" : "", e.Listener.LocalEndpoint, o.Hostname);
        _started.TrySetResult();

        try
        {
            await Task.WhenAll(_endpoints.Select(e => AcceptLoopAsync(e, services, stoppingToken)));
        }
        finally
        {
            foreach (var e in _endpoints) e.Listener.Stop();
        }
    }

    private void Bind(IPAddress address, int port, SmtpMode mode, bool implicitTls)
    {
        var listener = new TcpListener(address, port);
        listener.Start(backlog: 512);
        _endpoints.Add(new Endpoint(listener, mode, implicitTls));
    }

    private async Task AcceptLoopAsync(Endpoint endpoint, SmtpSessionServices services, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await endpoint.Listener.AcceptTcpClientAsync(ct);
                _ = HandleAsync(client, endpoint, services with { Certificate = certificates.Current }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient client, Endpoint endpoint, SmtpSessionServices services, CancellationToken ct)
    {
        var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var o = services.Options;
        var perIp = _perIp.AddOrUpdate(ip, 1, (_, n) => n + 1);
        var gotSlot = _slots!.Wait(0);
        try
        {
            using (client)
            {
                client.NoDelay = true;
                Stream stream = client.GetStream();
                if (!gotSlot || perIp > o.MaxConnectionsPerIp)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"421 4.7.0 {o.Hostname} Too many connections, try again later\r\n"), ct);
                    return;
                }
                if (endpoint.ImplicitTls)
                {
                    if (services.Certificate is null) return; // certificate not issued yet
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = services.Certificate.Context,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, handshakeTimeout.Token);
                    stream = ssl;
                }
                await using (stream)
                    await new SmtpSession(stream, ip, services, endpoint.Mode, endpoint.ImplicitTls).RunAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or AuthenticationException)
        {
            // Normal for clients that drop connections or fail the TLS handshake.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMTP session from {RemoteIp} failed", ip);
        }
        finally
        {
            if (gotSlot) _slots.Release();
            _perIp.AddOrUpdate(ip, 0, (_, n) => Math.Max(0, n - 1));
            if (_perIp.TryGetValue(ip, out var left) && left == 0) _perIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, 0));
        }
    }
}
