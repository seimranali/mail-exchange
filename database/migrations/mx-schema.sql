DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'mx') THEN
        CREATE SCHEMA mx;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS mx.__ef_migrations_history (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'mx') THEN
            CREATE SCHEMA mx;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.application_roles (
        id uuid NOT NULL,
        name character varying(64) NOT NULL,
        description character varying(500),
        is_platform_role boolean NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_application_roles PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.audit_events (
        id uuid NOT NULL,
        organization_id uuid,
        actor_user_id uuid,
        action character varying(100) NOT NULL,
        entity_type character varying(100) NOT NULL,
        entity_id character varying(64),
        ip_address character varying(45),
        is_privileged boolean NOT NULL,
        details jsonb,
        occurred_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_audit_events PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.mail_servers (
        id uuid NOT NULL,
        name character varying(100) NOT NULL,
        hostname character varying(253) NOT NULL,
        server_type character varying(32) NOT NULL,
        status character varying(32) NOT NULL,
        region character varying(64),
        credential_secret_ref character varying(256),
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_mail_servers PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.organizations (
        id uuid NOT NULL,
        name character varying(200) NOT NULL,
        slug character varying(63) NOT NULL,
        status character varying(32) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_organizations PRIMARY KEY (id),
        CONSTRAINT ck_organizations_slug CHECK (slug ~ '^[a-z0-9][a-z0-9-]{1,62}$')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.system_configurations (
        id uuid NOT NULL,
        key character varying(128) NOT NULL,
        value character varying(4000) NOT NULL,
        description character varying(500),
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_system_configurations PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.server_health_snapshots (
        id uuid NOT NULL,
        mail_server_id uuid NOT NULL,
        cpu_percent numeric(5,2) NOT NULL,
        memory_percent numeric(5,2) NOT NULL,
        disk_percent numeric(5,2) NOT NULL,
        queue_length integer NOT NULL,
        active_connections integer NOT NULL,
        status character varying(32) NOT NULL,
        metrics jsonb,
        captured_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_server_health_snapshots PRIMARY KEY (id),
        CONSTRAINT ck_health_percentages CHECK (cpu_percent BETWEEN 0 AND 100 AND memory_percent BETWEEN 0 AND 100 AND disk_percent BETWEEN 0 AND 100),
        CONSTRAINT fk_server_health_snapshots_mail_servers_mail_server_id FOREIGN KEY (mail_server_id) REFERENCES mx.mail_servers (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.application_users (
        id uuid NOT NULL,
        email character varying(320) NOT NULL,
        normalized_email character varying(320) NOT NULL,
        display_name character varying(200) NOT NULL,
        password_hash character varying(256) NOT NULL,
        is_active boolean NOT NULL,
        mfa_enabled boolean NOT NULL,
        failed_login_count integer NOT NULL,
        lockout_ends_at timestamp with time zone,
        last_login_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_application_users PRIMARY KEY (id),
        CONSTRAINT ck_users_failed_logins CHECK (failed_login_count >= 0),
        CONSTRAINT fk_application_users_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.backup_jobs (
        id uuid NOT NULL,
        scope character varying(32) NOT NULL,
        status character varying(32) NOT NULL,
        storage_location character varying(1024),
        size_bytes bigint,
        error_message character varying(4000),
        requested_by_user_id uuid,
        started_at timestamp with time zone,
        completed_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_backup_jobs PRIMARY KEY (id),
        CONSTRAINT fk_backup_jobs_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.legal_holds (
        id uuid NOT NULL,
        name character varying(200) NOT NULL,
        case_reference character varying(100) NOT NULL,
        description character varying(2000),
        status character varying(32) NOT NULL,
        mailbox_ids uuid[] NOT NULL,
        created_by_user_id uuid NOT NULL,
        starts_at timestamp with time zone NOT NULL,
        released_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_legal_holds PRIMARY KEY (id),
        CONSTRAINT ck_legal_holds_release CHECK ((status = 'Released') = (released_at IS NOT NULL)),
        CONSTRAINT fk_legal_holds_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.mail_routing_rules (
        id uuid NOT NULL,
        name character varying(200) NOT NULL,
        priority integer NOT NULL,
        is_enabled boolean NOT NULL,
        sender_pattern character varying(500),
        recipient_pattern character varying(500),
        subject_pattern character varying(500),
        action character varying(32) NOT NULL,
        action_target character varying(500),
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_mail_routing_rules PRIMARY KEY (id),
        CONSTRAINT ck_routing_rules_priority CHECK (priority >= 0),
        CONSTRAINT fk_mail_routing_rules_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.organization_domains (
        id uuid NOT NULL,
        domain_name character varying(253) NOT NULL,
        is_default boolean NOT NULL,
        verification_status character varying(32) NOT NULL,
        verification_token character varying(64) NOT NULL,
        mx_configured boolean NOT NULL,
        spf_configured boolean NOT NULL,
        dkim_configured boolean NOT NULL,
        dmarc_configured boolean NOT NULL,
        verified_at timestamp with time zone,
        certificate_expires_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_organization_domains PRIMARY KEY (id),
        CONSTRAINT ck_organization_domains_lowercase CHECK (domain_name = lower(domain_name)),
        CONSTRAINT fk_organization_domains_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.organization_subscriptions (
        id uuid NOT NULL,
        plan character varying(32) NOT NULL,
        status character varying(32) NOT NULL,
        mailbox_limit integer NOT NULL,
        storage_limit_bytes bigint NOT NULL,
        starts_at timestamp with time zone NOT NULL,
        ends_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_organization_subscriptions PRIMARY KEY (id),
        CONSTRAINT ck_subscriptions_dates CHECK (ends_at IS NULL OR ends_at > starts_at),
        CONSTRAINT ck_subscriptions_limits CHECK (mailbox_limit >= 0 AND storage_limit_bytes >= 0),
        CONSTRAINT fk_organization_subscriptions_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.retention_policies (
        id uuid NOT NULL,
        name character varying(200) NOT NULL,
        description character varying(1000),
        retention_days integer NOT NULL,
        action character varying(32) NOT NULL,
        applies_to_all_mailboxes boolean NOT NULL,
        is_enabled boolean NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_retention_policies PRIMARY KEY (id),
        CONSTRAINT ck_retention_days CHECK (retention_days BETWEEN 1 AND 36500),
        CONSTRAINT fk_retention_policies_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.security_policies (
        id uuid NOT NULL,
        password_min_length integer NOT NULL,
        require_mfa boolean NOT NULL,
        session_timeout_minutes integer NOT NULL,
        max_failed_logins integer NOT NULL,
        lockout_minutes integer NOT NULL,
        enforce_tls boolean NOT NULL,
        allowed_ip_ranges text[] NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_security_policies PRIMARY KEY (id),
        CONSTRAINT ck_security_policies_values CHECK (password_min_length BETWEEN 8 AND 128 AND session_timeout_minutes > 0 AND max_failed_logins > 0 AND lockout_minutes > 0),
        CONSTRAINT fk_security_policies_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.api_credentials (
        id uuid NOT NULL,
        name character varying(200) NOT NULL,
        key_prefix character varying(16) NOT NULL,
        key_hash character varying(128) NOT NULL,
        scopes text[] NOT NULL,
        created_by_user_id uuid NOT NULL,
        expires_at timestamp with time zone,
        revoked_at timestamp with time zone,
        last_used_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_api_credentials PRIMARY KEY (id),
        CONSTRAINT fk_api_credentials_application_users_created_by_user_id FOREIGN KEY (created_by_user_id) REFERENCES mx.application_users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_api_credentials_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.authentication_events (
        id uuid NOT NULL,
        organization_id uuid,
        user_id uuid,
        attempted_email character varying(320) NOT NULL,
        outcome character varying(32) NOT NULL,
        ip_address character varying(45),
        user_agent character varying(512),
        occurred_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_authentication_events PRIMARY KEY (id),
        CONSTRAINT fk_authentication_events_application_users_user_id FOREIGN KEY (user_id) REFERENCES mx.application_users (id) ON DELETE SET NULL,
        CONSTRAINT fk_authentication_events_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.notifications (
        id uuid NOT NULL,
        user_id uuid,
        title character varying(200) NOT NULL,
        message character varying(2000) NOT NULL,
        level character varying(32) NOT NULL,
        link character varying(500),
        read_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_notifications PRIMARY KEY (id),
        CONSTRAINT fk_notifications_application_users_user_id FOREIGN KEY (user_id) REFERENCES mx.application_users (id) ON DELETE CASCADE,
        CONSTRAINT fk_notifications_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.user_roles (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        role_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_user_roles PRIMARY KEY (id),
        CONSTRAINT fk_user_roles_application_roles_role_id FOREIGN KEY (role_id) REFERENCES mx.application_roles (id) ON DELETE RESTRICT,
        CONSTRAINT fk_user_roles_application_users_user_id FOREIGN KEY (user_id) REFERENCES mx.application_users (id) ON DELETE CASCADE,
        CONSTRAINT fk_user_roles_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.discovery_requests (
        id uuid NOT NULL,
        name character varying(200) NOT NULL,
        legal_hold_id uuid,
        query character varying(4000) NOT NULL,
        date_from timestamp with time zone,
        date_to timestamp with time zone,
        mailbox_ids uuid[] NOT NULL,
        status character varying(32) NOT NULL,
        result_count integer,
        export_ref character varying(512),
        requested_by_user_id uuid NOT NULL,
        completed_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_discovery_requests PRIMARY KEY (id),
        CONSTRAINT ck_discovery_dates CHECK (date_from IS NULL OR date_to IS NULL OR date_to >= date_from),
        CONSTRAINT fk_discovery_requests_legal_holds_legal_hold_id FOREIGN KEY (legal_hold_id) REFERENCES mx.legal_holds (id) ON DELETE SET NULL,
        CONSTRAINT fk_discovery_requests_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.distribution_lists (
        id uuid NOT NULL,
        domain_id uuid NOT NULL,
        name character varying(200) NOT NULL,
        email_address character varying(320) NOT NULL,
        description character varying(1000),
        allow_external_senders boolean NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_distribution_lists PRIMARY KEY (id),
        CONSTRAINT fk_distribution_lists_organization_domains_domain_id FOREIGN KEY (domain_id) REFERENCES mx.organization_domains (id) ON DELETE RESTRICT,
        CONSTRAINT fk_distribution_lists_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.mailboxes (
        id uuid NOT NULL,
        domain_id uuid NOT NULL,
        mail_server_id uuid,
        local_part character varying(64) NOT NULL,
        email_address character varying(320) NOT NULL,
        display_name character varying(200) NOT NULL,
        mailbox_type character varying(32) NOT NULL,
        status character varying(32) NOT NULL,
        quota_bytes bigint NOT NULL,
        used_bytes bigint NOT NULL,
        last_login_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_mailboxes PRIMARY KEY (id),
        CONSTRAINT ck_mailboxes_lowercase CHECK (email_address = lower(email_address)),
        CONSTRAINT ck_mailboxes_quota CHECK (quota_bytes > 0 AND used_bytes >= 0),
        CONSTRAINT fk_mailboxes_mail_servers_mail_server_id FOREIGN KEY (mail_server_id) REFERENCES mx.mail_servers (id) ON DELETE SET NULL,
        CONSTRAINT fk_mailboxes_organization_domains_domain_id FOREIGN KEY (domain_id) REFERENCES mx.organization_domains (id) ON DELETE RESTRICT,
        CONSTRAINT fk_mailboxes_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.distribution_list_members (
        id uuid NOT NULL,
        distribution_list_id uuid NOT NULL,
        mailbox_id uuid,
        external_address character varying(320),
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_distribution_list_members PRIMARY KEY (id),
        CONSTRAINT ck_dl_members_target CHECK ((mailbox_id IS NULL) <> (external_address IS NULL)),
        CONSTRAINT fk_distribution_list_members_distribution_lists_distribution_l FOREIGN KEY (distribution_list_id) REFERENCES mx.distribution_lists (id) ON DELETE CASCADE,
        CONSTRAINT fk_distribution_list_members_mailboxes_mailbox_id FOREIGN KEY (mailbox_id) REFERENCES mx.mailboxes (id) ON DELETE CASCADE,
        CONSTRAINT fk_distribution_list_members_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.mail_aliases (
        id uuid NOT NULL,
        mailbox_id uuid NOT NULL,
        alias_address character varying(320) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_mail_aliases PRIMARY KEY (id),
        CONSTRAINT fk_mail_aliases_mailboxes_mailbox_id FOREIGN KEY (mailbox_id) REFERENCES mx.mailboxes (id) ON DELETE CASCADE,
        CONSTRAINT fk_mail_aliases_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.quarantine_records (
        id uuid NOT NULL,
        mailbox_id uuid,
        message_id character varying(998) NOT NULL,
        sender character varying(320) NOT NULL,
        recipient character varying(320) NOT NULL,
        subject character varying(998),
        reason character varying(32) NOT NULL,
        status character varying(32) NOT NULL,
        spam_score numeric(6,2),
        size_bytes bigint NOT NULL,
        storage_ref character varying(512) NOT NULL,
        received_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        released_by_user_id uuid,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_quarantine_records PRIMARY KEY (id),
        CONSTRAINT ck_quarantine_expiry CHECK (expires_at > received_at AND size_bytes >= 0),
        CONSTRAINT fk_quarantine_records_mailboxes_mailbox_id FOREIGN KEY (mailbox_id) REFERENCES mx.mailboxes (id) ON DELETE SET NULL,
        CONSTRAINT fk_quarantine_records_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.restore_jobs (
        id uuid NOT NULL,
        backup_job_id uuid NOT NULL,
        target_mailbox_id uuid,
        status character varying(32) NOT NULL,
        error_message character varying(4000),
        requested_by_user_id uuid NOT NULL,
        started_at timestamp with time zone,
        completed_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_restore_jobs PRIMARY KEY (id),
        CONSTRAINT fk_restore_jobs_backup_jobs_backup_job_id FOREIGN KEY (backup_job_id) REFERENCES mx.backup_jobs (id) ON DELETE RESTRICT,
        CONSTRAINT fk_restore_jobs_mailboxes_target_mailbox_id FOREIGN KEY (target_mailbox_id) REFERENCES mx.mailboxes (id) ON DELETE SET NULL,
        CONSTRAINT fk_restore_jobs_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE TABLE mx.security_alerts (
        id uuid NOT NULL,
        title character varying(200) NOT NULL,
        description character varying(4000) NOT NULL,
        category character varying(64) NOT NULL,
        severity character varying(32) NOT NULL,
        status character varying(32) NOT NULL,
        mailbox_id uuid,
        acknowledged_by_user_id uuid,
        detected_at timestamp with time zone NOT NULL,
        resolved_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_security_alerts PRIMARY KEY (id),
        CONSTRAINT fk_security_alerts_mailboxes_mailbox_id FOREIGN KEY (mailbox_id) REFERENCES mx.mailboxes (id) ON DELETE SET NULL,
        CONSTRAINT fk_security_alerts_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_api_credentials_created_by_user_id ON mx.api_credentials (created_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_api_credentials_key_prefix ON mx.api_credentials (key_prefix);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_api_credentials_organization_id ON mx.api_credentials (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_application_roles_name ON mx.application_roles (name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_application_users_normalized_email ON mx.application_users (normalized_email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_application_users_organization_id ON mx.application_users (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_audit_events_entity_type_entity_id ON mx.audit_events (entity_type, entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_audit_events_organization_id_action ON mx.audit_events (organization_id, action);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_audit_events_organization_id_occurred_at ON mx.audit_events (organization_id, occurred_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_authentication_events_organization_id_occurred_at ON mx.authentication_events (organization_id, occurred_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_authentication_events_user_id_occurred_at ON mx.authentication_events (user_id, occurred_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_backup_jobs_organization_id_created_at ON mx.backup_jobs (organization_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_backup_jobs_status ON mx.backup_jobs (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_discovery_requests_legal_hold_id ON mx.discovery_requests (legal_hold_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_discovery_requests_organization_id_status ON mx.discovery_requests (organization_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_distribution_list_members_distribution_list_id_external_add ON mx.distribution_list_members (distribution_list_id, external_address) WHERE external_address IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_distribution_list_members_distribution_list_id_mailbox_id ON mx.distribution_list_members (distribution_list_id, mailbox_id) WHERE mailbox_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_distribution_list_members_mailbox_id ON mx.distribution_list_members (mailbox_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_distribution_list_members_organization_id ON mx.distribution_list_members (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_distribution_lists_domain_id ON mx.distribution_lists (domain_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_distribution_lists_email_address ON mx.distribution_lists (email_address);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_distribution_lists_organization_id ON mx.distribution_lists (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_legal_holds_mailbox_ids ON mx.legal_holds USING gin (mailbox_ids);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_legal_holds_organization_id_status ON mx.legal_holds (organization_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_mail_aliases_alias_address ON mx.mail_aliases (alias_address);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_mail_aliases_mailbox_id ON mx.mail_aliases (mailbox_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_mail_aliases_organization_id ON mx.mail_aliases (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_mail_routing_rules_organization_id_priority ON mx.mail_routing_rules (organization_id, priority);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_mail_servers_hostname ON mx.mail_servers (hostname);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_mailboxes_domain_id ON mx.mailboxes (domain_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_mailboxes_email_address ON mx.mailboxes (email_address) WHERE status <> 'Deleted';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_mailboxes_mail_server_id ON mx.mailboxes (mail_server_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_mailboxes_organization_id_created_at ON mx.mailboxes (organization_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_mailboxes_organization_id_status ON mx.mailboxes (organization_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_notifications_organization_id_user_id_read_at ON mx.notifications (organization_id, user_id, read_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_notifications_user_id ON mx.notifications (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_organization_domains_domain_name ON mx.organization_domains (domain_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ux_organization_domains_default ON mx.organization_domains (organization_id) WHERE is_default;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_organization_subscriptions_organization_id ON mx.organization_subscriptions (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_organizations_slug ON mx.organizations (slug);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_quarantine_records_expires_at ON mx.quarantine_records (expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_quarantine_records_mailbox_id ON mx.quarantine_records (mailbox_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_quarantine_records_organization_id_status_received_at ON mx.quarantine_records (organization_id, status, received_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_restore_jobs_backup_job_id ON mx.restore_jobs (backup_job_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_restore_jobs_organization_id_created_at ON mx.restore_jobs (organization_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_restore_jobs_target_mailbox_id ON mx.restore_jobs (target_mailbox_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_retention_policies_organization_id_name ON mx.retention_policies (organization_id, name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_security_alerts_mailbox_id ON mx.security_alerts (mailbox_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_security_alerts_organization_id_status_detected_at ON mx.security_alerts (organization_id, status, detected_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_security_policies_organization_id ON mx.security_policies (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_server_health_snapshots_mail_server_id_captured_at ON mx.server_health_snapshots (mail_server_id, captured_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_system_configurations_key ON mx.system_configurations (key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_user_roles_organization_id ON mx.user_roles (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE INDEX ix_user_roles_role_id ON mx.user_roles (role_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    CREATE UNIQUE INDEX ix_user_roles_user_id_role_id ON mx.user_roles (user_id, role_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063528_InitialSchema') THEN
    INSERT INTO mx.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261025063528_InitialSchema', '10.0.13');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.organization_domains ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.organization_domains FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.organization_domains
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.organization_subscriptions ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.organization_subscriptions FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.organization_subscriptions
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.mailboxes ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.mailboxes FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.mailboxes
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.mail_aliases ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.mail_aliases FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.mail_aliases
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.distribution_lists ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.distribution_lists FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.distribution_lists
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.distribution_list_members ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.distribution_list_members FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.distribution_list_members
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.mail_routing_rules ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.mail_routing_rules FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.mail_routing_rules
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.application_users ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.application_users FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.application_users
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.user_roles ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.user_roles FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.user_roles
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.api_credentials ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.api_credentials FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.api_credentials
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.security_policies ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.security_policies FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.security_policies
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.security_alerts ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.security_alerts FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.security_alerts
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.quarantine_records ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.quarantine_records FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.quarantine_records
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.retention_policies ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.retention_policies FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.retention_policies
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.legal_holds ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.legal_holds FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.legal_holds
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.discovery_requests ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.discovery_requests FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.discovery_requests
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.backup_jobs ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.backup_jobs FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.backup_jobs
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.restore_jobs ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.restore_jobs FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.restore_jobs
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.notifications ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.notifications FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.notifications
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.audit_events ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.audit_events FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.audit_events
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.authentication_events ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.authentication_events FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.authentication_events
      USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.organizations ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    ALTER TABLE mx.organizations FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON mx.organizations
      USING (current_setting('mx.platform_scope', true) = 'on' OR id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025063557_RowLevelSecurity') THEN
    INSERT INTO mx.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261025063557_RowLevelSecurity', '10.0.13');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025080553_MailMessages') THEN
    CREATE TABLE mx.mail_messages (
        id uuid NOT NULL,
        mailbox_id uuid NOT NULL,
        folder character varying(32) NOT NULL,
        internet_message_id character varying(998),
        envelope_from character varying(320) NOT NULL,
        from_header character varying(998),
        subject character varying(998),
        size_bytes bigint NOT NULL,
        storage_path character varying(512) NOT NULL,
        is_read boolean NOT NULL,
        is_flagged boolean NOT NULL,
        remote_ip character varying(45),
        helo_name character varying(255),
        received_over_tls boolean NOT NULL,
        spf_result character varying(32) NOT NULL,
        dkim_result character varying(32) NOT NULL,
        dmarc_result character varying(32) NOT NULL,
        received_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_mail_messages PRIMARY KEY (id),
        CONSTRAINT ck_mail_messages_size CHECK (size_bytes >= 0),
        CONSTRAINT fk_mail_messages_mailboxes_mailbox_id FOREIGN KEY (mailbox_id) REFERENCES mx.mailboxes (id) ON DELETE CASCADE,
        CONSTRAINT fk_mail_messages_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025080553_MailMessages') THEN
    CREATE INDEX ix_mail_messages_mailbox_id_folder_received_at ON mx.mail_messages (mailbox_id, folder, received_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025080553_MailMessages') THEN
    CREATE INDEX ix_mail_messages_organization_id_received_at ON mx.mail_messages (organization_id, received_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025080553_MailMessages') THEN
    CREATE UNIQUE INDEX ix_mail_messages_storage_path ON mx.mail_messages (storage_path);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025080553_MailMessages') THEN
    ALTER TABLE mx.mail_messages ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025080553_MailMessages') THEN
    ALTER TABLE mx.mail_messages FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025080553_MailMessages') THEN
    CREATE POLICY tenant_isolation ON mx.mail_messages
      USING (current_setting('mx.platform_scope', true) = 'on'
             OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
      WITH CHECK (current_setting('mx.platform_scope', true) = 'on'
             OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025080553_MailMessages') THEN
    DO $$
    BEGIN
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON mx.mail_messages TO mx_app;
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_backup') THEN
        GRANT SELECT ON mx.mail_messages TO mx_backup;
      END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025080553_MailMessages') THEN
    INSERT INTO mx.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261025080553_MailMessages', '10.0.13');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE TABLE mx.dkim_keys (
        id uuid NOT NULL,
        domain_id uuid NOT NULL,
        selector character varying(63) NOT NULL,
        public_key character varying(2048) NOT NULL,
        encrypted_private_key character varying(8192) NOT NULL,
        key_bits integer NOT NULL,
        is_active boolean NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_dkim_keys PRIMARY KEY (id),
        CONSTRAINT ck_dkim_keys_selector CHECK (selector ~ '^[a-z0-9][a-z0-9-]{0,62}$'),
        CONSTRAINT fk_dkim_keys_organization_domains_domain_id FOREIGN KEY (domain_id) REFERENCES mx.organization_domains (id) ON DELETE CASCADE,
        CONSTRAINT fk_dkim_keys_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE TABLE mx.outbound_messages (
        id uuid NOT NULL,
        source character varying(32) NOT NULL,
        envelope_from character varying(320) NOT NULL,
        subject character varying(998),
        internet_message_id character varying(998),
        size_bytes bigint NOT NULL,
        storage_path character varying(512) NOT NULL,
        dkim_signed boolean NOT NULL,
        created_by_user_id uuid,
        expires_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_outbound_messages PRIMARY KEY (id),
        CONSTRAINT ck_outbound_messages_size CHECK (size_bytes >= 0),
        CONSTRAINT fk_outbound_messages_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE TABLE mx.outbound_recipients (
        id uuid NOT NULL,
        outbound_message_id uuid NOT NULL,
        address character varying(320) NOT NULL,
        domain character varying(253) NOT NULL,
        status character varying(32) NOT NULL,
        attempts integer NOT NULL,
        next_attempt_at timestamp with time zone NOT NULL,
        locked_until timestamp with time zone,
        last_response character varying(1000),
        remote_host character varying(253),
        delivered_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_outbound_recipients PRIMARY KEY (id),
        CONSTRAINT ck_outbound_recipients_attempts CHECK (attempts >= 0),
        CONSTRAINT fk_outbound_recipients_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE,
        CONSTRAINT fk_outbound_recipients_outbound_messages_outbound_message_id FOREIGN KEY (outbound_message_id) REFERENCES mx.outbound_messages (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE UNIQUE INDEX ix_dkim_keys_domain_id_selector ON mx.dkim_keys (domain_id, selector);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE INDEX ix_dkim_keys_organization_id ON mx.dkim_keys (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE UNIQUE INDEX ux_dkim_keys_active ON mx.dkim_keys (domain_id) WHERE is_active;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE INDEX ix_outbound_messages_organization_id_created_at ON mx.outbound_messages (organization_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE UNIQUE INDEX ix_outbound_messages_storage_path ON mx.outbound_messages (storage_path);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE INDEX ix_outbound_recipients_due ON mx.outbound_recipients (next_attempt_at) WHERE status IN ('Queued', 'Deferred', 'Sending');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE INDEX ix_outbound_recipients_organization_id_status ON mx.outbound_recipients (organization_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE INDEX ix_outbound_recipients_outbound_message_id ON mx.outbound_recipients (outbound_message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    ALTER TABLE mx.outbound_messages ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    ALTER TABLE mx.outbound_messages FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE POLICY tenant_isolation ON mx.outbound_messages USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid) WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    DO $$
    BEGIN
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON mx.outbound_messages TO mx_app;
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_backup') THEN
        GRANT SELECT ON mx.outbound_messages TO mx_backup;
      END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    ALTER TABLE mx.outbound_recipients ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    ALTER TABLE mx.outbound_recipients FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE POLICY tenant_isolation ON mx.outbound_recipients USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid) WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    DO $$
    BEGIN
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON mx.outbound_recipients TO mx_app;
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_backup') THEN
        GRANT SELECT ON mx.outbound_recipients TO mx_backup;
      END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    ALTER TABLE mx.dkim_keys ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    ALTER TABLE mx.dkim_keys FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    CREATE POLICY tenant_isolation ON mx.dkim_keys USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid) WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    DO $$
    BEGIN
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON mx.dkim_keys TO mx_app;
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_backup') THEN
        GRANT SELECT ON mx.dkim_keys TO mx_backup;
      END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025082628_OutboundDelivery') THEN
    INSERT INTO mx.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261025082628_OutboundDelivery', '10.0.13');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025084153_MailboxPasswords') THEN
    ALTER TABLE mx.mailboxes ADD password_changed_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025084153_MailboxPasswords') THEN
    ALTER TABLE mx.mailboxes ADD password_hash character varying(256);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025084153_MailboxPasswords') THEN
    INSERT INTO mx.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261025084153_MailboxPasswords', '10.0.13');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    ALTER TABLE mx.mail_messages ADD is_answered boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    ALTER TABLE mx.mail_messages ADD is_deleted boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    ALTER TABLE mx.mail_messages ADD is_draft boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    ALTER TABLE mx.mail_messages ADD uid bigint NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    CREATE TABLE mx.mail_folder_states (
        id uuid NOT NULL,
        mailbox_id uuid NOT NULL,
        folder character varying(32) NOT NULL,
        uid_validity bigint NOT NULL,
        uid_next bigint NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        organization_id uuid NOT NULL,
        CONSTRAINT pk_mail_folder_states PRIMARY KEY (id),
        CONSTRAINT ck_mail_folder_states_uids CHECK (uid_validity > 0 AND uid_next > 0),
        CONSTRAINT fk_mail_folder_states_mailboxes_mailbox_id FOREIGN KEY (mailbox_id) REFERENCES mx.mailboxes (id) ON DELETE CASCADE,
        CONSTRAINT fk_mail_folder_states_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES mx.organizations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    SELECT set_config('mx.platform_scope', 'on', true);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    UPDATE mx.mail_messages m
       SET uid = n.rn
      FROM (SELECT id, row_number() OVER (PARTITION BY mailbox_id, folder ORDER BY received_at, id) AS rn
              FROM mx.mail_messages) n
     WHERE n.id = m.id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    INSERT INTO mx.mail_folder_states (id, organization_id, mailbox_id, folder, uid_validity, uid_next, created_at, updated_at)
    SELECT gen_random_uuid(), organization_id, mailbox_id, folder,
           extract(epoch FROM now())::bigint, max(uid) + 1, now(), now()
      FROM mx.mail_messages
     GROUP BY organization_id, mailbox_id, folder;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    CREATE UNIQUE INDEX ux_mail_messages_folder_uid ON mx.mail_messages (mailbox_id, folder, uid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    ALTER TABLE mx.mail_messages ADD CONSTRAINT ck_mail_messages_uid CHECK (uid > 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    CREATE UNIQUE INDEX ix_mail_folder_states_mailbox_id_folder ON mx.mail_folder_states (mailbox_id, folder);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    CREATE INDEX ix_mail_folder_states_organization_id ON mx.mail_folder_states (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    ALTER TABLE mx.mail_folder_states ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    ALTER TABLE mx.mail_folder_states FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    CREATE POLICY tenant_isolation ON mx.mail_folder_states USING (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid) WITH CHECK (current_setting('mx.platform_scope', true) = 'on' OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    DO $$
    BEGIN
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON mx.mail_folder_states TO mx_app;
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_backup') THEN
        GRANT SELECT ON mx.mail_folder_states TO mx_backup;
      END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025091025_ImapSupport') THEN
    INSERT INTO mx.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261025091025_ImapSupport', '10.0.13');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025094237_SpamScore') THEN
    ALTER TABLE mx.mail_messages ADD spam_score numeric(6,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mx.__ef_migrations_history WHERE "migration_id" = '20261025094237_SpamScore') THEN
    INSERT INTO mx.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261025094237_SpamScore', '10.0.13');
    END IF;
END $EF$;
COMMIT;

