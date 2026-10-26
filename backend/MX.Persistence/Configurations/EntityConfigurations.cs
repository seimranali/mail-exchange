using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MX.Domain.Entities;

namespace MX.Persistence.Configurations;

// Conventions: snake_case names, uuid keys, timestamptz for DateTimeOffset (Npgsql default),
// citext-free lowercase normalization done by the application, and explicit FK delete behaviour.

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Slug).HasMaxLength(63);
        b.HasIndex(x => x.Slug).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("ck_organizations_slug", "slug ~ '^[a-z0-9][a-z0-9-]{1,62}$'"));
        b.HasOne(x => x.Subscription).WithOne().HasForeignKey<OrganizationSubscription>(s => s.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationDomainConfiguration : IEntityTypeConfiguration<OrganizationDomain>
{
    public void Configure(EntityTypeBuilder<OrganizationDomain> b)
    {
        b.Property(x => x.DomainName).HasMaxLength(253);
        b.Property(x => x.VerificationToken).HasMaxLength(64);
        b.HasIndex(x => x.DomainName).IsUnique();
        b.HasIndex(x => x.OrganizationId).HasFilter("is_default").IsUnique().HasDatabaseName("ux_organization_domains_default");
        b.ToTable(t => t.HasCheckConstraint("ck_organization_domains_lowercase", "domain_name = lower(domain_name)"));
        b.HasOne(x => x.Organization).WithMany(o => o.Domains).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationSubscriptionConfiguration : IEntityTypeConfiguration<OrganizationSubscription>
{
    public void Configure(EntityTypeBuilder<OrganizationSubscription> b)
    {
        b.HasIndex(x => x.OrganizationId).IsUnique();
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_subscriptions_limits", "mailbox_limit >= 0 AND storage_limit_bytes >= 0");
            t.HasCheckConstraint("ck_subscriptions_dates", "ends_at IS NULL OR ends_at > starts_at");
        });
    }
}

internal sealed class MailServerConfiguration : IEntityTypeConfiguration<MailServer>
{
    public void Configure(EntityTypeBuilder<MailServer> b)
    {
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Hostname).HasMaxLength(253);
        b.Property(x => x.Region).HasMaxLength(64);
        b.Property(x => x.CredentialSecretRef).HasMaxLength(256);
        b.HasIndex(x => x.Hostname).IsUnique();
    }
}

