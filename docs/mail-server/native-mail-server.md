# Native mail server

MX includes its own mail server (`MX.MailServer`, run by the `MX.MailHost` process). No external
MTA software is used. Libraries: MimeKit (message parsing, DKIM verification) and DnsClient.

## Status

| Step | Scope | State |
|---|---|---|
| 1 | Inbound SMTP, SPF/DKIM/DMARC checks, message store, admin visibility | **Done** |
| 2 | Outbound delivery, DKIM signing, retry queue, bounces | **Done** |
| 3a | Submission (587 STARTTLS / 465 implicit TLS) with AUTH, mailbox passwords, Sent folder | **Done** |
| 3b | IMAP (reading mail from clients) | **Done** |
| 4 | Spam/phishing/malware filtering → quarantine, Let's Encrypt (ACME) with auto-renewal | **Done** |
| 5 | Deployment and DNS cutover | Planned |

## How inbound mail flows

```
remote MTA ──TCP 25──> SmtpListener ─> SmtpSession (RFC 5321)
                                         │ RCPT: RecipientDirectory (domain verified? mailbox/alias active? quota?)
                                         │ DATA: size limit, dot-unstuffing, bare-LF rejection
                                         │ SPF (RFC 7208) · DKIM (RFC 6376) · DMARC (RFC 7489)
                                         ▼
                                  LocalDelivery ─> FileMessageStore  (.eml on disk)
                                                └> mail_messages row  (tenant-scoped, RLS)
```

- Only addresses on domains registered in MX are accepted: **never an open relay**.
- Domains must be **Verified** (DNS TXT ownership) unless `AcceptUnverifiedDomains` is on (dev only).
- Folder choice: DMARC fail with `p=quarantine`, or SPF fail with no DMARC → **Junk**;
  DMARC fail with `p=reject` → rejected with `550 5.7.1` (configurable).
- Each stored message gets `Return-Path`, `Authentication-Results` and `Received` headers.
  Incoming `Authentication-Results` claiming our hostname are stripped (anti-forgery).
- Mailbox usage is incremented atomically per delivery.

## How outbound mail flows

```
IOutboundMailer (API test message; submission in step 3; bounces)
   │  sender domain must belong to the tenant · DKIM-sign (rsa-sha256, relaxed/relaxed)
   ▼
outbound_messages + outbound_recipients (tenant-scoped, RLS) · .eml in {store}/{org}/outbound/
   ▼
OutboundDeliveryWorker (MX.MailHost)
   │  claim due recipients: UPDATE … FOR UPDATE SKIP LOCKED + 15-min lease (multi-host safe, crash-safe)
   │  group by (message, domain) → one SMTP transaction per domain
   ├─ hosted domain? → LocalDelivery (never leaves the server)
   └─ SmtpDeliveryClient: MX lookup (A fallback, RFC 7505 null MX) → opportunistic STARTTLS → RCPT per recipient → DATA
   ▼
2xx Delivered · 4xx/connection error Deferred (retry 5m,10m,20m,40m,1h,2h,4h… up to 5 days) · 5xx Failed → RFC 3464 bounce
```

- Bounces use the null reverse-path (`MAIL FROM:<>`), so a bounce can never trigger another bounce.
- The queued file is deleted once every recipient reaches a final state.
- Admins can **Retry now** or **Cancel** from the Mail queue screen (audited).
- **DKIM keys**: generated per domain in the Domains screen (2048-bit RSA, date-based selector such as
  `mx202610`). The private key is encrypted with AES-256-GCM under `Secrets:KeyEncryptionKey`
  and bound to its domain; the plaintext key is never stored. Rotating keeps old selectors valid
  in DNS until you remove them.

### Key-encryption key

Generate once per installation and keep it in your secret store (losing it means regenerating
all DKIM keys and updating DNS):

```bash
openssl rand -base64 32          # → Secrets__KeyEncryptionKey for MX.Api and MX.MailHost
```

## Submission from mail apps (ports 587 / 465)

