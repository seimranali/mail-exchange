# Database schema

All tables live in schema `mx`, use snake_case, `uuid` primary keys (UUID v7, time-ordered),
`timestamptz` for every point in time (stored UTC), and enums stored as constrained text.
Source of truth: `backend/MX.Persistence/Configurations/EntityConfigurations.cs` and migrations.

| Area | Tables | Tenant-scoped (RLS) |
|---|---|---|
| Tenancy | `organizations`, `organization_domains`, `organization_subscriptions` | yes (`organizations` by its own `id`) |
| Mail | `mail_servers`, `mailboxes`, `mail_aliases`, `distribution_lists`, `distribution_list_members`, `mail_routing_rules` | all except `mail_servers` (shared infrastructure) |
| Identity | `application_users`, `application_roles`, `user_roles`, `api_credentials`, `security_policies` | all except `application_roles` |
| Security | `authentication_events`, `audit_events`, `security_alerts`, `quarantine_records` | yes (events allow NULL org for platform rows) |
| Compliance | `retention_policies`, `legal_holds`, `discovery_requests` | yes |
| Operations | `backup_jobs`, `restore_jobs`, `server_health_snapshots`, `notifications`, `system_configurations` | all except `server_health_snapshots`, `system_configurations` |

## Notable constraints

- Globally unique: `organizations.slug`, `organization_domains.domain_name`, `mail_aliases.alias_address`,
  `distribution_lists.email_address`, `application_users.normalized_email`.
- `mailboxes.email_address` unique **among non-deleted** mailboxes (partial index), so an address
  can be reused after deletion.
- At most one default domain per organization (partial unique index).
- `distribution_list_members`: exactly one of `mailbox_id` / `external_address`.
- `legal_holds`: `released_at` set if and only if status is `Released`; FK to organization is
  `RESTRICT` so holds cannot vanish with a tenant delete.
- Checks on quotas, percentages, retention days (1–36500), slug and lowercase addresses.
- `audit_events` has no organization FK: audit rows outlive the tenant.

## jsonb usage

Limited to genuinely open-ended data: `audit_events.details` and `server_health_snapshots.metrics`.
Everything queried or joined is relational.

## Indexes

Composite `(organization_id, …)` indexes back the common tenant-scoped filters and sort orders
(e.g. `mailboxes (organization_id, status)`, `audit_events (organization_id, occurred_at)`,
`quarantine_records (organization_id, status, received_at)`), plus a GIN index on
`legal_holds.mailbox_ids` for hold checks.
