using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;

namespace MX.Application.Compliance;

public sealed record RetentionPolicyDto(Guid Id, string Name, string? Description, int RetentionDays, RetentionAction Action, bool AppliesToAllMailboxes, bool IsEnabled);
public sealed record SaveRetentionPolicyRequest(string Name, string? Description, int RetentionDays, RetentionAction Action, bool AppliesToAllMailboxes, bool IsEnabled);
public sealed record LegalHoldDto(Guid Id, string Name, string CaseReference, string? Description, LegalHoldStatus Status, int MailboxCount, DateTimeOffset StartsAt, DateTimeOffset? ReleasedAt);
public sealed record CreateLegalHoldRequest(string Name, string CaseReference, string? Description, Guid[] MailboxIds);
public sealed record AuditEventDto(Guid Id, Guid? ActorUserId, string Action, string EntityType, string? EntityId, string? IpAddress, bool IsPrivileged, DateTimeOffset OccurredAt);

public sealed class ComplianceService(IApplicationDbContext db, ITenantContext tenant, IAuditLogger audit, IClock clock)
{
    public async Task<IReadOnlyList<RetentionPolicyDto>> ListRetentionPoliciesAsync(CancellationToken ct) =>
        await db.RetentionPolicies.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new RetentionPolicyDto(p.Id, p.Name, p.Description, p.RetentionDays, p.Action, p.AppliesToAllMailboxes, p.IsEnabled))
            .ToListAsync(ct);

    public async Task<RetentionPolicyDto> SaveRetentionPolicyAsync(Guid? id, SaveRetentionPolicyRequest request, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId ?? throw new ForbiddenException("Select an organization first: platform administrators must choose which organization to manage.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ValidationException(nameof(request.Name), "Name is required.");
        if (request.RetentionDays is < 1 or > 36500) throw new ValidationException(nameof(request.RetentionDays), "Must be between 1 and 36500 days.");

        RetentionPolicy policy;
        if (id is { } existing)
            policy = await db.RetentionPolicies.FirstOrDefaultAsync(p => p.Id == existing, ct) ?? throw new NotFoundException(nameof(RetentionPolicy), existing);
        else
            db.RetentionPolicies.Add(policy = new RetentionPolicy { OrganizationId = orgId, Name = request.Name });

        policy.Name = request.Name.Trim();
        policy.Description = request.Description;
        policy.RetentionDays = request.RetentionDays;
        policy.Action = request.Action;
        policy.AppliesToAllMailboxes = request.AppliesToAllMailboxes;
        policy.IsEnabled = request.IsEnabled;
        audit.Record(id is null ? "retention-policy.created" : "retention-policy.updated", nameof(RetentionPolicy), policy.Id.ToString(), request, privileged: true);
        await db.SaveChangesAsync(ct);
        return new RetentionPolicyDto(policy.Id, policy.Name, policy.Description, policy.RetentionDays, policy.Action, policy.AppliesToAllMailboxes, policy.IsEnabled);
    }

    public async Task<IReadOnlyList<LegalHoldDto>> ListLegalHoldsAsync(CancellationToken ct) =>
        await db.LegalHolds.AsNoTracking().OrderByDescending(h => h.StartsAt)
            .Select(h => new LegalHoldDto(h.Id, h.Name, h.CaseReference, h.Description, h.Status, h.MailboxIds.Length, h.StartsAt, h.ReleasedAt))
            .ToListAsync(ct);

    public async Task<LegalHoldDto> CreateLegalHoldAsync(CreateLegalHoldRequest request, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId ?? throw new ForbiddenException("Select an organization first: platform administrators must choose which organization to manage.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.CaseReference))
            throw new ValidationException(nameof(request.Name), "Name and case reference are required.");
        var ids = request.MailboxIds.Distinct().ToArray();
        // Ensures every mailbox belongs to the caller's organization.
        if (await db.Mailboxes.CountAsync(m => ids.Contains(m.Id), ct) != ids.Length)
            throw new ValidationException(nameof(request.MailboxIds), "One or more mailboxes were not found.");

        var hold = new LegalHold
        {
            OrganizationId = orgId, Name = request.Name.Trim(), CaseReference = request.CaseReference.Trim(), Description = request.Description,
            MailboxIds = ids, CreatedByUserId = tenant.UserId ?? Guid.Empty, StartsAt = clock.UtcNow,
        };
        db.LegalHolds.Add(hold);
        audit.Record("legal-hold.created", nameof(LegalHold), hold.Id.ToString(), new { hold.CaseReference, mailboxes = ids.Length }, privileged: true);
        await db.SaveChangesAsync(ct);
        return new LegalHoldDto(hold.Id, hold.Name, hold.CaseReference, hold.Description, hold.Status, ids.Length, hold.StartsAt, null);
    }

    public async Task ReleaseLegalHoldAsync(Guid id, CancellationToken ct)
    {
        var hold = await db.LegalHolds.FirstOrDefaultAsync(h => h.Id == id, ct) ?? throw new NotFoundException(nameof(LegalHold), id);
        if (hold.Status == LegalHoldStatus.Released) return;
        hold.Status = LegalHoldStatus.Released;
        hold.ReleasedAt = clock.UtcNow;
        audit.Record("legal-hold.released", nameof(LegalHold), id.ToString(), new { hold.CaseReference }, privileged: true);
        await db.SaveChangesAsync(ct);
    }

    public Task<PagedResult<AuditEventDto>> ListAuditEventsAsync(string? action, bool? privilegedOnly, DateTimeOffset? from, DateTimeOffset? to, PageRequest page, CancellationToken ct)
    {
        var q = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(e => e.Action.StartsWith(action));
        if (privilegedOnly == true) q = q.Where(e => e.IsPrivileged);
        if (from is { } f) q = q.Where(e => e.OccurredAt >= f);
        if (to is { } t) q = q.Where(e => e.OccurredAt < t);
        return q.OrderByDescending(e => e.OccurredAt)
            .Select(e => new AuditEventDto(e.Id, e.ActorUserId, e.Action, e.EntityType, e.EntityId, e.IpAddress, e.IsPrivileged, e.OccurredAt))
            .ToPagedAsync(page, ct);
    }
}