Users' mail apps send through MX after logging in with their **email address and mail app
password** (set by an administrator in Mailboxes → mailbox → *Mail app access*; Argon2id-hashed,
minimum length from the organization's security policy).

| Rule | Behaviour |
|---|---|
| Encryption first | AUTH is advertised and accepted only after STARTTLS (587) or on implicit TLS (465); otherwise `538 5.7.11` |
| Login required | `MAIL FROM` before AUTH → `530 5.7.0` |
| Own identity only | Envelope sender **and** visible `From:` must be the user's address or one of its aliases → otherwise `553`/`550 5.7.1` |
| Brute force | 5 failures per IP or 10 per account within 15 minutes → `454 4.7.0` and disconnect (even with the right password) |
| Stolen-password damage | At most 200 messages per mailbox per hour → `451 4.7.1` |
| Privacy | `Bcc:` stripped; the client's IP address is not written into `Received:` |
| Audit | Every login attempt appears under Security → sign-ins (`mail client (SMTP submission)`) |

Accepted messages are DKIM-signed, queued for delivery like any other outbound mail, and a copy
is filed in the sender's **Sent** folder.

Client settings to give users:

| Setting | Value |
|---|---|
| Username | full email address |
| Outgoing server | `mail.example.com`, port **465 (SSL/TLS)** or **587 (STARTTLS)**, password authentication |
| Incoming server | `mail.example.com`, port **993 (SSL/TLS)** or **143 (STARTTLS)**, password authentication |

## IMAP (ports 143 / 993)

IMAP4rev1 (RFC 3501) with **LITERAL+, SASL-IR, IDLE, UIDPLUS, MOVE, SPECIAL-USE, UNSELECT,
NAMESPACE, ID, ENABLE**. Verified in tests with MailKit, a widely used client library
(folders, envelopes, body structure, partial fetch, flags, search, copy/move, expunge,
append with APPENDUID, IDLE push).

| Topic | Behaviour |
|---|---|
| Folders | Fixed set: INBOX, Sent, Drafts, Junk, Trash, Archive, advertised with special-use flags so clients map them automatically. Custom folders are refused (`NO [CANNOT]`). |
| Login | `LOGINDISABLED` until TLS; same credentials, throttling and sign-in log as submission (`mail client (IMAP)`) |
| Isolation | Every query is scoped to the **logged-in mailbox**, not just the organization (tested: a colleague cannot see another mailbox) |
| UIDs | Per-folder, strictly increasing, allocated atomically in PostgreSQL (`mail_folder_states`); UIDVALIDITY fixed per folder |
| Flags | `\Seen \Answered \Flagged \Deleted \Draft` persisted; custom keywords accepted but not stored |
| Reading | `BODY[]`, `HEADER`, `TEXT` come straight from the stored file (sizes match `RFC822.SIZE` exactly); `BODY[...]` without `.PEEK` sets `\Seen` |
| Expunge | Deletes rows and files and frees quota; refused while the mailbox is under legal hold |
| Live updates | IDLE checks for changes every 2 seconds and pushes EXISTS / EXPUNGE / flag updates; also on NOOP |
| Limits | Literals up to the max message size; auto-logout after 30 idle minutes; quota enforced on APPEND/COPY (`[OVERQUOTA]`) |

## Content filtering (inbound)

Every inbound message is scored by named rules; the score and rules are written into the message
as `X-MX-Spam-Score` / `X-MX-Spam-Rules` (forged copies from senders are stripped).

| Score | Action |
|---|---|
| < 5 | Inbox |
| 5 – 9.9 | Junk folder |
| 10 – 19.9 | Quarantine (sender still gets `250`; an administrator decides) |
| ≥ 20 | Rejected during SMTP (`550 5.7.1`) |

| Rule family | Examples |
|---|---|
| Authentication | `SPF_FAIL` 3, `SPF_SOFTFAIL` 1, `DKIM_INVALID` 1.5, `DMARC_FAIL` 2 (and DMARC `p=reject` refuses before scoring) |
| Reputation | `DNSBL` 6 — connecting IP on zen.spamhaus.org / bl.spamcop.net (private and loopback IPs never queried; "query refused" codes ignored) |
| Header sanity | `HELO_NOT_FQDN`, `MISSING_MESSAGE_ID`, `MISSING_DATE`, `DATE_IN_FUTURE` |
| Phishing | `SPOOFED_LOCAL_DOMAIN` 8 (claims to be from one of *our* domains without authenticating), `LOOKALIKE_DOMAIN` 6 (examp1e.com, exarnple.com…), `DISPLAY_NAME_SPOOF`, `LINK_TEXT_MISMATCH` 3, `URL_IP_ADDRESS` 2, `PHISHING_PHRASES` |
| Content | `SPAM_PHRASES`, `SUBJECT_ALL_CAPS`, `SUBJECT_EXCLAMATIONS`, `HTML_ONLY`, `URL_SHORTENER` |

**Attachments (independent of the score):**

| Finding | Result |
|---|---|
| Blocked type (`.exe .scr .js .vbs .ps1 .hta .lnk .iso` …), also inside ZIPs (2 levels) | Quarantine as **Malware**, High alert, cannot be released |
| Executable content renamed to look harmless (MZ/ELF header) | Malware |
| EICAR test file | Malware |
| Macro document (`.docm .xlsm .pptm` …) | Quarantine as **Policy** (releasable) |
| Password-protected or unreadable archive | +5 / +2 score |

Quarantined messages are stored under `{store}/{org}/quarantine/`, listed in Security → Quarantine
with reason and score, and expire after 30 days (files deleted). **Release** delivers the stored
message to the recipient's Inbox and removes the quarantine copy; **Delete** removes it.

> **Honest scope:** malware detection is heuristic (file types, disguised executables, archives,
> macros). It does not recognise specific virus families by signature the way an antivirus engine
> does. `IMalwareScanner` is the plug-in point if a signature engine is added later.

> **DNSBL note:** Spamhaus blocks queries that arrive via large public resolvers (8.8.8.8, 1.1.1.1).
> Run a local caching resolver (e.g. `unbound`) on the mail host and point the system at it.

## TLS certificates (ACME / Let's Encrypt)

The mail host obtains and renews its own certificate with a built-in ACME client (RFC 8555,
HTTP-01 challenge; no certbot). Verified in tests against Pebble, Let's Encrypt's test CA.

- Renewal starts when **one third of the lifetime remains** (capped at 30 days before expiry), so
  both 90-day and Let's Encrypt's short-lived 6-day certificates renew at the right time.
- The certificate (with its intermediate chain) is written to `MailServer:CertificatePath` and
  **hot-reloaded**: new SMTP/IMAP connections use it immediately, no restart.
- The ACME account key is stored encrypted with the installation's key-encryption key.
- Ports 465/993 are bound at startup when ACME is enabled, and start accepting TLS as soon as the
  first certificate is issued.

Configuration (`MX.MailHost/appsettings.json` → `Acme`):

| Key | Example value |
|---|---|
| `Enabled` | `true` |
| `DirectoryUrl` | staging first (`https://acme-staging-v02.api.letsencrypt.org/directory`), then `https://acme-v02.api.letsencrypt.org/directory` |
| `Email` | your admin address (expiry notices) |
| `AcceptTermsOfService` | `true` (you accept Let's Encrypt's subscriber agreement) |
| `Domains` | `["mail.example.com"]` |
| `HttpPort` | `80` — must be reachable from the internet and **not** behind Cloudflare's proxy |

## Security measures

| Threat | Defence |
|---|---|
| Open relay | Recipient must be local; `550 5.7.1 Relaying denied` otherwise |
| SMTP smuggling | Data ends only at strict `CRLF.CRLF`; any bare LF → `550 5.5.2` |
| STARTTLS command injection | Buffer discarded on upgrade; pipelined data after STARTTLS → `554` and disconnect |
| Resource exhaustion | Max connections (total and per IP), message size, recipients, line length, idle timeout, error limit |
| Mail loops | More than 50 `Received` headers → `554 5.4.6` |
| Path traversal in store | Paths built from GUIDs only and checked against the store root |
| Admin snooping | API exposes metadata only (no bodies); every listing is audited |
| Weak TLS | TLS 1.2/1.3 only |
| Tenant spoofing outbound | A tenant can only send as (and sign for) its own domains |
| DKIM key theft from DB/backups | Private keys encrypted (AES-256-GCM); KEK held outside the database |
| Bounce loops | Null reverse-path on bounces; bounces never bounced |
| Duplicate sends with several hosts | Row claims with `SKIP LOCKED` and leases |

## Configuration (`MX.MailHost/appsettings.json`)

| Key | Production value | Notes |
|---|---|---|
| `MailServer:Hostname` | `mail.example.com` | Must match the PTR record and TLS certificate |
| `MailServer:SmtpPort` | `25` | Dev uses `2525` |
| `MailServer:CertificatePath` | `/etc/mx/tls/mail.pfx` | STARTTLS disabled if unset (step 4 automates this) |
| `MailServer:AcceptUnverifiedDomains` | `false` | |
| `MailServer:EnforceDmarcReject` | `true` | |
| `MailStore:Root` | `/var/lib/mx/mailstore` | Durable disk, include in backups |
| `ConnectionStrings:Mx` | `mx_app` connection | Same restricted role as the API |

## Run locally

```bash
docker compose -f deployment/docker/docker-compose.yml up -d --wait
cd backend
dotnet run --project MX.Api --launch-profile http   # applies migrations
DOTNET_ENVIRONMENT=Development dotnet run --project MX.MailHost   # SMTP on 127.0.0.1:2525
```

Send a test message with any SMTP tool (e.g. `swaks --server 127.0.0.1:2525 --to alice@example.com`),
then open the mailbox in the admin UI → **Recent messages**.

## DNS for your domain

Keep your current MX, SPF and DMARC records until the new server is tested. At cutover, at your
DNS provider (example: Cloudflare):

| Type | Name | Value | Proxy |
|---|---|---|---|
| A | `mail` | VPS IPv4 | **DNS only (grey cloud)** — mail cannot pass Cloudflare's proxy |
| MX | `@` | `mail.example.com` (priority 10) | — |
| TXT | `@` | `v=spf1 mx -all` | — |
| TXT | `mx._domainkey` | DKIM public key (generated in step 2) | — |
| TXT | `_dmarc` | `v=DMARC1; p=quarantine; rua=mailto:dmarc@example.com` | — |
| TXT | `@` | `mx-verification=<token shown in MX Domains page>` | — |

At the VPS provider: set reverse DNS (PTR) of the IP to `mail.example.com`.

## Known limitations (step 4)

- Malware detection is heuristic, not signature-based (see above).
- Spam rules are static; there is no learning from user "mark as spam" actions (Bayesian) yet, and
  scores cannot be tuned per organization (installation-wide thresholds only).
- Only the HTTP-01 challenge is implemented (port 80 required); no DNS-01 / wildcard certificates.
- No per-sender allow/block lists yet.

## Known limitations (step 3b)

- SEARCH on body/header text loads messages from disk and scans them; fine for typical mailboxes,
  slow for very large ones (a full-text index would come later).
- IDLE polls the database every 2 seconds rather than receiving push notifications
  (PostgreSQL LISTEN/NOTIFY would remove the polling).
- No CONDSTORE/QRESYNC (faster resync for mobile clients), no custom folders, no shared mailboxes.
- No `\Recent` flag (always 0), which RFC 9051 (IMAP4rev2) drops anyway.

## Known limitations (step 3a)

- Login throttling counters are kept in memory per mail-host process; with several hosts each
  enforces its own limits.
- Users cannot change their own password yet (administrators set it); a self-service page and
  app-specific passwords would follow with a user portal.
- No OAuth2 / XOAUTH2 login; password authentication only.

## Known limitations (step 2)

- Outbound TLS is opportunistic (certificates not validated, as is normal between MTAs). MTA-STS
  and DANE validation are not implemented yet; `RequireTls` forces validated TLS for all deliveries.
- Bounces from `mailer-daemon@<hostname>` are not DKIM-signed.
- No per-domain connection rate limiting towards large providers yet; matters only at volume.
- No delayed-delivery warning notices (only the final bounce after 5 days).

## Known limitations (step 1)

- Organizational-domain detection uses a short list of two-level suffixes instead of the full
  Public Suffix List (affects DMARC alignment for unusual TLDs).
- SPF `ptr` mechanism is treated as non-matching (discouraged by RFC 7208).
- `<postmaster>` without a domain is not specially handled.
- No greylisting, DNSBL or content filtering yet (step 4).
