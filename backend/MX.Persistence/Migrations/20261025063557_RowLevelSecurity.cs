using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MX.Persistence.Migrations
{
    /// <summary>
    /// Defence-in-depth tenant isolation with PostgreSQL Row-Level Security, plus least-privilege
    /// grants for the runtime role. See docs/security/tenant-isolation.md.
    /// </summary>
    public partial class RowLevelSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "organization_domains", "organization_subscriptions", "mailboxes", "mail_aliases", "distribution_lists",
            "distribution_list_members", "mail_routing_rules", "application_users", "user_roles", "api_credentials",
            "security_policies", "security_alerts", "quarantine_records", "retention_policies", "legal_holds",
            "discovery_requests", "backup_jobs", "restore_jobs", "notifications",
        ];

        // Tables whose organization_id may be NULL for platform-level rows.
        private static readonly string[] OptionalTenantTables = ["audit_events", "authentication_events"];

        private const string PlatformScope = "current_setting('mx.platform_scope', true) = 'on'";
        private const string CurrentTenant = "NULLIF(current_setting('mx.tenant_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables.Concat(OptionalTenantTables))
                EnableRls(migrationBuilder, table, $"organization_id = {CurrentTenant}");
            EnableRls(migrationBuilder, "organizations", $"id = {CurrentTenant}");

            // Runtime role: DML only, and append-only on the audit trails. Skipped when the role
            // does not exist (e.g. throwaway test databases).
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_app') THEN
                    GRANT USAGE ON SCHEMA mx TO mx_app;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA mx TO mx_app;
                    REVOKE UPDATE, DELETE ON mx.audit_events, mx.authentication_events FROM mx_app;
                    REVOKE ALL ON mx.__ef_migrations_history FROM mx_app;
                    GRANT SELECT ON mx.__ef_migrations_history TO mx_app;
                  END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables.Concat(OptionalTenantTables).Append("organizations"))
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON mx.{table};");
                migrationBuilder.Sql($"ALTER TABLE mx.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE mx.{table} DISABLE ROW LEVEL SECURITY;");
            }
        }

        private static void EnableRls(MigrationBuilder migrationBuilder, string table, string predicate)
        {
            migrationBuilder.Sql($"ALTER TABLE mx.{table} ENABLE ROW LEVEL SECURITY;");
            // FORCE applies the policy to the table owner too, so migrations/seed must set platform scope explicitly.
            migrationBuilder.Sql($"ALTER TABLE mx.{table} FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY tenant_isolation ON mx.{table}
                  USING ({PlatformScope} OR {predicate})
                  WITH CHECK ({PlatformScope} OR {predicate});
                """);
        }
    }
}
