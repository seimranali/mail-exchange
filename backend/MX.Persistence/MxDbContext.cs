using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Domain.Common;
using MX.Domain.Entities;

namespace MX.Persistence;

public sealed class MxDbContext(DbContextOptions<MxDbContext> options, ITenantContext tenant, IClock clock)
    : DbContext(options), IApplicationDbContext
{
    // Read by the global query filters. EF parameterizes these per context instance,
    // so a pooled/compiled query never captures another request's tenant.
    internal Guid? CurrentOrganizationId => tenant.OrganizationId;
    internal bool IsPlatformScope => tenant.IsPlatformScope;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationDomain> OrganizationDomains => Set<OrganizationDomain>();
    public DbSet<OrganizationSubscription> OrganizationSubscriptions => Set<OrganizationSubscription>();
    public DbSet<MailServer> MailServers => Set<MailServer>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<MailAlias> MailAliases => Set<MailAlias>();
    public DbSet<DistributionList> DistributionLists => Set<DistributionList>();
    public DbSet<DistributionListMember> DistributionListMembers => Set<DistributionListMember>();
    public DbSet<MailRoutingRule> MailRoutingRules => Set<MailRoutingRule>();
    public DbSet<ApplicationUser> ApplicationUsers => Set<ApplicationUser>();
    public DbSet<ApplicationRole> ApplicationRoles => Set<ApplicationRole>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<ApiCredential> ApiCredentials => Set<ApiCredential>();
    public DbSet<SecurityPolicy> SecurityPolicies => Set<SecurityPolicy>();
    public DbSet<AuthenticationEvent> AuthenticationEvents => Set<AuthenticationEvent>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SecurityAlert> SecurityAlerts => Set<SecurityAlert>();
    public DbSet<QuarantineRecord> QuarantineRecords => Set<QuarantineRecord>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<LegalHold> LegalHolds => Set<LegalHold>();
    public DbSet<DiscoveryRequest> DiscoveryRequests => Set<DiscoveryRequest>();
    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();
    public DbSet<RestoreJob> RestoreJobs => Set<RestoreJob>();
    public DbSet<ServerHealthSnapshot> ServerHealthSnapshots => Set<ServerHealthSnapshot>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SystemConfiguration> SystemConfigurations => Set<SystemConfiguration>();
    public DbSet<MailMessage> MailMessages => Set<MailMessage>();
    public DbSet<MailFolderState> MailFolderStates => Set<MailFolderState>();
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();
    public DbSet<OutboundRecipient> OutboundRecipients => Set<OutboundRecipient>();
    public DbSet<DkimKey> DkimKeys => Set<DkimKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("mx");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MxDbContext).Assembly);

        // Enums are stored as readable text, constrained by length.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (type.IsEnum)
            {
                property.SetProviderClrType(typeof(string));
                property.SetMaxLength(32);
            }
        }

        // Tenant isolation: every tenant-owned entity is filtered by organization.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            var method = typeof(MxDbContext).GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(entityType.ClrType);
            method.Invoke(this, [modelBuilder]);
        }
        modelBuilder.Entity<Organization>().HasQueryFilter(o => IsPlatformScope || o.Id == CurrentOrganizationId);
        modelBuilder.Entity<AuditEvent>().HasQueryFilter(e => IsPlatformScope || e.OrganizationId == CurrentOrganizationId);
        modelBuilder.Entity<AuthenticationEvent>().HasQueryFilter(e => IsPlatformScope || e.OrganizationId == CurrentOrganizationId);
    }

    private void ApplyTenantFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned
    {
        Expression<Func<T, bool>> filter = e => IsPlatformScope || e.OrganizationId == CurrentOrganizationId;
        modelBuilder.Entity<T>().HasQueryFilter(filter);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAndGuard();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampAndGuard();
        return base.SaveChanges();
    }

    private void StampAndGuard()
    {
        var now = clock.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Property(e => e.CreatedAt).IsModified = false;
            }

            // Second line of defence: never write a row into another tenant from a tenant scope.
            if (!IsPlatformScope && entry.Entity is ITenantOwned owned && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && owned.OrganizationId != CurrentOrganizationId)
                throw new InvalidOperationException($"Cross-tenant write blocked for {entry.Entity.GetType().Name}.");

            if (entry.Entity is AuditEvent or AuthenticationEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit and authentication events are append-only.");
        }
    }
}
