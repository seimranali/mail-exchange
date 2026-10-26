using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MX.Application.Abstractions;
using MX.Domain;

namespace MX.Compliance;

public sealed record RetentionRunResult(int QuarantineExpired);

/// <summary>
/// Applies time-based compliance rules for the current tenant scope. Message-level retention
/// is delegated to the mail store; this handles records held in the management database.
/// </summary>
public sealed class RetentionEnforcer(IApplicationDbContext db, IAuditLogger audit, IClock clock, IQuarantineStore quarantine, ILogger<RetentionEnforcer> logger)
{
    public async Task<RetentionRunResult> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        // Messages for mailboxes under an active legal hold are never expired.
        var heldMailboxIds = await db.LegalHolds.Where(h => h.Status == LegalHoldStatus.Active)
            .SelectMany(h => h.MailboxIds).Distinct().ToListAsync(ct);

        var expired = await db.QuarantineRecords
            .Where(r => r.Status == QuarantineStatus.Held && r.ExpiresAt <= now)
            .Where(r => r.MailboxId == null || !heldMailboxIds.Contains(r.MailboxId.Value))
            .ToListAsync(ct);

        foreach (var record in expired) record.Status = QuarantineStatus.Deleted;
        if (expired.Count > 0)
        {
            audit.Record("retention.quarantine-expired", "QuarantineRecord", null, new { count = expired.Count });
            await db.SaveChangesAsync(ct);
            foreach (var record in expired) quarantine.DeleteContent(record.StorageRef);
            logger.LogInformation("Expired {Count} quarantine records", expired.Count);
        }
        return new RetentionRunResult(expired.Count);
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddCompliance(this IServiceCollection services) => services.AddScoped<RetentionEnforcer>();
}
