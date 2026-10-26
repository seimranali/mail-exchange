using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;

namespace MX.Persistence;

public sealed record SeedOptions(string AdminEmail, string AdminPassword, bool IncludeDemoData, string MailServerHostname = "mx-dev-01.local");

/// <summary>
/// Applies migrations and seeds reference data. Runs with the schema-owner connection, never the
/// restricted application role. Production deployments apply the idempotent SQL script instead.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(string ownerConnectionString, IPasswordHasher hasher, SeedOptions seed, ILogger logger, CancellationToken ct = default)
    {
        var tenant = new TenantContext();
        tenant.SetPlatformScope(null);
        var options = DependencyInjection.Configure(new DbContextOptionsBuilder<MxDbContext>(), ownerConnectionString)
            .AddInterceptors(new TenantSessionInterceptor(tenant)).Options;
        await using var db = new MxDbContext((DbContextOptions<MxDbContext>)options, tenant, new SystemClock());

        logger.LogInformation("Applying database migrations");
        await db.Database.MigrateAsync(ct);

        foreach (var name in Roles.All)
            if (!await db.ApplicationRoles.AnyAsync(r => r.Name == name, ct))
                db.ApplicationRoles.Add(new ApplicationRole { Name = name, IsPlatformRole = name == Roles.PlatformAdmin });
        await db.SaveChangesAsync(ct);

        if (!await db.Organizations.AnyAsync(ct))
        {
            logger.LogInformation("Seeding platform organization and administrator {Email}", seed.AdminEmail);
            var roles = await db.ApplicationRoles.ToDictionaryAsync(r => r.Name, ct);
            var platform = new Organization { Name = "MX Platform", Slug = "platform" };
            db.Organizations.Add(platform);
            db.SecurityPolicies.Add(new SecurityPolicy { OrganizationId = platform.Id });
            AddUser(db, hasher, platform.Id, seed.AdminEmail, "Platform Administrator", seed.AdminPassword, roles[Roles.PlatformAdmin], roles[Roles.OrgAdmin]);
            db.MailServers.Add(new MailServer { Name = seed.MailServerHostname.Split('.')[0], Hostname = seed.MailServerHostname, ServerType = MailServerType.Native, Region = "primary" });
            await db.SaveChangesAsync(ct);

            if (seed.IncludeDemoData) await SeedDemoAsync(db, hasher, roles, seed.AdminPassword, ct);
        }
    }

    private static ApplicationUser AddUser(MxDbContext db, IPasswordHasher hasher, Guid orgId, string email, string name, string password, params ApplicationRole[] roles)
    {
        var user = new ApplicationUser
        {
            OrganizationId = orgId, Email = email, NormalizedEmail = email.ToUpperInvariant(), DisplayName = name, PasswordHash = hasher.Hash(password),
        };
        db.ApplicationUsers.Add(user);
        foreach (var role in roles) db.UserRoles.Add(new UserRole { OrganizationId = orgId, UserId = user.Id, RoleId = role.Id });
        return user;
    }

    private static async Task SeedDemoAsync(MxDbContext db, IPasswordHasher hasher, Dictionary<string, ApplicationRole> roles, string password, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var server = await db.MailServers.FirstAsync(ct);
        var org = new Organization { Name = "Example Corporation", Slug = "example" };
        org.Subscription = new OrganizationSubscription
        {
            OrganizationId = org.Id, Plan = SubscriptionPlan.Business, Status = SubscriptionStatus.Active, MailboxLimit = 250,
            StorageLimitBytes = 250L * 50 * 1024 * 1024 * 1024, StartsAt = now.AddMonths(-6),
        };
        db.Organizations.Add(org);
        db.SecurityPolicies.Add(new SecurityPolicy { OrganizationId = org.Id, RequireMfa = true });
        AddUser(db, hasher, org.Id, "admin@example.com", "Avery Admin", password, roles[Roles.OrgAdmin]);
        AddUser(db, hasher, org.Id, "security@example.com", "Sam Security", password, roles[Roles.SecurityAdmin], roles[Roles.Auditor]);

        var domain = new OrganizationDomain
        {
            OrganizationId = org.Id, DomainName = "example.com", IsDefault = true, VerificationStatus = DomainVerificationStatus.Verified,
            MxConfigured = true, SpfConfigured = true, DkimConfigured = true, DmarcConfigured = false, VerifiedAt = now.AddMonths(-6),
            CertificateExpiresAt = now.AddDays(21),
        };
        var domain2 = new OrganizationDomain { OrganizationId = org.Id, DomainName = "example.org", VerificationStatus = DomainVerificationStatus.Pending };
        db.OrganizationDomains.AddRange(domain, domain2);

        string[] names = ["Jordan Lee", "Priya Shah", "Marcus Chen", "Elena Garcia", "Noah Williams", "Fatima Khan", "Liam O'Brien", "Yuki Tanaka",
            "Grace Okafor", "Diego Ramirez", "Hannah Becker", "Omar Haddad", "Sofia Rossi", "Ethan Brooks", "Amara Nwosu", "Lucas Martin"];
        var mailboxes = names.Select((n, i) =>
        {
            var local = n.ToLowerInvariant().Replace("'", "").Replace(' ', '.');
            return new Mailbox
            {
                OrganizationId = org.Id, DomainId = domain.Id, MailServerId = server.Id, LocalPart = local, EmailAddress = $"{local}@example.com",
                DisplayName = n, QuotaBytes = 50L * 1024 * 1024 * 1024, UsedBytes = (long)(i * 2.7 * 1024 * 1024 * 1024) % (45L * 1024 * 1024 * 1024),
                Status = i == 5 ? MailboxStatus.Disabled : MailboxStatus.Active, LastLoginAt = now.AddHours(-i * 5),
                MailboxType = i == 15 ? MailboxType.Shared : MailboxType.User,
            };
        }).ToList();
        db.Mailboxes.AddRange(mailboxes);
        db.MailAliases.Add(new MailAlias { OrganizationId = org.Id, MailboxId = mailboxes[0].Id, AliasAddress = "support@example.com" });

        db.SecurityAlerts.AddRange(
            new SecurityAlert { OrganizationId = org.Id, Title = "Impossible travel sign-in", Category = "Identity", Severity = AlertSeverity.High,
                Description = "Successful sign-ins from two distant countries within 20 minutes.", MailboxId = mailboxes[2].Id, DetectedAt = now.AddHours(-2) },
            new SecurityAlert { OrganizationId = org.Id, Title = "Malware campaign blocked", Category = "Malware", Severity = AlertSeverity.Critical,
                Description = "42 messages carrying a known malicious attachment were quarantined.", DetectedAt = now.AddHours(-5) },
            new SecurityAlert { OrganizationId = org.Id, Title = "Forwarding rule to external domain", Category = "Data loss", Severity = AlertSeverity.Medium,
                Description = "A mailbox rule forwards all mail to an external address.", MailboxId = mailboxes[7].Id, DetectedAt = now.AddDays(-1), Status = AlertStatus.Acknowledged });

        var reasons = new[] { QuarantineReason.Spam, QuarantineReason.Phishing, QuarantineReason.Malware, QuarantineReason.Spam, QuarantineReason.Policy };
        for (var i = 0; i < 12; i++)
            db.QuarantineRecords.Add(new QuarantineRecord
            {
                OrganizationId = org.Id, MailboxId = mailboxes[i % mailboxes.Count].Id, MessageId = $"<{Guid.NewGuid():N}@sender.example.net>",
                Sender = $"offers{i}@sender.example.net", Recipient = mailboxes[i % mailboxes.Count].EmailAddress,
                Subject = reasons[i % 5] switch { QuarantineReason.Phishing => "Verify your account now", QuarantineReason.Malware => "Invoice attached", _ => "Limited time offer" },
                Reason = reasons[i % 5], SpamScore = 5 + i % 10, SizeBytes = 20_000 + i * 1500, StorageRef = $"quarantine/{org.Id}/{i}",
                ReceivedAt = now.AddHours(-i * 3), ExpiresAt = now.AddDays(30).AddHours(-i * 3),
            });

        db.RetentionPolicies.AddRange(
            new RetentionPolicy { OrganizationId = org.Id, Name = "Default 7-year retention", RetentionDays = 2555, Action = RetentionAction.Retain },
            new RetentionPolicy { OrganizationId = org.Id, Name = "Deleted items cleanup", RetentionDays = 30, Action = RetentionAction.Delete });
        db.LegalHolds.Add(new LegalHold
        {
            OrganizationId = org.Id, Name = "Contract dispute 2026", CaseReference = "CASE-2026-014", MailboxIds = [mailboxes[1].Id, mailboxes[3].Id],
            CreatedByUserId = (await db.ApplicationUsers.FirstAsync(ct)).Id, StartsAt = now.AddDays(-14),
        });

        for (var i = 0; i < 12; i++)
            db.ServerHealthSnapshots.Add(new ServerHealthSnapshot
            {
                MailServerId = server.Id, CpuPercent = 22 + i % 7 * 3, MemoryPercent = 51 + i % 5, DiskPercent = 38, QueueLength = i % 6,
                ActiveConnections = 14 + i, Status = MailServerStatus.Online, CapturedAt = now.AddMinutes(-i * 5),
            });

        db.AuditEvents.AddRange(
            new AuditEvent { OrganizationId = org.Id, Action = "mailbox.created", EntityType = "Mailbox", EntityId = mailboxes[15].Id.ToString(), OccurredAt = now.AddHours(-1) },
            new AuditEvent { OrganizationId = org.Id, Action = "security-policy.updated", EntityType = "SecurityPolicy", IsPrivileged = true, OccurredAt = now.AddHours(-3) },
            new AuditEvent { OrganizationId = org.Id, Action = "legal-hold.created", EntityType = "LegalHold", IsPrivileged = true, OccurredAt = now.AddDays(-14) });

        await db.SaveChangesAsync(ct);
    }
}
