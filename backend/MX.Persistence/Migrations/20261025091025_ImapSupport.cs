using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MX.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImapSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_answered",
                schema: "mx",
                table: "mail_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_deleted",
                schema: "mx",
                table: "mail_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_draft",
                schema: "mx",
                table: "mail_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "uid",
                schema: "mx",
                table: "mail_messages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "mail_folder_states",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    uid_validity = table.Column<long>(type: "bigint", nullable: false),
                    uid_next = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_folder_states", x => x.id);
                    table.CheckConstraint("ck_mail_folder_states_uids", "uid_validity > 0 AND uid_next > 0");
                    table.ForeignKey(
                        name: "fk_mail_folder_states_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalSchema: "mx",
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_mail_folder_states_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Backfill: number existing messages per (mailbox, folder) in arrival order, then seed
            // each folder's UIDVALIDITY and next UID. Runs as the owner with RLS bypassed.
            migrationBuilder.Sql("SELECT set_config('mx.platform_scope', 'on', true);");
            migrationBuilder.Sql("""
                UPDATE mx.mail_messages m
                   SET uid = n.rn
                  FROM (SELECT id, row_number() OVER (PARTITION BY mailbox_id, folder ORDER BY received_at, id) AS rn
                          FROM mx.mail_messages) n
                 WHERE n.id = m.id;
                """);
            migrationBuilder.Sql("""
                INSERT INTO mx.mail_folder_states (id, organization_id, mailbox_id, folder, uid_validity, uid_next, created_at, updated_at)
                SELECT gen_random_uuid(), organization_id, mailbox_id, folder,
                       extract(epoch FROM now())::bigint, max(uid) + 1, now(), now()
                  FROM mx.mail_messages
                 GROUP BY organization_id, mailbox_id, folder;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_mail_messages_folder_uid",
                schema: "mx",
                table: "mail_messages",
                columns: new[] { "mailbox_id", "folder", "uid" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_mail_messages_uid",
                schema: "mx",
                table: "mail_messages",
                sql: "uid > 0");

            migrationBuilder.CreateIndex(
                name: "ix_mail_folder_states_mailbox_id_folder",
                schema: "mx",
                table: "mail_folder_states",
                columns: new[] { "mailbox_id", "folder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_folder_states_organization_id",
                schema: "mx",
                table: "mail_folder_states",
                column: "organization_id");

            TenantRls.Enable(migrationBuilder, "mail_folder_states");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_folder_states",
                schema: "mx");

            migrationBuilder.DropIndex(
                name: "ux_mail_messages_folder_uid",
                schema: "mx",
                table: "mail_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mail_messages_uid",
                schema: "mx",
                table: "mail_messages");

            migrationBuilder.DropColumn(
                name: "is_answered",
                schema: "mx",
                table: "mail_messages");

            migrationBuilder.DropColumn(
                name: "is_deleted",
                schema: "mx",
                table: "mail_messages");

            migrationBuilder.DropColumn(
                name: "is_draft",
                schema: "mx",
                table: "mail_messages");

            migrationBuilder.DropColumn(
                name: "uid",
                schema: "mx",
                table: "mail_messages");
        }
    }
}
