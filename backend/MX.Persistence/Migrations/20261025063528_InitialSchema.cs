using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MX.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mx");

            migrationBuilder.CreateTable(
                name: "application_roles",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_platform_role = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    is_privileged = table.Column<bool>(type: "boolean", nullable: false),
                    details = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mail_servers",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    server_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    credential_secret_ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_servers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                    table.CheckConstraint("ck_organizations_slug", "slug ~ '^[a-z0-9][a-z0-9-]{1,62}$'");
                });

            migrationBuilder.CreateTable(
                name: "system_configurations",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_configurations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "server_health_snapshots",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mail_server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cpu_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    memory_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    disk_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    queue_length = table.Column<int>(type: "integer", nullable: false),
                    active_connections = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    metrics = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_health_snapshots", x => x.id);
                    table.CheckConstraint("ck_health_percentages", "cpu_percent BETWEEN 0 AND 100 AND memory_percent BETWEEN 0 AND 100 AND disk_percent BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_server_health_snapshots_mail_servers_mail_server_id",
                        column: x => x.mail_server_id,
                        principalSchema: "mx",
                        principalTable: "mail_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "application_users",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    mfa_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_users", x => x.id);
                    table.CheckConstraint("ck_users_failed_logins", "failed_login_count >= 0");
                    table.ForeignKey(
                        name: "fk_application_users_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backup_jobs",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    storage_location = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_backup_jobs_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "legal_holds",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    case_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    mailbox_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_holds", x => x.id);
                    table.CheckConstraint("ck_legal_holds_release", "(status = 'Released') = (released_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_legal_holds_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mail_routing_rules",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    sender_pattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recipient_pattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    subject_pattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    action_target = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_routing_rules", x => x.id);
                    table.CheckConstraint("ck_routing_rules_priority", "priority >= 0");
                    table.ForeignKey(
                        name: "fk_mail_routing_rules_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_domains",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain_name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    verification_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    verification_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    mx_configured = table.Column<bool>(type: "boolean", nullable: false),
                    spf_configured = table.Column<bool>(type: "boolean", nullable: false),
                    dkim_configured = table.Column<bool>(type: "boolean", nullable: false),
                    dmarc_configured = table.Column<bool>(type: "boolean", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    certificate_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_domains", x => x.id);
                    table.CheckConstraint("ck_organization_domains_lowercase", "domain_name = lower(domain_name)");
                    table.ForeignKey(
                        name: "fk_organization_domains_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_subscriptions",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    mailbox_limit = table.Column<int>(type: "integer", nullable: false),
                    storage_limit_bytes = table.Column<long>(type: "bigint", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_subscriptions", x => x.id);
                    table.CheckConstraint("ck_subscriptions_dates", "ends_at IS NULL OR ends_at > starts_at");
                    table.CheckConstraint("ck_subscriptions_limits", "mailbox_limit >= 0 AND storage_limit_bytes >= 0");
                    table.ForeignKey(
                        name: "fk_organization_subscriptions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "retention_policies",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    retention_days = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    applies_to_all_mailboxes = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_policies", x => x.id);
                    table.CheckConstraint("ck_retention_days", "retention_days BETWEEN 1 AND 36500");
                    table.ForeignKey(
                        name: "fk_retention_policies_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_policies",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    password_min_length = table.Column<int>(type: "integer", nullable: false),
                    require_mfa = table.Column<bool>(type: "boolean", nullable: false),
                    session_timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    max_failed_logins = table.Column<int>(type: "integer", nullable: false),
                    lockout_minutes = table.Column<int>(type: "integer", nullable: false),
                    enforce_tls = table.Column<bool>(type: "boolean", nullable: false),
                    allowed_ip_ranges = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_policies", x => x.id);
                    table.CheckConstraint("ck_security_policies_values", "password_min_length BETWEEN 8 AND 128 AND session_timeout_minutes > 0 AND max_failed_logins > 0 AND lockout_minutes > 0");
                    table.ForeignKey(
                        name: "fk_security_policies_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_credentials",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_credentials_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "mx",
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_api_credentials_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "authentication_events",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempted_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_authentication_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_authentication_events_application_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "mx",
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_authentication_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    link = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_application_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "mx",
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_roles_application_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "mx",
                        principalTable: "application_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_roles_application_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "mx",
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_roles_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "discovery_requests",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    legal_hold_id = table.Column<Guid>(type: "uuid", nullable: true),
                    query = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    date_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    date_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    mailbox_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    result_count = table.Column<int>(type: "integer", nullable: true),
                    export_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discovery_requests", x => x.id);
                    table.CheckConstraint("ck_discovery_dates", "date_from IS NULL OR date_to IS NULL OR date_to >= date_from");
                    table.ForeignKey(
                        name: "fk_discovery_requests_legal_holds_legal_hold_id",
                        column: x => x.legal_hold_id,
                        principalSchema: "mx",
                        principalTable: "legal_holds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_discovery_requests_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "distribution_lists",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    allow_external_senders = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_distribution_lists", x => x.id);
                    table.ForeignKey(
                        name: "fk_distribution_lists_organization_domains_domain_id",
                        column: x => x.domain_id,
                        principalSchema: "mx",
                        principalTable: "organization_domains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_distribution_lists_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mailboxes",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mail_server_id = table.Column<Guid>(type: "uuid", nullable: true),
                    local_part = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    email_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    mailbox_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quota_bytes = table.Column<long>(type: "bigint", nullable: false),
                    used_bytes = table.Column<long>(type: "bigint", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mailboxes", x => x.id);
                    table.CheckConstraint("ck_mailboxes_lowercase", "email_address = lower(email_address)");
                    table.CheckConstraint("ck_mailboxes_quota", "quota_bytes > 0 AND used_bytes >= 0");
                    table.ForeignKey(
                        name: "fk_mailboxes_mail_servers_mail_server_id",
                        column: x => x.mail_server_id,
                        principalSchema: "mx",
                        principalTable: "mail_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_mailboxes_organization_domains_domain_id",
                        column: x => x.domain_id,
                        principalSchema: "mx",
                        principalTable: "organization_domains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mailboxes_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "distribution_list_members",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    distribution_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_distribution_list_members", x => x.id);
                    table.CheckConstraint("ck_dl_members_target", "(mailbox_id IS NULL) <> (external_address IS NULL)");
                    table.ForeignKey(
                        name: "fk_distribution_list_members_distribution_lists_distribution_l",
                        column: x => x.distribution_list_id,
                        principalSchema: "mx",
                        principalTable: "distribution_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_distribution_list_members_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalSchema: "mx",
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_distribution_list_members_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mail_aliases",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_aliases", x => x.id);
                    table.ForeignKey(
                        name: "fk_mail_aliases_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalSchema: "mx",
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_mail_aliases_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quarantine_records",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    sender = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    spam_score = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quarantine_records", x => x.id);
                    table.CheckConstraint("ck_quarantine_expiry", "expires_at > received_at AND size_bytes >= 0");
                    table.ForeignKey(
                        name: "fk_quarantine_records_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalSchema: "mx",
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_quarantine_records_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "restore_jobs",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    backup_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_mailbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restore_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_restore_jobs_backup_jobs_backup_job_id",
                        column: x => x.backup_job_id,
                        principalSchema: "mx",
                        principalTable: "backup_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_restore_jobs_mailboxes_target_mailbox_id",
                        column: x => x.target_mailbox_id,
                        principalSchema: "mx",
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_restore_jobs_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_alerts",
                schema: "mx",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_alerts_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalSchema: "mx",
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_security_alerts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "mx",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_credentials_created_by_user_id",
                schema: "mx",
                table: "api_credentials",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_credentials_key_prefix",
                schema: "mx",
                table: "api_credentials",
                column: "key_prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_credentials_organization_id",
                schema: "mx",
                table: "api_credentials",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_roles_name",
                schema: "mx",
                table: "application_roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_users_normalized_email",
                schema: "mx",
                table: "application_users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_users_organization_id",
                schema: "mx",
                table: "application_users",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_entity_type_entity_id",
                schema: "mx",
                table: "audit_events",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_organization_id_action",
                schema: "mx",
                table: "audit_events",
                columns: new[] { "organization_id", "action" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_organization_id_occurred_at",
                schema: "mx",
                table: "audit_events",
                columns: new[] { "organization_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_authentication_events_organization_id_occurred_at",
                schema: "mx",
                table: "authentication_events",
                columns: new[] { "organization_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_authentication_events_user_id_occurred_at",
                schema: "mx",
                table: "authentication_events",
                columns: new[] { "user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_jobs_organization_id_created_at",
                schema: "mx",
                table: "backup_jobs",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_jobs_status",
                schema: "mx",
                table: "backup_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_discovery_requests_legal_hold_id",
                schema: "mx",
                table: "discovery_requests",
                column: "legal_hold_id");

            migrationBuilder.CreateIndex(
                name: "ix_discovery_requests_organization_id_status",
                schema: "mx",
                table: "discovery_requests",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_distribution_list_members_distribution_list_id_external_add",
                schema: "mx",
                table: "distribution_list_members",
                columns: new[] { "distribution_list_id", "external_address" },
                unique: true,
                filter: "external_address IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_list_members_distribution_list_id_mailbox_id",
                schema: "mx",
                table: "distribution_list_members",
                columns: new[] { "distribution_list_id", "mailbox_id" },
                unique: true,
                filter: "mailbox_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_list_members_mailbox_id",
                schema: "mx",
                table: "distribution_list_members",
                column: "mailbox_id");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_list_members_organization_id",
                schema: "mx",
                table: "distribution_list_members",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_lists_domain_id",
                schema: "mx",
                table: "distribution_lists",
                column: "domain_id");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_lists_email_address",
                schema: "mx",
                table: "distribution_lists",
                column: "email_address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_distribution_lists_organization_id",
                schema: "mx",
                table: "distribution_lists",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_legal_holds_mailbox_ids",
                schema: "mx",
                table: "legal_holds",
                column: "mailbox_ids")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_legal_holds_organization_id_status",
                schema: "mx",
                table: "legal_holds",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_aliases_alias_address",
                schema: "mx",
                table: "mail_aliases",
                column: "alias_address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_aliases_mailbox_id",
                schema: "mx",
                table: "mail_aliases",
                column: "mailbox_id");

            migrationBuilder.CreateIndex(
                name: "ix_mail_aliases_organization_id",
                schema: "mx",
                table: "mail_aliases",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_mail_routing_rules_organization_id_priority",
                schema: "mx",
                table: "mail_routing_rules",
                columns: new[] { "organization_id", "priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_servers_hostname",
                schema: "mx",
                table: "mail_servers",
                column: "hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_domain_id",
                schema: "mx",
                table: "mailboxes",
                column: "domain_id");

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_email_address",
                schema: "mx",
                table: "mailboxes",
                column: "email_address",
                unique: true,
                filter: "status <> 'Deleted'");

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_mail_server_id",
                schema: "mx",
                table: "mailboxes",
                column: "mail_server_id");

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_organization_id_created_at",
                schema: "mx",
                table: "mailboxes",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_organization_id_status",
                schema: "mx",
                table: "mailboxes",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_organization_id_user_id_read_at",
                schema: "mx",
                table: "notifications",
                columns: new[] { "organization_id", "user_id", "read_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id",
                schema: "mx",
                table: "notifications",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_organization_domains_domain_name",
                schema: "mx",
                table: "organization_domains",
                column: "domain_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_organization_domains_default",
                schema: "mx",
                table: "organization_domains",
                column: "organization_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_organization_subscriptions_organization_id",
                schema: "mx",
                table: "organization_subscriptions",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organizations_slug",
                schema: "mx",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quarantine_records_expires_at",
                schema: "mx",
                table: "quarantine_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_quarantine_records_mailbox_id",
                schema: "mx",
                table: "quarantine_records",
                column: "mailbox_id");

            migrationBuilder.CreateIndex(
                name: "ix_quarantine_records_organization_id_status_received_at",
                schema: "mx",
                table: "quarantine_records",
                columns: new[] { "organization_id", "status", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_jobs_backup_job_id",
                schema: "mx",
                table: "restore_jobs",
                column: "backup_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_restore_jobs_organization_id_created_at",
                schema: "mx",
                table: "restore_jobs",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_jobs_target_mailbox_id",
                schema: "mx",
                table: "restore_jobs",
                column: "target_mailbox_id");

            migrationBuilder.CreateIndex(
                name: "ix_retention_policies_organization_id_name",
                schema: "mx",
                table: "retention_policies",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_alerts_mailbox_id",
                schema: "mx",
                table: "security_alerts",
                column: "mailbox_id");

            migrationBuilder.CreateIndex(
                name: "ix_security_alerts_organization_id_status_detected_at",
                schema: "mx",
                table: "security_alerts",
                columns: new[] { "organization_id", "status", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "ix_security_policies_organization_id",
                schema: "mx",
                table: "security_policies",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_server_health_snapshots_mail_server_id_captured_at",
                schema: "mx",
                table: "server_health_snapshots",
                columns: new[] { "mail_server_id", "captured_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_system_configurations_key",
                schema: "mx",
                table: "system_configurations",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_organization_id",
                schema: "mx",
                table: "user_roles",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                schema: "mx",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_user_id_role_id",
                schema: "mx",
                table: "user_roles",
                columns: new[] { "user_id", "role_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_credentials",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "authentication_events",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "discovery_requests",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "distribution_list_members",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "mail_aliases",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "mail_routing_rules",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "organization_subscriptions",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "quarantine_records",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "restore_jobs",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "retention_policies",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "security_alerts",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "security_policies",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "server_health_snapshots",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "system_configurations",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "legal_holds",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "distribution_lists",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "backup_jobs",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "mailboxes",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "application_roles",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "application_users",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "mail_servers",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "organization_domains",
                schema: "mx");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "mx");
        }
    }
}
