#!/bin/bash
# Restores an MX dump into an EMPTY database created beforehand with database/init roles.
# Runs as a superuser (break-glass): tables FORCE row-level security, so only a role that
# bypasses RLS can load data. Objects are re-owned by mx_owner.
# Usage: PGHOST=... PGUSER=postgres PGPASSWORD=... ./restore.sh /var/backups/mx/mx-<stamp>.dump [target_db]
set -euo pipefail

file="${1:?dump file required}"
db="${2:-${PGDATABASE:-mx}}"

sha256sum --check "${file}.sha256"
pg_restore --exit-on-error --no-owner --role=mx_owner --dbname="$db" "$file"

# Re-apply grants for the runtime role (not carried by --no-owner restores).
psql --dbname="$db" -v ON_ERROR_STOP=1 <<'SQL'
GRANT USAGE ON SCHEMA mx TO mx_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA mx TO mx_app;
REVOKE UPDATE, DELETE ON mx.audit_events, mx.authentication_events FROM mx_app;
SQL
echo "Restore complete into database '$db'"
