# MX: Mail Exchange

A self-hosted, multi-tenant mail server with its own SMTP, IMAP, spam filtering, Let's Encrypt
client, webmail and admin UI. No Postfix, Dovecot or other third-party MTA: everything is .NET.

- **Mail server**: inbound SMTP with SPF/DKIM/DMARC, outbound delivery with DKIM signing, retries
  and bounces, submission on 587/465, IMAP on 143/993, spam/phishing/malware filtering with
  quarantine, automatic TLS certificates (ACME).
- **Webmail**: read, search, organize and send mail with attachments at `https://<host>/mail`.
- **Admin UI**: organizations, domains (DNS checks, DKIM), mailboxes, mail queue, security,
  quarantine, retention, legal hold and audit.
- **Multi-tenant**: PostgreSQL row-level security isolates organizations.

| Layer | Stack |
|---|---|
| Frontend | React 19, TypeScript (strict), Vite, CSS Modules + design tokens, TanStack Query, React Hook Form + Zod |
| Backend | ASP.NET Core (.NET 10), EF Core 10, Npgsql, MimeKit, SignalR, background workers |
| Database | PostgreSQL 17 with row-level security |

## Install

One PowerShell command on **Linux, Windows or macOS**. It downloads the latest
[release](https://github.com/seimranali/mail-exchange/releases), verifies its checksum, installs
PostgreSQL if needed and sets up the services.

**Windows** (PowerShell as Administrator):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/seimranali/mail-exchange/main/install.ps1))) -Domain mail.example.com -Email admin@example.com
```

**Linux / macOS** ([PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell)):

```bash
curl -fsSLO https://raw.githubusercontent.com/seimranali/mail-exchange/main/install.ps1
sudo pwsh ./install.ps1 -Domain mail.example.com -Email admin@example.com
```

Re-run the same command to upgrade. Before installing, point an A record for your mail host to
the server (no proxy/CDN) and set its reverse DNS. The full guide covers requirements, DNS
records, options and operations: [docs/deployment/install.md](docs/deployment/install.md).

## Development

Prerequisites: .NET SDK 10, Node 24+, Docker.

```bash
# 1. Database (port 5433 so it does not clash with a local PostgreSQL)
docker compose -f deployment/docker/docker-compose.yml up -d --wait

# 2. API: applies migrations and seeds demo data on first start
cd backend && dotnet run --project MX.Api --launch-profile http     # http://localhost:5134

# 3. Mail host (SMTP/IMAP) and background workers, optional
cd backend && dotnet run --project MX.MailHost
cd backend && dotnet run --project MX.Workers

# 4. Frontend
cd frontend && npm install && npm run dev                          # http://localhost:5173
```

Development sign-ins (password `ChangeMe!Dev2026`, development only):

| Account | Roles | Organization |
|---|---|---|
| `admin@mx.local` | PlatformAdmin, OrgAdmin | MX Platform |
| `admin@example.com` | OrgAdmin | Example Corporation (demo data) |
| `security@example.com` | SecurityAdmin, Auditor | Example Corporation |

API reference (development): http://localhost:5134/scalar

## Tests

```bash
cd backend && dotnet test          # uses Testcontainers (Docker required)
cd frontend && npm test            # component tests incl. axe accessibility checks
cd frontend && npm run build       # type-check + production build
```

## Releases

Pushing a tag `v<version>` builds self-contained packages for linux-x64, linux-arm64, win-x64,
osx-x64 and osx-arm64 with GitHub Actions and publishes them, with `SHA256SUMS.txt`, as a GitHub
release. To build locally: `pwsh deployment/build-release.ps1 -Version 1.2.0 [-Runtime linux-x64]`.

## Repository layout

```
backend/        MX.Api, MX.MailHost (SMTP/IMAP), MX.MailServer, MX.Application, MX.Domain,
                MX.Persistence, MX.Infrastructure, MX.MailIntegration, MX.Security,
                MX.Compliance, MX.Workers, MX.Tests
frontend/src/   app, components, layouts, pages (incl. webmail), modules, services, hooks, styles, routes
database/       init (roles), migrations, documentation
deployment/     build-release.ps1, linux (install.sh, systemd), docker, scripts (backup/restore)
install.ps1     cross-platform installer
docs/           architecture, mail server, security, deployment, backup-recovery
```

See [docs/architecture/overview.md](docs/architecture/overview.md) to get oriented.

## License

[MIT](LICENSE)