internal sealed class MailboxConfiguration : IEntityTypeConfiguration<Mailbox>
{
    public void Configure(EntityTypeBuilder<Mailbox> b)
    {
        b.Property(x => x.LocalPart).HasMaxLength(64);
        b.Property(x => x.EmailAddress).HasMaxLength(320);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.PasswordHash).HasMaxLength(256);
        // An address may be reused only after the previous mailbox was deleted.
        b.HasIndex(x => x.EmailAddress).IsUnique().HasFilter("status <> 'Deleted'");
        b.HasIndex(x => new { x.OrganizationId, x.Status });
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        b.HasIndex(x => x.DomainId);
        b.HasIndex(x => x.MailServerId);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_mailboxes_quota", "quota_bytes > 0 AND used_bytes >= 0");
            t.HasCheckConstraint("ck_mailboxes_lowercase", "email_address = lower(email_address)");
        });
        b.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.MailServer).WithMany().HasForeignKey(x => x.MailServerId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MailAliasConfiguration : IEntityTypeConfiguration<MailAlias>
{
    public void Configure(EntityTypeBuilder<MailAlias> b)
    {
        b.Property(x => x.AliasAddress).HasMaxLength(320);
        b.HasIndex(x => x.AliasAddress).IsUnique();
        b.HasIndex(x => x.MailboxId);
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Mailbox).WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DistributionListConfiguration : IEntityTypeConfiguration<DistributionList>
{
    public void Configure(EntityTypeBuilder<DistributionList> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.EmailAddress).HasMaxLength(320);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.HasIndex(x => x.EmailAddress).IsUnique();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne<OrganizationDomain>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DistributionListMemberConfiguration : IEntityTypeConfiguration<DistributionListMember>
{
    public void Configure(EntityTypeBuilder<DistributionListMember> b)
    {
        b.Property(x => x.ExternalAddress).HasMaxLength(320);
        b.HasIndex(x => new { x.DistributionListId, x.MailboxId }).IsUnique().HasFilter("mailbox_id IS NOT NULL");
        b.HasIndex(x => new { x.DistributionListId, x.ExternalAddress }).IsUnique().HasFilter("external_address IS NOT NULL");
        b.HasIndex(x => x.MailboxId);
        b.ToTable(t => t.HasCheckConstraint("ck_dl_members_target", "(mailbox_id IS NULL) <> (external_address IS NULL)"));
        b.HasOne(x => x.DistributionList).WithMany(d => d.Members).HasForeignKey(x => x.DistributionListId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Mailbox).WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MailRoutingRuleConfiguration : IEntityTypeConfiguration<MailRoutingRule>
{
    public void Configure(EntityTypeBuilder<MailRoutingRule> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.SenderPattern).HasMaxLength(500);
        b.Property(x => x.RecipientPattern).HasMaxLength(500);
        b.Property(x => x.SubjectPattern).HasMaxLength(500);
        b.Property(x => x.ActionTarget).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.Priority }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("ck_routing_rules_priority", "priority >= 0"));
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> b)
    {
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.NormalizedEmail).HasMaxLength(320);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.PasswordHash).HasMaxLength(256);
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
        b.HasIndex(x => x.OrganizationId);
        b.ToTable(t => t.HasCheckConstraint("ck_users_failed_logins", "failed_login_count >= 0"));
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> b)
    {
        b.Property(x => x.Name).HasMaxLength(64);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
        b.HasIndex(x => x.RoleId);
        b.HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ApiCredentialConfiguration : IEntityTypeConfiguration<ApiCredential>
{
    public void Configure(EntityTypeBuilder<ApiCredential> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.KeyPrefix).HasMaxLength(16);
        b.Property(x => x.KeyHash).HasMaxLength(128);
        b.HasIndex(x => x.KeyPrefix).IsUnique();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SecurityPolicyConfiguration : IEntityTypeConfiguration<SecurityPolicy>
{
    public void Configure(EntityTypeBuilder<SecurityPolicy> b)
    {
        b.HasIndex(x => x.OrganizationId).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("ck_security_policies_values",
            "password_min_length BETWEEN 8 AND 128 AND session_timeout_minutes > 0 AND max_failed_logins > 0 AND lockout_minutes > 0"));
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuthenticationEventConfiguration : IEntityTypeConfiguration<AuthenticationEvent>
{
    public void Configure(EntityTypeBuilder<AuthenticationEvent> b)
    {
        b.Property(x => x.AttemptedEmail).HasMaxLength(320);
        b.Property(x => x.IpAddress).HasMaxLength(45);
        b.Property(x => x.UserAgent).HasMaxLength(512);
        b.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
        b.HasIndex(x => new { x.UserId, x.OccurredAt });
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.Property(x => x.Action).HasMaxLength(100);
        b.Property(x => x.EntityType).HasMaxLength(100);
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.IpAddress).HasMaxLength(45);
        b.Property(x => x.Details).HasColumnType("jsonb");
        b.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
        b.HasIndex(x => new { x.OrganizationId, x.Action });
        b.HasIndex(x => new { x.EntityType, x.EntityId });
        // Audit rows outlive the tenant that produced them; organization FK intentionally omitted.
    }
}

internal sealed class SecurityAlertConfiguration : IEntityTypeConfiguration<SecurityAlert>
{
    public void Configure(EntityTypeBuilder<SecurityAlert> b)
    {
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.Category).HasMaxLength(64);
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.DetectedAt });
        b.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class QuarantineRecordConfiguration : IEntityTypeConfiguration<QuarantineRecord>
{
    public void Configure(EntityTypeBuilder<QuarantineRecord> b)
    {
        b.Property(x => x.MessageId).HasMaxLength(998);
        b.Property(x => x.Sender).HasMaxLength(320);
        b.Property(x => x.Recipient).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(998);
        b.Property(x => x.StorageRef).HasMaxLength(512);
        b.Property(x => x.SpamScore).HasPrecision(6, 2);
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.ReceivedAt });
        b.HasIndex(x => x.ExpiresAt);
        b.HasIndex(x => x.MailboxId);
        b.ToTable(t => t.HasCheckConstraint("ck_quarantine_expiry", "expires_at > received_at AND size_bytes >= 0"));
        b.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RetentionPolicyConfiguration : IEntityTypeConfiguration<RetentionPolicy>
{
    public void Configure(EntityTypeBuilder<RetentionPolicy> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("ck_retention_days", "retention_days BETWEEN 1 AND 36500"));
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LegalHoldConfiguration : IEntityTypeConfiguration<LegalHold>
{
    public void Configure(EntityTypeBuilder<LegalHold> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.CaseReference).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.HasIndex(x => new { x.OrganizationId, x.Status });
        b.HasIndex(x => x.MailboxIds).HasMethod("gin");
        b.ToTable(t => t.HasCheckConstraint("ck_legal_holds_release", "(status = 'Released') = (released_at IS NOT NULL)"));
        // Legal holds must not disappear with a careless tenant delete.
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DiscoveryRequestConfiguration : IEntityTypeConfiguration<DiscoveryRequest>
{
    public void Configure(EntityTypeBuilder<DiscoveryRequest> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Query).HasMaxLength(4000);
        b.Property(x => x.ExportRef).HasMaxLength(512);
        b.HasIndex(x => new { x.OrganizationId, x.Status });
        b.ToTable(t => t.HasCheckConstraint("ck_discovery_dates", "date_from IS NULL OR date_to IS NULL OR date_to >= date_from"));
        b.HasOne(x => x.LegalHold).WithMany().HasForeignKey(x => x.LegalHoldId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupJobConfiguration : IEntityTypeConfiguration<BackupJob>
{
    public void Configure(EntityTypeBuilder<BackupJob> b)
    {
        b.Property(x => x.StorageLocation).HasMaxLength(1024);
        b.Property(x => x.ErrorMessage).HasMaxLength(4000);
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        b.HasIndex(x => x.Status);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RestoreJobConfiguration : IEntityTypeConfiguration<RestoreJob>
{
    public void Configure(EntityTypeBuilder<RestoreJob> b)
    {
        b.Property(x => x.ErrorMessage).HasMaxLength(4000);
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        b.HasIndex(x => x.BackupJobId);
        b.HasOne(x => x.BackupJob).WithMany().HasForeignKey(x => x.BackupJobId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.TargetMailboxId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ServerHealthSnapshotConfiguration : IEntityTypeConfiguration<ServerHealthSnapshot>
{
    public void Configure(EntityTypeBuilder<ServerHealthSnapshot> b)
    {
        b.Property(x => x.CpuPercent).HasPrecision(5, 2);
        b.Property(x => x.MemoryPercent).HasPrecision(5, 2);
        b.Property(x => x.DiskPercent).HasPrecision(5, 2);
        b.Property(x => x.Metrics).HasColumnType("jsonb");
        b.HasIndex(x => new { x.MailServerId, x.CapturedAt }).IsDescending(false, true);
        b.ToTable(t => t.HasCheckConstraint("ck_health_percentages",
            "cpu_percent BETWEEN 0 AND 100 AND memory_percent BETWEEN 0 AND 100 AND disk_percent BETWEEN 0 AND 100"));
        b.HasOne(x => x.MailServer).WithMany().HasForeignKey(x => x.MailServerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Message).HasMaxLength(2000);
        b.Property(x => x.Link).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.UserId, x.ReadAt });
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MailMessageConfiguration : IEntityTypeConfiguration<MailMessage>
{
    public void Configure(EntityTypeBuilder<MailMessage> b)
    {
        b.Property(x => x.InternetMessageId).HasMaxLength(998);
        b.Property(x => x.EnvelopeFrom).HasMaxLength(320);
        b.Property(x => x.FromHeader).HasMaxLength(998);
        b.Property(x => x.Subject).HasMaxLength(998);
        b.Property(x => x.StoragePath).HasMaxLength(512);
        b.Property(x => x.RemoteIp).HasMaxLength(45);
        b.Property(x => x.HeloName).HasMaxLength(255);
        b.Property(x => x.SpamScore).HasPrecision(6, 2);
        b.HasIndex(x => new { x.MailboxId, x.Folder, x.ReceivedAt }).IsDescending(false, false, true);
        b.HasIndex(x => new { x.MailboxId, x.Folder, x.Uid }).IsUnique().HasDatabaseName("ux_mail_messages_folder_uid");
        b.ToTable(t => t.HasCheckConstraint("ck_mail_messages_uid", "uid > 0"));
        b.HasIndex(x => new { x.OrganizationId, x.ReceivedAt });
        b.HasIndex(x => x.StoragePath).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("ck_mail_messages_size", "size_bytes >= 0"));
        b.HasOne(x => x.Mailbox).WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MailFolderStateConfiguration : IEntityTypeConfiguration<MailFolderState>
{
    public void Configure(EntityTypeBuilder<MailFolderState> b)
    {
        b.HasIndex(x => new { x.MailboxId, x.Folder }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("ck_mail_folder_states_uids", "uid_validity > 0 AND uid_next > 0"));
        b.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutboundMessageConfiguration : IEntityTypeConfiguration<OutboundMessage>
{
    public void Configure(EntityTypeBuilder<OutboundMessage> b)
    {
        b.Property(x => x.EnvelopeFrom).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(998);
        b.Property(x => x.InternetMessageId).HasMaxLength(998);
        b.Property(x => x.StoragePath).HasMaxLength(512);
        b.HasIndex(x => x.StoragePath).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        b.ToTable(t => t.HasCheckConstraint("ck_outbound_messages_size", "size_bytes >= 0"));
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutboundRecipientConfiguration : IEntityTypeConfiguration<OutboundRecipient>
{
    public void Configure(EntityTypeBuilder<OutboundRecipient> b)
    {
        b.Property(x => x.Address).HasMaxLength(320);
        b.Property(x => x.Domain).HasMaxLength(253);
        b.Property(x => x.LastResponse).HasMaxLength(1000);
        b.Property(x => x.RemoteHost).HasMaxLength(253);
        // The delivery worker's claim query: due, unfinished recipients in time order.
        b.HasIndex(x => x.NextAttemptAt).HasFilter("status IN ('Queued', 'Deferred', 'Sending')").HasDatabaseName("ix_outbound_recipients_due");
        b.HasIndex(x => new { x.OrganizationId, x.Status });
        b.HasIndex(x => x.OutboundMessageId);
        b.ToTable(t => t.HasCheckConstraint("ck_outbound_recipients_attempts", "attempts >= 0"));
        b.HasOne(x => x.OutboundMessage).WithMany(m => m.Recipients).HasForeignKey(x => x.OutboundMessageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DkimKeyConfiguration : IEntityTypeConfiguration<DkimKey>
{
    public void Configure(EntityTypeBuilder<DkimKey> b)
    {
        b.Property(x => x.Selector).HasMaxLength(63);
        b.Property(x => x.PublicKey).HasMaxLength(2048);
        b.Property(x => x.EncryptedPrivateKey).HasMaxLength(8192);
        b.HasIndex(x => new { x.DomainId, x.Selector }).IsUnique();
        b.HasIndex(x => x.DomainId).HasFilter("is_active").IsUnique().HasDatabaseName("ux_dkim_keys_active");
        b.ToTable(t => t.HasCheckConstraint("ck_dkim_keys_selector", "selector ~ '^[a-z0-9][a-z0-9-]{0,62}$'"));
        b.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SystemConfigurationConfiguration : IEntityTypeConfiguration<SystemConfiguration>
{
    public void Configure(EntityTypeBuilder<SystemConfiguration> b)
    {
        b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.Value).HasMaxLength(4000);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Key).IsUnique();
    }
}
