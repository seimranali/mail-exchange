#!/bin/bash
# Creates the MX database roles on first container start.
#   mx_owner - owns the schema; used only for migrations.
#   mx_app    - restricted runtime role used by MX.Api and MX.Workers. No DDL, no BYPASSRLS.
#   mx_backup - read-only role for pg_dump.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v owner_pw="$MX_OWNER_PASSWORD" -v app_pw="$MX_APP_PASSWORD" -v backup_pw="$MX_BACKUP_PASSWORD" <<'SQL'
CREATE ROLE mx_owner LOGIN PASSWORD :'owner_pw' NOSUPERUSER NOCREATEROLE NOCREATEDB NOBYPASSRLS;
CREATE ROLE mx_app   LOGIN PASSWORD :'app_pw'   NOSUPERUSER NOCREATEROLE NOCREATEDB NOBYPASSRLS;

GRANT CONNECT ON DATABASE mx TO mx_owner, mx_app;
CREATE SCHEMA IF NOT EXISTS mx AUTHORIZATION mx_owner;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA mx TO mx_app;
-- Tables created by future migrations are usable by the runtime role automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE mx_owner IN SCHEMA mx GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mx_app;

-- Read-only backup role. BYPASSRLS is required because tables FORCE row-level security
-- and pg_dump refuses to produce a partial dump.
CREATE ROLE mx_backup LOGIN PASSWORD :'backup_pw' NOSUPERUSER NOCREATEROLE NOCREATEDB BYPASSRLS;
GRANT CONNECT ON DATABASE mx TO mx_backup;
GRANT USAGE ON SCHEMA mx TO mx_backup;
ALTER DEFAULT PRIVILEGES FOR ROLE mx_owner IN SCHEMA mx GRANT SELECT ON TABLES TO mx_backup;
ALTER DEFAULT PRIVILEGES FOR ROLE mx_owner IN SCHEMA mx GRANT SELECT ON SEQUENCES TO mx_backup;
SQL
