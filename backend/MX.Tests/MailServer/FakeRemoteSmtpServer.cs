using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MX.Tests.MailServer;

/// <summary>A scriptable remote MTA that records what our outbound client sends.</summary>
public sealed class FakeRemoteSmtpServer : IAsyncDisposable
{
    public sealed record Transaction(string MailFrom, List<string> Recipients, string Data);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ConcurrentQueue<Transaction> Transactions { get; } = new();

    /// <summary>Reply to RCPT for a given address (default "250 OK").</summary>
    public Func<string, string> RcptReply { get; set; } = _ => "250 2.1.5 OK";
    /// <summary>Reply after the message data (default "250 queued").</summary>
    public Func<string> DataReply { get; set; } = () => "250 2.0.0 queued";

    public FakeRemoteSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.Latin1);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake.remote ESMTP");
            string? from = null;
            var rcpts = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
            {
                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO")) await writer.WriteAsync("250-fake.remote\r\n250-SIZE 10000000\r\n250 8BITMIME\r\n");
                else if (upper.StartsWith("MAIL FROM:")) { from = Between(line); rcpts.Clear(); await writer.WriteLineAsync("250 OK"); }
                else if (upper.StartsWith("RCPT TO:"))
                {
                    var rcpt = Between(line);
                    var reply = RcptReply(rcpt);
                    if (reply.StartsWith('2')) rcpts.Add(rcpt);
                    await writer.WriteLineAsync(reply);
                }
                else if (upper == "DATA")
                {
                    await writer.WriteLineAsync("354 go ahead");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } d && d != ".") data.Append(d.StartsWith("..") ? d[1..] : d).Append("\r\n");
                    var reply = DataReply();
                    if (reply.StartsWith('2')) Transactions.Enqueue(new Transaction(from ?? "", [.. rcpts], data.ToString()));
                    await writer.WriteLineAsync(reply);
                }
                else if (upper == "QUIT") { await writer.WriteLineAsync("221 bye"); return; }
                else await writer.WriteLineAsync("250 OK");
            }
        }
    }

    private static string Between(string line) => line[(line.IndexOf('<') + 1)..line.IndexOf('>')];

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch (OperationCanceledException) { }
    }
}
