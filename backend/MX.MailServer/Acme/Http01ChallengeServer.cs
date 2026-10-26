using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MX.MailServer.Acme;

/// <summary>
/// Tiny HTTP server for ACME HTTP-01 validation: answers GET /.well-known/acme-challenge/{token}
/// while an order is in progress, and 404 for everything else. No web framework involved.
/// </summary>
public sealed class Http01ChallengeServer(IOptions<AcmeOptions> options, ILogger<Http01ChallengeServer> logger) : BackgroundService
{
    private const string Prefix = "/.well-known/acme-challenge/";
    private readonly ConcurrentDictionary<string, string> _responses = new();
    private TcpListener? _listener;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;
    public int? Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port;

    public void Publish(string token, string keyAuthorization) => _responses[token] = keyAuthorization;
    public void Remove(string token) => _responses.TryRemove(token, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled || o.HttpPort < 0) { _started.TrySetResult(); return; }
        _listener = new TcpListener(IPAddress.Parse(o.ListenAddress), o.HttpPort);
        _listener.Start();
        logger.LogInformation("ACME HTTP-01 responder listening on {Endpoint}", _listener.LocalEndpoint);
        _started.TrySetResult();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var requestLine = await reader.ReadLineAsync(timeout.Token) ?? "";
                string? host = null;
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header)
                    if (header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = header[5..].Trim().Split(':')[0];

                var parts = requestLine.Split(' ');
                var path = parts.Length >= 2 ? parts[1] : "";
                var found = parts.Length >= 2 && parts[0] == "GET" && path.StartsWith(Prefix, StringComparison.Ordinal)
                    && _responses.TryGetValue(path[Prefix.Length..], out var body) ? body : null;
                // Browsers typing http:// get sent to the HTTPS admin UI — only for our own names (no open redirect).
                var redirect = found is null && host is not null && path.StartsWith('/') && !path.StartsWith(Prefix, StringComparison.Ordinal)
                    && options.Value.Domains.Contains(host, StringComparer.OrdinalIgnoreCase);
                var response = found is not null
                    ? $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {found.Length}\r\nConnection: close\r\n\r\n{found}"
                    : redirect
                        ? $"HTTP/1.1 301 Moved Permanently\r\nLocation: https://{host!.ToLowerInvariant()}{path}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }
}
