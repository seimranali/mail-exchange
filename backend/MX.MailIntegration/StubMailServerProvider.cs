using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MX.Application.Abstractions;
using MX.Domain;

namespace MX.MailIntegration;

/// <summary>
/// In-memory mail server used for development and tests. Lets the management plane run
/// end-to-end without a real MTA. Replace with a concrete adapter per deployment.
/// </summary>
public sealed class StubMailServerProvider(ILogger<StubMailServerProvider> logger) : IMailServerProvider
{
    private readonly ConcurrentDictionary<string, (string DisplayName, long Quota, bool Enabled)> _mailboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public MailServerType ServerType => MailServerType.Stub;

    public Task ProvisionMailboxAsync(MailboxProvisionRequest request, CancellationToken ct)
    {
        _mailboxes[request.EmailAddress] = (request.DisplayName, request.QuotaBytes, true);
        logger.LogInformation("Stub: provisioned {Email}", request.EmailAddress);
        return Task.CompletedTask;
    }

    public Task SetMailboxEnabledAsync(string emailAddress, bool enabled, CancellationToken ct)
    {
        _mailboxes.AddOrUpdate(emailAddress, _ => (emailAddress, 0, enabled), (_, v) => v with { Enabled = enabled });
        return Task.CompletedTask;
    }

    public Task DeleteMailboxAsync(string emailAddress, CancellationToken ct)
    {
        _mailboxes.TryRemove(emailAddress, out _);
        return Task.CompletedTask;
    }

    public Task<MailboxUsage> GetMailboxUsageAsync(string emailAddress, CancellationToken ct)
    {
        // Deterministic pseudo-usage so dashboards have stable numbers in development.
        var seed = Math.Abs(emailAddress.GetHashCode(StringComparison.OrdinalIgnoreCase)) % 1000;
        return Task.FromResult(new MailboxUsage(seed * 12L * 1024 * 1024, seed * 7));
    }

    public Task AddAliasAsync(string aliasAddress, string targetAddress, CancellationToken ct)
    {
        _aliases[aliasAddress] = targetAddress;
        return Task.CompletedTask;
    }

    public Task RemoveAliasAsync(string aliasAddress, CancellationToken ct)
    {
        _aliases.TryRemove(aliasAddress, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueuedMessage>> GetQueueAsync(int limit, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<QueuedMessage> items = Enumerable.Range(1, Math.Min(limit, 8))
            .Select(i => new QueuedMessage($"Q{i:D6}", $"sender{i}@example.net", $"user{i}@example.com", 4096 * i,
                i % 3 == 0 ? "Deferred" : "Active", now.AddMinutes(-i * 3), i % 3 == 0 ? "421 Temporary failure, try again later" : null))
            .ToList();
        return Task.FromResult(items);
    }

    public Task<ServerHealth> GetHealthAsync(CancellationToken ct)
    {
        var minute = DateTimeOffset.UtcNow.Minute;
        return Task.FromResult(new ServerHealth(MailServerStatus.Online, 20 + minute % 30, 45 + minute % 20, 38, minute % 10, 12 + minute % 15));
    }
}

public sealed class MailServerProviderFactory(IEnumerable<IMailServerProvider> providers) : IMailServerProviderFactory
{
    private readonly Dictionary<MailServerType, IMailServerProvider> _byType = providers.ToDictionary(p => p.ServerType);

    public IMailServerProvider Get(MailServerType serverType) =>
        _byType.TryGetValue(serverType, out var provider)
            ? provider
            : throw new NotSupportedException($"No mail server provider is registered for '{serverType}'.");
}

public static class DependencyInjection
{
    public static IServiceCollection AddMailIntegration(this IServiceCollection services)
    {
        services.AddSingleton<IMailServerProvider, StubMailServerProvider>();
        services.AddSingleton<IMailServerProviderFactory, MailServerProviderFactory>();
        return services;
    }
}
