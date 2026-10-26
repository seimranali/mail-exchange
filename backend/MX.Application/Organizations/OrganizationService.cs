using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;

namespace MX.Application.Organizations;

public sealed record OrganizationDto(Guid Id, string Name, string Slug, OrganizationStatus Status, int DomainCount, int MailboxCount, DateTimeOffset CreatedAt);
public sealed record CreateOrganizationRequest(string Name, string Slug, SubscriptionPlan Plan = SubscriptionPlan.Starter, int MailboxLimit = 50);
public sealed record UpdateOrganizationRequest(string Name, OrganizationStatus Status);

public sealed class OrganizationService(IApplicationDbContext db, ITenantContext tenant, IAuditLogger audit, IClock clock)
{
    public Task<PagedResult<OrganizationDto>> ListAsync(PageRequest page, string? search, CancellationToken ct)
    {
        // Organizations are filtered to the caller's own organization unless in platform scope.
        var query = db.Organizations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(o => o.Name.ToLower().Contains(term) || o.Slug.Contains(term));
        }

        return query.OrderBy(o => o.Name).Select(Project(db)).ToPagedAsync(page, ct);
    }

    public async Task<OrganizationDto> GetAsync(Guid id, CancellationToken ct) =>
        await db.Organizations.AsNoTracking().Where(o => o.Id == id).Select(Project(db)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException(nameof(Organization), id);

    public async Task<OrganizationDto> CreateAsync(CreateOrganizationRequest request, CancellationToken ct)
    {
        if (!tenant.IsPlatformScope) throw new ForbiddenException();
        var slug = request.Slug.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ValidationException(nameof(request.Name), "Name is required.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{1,62}$"))
            throw new ValidationException(nameof(request.Slug), "Slug must be 2-63 lowercase letters, digits or hyphens.");
        if (await db.Organizations.AnyAsync(o => o.Slug == slug, ct))
            throw new ConflictException($"An organization with slug '{slug}' already exists.");

        var org = new Organization { Name = request.Name.Trim(), Slug = slug };
        org.Subscription = new OrganizationSubscription
        {
            OrganizationId = org.Id,
            Plan = request.Plan,
            MailboxLimit = request.MailboxLimit,
            StorageLimitBytes = request.MailboxLimit * 50L * 1024 * 1024 * 1024,
            StartsAt = clock.UtcNow,
        };
        db.Organizations.Add(org);
        db.SecurityPolicies.Add(new SecurityPolicy { OrganizationId = org.Id });
        audit.Record("organization.created", nameof(Organization), org.Id.ToString(), new { org.Name, org.Slug }, privileged: true);
        await db.SaveChangesAsync(ct);
        return await GetAsync(org.Id, ct);
    }

    public async Task<OrganizationDto> UpdateAsync(Guid id, UpdateOrganizationRequest request, CancellationToken ct)
    {
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException(nameof(Organization), id);
        if (org.Status != request.Status && !tenant.IsPlatformScope) throw new ForbiddenException("Only platform administrators can change organization status.");
        org.Name = request.Name.Trim();
        org.Status = request.Status;
        audit.Record("organization.updated", nameof(Organization), id.ToString(), request, privileged: true);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private static System.Linq.Expressions.Expression<Func<Organization, OrganizationDto>> Project(IApplicationDbContext db) =>
        o => new OrganizationDto(o.Id, o.Name, o.Slug, o.Status,
            o.Domains.Count,
            db.Mailboxes.Count(m => m.OrganizationId == o.Id && m.Status != MailboxStatus.Deleted),
            o.CreatedAt);
}
