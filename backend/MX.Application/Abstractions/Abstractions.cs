using Microsoft.EntityFrameworkCore;
using MX.Domain.Entities;

namespace MX.Application.Abstractions;

/// <summary>
/// Request-scoped tenant identity. Resolved from the authenticated principal by the API,
/// or set explicitly by background workers.
/// </summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }
    Guid? UserId { get; }
    /// <summary>True only for platform administrators and trusted system processes.</summary>
    bool IsPlatformScope { get; }
}

/// <summary>Mutable tenant context used by the API middleware and workers to establish scope.</summary>
public interface ITenantContextSetter
{
    void SetTenant(Guid organizationId, Guid? userId);
    void SetPlatformScope(Guid? userId);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Persistence-agnostic view of the management database.</summary>
public interface IApplicationDbContext
{
    DbSet<Organization> Organizations { get; }
    DbSet<OrganizationDomain> OrganizationDomains { get; }
    DbSet<OrganizationSubscription> OrganizationSubscriptions { get; }
    DbSet<MailServer> MailServers { get; }
    DbSet<Mailbox> Mailboxes { get; }
    DbSet<MailAlias> MailAliases { get; }
    DbSet<DistributionList> DistributionLists { get; }
    DbSet<DistributionListMember> DistributionListMembers { get; }
    DbSet<MailRoutingRule> MailRoutingRules { get; }
    DbSet<ApplicationUser> ApplicationUsers { get; }
    DbSet<ApplicationRole> ApplicationRoles { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<ApiCredential> ApiCredentials { get; }
    DbSet<SecurityPolicy> SecurityPolicies { get; }
    DbSet<AuthenticationEvent> AuthenticationEvents { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<SecurityAlert> SecurityAlerts { get; }
    DbSet<QuarantineRecord> QuarantineRecords { get; }
    DbSet<RetentionPolicy> RetentionPolicies { get; }
    DbSet<LegalHold> LegalHolds { get; }
    DbSet<DiscoveryRequest> DiscoveryRequests { get; }
    DbSet<BackupJob> BackupJobs { get; }
    DbSet<RestoreJob> RestoreJobs { get; }
    DbSet<ServerHealthSnapshot> ServerHealthSnapshots { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<SystemConfiguration> SystemConfigurations { get; }
    DbSet<MailMessage> MailMessages { get; }
    DbSet<MailFolderState> MailFolderStates { get; }
    DbSet<OutboundMessage> OutboundMessages { get; }
    DbSet<OutboundRecipient> OutboundRecipients { get; }
    DbSet<DkimKey> DkimKeys { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface IAccessTokenService
{
    AccessToken Issue(ApplicationUser user, IReadOnlyCollection<string> roles, bool isPlatformAdmin);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public interface IAuditLogger
{
    void Record(string action, string entityType, string? entityId, object? details = null, bool privileged = false);
}
