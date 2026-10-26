using Microsoft.EntityFrameworkCore;
using MX.Domain;
using MX.Domain.Entities;
using MX.Tests.Infrastructure;
using Npgsql;

namespace MX.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DatabaseTests(PostgresFixture db)
{
    [Fact]
    public async Task All_migrations_are_applied_to_a_clean_database()
    {
        await using var ctx = db.CreateContext(platform: true);
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
        Assert.Contains("20261025063557_RowLevelSecurity", await ctx.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Check_constraint_rejects_invalid_quota()
    {
        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var domain = await ctx.OrganizationDomains.FirstAsync();
        ctx.Mailboxes.Add(new Mailbox
        {
            OrganizationId = db.ExampleOrgId, DomainId = domain.Id, LocalPart = "bad.quota", EmailAddress = $"bad.quota@{domain.DomainName}",
            DisplayName = "Bad", QuotaBytes = 0,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task Unique_index_enforces_global_email_uniqueness_across_tenants()
    {
        // The platform tenant cannot see example.com mailboxes, but the unique index still applies.
        await using var ctx = db.CreateContext(db.PlatformOrgId);
        var domain = new OrganizationDomain { OrganizationId = db.PlatformOrgId, DomainName = $"p{Guid.NewGuid():N}.test" };
        ctx.OrganizationDomains.Add(domain);
        await ctx.SaveChangesAsync();
        ctx.Mailboxes.Add(new Mailbox
        {
            OrganizationId = db.PlatformOrgId, DomainId = domain.Id, LocalPart = "jordan.lee", EmailAddress = "jordan.lee@example.com",
            DisplayName = "Clash", QuotaBytes = 1,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task Foreign_key_prevents_deleting_a_domain_with_mailboxes()
    {
        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var domainId = await ctx.Mailboxes.Select(m => m.DomainId).FirstAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.OrganizationDomains.Where(d => d.Id == domainId).ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Rolled_back_transaction_leaves_no_rows()
    {
        var name = $"rollback-{Guid.NewGuid():N}";
        await using (var ctx = db.CreateContext(db.ExampleOrgId))
        {
            // Explicit transactions must run inside the retrying execution strategy.
            await ctx.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await ctx.Database.BeginTransactionAsync();
                ctx.RetentionPolicies.Add(new RetentionPolicy { OrganizationId = db.ExampleOrgId, Name = name, RetentionDays = 5, Action = RetentionAction.Delete });
                await ctx.SaveChangesAsync();
                Assert.True(await ctx.RetentionPolicies.AnyAsync(p => p.Name == name));
                await tx.RollbackAsync();
            });
        }
        await using var verify = db.CreateContext(db.ExampleOrgId);
        Assert.False(await verify.RetentionPolicies.AnyAsync(p => p.Name == name));
    }

    [Fact]
    public async Task Timestamps_are_stored_as_utc_timestamptz()
    {
        await using var ctx = db.CreateContext(db.ExampleOrgId);
        var mailbox = await ctx.Mailboxes.FirstAsync();
        Assert.Equal(TimeSpan.Zero, mailbox.CreatedAt.Offset);

        var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT data_type FROM information_schema.columns WHERE table_schema='mx' AND table_name='mailboxes' AND column_name='created_at'", conn);
        Assert.Equal("timestamp with time zone", await cmd.ExecuteScalarAsync());
    }
}
