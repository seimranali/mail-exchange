#!/bin/bash
# Logical backup of the MX management database.
# Usage: PGHOST=... PGUSER=mx_backup PGPASSWORD=... ./backup.sh /var/backups/mx [retention_days]
# Produces a compressed custom-format dump plus a SHA-256 checksum. Message content is NOT
# included: back up the mail store with the mail server's own tooling.
set -euo pipefail

dest="${1:?destination directory required}"
keep_days="${2:-30}"
db="${PGDATABASE:-mx}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
file="${dest}/mx-${stamp}.dump"

mkdir -p "$dest"
pg_dump --format=custom --compress=9 --no-owner --dbname="$db" --file="$file"
sha256sum "$file" > "${file}.sha256"

# Prove the archive is readable before trusting it.
pg_restore --list "$file" > /dev/null

find "$dest" -name 'mx-*.dump*' -type f -mtime "+${keep_days}" -delete
echo "Backup written: $file"
