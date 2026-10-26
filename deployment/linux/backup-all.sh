#!/bin/bash
# Nightly MX backup: PostgreSQL dump (via backup.sh as mx_backup) + compressed message store.
# Environment comes from /etc/mx/backup.env (see mx-backup.service).
set -euo pipefail

: "${BACKUP_DIR:?}" "${MAILSTORE:?}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

"$HERE/backup.sh" "$BACKUP_DIR/db" "$RETENTION_DAYS"

mkdir -p "$BACKUP_DIR/mailstore"
ARCHIVE="$BACKUP_DIR/mailstore/mailstore-$STAMP.tar.zst"
# Message files are written atomically (temp + rename), so a live copy is consistent per message.
tar --exclude='*.tmp' -C "$(dirname "$MAILSTORE")" -cf - "$(basename "$MAILSTORE")" | zstd -q -T0 -19 -o "$ARCHIVE"
sha256sum "$ARCHIVE" > "$ARCHIVE.sha256"
find "$BACKUP_DIR/mailstore" -name 'mailstore-*' -type f -mtime "+$RETENTION_DAYS" -delete

echo "Backup complete: $BACKUP_DIR (db + $ARCHIVE)"
echo "Copy $BACKUP_DIR off this server (object storage / another host) - a backup on the same disk is not a backup."
