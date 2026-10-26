using MX.Domain.Common;

namespace MX.Domain.Entities;

/// <summary>A tenant. Organization rows are scoped by their own Id.</summary>
public class Organization : Entity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public OrganizationStatus Status { get; set; } = OrganizationStatus.Active;

    public ICollection<OrganizationDomain> Domains { get; set; } = [];
    public OrganizationSubscription? Subscription { get; set; }
}

public class OrganizationDomain : TenantEntity
{
    public required string DomainName { get; set; }
    public bool IsDefault { get; set; }
    public DomainVerificationStatus VerificationStatus { get; set; } = DomainVerificationStatus.Pending;
    public string VerificationToken { get; set; } = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
    public bool MxConfigured { get; set; }
    public bool SpfConfigured { get; set; }
    public bool DkimConfigured { get; set; }
    public bool DmarcConfigured { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? CertificateExpiresAt { get; set; }

    public Organization? Organization { get; set; }
}

public class OrganizationSubscription : TenantEntity
{
    public SubscriptionPlan Plan { get; set; } = SubscriptionPlan.Starter;
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trial;
    public int MailboxLimit { get; set; }
    public long StorageLimitBytes { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
}
