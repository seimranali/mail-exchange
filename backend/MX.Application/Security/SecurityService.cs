using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;

namespace MX.Application.Security;

public sealed record SecurityAlertDto(Guid Id, string Title, string Description, string Category, AlertSeverity Severity, AlertStatus Status, Guid? MailboxId, DateTimeOffset DetectedAt, DateTimeOffset? ResolvedAt);
public sealed record QuarantineDto(Guid Id, string MessageId, string Sender, string Recipient, string? Subject, QuarantineReason Reason, QuarantineStatus Status, decimal? SpamScore, long SizeBytes, DateTimeOffset ReceivedAt, DateTimeOffset ExpiresAt);
public sealed record SecurityPolicyDto(int PasswordMinLength, bool RequireMfa, int SessionTimeoutMinutes, int MaxFailedLogins, int LockoutMinutes, bool EnforceTls, string[] AllowedIpRanges);
public sealed record AuthenticationEventDto(Guid Id, string AttemptedEmail, AuthenticationOutcome Outcome, string? IpAddress, DateTimeOffset OccurredAt);

public sealed class SecurityService(IApplicationDbContext db, ITenantContext tenant, IAuditLogger audit, IClock clock, IQuarantineStore quarantine)
{
    public Task<PagedResult<SecurityAlertDto>> ListAlertsAsync(AlertStatus? status, AlertSeverity? severity, PageRequest page, CancellationToken ct)
    {
        var q = db.SecurityAlerts.AsNoTracking();
        if (status is { } s) q = q.Where(a => a.Status == s);
        if (severity is { } sev) q = q.Where(a => a.Severity == sev);
        return q.OrderByDescending(a => a.DetectedAt)
            .Select(a => new SecurityAlertDto(a.Id, a.Title, a.Description, a.Category, a.Severity, a.Status, a.MailboxId, a.DetectedAt, a.ResolvedAt))
            .ToPagedAsync(page, ct);
    }

    public async Task UpdateAlertStatusAsync(Guid id, AlertStatus status, CancellationToken ct)
    {
        var alert = await db.SecurityAlerts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException(nameof(SecurityAlert), id);
        alert.Status = status;
        if (status == AlertStatus.Acknowledged) alert.AcknowledgedByUserId = tenant.UserId;
        if (status is AlertStatus.Resolved or AlertStatus.Dismissed) alert.ResolvedAt = clock.UtcNow;
        audit.Record("security-alert.status-changed", nameof(SecurityAlert), id.ToString(), new { status });
        await db.SaveChangesAsync(ct);
    }

    public Task<PagedResult<QuarantineDto>> ListQuarantineAsync(QuarantineStatus? status, string? search, PageRequest page, CancellationToken ct)
    {
        var q = db.QuarantineRecords.AsNoTracking();
        if (status is { } s) q = q.Where(r => r.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            q = q.Where(r => r.Sender.ToLower().Contains(term) || r.Recipient.ToLower().Contains(term) || (r.Subject != null && r.Subject.ToLower().Contains(term)));
        }
        return q.OrderByDescending(r => r.ReceivedAt)
            .Select(r => new QuarantineDto(r.Id, r.MessageId, r.Sender, r.Recipient, r.Subject, r.Reason, r.Status, r.SpamScore, r.SizeBytes, r.ReceivedAt, r.ExpiresAt))
            .ToPagedAsync(page, ct);
    }

    public async Task ReleaseQuarantineAsync(Guid id, CancellationToken ct)
    {
        var record = await db.QuarantineRecords.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException(nameof(QuarantineRecord), id);
        if (record.Status != QuarantineStatus.Held) throw new ConflictException("Only held messages can be released.");
        if (record.Reason == QuarantineReason.Malware) throw new ConflictException("Messages quarantined for malware cannot be released.");
        // Deliver first: if the content is gone, the record stays Held and the admin is told why.
        if (!await quarantine.DeliverReleasedAsync(record, ct))
            throw new ConflictException("The quarantined message content is no longer available.");
        record.Status = QuarantineStatus.Released;
        record.ReleasedByUserId = tenant.UserId;
        audit.Record("quarantine.released", nameof(QuarantineRecord), id.ToString(), new { record.Sender, record.Recipient, record.Reason }, privileged: true);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteQuarantineAsync(Guid id, CancellationToken ct)
    {
        var record = await db.QuarantineRecords.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException(nameof(QuarantineRecord), id);
        record.Status = QuarantineStatus.Deleted;
        audit.Record("quarantine.deleted", nameof(QuarantineRecord), id.ToString());
        await db.SaveChangesAsync(ct);
        quarantine.DeleteContent(record.StorageRef);
    }

    public async Task<SecurityPolicyDto> GetPolicyAsync(CancellationToken ct)
    {
        var p = await db.SecurityPolicies.AsNoTracking().FirstOrDefaultAsync(ct) ?? new SecurityPolicy();
        return new SecurityPolicyDto(p.PasswordMinLength, p.RequireMfa, p.SessionTimeoutMinutes, p.MaxFailedLogins, p.LockoutMinutes, p.EnforceTls, p.AllowedIpRanges);
    }

    public async Task<SecurityPolicyDto> UpdatePolicyAsync(SecurityPolicyDto dto, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId ?? throw new ForbiddenException("Select an organization first: platform administrators must choose which organization to manage.");
        if (dto.PasswordMinLength is < 8 or > 128) throw new ValidationException(nameof(dto.PasswordMinLength), "Must be between 8 and 128.");
        if (dto.MaxFailedLogins is < 1 or > 50) throw new ValidationException(nameof(dto.MaxFailedLogins), "Must be between 1 and 50.");
        var p = await db.SecurityPolicies.FirstOrDefaultAsync(ct);
        if (p is null) db.SecurityPolicies.Add(p = new SecurityPolicy { OrganizationId = orgId });
        p.PasswordMinLength = dto.PasswordMinLength;
        p.RequireMfa = dto.RequireMfa;
        p.SessionTimeoutMinutes = dto.SessionTimeoutMinutes;
        p.MaxFailedLogins = dto.MaxFailedLogins;
        p.LockoutMinutes = dto.LockoutMinutes;
        p.EnforceTls = dto.EnforceTls;
        p.AllowedIpRanges = dto.AllowedIpRanges;
        audit.Record("security-policy.updated", nameof(SecurityPolicy), p.Id.ToString(), dto, privileged: true);
        await db.SaveChangesAsync(ct);
        return await GetPolicyAsync(ct);
    }

    public Task<PagedResult<AuthenticationEventDto>> ListAuthenticationEventsAsync(PageRequest page, CancellationToken ct) =>
        db.AuthenticationEvents.AsNoTracking().OrderByDescending(e => e.OccurredAt)
            .Select(e => new AuthenticationEventDto(e.Id, e.AttemptedEmail, e.Outcome, e.IpAddress, e.OccurredAt))
            .ToPagedAsync(page, ct);
}
