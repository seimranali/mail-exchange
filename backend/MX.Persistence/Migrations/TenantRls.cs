using Microsoft.EntityFrameworkCore.Migrations;

namespace MX.Persistence.Migrations;

/// <summary>Applies the standard tenant-isolation policy and role grants to a new tenant-owned table.</summary>
internal static class TenantRls
{
    private const string Predicate =
        "current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid";

    public static void Enable(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"ALTER TABLE mx.{table} ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"ALTER TABLE mx.{table} FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON mx.{table} USING ({Predicate}) WITH CHECK ({Predicate});");
        migrationBuilder.Sql($"""
            DO $$
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_app') THEN
                GRANT SELECT, INSERT, UPDATE, DELETE ON mx.{table} TO mx_app;
              END IF;
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_backup') THEN
                GRANT SELECT ON mx.{table} TO mx_backup;
              END IF;
            END $$;
            """);
    }
}
