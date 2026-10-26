# PostgreSQL deployment

Supported: PostgreSQL 15+ (developed and tested on 17.6) with Npgsql EF Core provider 10.x.

## 1. Create roles and schema

Run `database/init/01-roles.sh` as a superuser once per server (the Docker image runs it
automatically). It creates `mx_owner`, `mx_app` and `mx_backup`, the `mx` schema, and default
privileges so that tables from future migrations are granted to `mx_app` / `mx_backup`.

Set strong, distinct passwords via `MX_OWNER_PASSWORD`, `MX_APP_PASSWORD`, `MX_BACKUP_PASSWORD`.

## 2. Apply migrations

Production does **not** migrate on startup. Apply the idempotent script as `mx_owner`:

```bash
psql "host=db01 dbname=mx user=mx_owner" -v ON_ERROR_STOP=1 -f database/migrations/mx-schema.sql
```

Regenerate the script after adding a migration:

```bash
cd backend
dotnet ef migrations add <Name> -p MX.Persistence -s MX.Api -o Migrations
dotnet ef migrations script --idempotent -p MX.Persistence -s MX.Api -o ../database/migrations/mx-schema.sql
```

Any new tenant-owned table must be added to the RLS migration list (copy the pattern in
`20261025063557_RowLevelSecurity.cs` into a new migration).

## 3. Configure the application

Environment variables (double underscore = section separator):

```bash
ConnectionStrings__Mx="Host=db01;Database=mx;Username=mx_app;Password=...;SSL Mode=VerifyFull;Maximum Pool Size=100"
Jwt__SigningKey="<random 32+ byte secret>"
Cors__AllowedOrigins__0="https://mx-admin.example.com"
Database__MigrateOnStartup=false
```

- Use `SSL Mode=VerifyFull` across networks.
- Size `Maximum Pool Size` × instances below `max_connections` minus headroom, or front the
  server with PgBouncer in **session** pooling mode. (Transaction pooling is also safe for the
  tenant settings because they are rewritten on every open, but session mode is simpler to reason
  about.)
- The API retries transient failures (`EnableRetryOnFailure`). Code that opens explicit
  transactions must wrap them in `Database.CreateExecutionStrategy().ExecuteAsync(...)`.

## 4. Recommended server settings

```
password_encryption = scram-sha-256
ssl = on
log_min_duration_statement = 500ms
log_connections = on
log_disconnections = on
wal_level = replica
archive_mode = on            # see backup-recovery for archive_command
```

`pg_hba.conf`: allow `mx_app` only from API/worker hosts, `mx_owner` only from the deployment
host, `mx_backup` only from the backup host, all with `scram-sha-256` over `hostssl`.

## 5. Health

`GET /health` checks database connectivity (used by load balancers and orchestrators).
