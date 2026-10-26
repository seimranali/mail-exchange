using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MX.MailServer.Acme;
using MX.MailServer.Storage;
using MX.MailServer.Submission;
using MX.MailServer.Tls;

namespace MX.MailServer.Imap;

/// <summary>Accepts IMAP connections on 143 (STARTTLS) and 993 (implicit TLS), with connection limits.</summary>
public sealed class ImapListener(
    IOptions<MailServerOptions> options,
    IOptions<AcmeOptions> acme,
    CertificateProvider certificates,
    IMailboxAuthenticator authenticator,
    IServiceScopeFactory scopes,
    IMessageStore store,
    ILogger<ImapListener> logger) : BackgroundService
{
    private readonly List<(TcpListener Listener, bool ImplicitTls)> _endpoints = [];
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private SemaphoreSlim? _slots;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;
    public IPEndPoint? Endpoint => _endpoints.FirstOrDefault(e => !e.ImplicitTls).Listener?.LocalEndpoint as IPEndPoint;
    public IPEndPoint? TlsEndpoint => _endpoints.FirstOrDefault(e => e.ImplicitTls).Listener?.LocalEndpoint as IPEndPoint;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var services = new ImapSessionServices(o, null, authenticator, scopes, store, logger);
        _slots = new SemaphoreSlim(o.MaxConnections);
        var address = IPAddress.Parse(o.ListenAddress);

        // Port semantics: -1 = disabled, 0 = any free port (tests).
        if (o.ImapPort >= 0) Bind(address, o.ImapPort, implicitTls: false);
        if (o.ImapTlsPort >= 0)
        {
            if (certificates.Current is not null || acme.Value.Enabled) Bind(address, o.ImapTlsPort, implicitTls: true);
            else logger.LogWarning("IMAP implicit-TLS port {Port} disabled: no certificate configured", o.ImapTlsPort);
        }
        foreach (var (listener, tls) in _endpoints)
            logger.LogInformation("IMAP{Tls} listening on {Endpoint}", tls ? " (implicit TLS)" : "", listener.LocalEndpoint);
        _started.TrySetResult();

        try
        {
            await Task.WhenAll(_endpoints.Select(e => AcceptLoopAsync(e.Listener, e.ImplicitTls, services, stoppingToken)));
        }
        finally
        {
            foreach (var (listener, _) in _endpoints) listener.Stop();
        }
    }

    private void Bind(IPAddress address, int port, bool implicitTls)
    {
        var listener = new TcpListener(address, port);
        listener.Start(backlog: 512);
        _endpoints.Add((listener, implicitTls));
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool implicitTls, ImapSessionServices services, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleAsync(client, implicitTls, services with { Certificate = certificates.Current }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient client, bool implicitTls, ImapSessionServices services, CancellationToken ct)
    {
        var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var perIp = _perIp.AddOrUpdate(ip, 1, (_, n) => n + 1);
        var gotSlot = _slots!.Wait(0);
        try
        {
            using (client)
            {
                client.NoDelay = true;
                Stream stream = client.GetStream();
                // Mail apps keep several IMAP connections open per device, so allow more than SMTP does.
                if (!gotSlot || perIp > services.Options.MaxConnectionsPerIp * 3)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("* BYE Too many connections\r\n"), ct);
                    return;
                }
                if (implicitTls)
                {
                    if (services.Certificate is null) return; // certificate not issued yet
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshake.CancelAfter(TimeSpan.FromSeconds(30));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = services.Certificate.Context,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, handshake.Token);
                    stream = ssl;
                }
                await using (stream)
                    await new ImapSession(stream, ip, services, implicitTls).RunAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or AuthenticationException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "IMAP session from {RemoteIp} failed", ip);
        }
        finally
        {
            if (gotSlot) _slots.Release();
            _perIp.AddOrUpdate(ip, 0, (_, n) => Math.Max(0, n - 1));
            if (_perIp.TryGetValue(ip, out var left) && left == 0) _perIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, 0));
        }
    }
}
