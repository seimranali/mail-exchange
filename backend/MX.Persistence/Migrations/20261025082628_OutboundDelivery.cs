using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MX.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboundDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dkim_keys",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selector = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    public_key = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    encrypted_private_key = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    key_bits = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dkim_keys", x => x.id);
                    table.CheckConstraint("ck_dkim_keys_selector", "selector ~ '^[a-z0-9][a-z0-9-]{0,62}$'");
                    table.ForeignKey(
                        name: "fk_dkim_keys_organization_domains_domain_id",
                        column: x => x.domain_id,
                        principalSchema: "mx",
                        principalTable: "organization_domains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_dkim_keys_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbound_messages",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    envelope_from = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    internet_message_id = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    dkim_signed = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_messages", x => x.id);
                    table.CheckConstraint("ck_outbound_messages_size", "size_bytes >= 0");
                    table.ForeignKey(
                        name: "fk_outbound_messages_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbound_recipients",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbound_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_response = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    remote_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_recipients", x => x.id);
                    table.CheckConstraint("ck_outbound_recipients_attempts", "attempts >= 0");
                    table.ForeignKey(
                        name: "fk_outbound_recipients_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outbound_recipients_outbound_messages_outbound_message_id",
                        column: x => x.outbound_message_id,
                        principalSchema: "mx",
                        principalTable: "outbound_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dkim_keys_domain_id_selector",
                schema: "mx",
                table: "dkim_keys",
                columns: new[] { "domain_id", "selector" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dkim_keys_organization_id",
                schema: "mx",
                table: "dkim_keys",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_dkim_keys_active",
                schema: "mx",
                table: "dkim_keys",
                column: "domain_id",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_organization_id_created_at",
                schema: "mx",
                table: "outbound_messages",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_storage_path",
                schema: "mx",
                table: "outbound_messages",
                column: "storage_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_recipients_due",
                schema: "mx",
                table: "outbound_recipients",
                column: "next_attempt_at",
                filter: "status IN ('Queued', 'Deferred', 'Sending')");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_recipients_organization_id_status",
                schema: "mx",
                table: "outbound_recipients",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_recipients_outbound_message_id",
                schema: "mx",
                table: "outbound_recipients",
                column: "outbound_message_id");

            TenantRls.Enable(migrationBuilder, "outbound_messages");
            TenantRls.Enable(migrationBuilder, "outbound_recipients");
            TenantRls.Enable(migrationBuilder, "dkim_keys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dkim_keys",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "outbound_recipients",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "outbound_messages",
                schema: "mx");
        }
    }
}
