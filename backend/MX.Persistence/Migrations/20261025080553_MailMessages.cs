using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MX.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MailMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mail_messages",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    internet_message_id = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    envelope_from = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    from_header = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    is_read = table.Column<bool>(type: "boolean", nullable: false),
                    is_flagged = table.Column<bool>(type: "boolean", nullable: false),
                    remote_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    helo_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    received_over_tls = table.Column<bool>(type: "boolean", nullable: false),
                    spf_result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    dkim_result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    dmarc_result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_messages", x => x.id);
                    table.CheckConstraint("ck_mail_messages_size", "size_bytes >= 0");
                    table.ForeignKey(
                        name: "fk_mail_messages_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalSchema: "mx",
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_mail_messages_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_mailbox_id_folder_received_at",
                schema: "mx",
                table: "mail_messages",
                columns: new[] { "mailbox_id", "folder", "received_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_organization_id_received_at",
                schema: "mx",
                table: "mail_messages",
                columns: new[] { "organization_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_storage_path",
                schema: "mx",
                table: "mail_messages",
                column: "storage_path",
                unique: true);

            // Same tenant isolation as every other tenant-owned table (see RowLevelSecurity migration).
            migrationBuilder.Sql("ALTER TABLE mx.mail_messages ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE mx.mail_messages FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON mx.mail_messages
                  USING (current_setting('mx.platform_scope', true) = 'on'
                         OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
                  WITH CHECK (current_setting('mx.platform_scope', true) = 'on'
                         OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
                """);
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_app') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE ON mx.mail_messages TO mx_app;
                  END IF;
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_backup') THEN
                    GRANT SELECT ON mx.mail_messages TO mx_backup;
                  END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_messages",
                schema: "mx");
        }
    }
}
