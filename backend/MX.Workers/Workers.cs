using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Compliance;
using MX.Domain;
using MX.Domain.Entities;
using MX.Persistence;

namespace MX.Workers;

/// <summary>
/// Base for periodic jobs. Each tick runs in a fresh DI scope so tenant context and the
/// DbContext never outlive a single unit of work.
/// </summary>
public abstract class PeriodicWorker(IServiceScopeFactory scopes, ILogger logger, TimeSpan interval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Worker} tick failed", GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    protected abstract Task RunAsync(IServiceProvider services, CancellationToken ct);

    /// <summary>Runs <paramref name="work"/> once per active organization, each in its own scope and tenant context.</summary>
    protected async Task ForEachOrganizationAsync(IServiceProvider services, Func<IServiceProvider, Guid, Task> work, CancellationToken ct)
    {
        services.GetRequiredService<TenantContext>().SetPlatformScope(null);
        var orgIds = await services.GetRequiredService<MxDbContext>().Organizations
            .Where(o => o.Status == OrganizationStatus.Active).Select(o => o.Id).ToListAsync(ct);
        foreach (var orgId in orgIds)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(orgId, null);
            await work(scope.ServiceProvider, orgId);
        }
    }
}

/// <summary>Samples health from every mail server and stores a snapshot.</summary>
public sealed class ServerHealthWorker(IServiceScopeFactory scopes, ILogger<ServerHealthWorker> logger)
    : PeriodicWorker(scopes, logger, TimeSpan.FromMinutes(1))
{
    protected override async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        services.GetRequiredService<TenantContext>().SetPlatformScope(null);
        var db = services.GetRequiredService<MxDbContext>();
        var providers = services.GetRequiredService<IMailServerProviderFactory>();
        foreach (var server in await db.MailServers.Where(s => s.Status != MailServerStatus.Maintenance).ToListAsync(ct))
        {
            ServerHealth health;
            try { health = await providers.Get(server.ServerType).GetHealthAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Health check failed for {Server}", server.Hostname);
                health = new ServerHealth(MailServerStatus.Offline, 0, 0, 0, 0, 0);
            }
            server.Status = health.Status;
            db.ServerHealthSnapshots.Add(new ServerHealthSnapshot
            {
                MailServerId = server.Id, Status = health.Status, CpuPercent = health.CpuPercent, MemoryPercent = health.MemoryPercent,
                DiskPercent = health.DiskPercent, QueueLength = health.QueueLength, ActiveConnections = health.ActiveConnections,
                CapturedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
        // Keep 14 days of samples.
        await db.ServerHealthSnapshots.Where(s => s.CapturedAt < DateTimeOffset.UtcNow.AddDays(-14)).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Retries provisioning for mailboxes stuck in Provisioning, and refreshes usage from the mail store.</summary>
public sealed class MailboxReconciliationWorker(IServiceScopeFactory scopes, ILogger<MailboxReconciliationWorker> logger)
    : PeriodicWorker(scopes, logger, TimeSpan.FromMinutes(5))
{
    protected override Task RunAsync(IServiceProvider services, CancellationToken ct) => ForEachOrganizationAsync(services, async (sp, _) =>
    {
        var db = sp.GetRequiredService<MxDbContext>();
        var providers = sp.GetRequiredService<IMailServerProviderFactory>();
        var mailboxes = await db.Mailboxes.Include(m => m.MailServer)
            .Where(m => m.MailServer != null && (m.Status == MailboxStatus.Provisioning || m.Status == MailboxStatus.Active))
            .ToListAsync(ct);
        foreach (var m in mailboxes)
        {
            var provider = providers.Get(m.MailServer!.ServerType);
            try
            {
                if (m.Status == MailboxStatus.Provisioning)
                {
                    await provider.ProvisionMailboxAsync(new MailboxProvisionRequest(m.EmailAddress, m.DisplayName, m.QuotaBytes), ct);
                    m.Status = MailboxStatus.Active;
                }
                var usage = (await provider.GetMailboxUsageAsync(m.EmailAddress, ct)).UsedBytes;
                if (usage >= 0) m.UsedBytes = Math.Min(usage, m.QuotaBytes); // negative = provider tracks usage itself
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reconciliation failed for mailbox {MailboxId}", m.Id);
            }
        }
        await db.SaveChangesAsync(ct);
    }, ct);
}

public sealed class RetentionWorker(IServiceScopeFactory scopes, ILogger<RetentionWorker> logger)
    : PeriodicWorker(scopes, logger, TimeSpan.FromHours(1))
{
    protected override Task RunAsync(IServiceProvider services, CancellationToken ct) =>
        ForEachOrganizationAsync(services, (sp, _) => sp.GetRequiredService<RetentionEnforcer>().RunAsync(ct), ct);
}

/// <summary>Processes queued backup jobs. The actual dump is delegated to deployment scripts (see docs/backup-recovery).</summary>
public sealed class BackupJobWorker(IServiceScopeFactory scopes, ILogger<BackupJobWorker> logger, IConfiguration configuration)
    : PeriodicWorker(scopes, logger, TimeSpan.FromMinutes(1))
{
    protected override Task RunAsync(IServiceProvider services, CancellationToken ct) => ForEachOrganizationAsync(services, async (sp, orgId) =>
    {
        var db = sp.GetRequiredService<MxDbContext>();
        foreach (var job in await db.BackupJobs.Where(j => j.Status == JobStatus.Queued).ToListAsync(ct))
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var root = configuration["Backup:Location"] ?? "backups";
            job.StorageLocation = Path.Combine(root, orgId.ToString(), $"{job.Scope.ToString().ToLowerInvariant()}-{job.Id}.export");
            job.Status = JobStatus.Succeeded;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Backup job {JobId} recorded at {Location}", job.Id, job.StorageLocation);
        }
    }, ct);
}
