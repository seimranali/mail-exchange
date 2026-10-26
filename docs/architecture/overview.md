# Architecture overview

## Backend projects

Dependencies point inward. `MX.Domain` has no dependencies; `MX.Application` depends only on
Domain plus EF Core abstractions (no Npgsql).

| Project | Responsibility |
|---|---|
| `MX.Domain` | Entities (26 tables), enums, `ITenantOwned` marker. UUID v7 keys, `DateTimeOffset` timestamps. |
| `MX.Application` | Use-case services (`MailboxService`, `DomainService`, `SecurityService`, ...), DTOs, interfaces (`IApplicationDbContext`, `IMailServerProvider`, `ITenantContext`, `IAuditLogger`). Provider-neutral LINQ only. |
| `MX.Persistence` | `MxDbContext`, entity configurations (constraints, indexes, FKs), migrations, RLS session interceptor, audit logger, seed. All PostgreSQL specifics live here. |
| `MX.Security` | Argon2id password hashing, JWT issuing, API key hashing. |
| `MX.Infrastructure` | Real DNS verification (TXT/MX/SPF/DKIM/DMARC). |
| `MX.MailIntegration` | `IMailServerProvider` implementations. Ships a stub provider; add adapters per MTA. |
| `MX.Compliance` | Retention enforcement (respects legal holds). |
| `MX.Api` | Controllers, tenant-resolution middleware, ProblemDetails exception mapping, SignalR hub, auth, rate limiting. |
| `MX.Workers` | Periodic jobs: server health sampling, mailbox provisioning reconciliation, retention, backup job processing. |
| `MX.Tests` | xUnit + Testcontainers (real PostgreSQL) + WebApplicationFactory. |

## Request flow

1. JWT validated → `TenantResolutionMiddleware` sets the scoped `TenantContext` from the token's
   `org` claim. The client never chooses its tenant; platform admins may target one via
   `X-MX-Organization`.
2. Controller `[Authorize(Roles=...)]` enforces role access.
3. Service queries `IApplicationDbContext`; EF global query filters scope every tenant-owned set.
4. When EF opens a connection, `TenantSessionInterceptor` writes the same scope into the PostgreSQL
   session, and RLS policies enforce it again at the database.
5. Changes and their audit events commit in one `SaveChanges`.

## Mail integration boundary

The management database and the mail servers are independent systems. Services write the
management record first, then call the provider; if the mail server is unavailable the mailbox
stays `Provisioning` and `MailboxReconciliationWorker` retries. The mail queue is read live from
the providers and never stored. Message bodies and attachments are only referenced (`StorageRef`).

## Frontend

- `styles/tokens.css` (primitives), `themes.css` (semantic tokens for light/dark), `global.css`.
  Components reference semantic tokens only.
- `components/` is the MX component library; pages compose it and never style raw elements.
- `services/` holds the API client (RFC 9457 errors → `ApiError`) and typed endpoints; `modules/`
  holds query/mutation hooks; `pages/` are presentation.
- Role-based navigation hiding is a UX convenience; the API enforces every permission.

## Adding a mail server adapter

Implement `IMailServerProvider` in `MX.MailIntegration`, set a new `MailServerType`, register it in
`AddMailIntegration()`. Nothing else changes: the factory resolves providers by type.
