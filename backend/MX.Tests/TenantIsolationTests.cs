using Microsoft.EntityFrameworkCore;
using MX.Domain;
using MX.Domain.Entities;
using MX.Tests.Infrastructure;
using Npgsql;

namespace MX.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TenantIsolationTests(PostgresFixture db)
{
    [Fact]
    public async Task Query_filter_returns_only_the_current_tenants_rows()
    {
        await using var example = db.CreateContext(db.ExampleOrgId);
        await using var platform = db.CreateContext(db.PlatformOrgId);

        Assert.True(await example.Mailboxes.CountAsync() > 0);
        Assert.Equal(0, await platform.Mailboxes.CountAsync());
        Assert.All(await example.Mailboxes.ToListAsync(), m => Assert.Equal(db.ExampleOrgId, m.OrganizationId));
    }

    [Fact]
    public async Task Row_level_security_hides_rows_even_when_query_filters_are_bypassed()
    {
        // Simulates an application bug that forgets the tenant filter.
        await using var platformTenant = db.CreateContext(db.PlatformOrgId);
        Assert.Equal(0, await platformTenant.Mailboxes.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await platformTenant.QuarantineRecords.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Without_any_tenant_scope_nothing_is_visible()
    {
        await using var none = db.CreateContext();
        Assert.Equal(0, await none.Mailboxes.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await none.Organizations.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Cross_tenant_write_is_blocked_by_the_context()
    {
        await using var platformTenant = db.CreateContext(db.PlatformOrgId);
        platformTenant.RetentionPolicies.Add(new RetentionPolicy { OrganizationId = db.ExampleOrgId, Name = "x", RetentionDays = 10, Action = RetentionAction.Delete });
        await Assert.ThrowsAsync<InvalidOperationException>(() => platformTenant.SaveChangesAsync());
    }

    [Fact]
    public async Task Cross_tenant_write_is_blocked_by_row_level_security()
    {
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT set_config('mx.tenant_id', @tenant, false);
            INSERT INTO mx.retention_policies (id, organization_id, name, retention_days, action, applies_to_all_mailboxes, is_enabled, created_at, updated_at)
            VALUES (gen_random_uuid(), @victim, 'evil', 10, 'Delete', true, true, now(), now());
            """, conn);
        cmd.Parameters.AddWithValue("tenant", db.PlatformOrgId.ToString());
        cmd.Parameters.AddWithValue("victim", db.ExampleOrgId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Pooled_connections_do_not_leak_tenant_scope_between_contexts()
    {
        // Same connection string => same physical pool. Run tenant A, then an unscoped context.
        for (var i = 0; i < 20; i++)
        {
            await using (var a = db.CreateContext(db.ExampleOrgId))
                Assert.True(await a.Mailboxes.CountAsync() > 0);
            await using (var none = db.CreateContext())
                Assert.Equal(0, await none.Mailboxes.IgnoreQueryFilters().CountAsync());
            await using (var b = db.CreateContext(db.PlatformOrgId))
                Assert.Equal(0, await b.Mailboxes.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public async Task Audit_events_are_append_only_for_the_app_role()
    {
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT set_config('mx.platform_scope','on',false); DELETE FROM mx.audit_events;", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }
}
