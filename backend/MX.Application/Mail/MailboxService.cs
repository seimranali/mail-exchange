using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;

namespace MX.Application.Mail;

public sealed record MailboxDto(Guid Id, Guid DomainId, string EmailAddress, string DisplayName, MailboxType MailboxType, MailboxStatus Status,
    long QuotaBytes, long UsedBytes, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt, IReadOnlyList<string> Aliases, bool HasClientPassword);
public sealed record CreateMailboxRequest(Guid DomainId, string LocalPart, string DisplayName, MailboxType MailboxType = MailboxType.User, long QuotaBytes = 50L * 1024 * 1024 * 1024);
public sealed record UpdateMailboxRequest(string DisplayName, long QuotaBytes, bool Enabled);
public sealed record MailboxQuery(string? Search, MailboxStatus? Status, MailboxType? Type, Guid? DomainId, string? Sort, bool Descending);

public sealed partial class MailboxService(
    IApplicationDbContext db, ITenantContext tenant, IMailServerProviderFactory providers, IAuditLogger audit, IPasswordHasher hasher, IClock clock, ILogger<MailboxService> logger)
{
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9._+-]{0,62}[a-z0-9])?$")]
    private static partial Regex LocalPartPattern();

    public Task<PagedResult<MailboxDto>> ListAsync(MailboxQuery filter, PageRequest page, CancellationToken ct)
    {
        var query = db.Mailboxes.AsNoTracking().Where(m => m.Status != MailboxStatus.Deleted);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(m => m.EmailAddress.Contains(term) || m.DisplayName.ToLower().Contains(term));
        }
        if (filter.Status is { } status) query = query.Where(m => m.Status == status);
        if (filter.Type is { } type) query = query.Where(m => m.MailboxType == type);
        if (filter.DomainId is { } domainId) query = query.Where(m => m.DomainId == domainId);

        query = (filter.Sort?.ToLowerInvariant(), filter.Descending) switch
        {
            ("displayname", false) => query.OrderBy(m => m.DisplayName),
            ("displayname", true) => query.OrderByDescending(m => m.DisplayName),
            ("usedbytes", false) => query.OrderBy(m => m.UsedBytes),
            ("usedbytes", true) => query.OrderByDescending(m => m.UsedBytes),
            ("createdat", false) => query.OrderBy(m => m.CreatedAt),
            ("createdat", true) => query.OrderByDescending(m => m.CreatedAt),
            (_, true) => query.OrderByDescending(m => m.EmailAddress),
            _ => query.OrderBy(m => m.EmailAddress),
        };
        return query.Select(Project(db)).ToPagedAsync(page, ct);
    }

    public async Task<MailboxDto> GetAsync(Guid id, CancellationToken ct) =>
        await db.Mailboxes.AsNoTracking().Where(m => m.Id == id).Select(Project(db)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException(nameof(Mailbox), id);

    public async Task<MailboxDto> CreateAsync(CreateMailboxRequest request, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId ?? throw new ForbiddenException("Select an organization first: platform administrators must choose which organization to manage.");
        var localPart = request.LocalPart.Trim().ToLowerInvariant();
        if (!LocalPartPattern().IsMatch(localPart)) throw new ValidationException(nameof(request.LocalPart), "Enter a valid mailbox name.");
        if (string.IsNullOrWhiteSpace(request.DisplayName)) throw new ValidationException(nameof(request.DisplayName), "Display name is required.");
        if (request.QuotaBytes <= 0) throw new ValidationException(nameof(request.QuotaBytes), "Quota must be greater than zero.");

        // Tenant filter guarantees a domain from another organization is never found here.
        var domain = await db.OrganizationDomains.FirstOrDefaultAsync(d => d.Id == request.DomainId, ct)
            ?? throw new ValidationException(nameof(request.DomainId), "Domain not found.");
        var email = $"{localPart}@{domain.DomainName}";

        if (await db.Mailboxes.IgnoreQueryFilters().AnyAsync(m => m.EmailAddress == email && m.Status != MailboxStatus.Deleted, ct)
            || await db.MailAliases.IgnoreQueryFilters().AnyAsync(a => a.AliasAddress == email, ct))
            throw new ConflictException($"Address '{email}' is already in use.");

        var subscription = await db.OrganizationSubscriptions.FirstOrDefaultAsync(ct);
        if (subscription is not null && subscription.MailboxLimit > 0
            && await db.Mailboxes.CountAsync(m => m.Status != MailboxStatus.Deleted, ct) >= subscription.MailboxLimit)
            throw new ConflictException("The organization has reached its mailbox limit.");

        var server = await db.MailServers.Where(s => s.Status == MailServerStatus.Online).OrderBy(s => s.Name).FirstOrDefaultAsync(ct);
        var mailbox = new Mailbox
        {
            OrganizationId = orgId,
            DomainId = domain.Id,
            MailServerId = server?.Id,
            LocalPart = localPart,
            EmailAddress = email,
            DisplayName = request.DisplayName.Trim(),
            MailboxType = request.MailboxType,
            QuotaBytes = request.QuotaBytes,
            Status = MailboxStatus.Provisioning,
        };
        db.Mailboxes.Add(mailbox);
        audit.Record("mailbox.created", nameof(Mailbox), mailbox.Id.ToString(), new { email });
        await db.SaveChangesAsync(ct);

        // The mail server is a separate system: a provisioning failure leaves the record in
        // Provisioning state for the reconciliation worker to retry, rather than rolling back.
        if (server is not null)
        {
            try
            {
                await providers.Get(server.ServerType).ProvisionMailboxAsync(new MailboxProvisionRequest(email, mailbox.DisplayName, mailbox.QuotaBytes), ct);
                mailbox.Status = MailboxStatus.Active;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Provisioning mailbox {MailboxId} on server {ServerId} failed; will retry", mailbox.Id, server.Id);
            }
        }
        return await GetAsync(mailbox.Id, ct);
    }

    public async Task<MailboxDto> UpdateAsync(Guid id, UpdateMailboxRequest request, CancellationToken ct)
    {
        var mailbox = await db.Mailboxes.Include(m => m.MailServer).FirstOrDefaultAsync(m => m.Id == id && m.Status != MailboxStatus.Deleted, ct)
            ?? throw new NotFoundException(nameof(Mailbox), id);
        if (request.QuotaBytes < mailbox.UsedBytes) throw new ValidationException(nameof(request.QuotaBytes), "Quota cannot be less than current usage.");

        var wasEnabled = mailbox.Status == MailboxStatus.Active;
        mailbox.DisplayName = request.DisplayName.Trim();
        mailbox.QuotaBytes = request.QuotaBytes;
        if (wasEnabled != request.Enabled && mailbox.Status != MailboxStatus.Provisioning)
        {
            mailbox.Status = request.Enabled ? MailboxStatus.Active : MailboxStatus.Disabled;
            if (mailbox.MailServer is { } server)
                await providers.Get(server.ServerType).SetMailboxEnabledAsync(mailbox.EmailAddress, request.Enabled, ct);
        }
        audit.Record("mailbox.updated", nameof(Mailbox), id.ToString(), request);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var mailbox = await db.Mailboxes.Include(m => m.MailServer).FirstOrDefaultAsync(m => m.Id == id && m.Status != MailboxStatus.Deleted, ct)
            ?? throw new NotFoundException(nameof(Mailbox), id);
        var onHold = await db.LegalHolds.AnyAsync(h => h.Status == LegalHoldStatus.Active && h.MailboxIds.Contains(id), ct);
        if (onHold) throw new ConflictException("This mailbox is under an active legal hold and cannot be deleted.");

        if (mailbox.MailServer is { } server)
            await providers.Get(server.ServerType).DeleteMailboxAsync(mailbox.EmailAddress, ct);
        mailbox.Status = MailboxStatus.Deleted;
        await db.MailAliases.Where(a => a.MailboxId == id).ExecuteDeleteAsync(ct);
        audit.Record("mailbox.deleted", nameof(Mailbox), id.ToString(), new { mailbox.EmailAddress }, privileged: true);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MailboxDto> AddAliasAsync(Guid id, string aliasLocalPart, Guid domainId, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId ?? throw new ForbiddenException("Select an organization first: platform administrators must choose which organization to manage.");
        var mailbox = await db.Mailboxes.Include(m => m.MailServer).FirstOrDefaultAsync(m => m.Id == id && m.Status != MailboxStatus.Deleted, ct)
            ?? throw new NotFoundException(nameof(Mailbox), id);
        var domain = await db.OrganizationDomains.FirstOrDefaultAsync(d => d.Id == domainId, ct)
            ?? throw new ValidationException(nameof(domainId), "Domain not found.");
        var local = aliasLocalPart.Trim().ToLowerInvariant();
        if (!LocalPartPattern().IsMatch(local)) throw new ValidationException(nameof(aliasLocalPart), "Enter a valid alias.");
        var address = $"{local}@{domain.DomainName}";
        if (await db.Mailboxes.IgnoreQueryFilters().AnyAsync(m => m.EmailAddress == address && m.Status != MailboxStatus.Deleted, ct)
            || await db.MailAliases.IgnoreQueryFilters().AnyAsync(a => a.AliasAddress == address, ct))
            throw new ConflictException($"Address '{address}' is already in use.");

        db.MailAliases.Add(new MailAlias { OrganizationId = orgId, MailboxId = id, AliasAddress = address });
        if (mailbox.MailServer is { } server)
            await providers.Get(server.ServerType).AddAliasAsync(address, mailbox.EmailAddress, ct);
        audit.Record("alias.created", nameof(MailAlias), id.ToString(), new { address });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Sets (or clears, when null) the password mail clients use for submission and IMAP.</summary>
    public async Task SetClientPasswordAsync(Guid id, string? password, CancellationToken ct)
    {
        var mailbox = await db.Mailboxes.FirstOrDefaultAsync(m => m.Id == id && m.Status != MailboxStatus.Deleted, ct)
            ?? throw new NotFoundException(nameof(Mailbox), id);
        if (password is not null)
        {
            var minLength = await db.SecurityPolicies.Select(p => (int?)p.PasswordMinLength).FirstOrDefaultAsync(ct) ?? 12;
            if (password.Length < minLength) throw new ValidationException(nameof(password), $"Use at least {minLength} characters.");
            if (password.Length > 256) throw new ValidationException(nameof(password), "Use at most 256 characters.");
            if (password.Contains(mailbox.LocalPart, StringComparison.OrdinalIgnoreCase)) throw new ValidationException(nameof(password), "The password must not contain the mailbox name.");
        }
        mailbox.PasswordHash = password is null ? null : hasher.Hash(password);
        mailbox.PasswordChangedAt = clock.UtcNow;
        audit.Record(password is null ? "mailbox.client-login-disabled" : "mailbox.password-set", nameof(Mailbox), id.ToString(), new { mailbox.EmailAddress }, privileged: true);
        await db.SaveChangesAsync(ct);
    }

    private static System.Linq.Expressions.Expression<Func<Mailbox, MailboxDto>> Project(IApplicationDbContext db) =>
        m => new MailboxDto(m.Id, m.DomainId, m.EmailAddress, m.DisplayName, m.MailboxType, m.Status, m.QuotaBytes, m.UsedBytes, m.LastLoginAt, m.CreatedAt,
            db.MailAliases.Where(a => a.MailboxId == m.Id).OrderBy(a => a.AliasAddress).Select(a => a.AliasAddress).ToList(),
            m.PasswordHash != null);
}
