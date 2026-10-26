namespace MX.MailServer.Outbound;

public sealed class OutboundOptions
{
    public const string Section = "Outbound";

    /// <summary>Remote SMTP port. Always 25 in production; tests point it at a local fake server.</summary>
    public int RemotePort { get; set; } = 25;
    public int ConnectTimeoutSeconds { get; set; } = 30;
    public int CommandTimeoutSeconds { get; set; } = 300;
    /// <summary>Delivery stops (and the sender gets a bounce) after this long.</summary>
    public int MaxQueueLifetimeHours { get; set; } = 120;
    public int BatchSize { get; set; } = 50;
    public int MaxConcurrentDeliveries { get; set; } = 10;
    public int PollIntervalSeconds { get; set; } = 10;
    /// <summary>Use STARTTLS when the remote offers it (opportunistic TLS, the norm between MTAs).</summary>
    public bool OpportunisticTls { get; set; } = true;
    /// <summary>Refuse to deliver over plaintext or to hosts with invalid certificates.</summary>
    public bool RequireTls { get; set; }
    public int MaxMxHosts { get; set; } = 5;
}

public static class RetrySchedule
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(40),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4),
    ];

    /// <summary>Delay before the next attempt after <paramref name="attempts"/> tries (capped at 4 hours).</summary>
    public static TimeSpan After(int attempts) => Delays[Math.Clamp(attempts - 1, 0, Delays.Length - 1)];
}
