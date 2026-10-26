using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MX.Application.Common;
using MX.Domain;
using MX.Persistence;

namespace MX.MailServer.Delivery;

public sealed record LocalRecipient(Guid OrganizationId, Guid MailboxId, string Address, string MailboxAddress, long QuotaBytes, long UsedBytes);

public enum RecipientStatus { Accepted, NotLocal, UnknownUser, Disabled, OverQuota }

public interface IRecipientDirectory
{
    Task<(RecipientStatus Status, LocalRecipient? Recipient)> ResolveAsync(string address, CancellationToken ct);

    /// <summary>All domains hosted here (used for spoofing and look-alike detection). Cached briefly.</summary>
    Task<IReadOnlyCollection<string>> GetHostedDomainsAsync(CancellationToken ct);
}

/// <summary>
/// Looks up local recipients across all tenants. Runs in platform scope because the tenant is
/// not known until the address is resolved; it only reads the matching domain/mailbox/alias.
/// </summary>
public sealed class RecipientDirectory(IServiceScopeFactory scopes, IOptions<MailServerOptions> options) : IRecipientDirectory
{
    private (DateTimeOffset At, IReadOnlyCollection<string> Domains) _hosted = (DateTimeOffset.MinValue, []);

    public async Task<IReadOnlyCollection<string>> GetHostedDomainsAsync(CancellationToken ct)
    {
        var cached = _hosted;
        if (DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1)) return cached.Domains;
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope(null);
        var domains = await scope.ServiceProvider.GetRequiredService<MxDbContext>().OrganizationDomains.AsNoTracking().Select(d => d.DomainName).ToListAsync(ct);
        var set = new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);
        _hosted = (DateTimeOffset.UtcNow, set);
        return set;
    }

    public async Task<(RecipientStatus, LocalRecipient?)> ResolveAsync(string address, CancellationToken ct)
    {
        address = address.Trim().ToLowerInvariant();
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1) return (RecipientStatus.UnknownUser, null);
        var domain = address[(at + 1)..];

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope(null);
        var db = scope.ServiceProvider.GetRequiredService<MxDbContext>();

        var acceptUnverified = options.Value.AcceptUnverifiedDomains;
        var isLocal = await db.OrganizationDomains.AsNoTracking().AnyAsync(d =>
            d.DomainName == domain && (acceptUnverified || d.VerificationStatus == DomainVerificationStatus.Verified), ct);
        if (!isLocal) return (RecipientStatus.NotLocal, null);

        var mailbox = await db.Mailboxes.AsNoTracking()
            .Where(m => m.Status != MailboxStatus.Deleted && m.EmailAddress == address)
            .Select(m => new { m.Id, m.OrganizationId, m.EmailAddress, m.Status, m.QuotaBytes, m.UsedBytes })
            .FirstOrDefaultAsync(ct)
            ?? await db.MailAliases.AsNoTracking()
                .Where(a => a.AliasAddress == address && a.Mailbox!.Status != MailboxStatus.Deleted)
                .Select(a => new { a.Mailbox!.Id, a.Mailbox.OrganizationId, a.Mailbox.EmailAddress, a.Mailbox.Status, a.Mailbox.QuotaBytes, a.Mailbox.UsedBytes })
                .FirstOrDefaultAsync(ct);

        if (mailbox is null) return (RecipientStatus.UnknownUser, null);
        if (mailbox.Status != MailboxStatus.Active) return (RecipientStatus.Disabled, null);

        var recipient = new LocalRecipient(mailbox.OrganizationId, mailbox.Id, address, mailbox.EmailAddress, mailbox.QuotaBytes, mailbox.UsedBytes);
        return mailbox.UsedBytes >= mailbox.QuotaBytes ? (RecipientStatus.OverQuota, recipient) : (RecipientStatus.Accepted, recipient);
    }
}
