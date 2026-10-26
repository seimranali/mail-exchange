# Tenant isolation

Tenant isolation is enforced in four independent layers. Any one of them failing does not expose
another tenant's data.

| Layer | Mechanism | Where |
|---|---|---|
| 1. Identity | Tenant comes only from the signed JWT `org` claim | `TenantResolutionMiddleware` |
| 2. Query filters | Every `ITenantOwned` entity filtered by `OrganizationId` | `MxDbContext.OnModelCreating` |
| 3. Write guard | `SaveChanges` throws on any add/modify/delete outside the current tenant | `MxDbContext.StampAndGuard` |
| 4. Row-level security | `FORCE ROW LEVEL SECURITY` + `tenant_isolation` policy on 22 tables | migration `RowLevelSecurity` |

## Database roles

| Role | Used by | Rights |
|---|---|---|
| `mx_owner` | Migrations only | Owns schema. Subject to RLS (FORCE). |
| `mx_app` | API, Workers | DML only. No DDL. **No UPDATE/DELETE on `audit_events` and `authentication_events`** (append-only at the DB level). No BYPASSRLS. |
| `mx_backup` | `pg_dump` | SELECT only, BYPASSRLS (required to produce a complete dump). Protect its credentials like the data itself. |

## How RLS gets the tenant

RLS policies read two session settings:

```sql
USING (current_setting('mx.platform_scope', true) = 'on'
       OR organization_id = NULLIF(current_setting('mx.tenant_id', true), '')::uuid)
```

`TenantSessionInterceptor` runs `set_config(...)` for **both** settings on every connection
open, always overwriting them (empty tenant / `off` when none). This prevents leakage through the
connection pool, because:

- The settings are rewritten every time EF opens a connection, before any query runs.
- Npgsql resets session state (`DISCARD ALL`) when a connection returns to the pool.
- DbContext pooling is deliberately **not** used (the context holds request-scoped tenant state).
- The test `Pooled_connections_do_not_leak_tenant_scope_between_contexts` alternates tenants on
  the same pool and asserts nothing crosses.

An unscoped connection (no tenant, platform scope off) sees **zero** rows.

### Platform scope

Platform scope is set only for: platform administrators without `X-MX-Organization`, the login
endpoint (before any tenant is known; reads just the matching user), and workers enumerating
organizations. Workers then process each organization in its own DI scope with that tenant set.

### Limits

RLS here is defence in depth against application bugs (a forgotten filter, a raw SQL query). The
`mx_app` role can itself call `set_config`, so RLS does not defend against an attacker executing
arbitrary SQL as `mx_app`. Treat `mx_app` credentials accordingly.

Global uniqueness (domain names, email addresses) is enforced by unique indexes, which apply
regardless of RLS. The application's pre-checks may not see other tenants' rows; a violation
then surfaces as HTTP 409 from the unique index.

## Tested attacks

`MX.Tests/TenantIsolationTests.cs` and `ApiTests.cs` cover: reading another tenant's mailbox by
id (404), deleting it (404), listing (empty), bypassing EF filters (RLS returns 0 rows), writing a
row into another tenant (blocked by context and by RLS), tampering with audit rows (permission
denied), and pooled connection leakage.

## Other controls

- Passwords: Argon2id (m=64MiB, t=3, p=2), PHC format. Constant-time compare; unknown users take
  the same time path.
- Account lockout per organization security policy; every attempt recorded in
  `authentication_events`.
- Login rate limit: 10 requests/minute per IP.
- API keys: only SHA-256 hashes stored; shown once at creation.
- Audit: privileged and tenant-sensitive operations write `audit_events` in the same transaction.
- Errors: ProblemDetails with trace id; no stack traces or SQL reach clients.
- Secrets: `Jwt:SigningKey` and connection strings come from environment / secret store. Values in
  `appsettings.Development.json` are development-only.
