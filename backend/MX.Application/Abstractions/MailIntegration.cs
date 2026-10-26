using MX.Domain;

namespace MX.Application.Abstractions;

/// <summary>
/// Contract for a concrete mail server. Implementations talk to the mail system's own
/// APIs and storage; they never read or write the management database.
/// </summary>
public interface IMailServerProvider
{
    MailServerType ServerType { get; }

    Task ProvisionMailboxAsync(MailboxProvisionRequest request, CancellationToken ct);
    Task SetMailboxEnabledAsync(string emailAddress, bool enabled, CancellationToken ct);
    Task DeleteMailboxAsync(string emailAddress, CancellationToken ct);
    Task<MailboxUsage> GetMailboxUsageAsync(string emailAddress, CancellationToken ct);
    Task AddAliasAsync(string aliasAddress, string targetAddress, CancellationToken ct);
    Task RemoveAliasAsync(string aliasAddress, CancellationToken ct);
    Task<IReadOnlyList<QueuedMessage>> GetQueueAsync(int limit, CancellationToken ct);
    Task<ServerHealth> GetHealthAsync(CancellationToken ct);
}

public interface IMailServerProviderFactory
{
    IMailServerProvider Get(MailServerType serverType);
}

public interface IDnsVerifier
{
    Task<DnsCheckResult> CheckAsync(string domainName, string verificationToken, string? dkimSelector, CancellationToken ct);
}

public sealed record MailboxProvisionRequest(string EmailAddress, string DisplayName, long QuotaBytes);
public sealed record MailboxUsage(long UsedBytes, int MessageCount);
public sealed record QueuedMessage(string QueueId, string Sender, string Recipient, long SizeBytes, string State, DateTimeOffset QueuedAt, string? LastError,
    int Attempts = 0, DateTimeOffset? NextAttemptAt = null, string? RemoteHost = null, string? Subject = null, DateTimeOffset? DeliveredAt = null);
public sealed record ServerHealth(MailServerStatus Status, decimal CpuPercent, decimal MemoryPercent, decimal DiskPercent, int QueueLength, int ActiveConnections);
public sealed record DnsCheckResult(bool OwnershipVerified, bool Mx, bool Spf, bool Dkim, bool Dmarc);
