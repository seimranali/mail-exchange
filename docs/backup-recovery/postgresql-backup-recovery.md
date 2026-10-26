# Backup and recovery

Scope: the PostgreSQL management database. Mail content is backed up with the mail server's own
tooling. Both must be restored to a consistent point to recover the full system.

## Strategy

| Layer | Tool | Frequency | Recovers from |
|---|---|---|---|
| Logical dump | `deployment/scripts/backup.sh` (`pg_dump -Fc`) | Nightly, keep 30 days | Accidental deletes, corruption, migrations gone wrong |
| Physical base backup + WAL archive | `pg_basebackup` + `archive_command` | Weekly base, continuous WAL | Point-in-time recovery (PITR) to any second |
| Off-site copy | Object storage with versioning / immutability | After each backup | Site loss, ransomware |

Targets (adjust to your SLA): RPO ≤ 5 minutes (WAL), RTO ≤ 1 hour.

## Logical backups

```bash
PGHOST=db01 PGUSER=mx_backup PGPASSWORD=... deployment/scripts/backup.sh /var/backups/mx 30
```

Must run as `mx_backup`: tables use `FORCE ROW LEVEL SECURITY`, so other roles get an
incomplete dump and `pg_dump` fails. The script writes a checksum, verifies the archive is
readable and prunes old files. Windows hosts: `deployment/scripts/backup.ps1`.

Schedule (Linux cron): `15 2 * * * /opt/mx/backup.sh /var/backups/mx 30 >> /var/log/mx-backup.log 2>&1`

## Restore a logical backup

1. Stop API and Workers (avoid writes during restore).
2. Create an empty database owned by `mx_owner` (run `01-roles.sh` first on a new server).
3. Restore as a superuser (break-glass; RLS prevents other roles loading data):
   ```bash
   PGHOST=db01 PGUSER=postgres deployment/scripts/restore.sh /var/backups/mx/mx-<stamp>.dump mx
   ```
   The script verifies the checksum, restores with `mx_owner` ownership and re-applies `mx_app`
   grants including the append-only audit restriction.
4. Verify: `SELECT count(*) FROM mx.mailboxes;` and `SELECT count(*) FROM pg_class WHERE relforcerowsecurity;` (expect 22).
5. Start API, check `GET /health`, sign in, spot-check a tenant.

This procedure was exercised during development: a dump of the dev database restored into a
fresh database with all rows, RLS flags and ownership intact.

## Point-in-time recovery

Enable WAL archiving:

```
wal_level = replica
archive_mode = on
archive_command = 'test ! -f /mnt/wal/%f && cp %p /mnt/wal/%f'   # or pgBackRest / WAL-G
```

Weekly base backup: `pg_basebackup -D /mnt/base/$(date +%F) -Ft -z -X stream -U replicator`

To recover to a timestamp: stop PostgreSQL, restore the latest base backup before the target
into the data directory, then set

```
restore_command = 'cp /mnt/wal/%f %p'
recovery_target_time = '2026-10-25 09:41:00+00'
recovery_target_action = 'promote'
```

create `recovery.signal`, and start the server. For production we recommend pgBackRest or WAL-G,
which manage retention, encryption and verification.

## Testing

Restore the latest backup into a scratch database at least monthly and run the verification
queries above. An untested backup should be treated as no backup.

## Coordinating with mail storage

After restoring the management database to time T, mailboxes created after T exist on the mail
server but not in the database (and vice versa for deletions). Run the reconciliation worker and
review its warnings, or restore the mail store to the same T.
