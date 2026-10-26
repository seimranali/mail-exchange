using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;

namespace MX.Application.Mail;

public sealed record DomainDto(Guid Id, string DomainName, bool IsDefault, DomainVerificationStatus VerificationStatus, string VerificationToken,
    bool MxConfigured, bool SpfConfigured, bool DkimConfigured, bool DmarcConfigured, DateTimeOffset? VerifiedAt, DateTimeOffset? CertificateExpiresAt, int MailboxCount,
    string? DkimSelector, string? DkimPublicKey);
public sealed record CreateDomainRequest(string DomainName, bool IsDefault = false);

public sealed partial class DomainService(IApplicationDbContext db, ITenantContext tenant, IDnsVerifier dns, IAuditLogger audit, IClock clock)
{
    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();

    public Task<PagedResult<DomainDto>> ListAsync(PageRequest page, CancellationToken ct) =>
        db.OrganizationDomains.AsNoTracking().OrderBy(d => d.DomainName).Select(Project(db)).ToPagedAsync(page, ct);

    public async Task<DomainDto> GetAsync(Guid id, CancellationToken ct) =>
        await db.OrganizationDomains.AsNoTracking().Where(d => d.Id == id).Select(Project(db)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Domain", id);

    public async Task<DomainDto> CreateAsync(CreateDomainRequest request, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId ?? throw new ForbiddenException("Select an organization first: platform administrators must choose which organization to manage.");
        var name = request.DomainName.Trim().TrimEnd('.').ToLowerInvariant();
        if (!DomainPattern().IsMatch(name)) throw new ValidationException(nameof(request.DomainName), "Enter a valid domain name.");
        // Domain names are globally unique across tenants; this check intentionally ignores tenant filters.
        if (await db.OrganizationDomains.IgnoreQueryFilters().AnyAsync(d => d.DomainName == name, ct))
            throw new ConflictException($"Domain '{name}' is already registered.");

        var hasDefault = await db.OrganizationDomains.AnyAsync(d => d.IsDefault, ct);
        if (request.IsDefault && hasDefault)
            await db.OrganizationDomains.Where(d => d.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(d => d.IsDefault, false), ct);

        var domain = new OrganizationDomain { OrganizationId = orgId, DomainName = name, IsDefault = request.IsDefault || !hasDefault };
        db.OrganizationDomains.Add(domain);
        audit.Record("domain.created", "Domain", domain.Id.ToString(), new { name });
        await db.SaveChangesAsync(ct);
        return await GetAsync(domain.Id, ct);
    }

    public async Task<DomainDto> VerifyAsync(Guid id, CancellationToken ct)
    {
        var domain = await db.OrganizationDomains.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Domain", id);
        var selector = await db.DkimKeys.Where(k => k.DomainId == id && k.IsActive).Select(k => k.Selector).FirstOrDefaultAsync(ct);
        var result = await dns.CheckAsync(domain.DomainName, domain.VerificationToken, selector, ct);
        domain.MxConfigured = result.Mx;
        domain.SpfConfigured = result.Spf;
        domain.DkimConfigured = result.Dkim;
        domain.DmarcConfigured = result.Dmarc;
        // Ownership is proven once; a later DNS glitch or a removed token must not stop mail for a live domain.
        if (result.OwnershipVerified || domain.VerificationStatus != DomainVerificationStatus.Verified)
            domain.VerificationStatus = result.OwnershipVerified ? DomainVerificationStatus.Verified : DomainVerificationStatus.Failed;
        domain.VerifiedAt = result.OwnershipVerified ? clock.UtcNow : domain.VerifiedAt;
        audit.Record("domain.verified", "Domain", id.ToString(), result);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var domain = await db.OrganizationDomains.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Domain", id);
        if (await db.Mailboxes.AnyAsync(m => m.DomainId == id && m.Status != MailboxStatus.Deleted, ct))
            throw new ConflictException("Remove all mailboxes on this domain before deleting it.");
        db.OrganizationDomains.Remove(domain);
        audit.Record("domain.deleted", "Domain", id.ToString(), new { domain.DomainName }, privileged: true);
        await db.SaveChangesAsync(ct);
    }

    private static System.Linq.Expressions.Expression<Func<OrganizationDomain, DomainDto>> Project(IApplicationDbContext db) =>
        d => new DomainDto(d.Id, d.DomainName, d.IsDefault, d.VerificationStatus, d.VerificationToken, d.MxConfigured, d.SpfConfigured,
            d.DkimConfigured, d.DmarcConfigured, d.VerifiedAt, d.CertificateExpiresAt,
            db.Mailboxes.Count(m => m.DomainId == d.Id && m.Status != MailboxStatus.Deleted),
            db.DkimKeys.Where(k => k.DomainId == d.Id && k.IsActive).Select(k => k.Selector).FirstOrDefault(),
            db.DkimKeys.Where(k => k.DomainId == d.Id && k.IsActive).Select(k => k.PublicKey).FirstOrDefault());
}
