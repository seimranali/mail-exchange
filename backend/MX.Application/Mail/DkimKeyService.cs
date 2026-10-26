using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain.Entities;

namespace MX.Application.Mail;

public sealed record DkimKeyDto(string Selector, int KeyBits, string DnsName, string DnsValue, DateTimeOffset CreatedAt);

/// <summary>Generates and rotates per-domain DKIM keys. Private keys are encrypted before storage.</summary>
public sealed class DkimKeyService(IApplicationDbContext db, ITenantContext tenant, IKeyProtector protector, IAuditLogger audit, IClock clock)
{
    public static string Purpose(Guid domainId) => $"dkim:{domainId}";

    public async Task<DkimKeyDto?> GetActiveAsync(Guid domainId, CancellationToken ct)
    {
        var key = await db.DkimKeys.AsNoTracking().Include(k => k.Domain)
            .FirstOrDefaultAsync(k => k.DomainId == domainId && k.IsActive, ct);
        return key is null ? null : ToDto(key, key.Domain!.DomainName);
    }

    /// <summary>Creates a new 2048-bit RSA key and makes it active; any previous key is deactivated.</summary>
    public async Task<DkimKeyDto> RotateAsync(Guid domainId, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId ?? throw new ForbiddenException("Select an organization first: platform administrators must choose which organization to manage.");
        var domain = await db.OrganizationDomains.FirstOrDefaultAsync(d => d.Id == domainId, ct) ?? throw new NotFoundException("Domain", domainId);

        using var rsa = RSA.Create(2048);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        string encrypted;
        try
        {
            encrypted = protector.Protect(pkcs8, Purpose(domainId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        // Date-based selectors let old and new keys coexist in DNS during rotation.
        var selector = $"mx{clock.UtcNow:yyyyMM}";
        if (await db.DkimKeys.AnyAsync(k => k.DomainId == domainId && k.Selector == selector, ct))
            selector = $"{selector}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";

        await db.DkimKeys.Where(k => k.DomainId == domainId && k.IsActive).ExecuteUpdateAsync(s => s.SetProperty(k => k.IsActive, false), ct);
        var key = new DkimKey
        {
            OrganizationId = orgId,
            DomainId = domainId,
            Selector = selector,
            PublicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),
            EncryptedPrivateKey = encrypted,
            KeyBits = 2048,
        };
        db.DkimKeys.Add(key);
        audit.Record("dkim-key.rotated", "Domain", domainId.ToString(), new { domain.DomainName, selector }, privileged: true);
        await db.SaveChangesAsync(ct);
        key.CreatedAt = clock.UtcNow;
        return ToDto(key, domain.DomainName);
    }

    private static DkimKeyDto ToDto(DkimKey key, string domainName) =>
        new(key.Selector, key.KeyBits, $"{key.Selector}._domainkey.{domainName}", $"v=DKIM1; k=rsa; p={key.PublicKey}", key.CreatedAt);
}
