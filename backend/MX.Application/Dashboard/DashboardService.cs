using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Domain;

namespace MX.Application.Dashboard;

public sealed record DashboardSummary(
    int ActiveMailboxes, int MailboxLimit, long StorageUsedBytes, long StorageLimitBytes,
    int Domains, int VerifiedDomains, int OpenAlerts, int CriticalAlerts, int QuarantineHeld,
    IReadOnlyList<ServerStatusDto> Servers, IReadOnlyList<RecentActivityDto> RecentActivity);
public sealed record ServerStatusDto(Guid Id, string Name, string Hostname, MailServerStatus Status, decimal? CpuPercent, decimal? MemoryPercent, decimal? DiskPercent, int? QueueLength, DateTimeOffset? CapturedAt);
public sealed record RecentActivityDto(Guid Id, string Action, string EntityType, DateTimeOffset OccurredAt);

public sealed class DashboardService(IApplicationDbContext db)
{
    public async Task<DashboardSummary> GetAsync(CancellationToken ct)
    {
        var mailboxes = db.Mailboxes.Where(m => m.Status != MailboxStatus.Deleted);
        var sub = await db.OrganizationSubscriptions.AsNoTracking().FirstOrDefaultAsync(ct);

        var servers = await db.MailServers.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new { s, h = db.ServerHealthSnapshots.Where(h => h.MailServerId == s.Id).OrderByDescending(h => h.CapturedAt).FirstOrDefault() })
            .Select(x => new ServerStatusDto(x.s.Id, x.s.Name, x.s.Hostname, x.s.Status,
                x.h != null ? x.h.CpuPercent : null, x.h != null ? x.h.MemoryPercent : null, x.h != null ? x.h.DiskPercent : null,
                x.h != null ? x.h.QueueLength : null, x.h != null ? x.h.CapturedAt : null))
            .ToListAsync(ct);

        var activity = await db.AuditEvents.AsNoTracking().OrderByDescending(e => e.OccurredAt).Take(10)
            .Select(e => new RecentActivityDto(e.Id, e.Action, e.EntityType, e.OccurredAt)).ToListAsync(ct);

        return new DashboardSummary(
            await mailboxes.CountAsync(m => m.Status == MailboxStatus.Active, ct),
            sub?.MailboxLimit ?? 0,
            await mailboxes.SumAsync(m => (long?)m.UsedBytes, ct) ?? 0,
            sub?.StorageLimitBytes ?? 0,
            await db.OrganizationDomains.CountAsync(ct),
            await db.OrganizationDomains.CountAsync(d => d.VerificationStatus == DomainVerificationStatus.Verified, ct),
            await db.SecurityAlerts.CountAsync(a => a.Status == AlertStatus.Open || a.Status == AlertStatus.Acknowledged, ct),
            await db.SecurityAlerts.CountAsync(a => a.Status == AlertStatus.Open && a.Severity == AlertSeverity.Critical, ct),
            await db.QuarantineRecords.CountAsync(r => r.Status == QuarantineStatus.Held, ct),
            servers, activity);
    }
}
