# Installing MX

One machine runs everything: PostgreSQL 17, the mail host (SMTP, IMAP, ACME), the admin API with
the web UI and webmail (HTTPS 443), and background workers. Examples use `mail.example.com`;
replace it with your own mail host name.

| Platform | Services | PostgreSQL | Recommended for |
|---|---|---|---|
| **Linux** (Ubuntu 24.04, x64/arm64) | hardened systemd units, unprivileged `mx` user | PGDG apt packages, or an existing cluster | **production** |
| **Windows** (10/11, Server 2019+, x64) | Windows services under virtual accounts (`NT SERVICE\mx-*`) | winget, or an existing install | small offices, labs |
| **macOS** (12+, Apple silicon/Intel) | launchd daemons under a hidden `_mx` user | Homebrew, or an existing server | development, labs |

## 0. Requirements

| Requirement | Why |
|---|---|
| 2 vCPU, 4 GB RAM, 40 GB+ SSD | comfortable for a small organization; storage grows with mail |
| Static public IPv4 | mail servers are identified by IP |
| **Port 25 open in both directions** | many providers block it by default; ask support |
| **Reverse DNS (PTR) settable** | must equal your mail host name, or Gmail/Outlook reject or junk your mail |
| Ports 80, 443, 465, 587, 143, 993 reachable | ACME certificate, web UI/webmail, mail apps |

## 1. DNS: before installing

| Type | Name | Value | Notes |
|---|---|---|---|
| A | `mail` | server IPv4 | **no proxy/CDN** (Cloudflare: *DNS only*): mail ports must reach the server |

At the hosting provider, set the PTR (reverse DNS) of the IP to `mail.example.com`.

## 2. Install with PowerShell (all platforms)

The installer downloads the release for your platform from GitHub, verifies its SHA-256 checksum
and installs or upgrades in place.

**Windows**: open *PowerShell as Administrator*:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/seimranali/mail-exchange/main/install.ps1))) -Domain mail.example.com -Email admin@example.com
```

**Linux / macOS**: needs [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell) (`pwsh`):

```bash
curl -fsSLO https://raw.githubusercontent.com/seimranali/mail-exchange/main/install.ps1
sudo pwsh ./install.ps1 -Domain mail.example.com -Email admin@example.com
```

| Option | Meaning |
|---|---|
| `-Domain` / `-Email` | mail host name and Let's Encrypt contact (remembered for upgrades) |
| `-Version 1.2.0` | install a specific release (default: latest) |
| `-Staging` | Let's Encrypt staging certificates, for testing |
| `-SharedHost` | Linux: run behind an existing nginx and PostgreSQL (see below) |
| `-Package <file>` | install from a downloaded `.zip`/`.tar.gz` (offline) |
| `-PostgresUser` / `-PostgresPassword` / `-PostgresPort` | use an existing PostgreSQL server |
| `-Uninstall` | remove the services; keeps configuration, database and mail |

The first install prints the admin login (`admin@<your domain>`) and a generated password.

### Linux without PowerShell

Each Linux package also contains `install.sh`:

```bash
tar xzf mx-<version>-linux-x64.tar.gz && cd mx-<version>-linux-x64
sudo bash install.sh --domain mail.example.com --email admin@example.com --acme-production
```

### Linux: shared server (existing nginx and PostgreSQL)

Add `-SharedHost` (or `--shared-host` for `install.sh`) on a server that already runs websites:

- the admin UI listens on `127.0.0.1:5080`; the installer adds an nginx site with a certbot certificate;
- the MX ACME responder listens on `127.0.0.1:8402`; nginx forwards `/.well-known/acme-challenge/` to it;
- the existing PostgreSQL cluster is reused (new `mx` database and roles only);
- hostname, firewall defaults and system DNS are left alone; only mail ports are opened;
- it refuses to start if another program (e.g. Postfix) holds ports 25/465/587/143/993.

## 3. What gets installed

| | Linux | Windows | macOS |
|---|---|---|---|
| Binaries (current + previous release) | `/opt/mx/releases`, `/opt/mx/current` | `C:\Program Files\MX` | `/opt/mx` |
| Configuration and secrets | `/etc/mx/*.env` | `C:\ProgramData\MX\config` | `/etc/mx`, `/Library/LaunchDaemons/com.mx.*.plist` |
| Mail store, TLS, ACME, backups | `/var/lib/mx` | `C:\ProgramData\MX` | `/var/lib/mx` |
| Logs | `journalctl -u mx-mailhost` | Event Viewer → Application | `/var/log/mx/*.log` |
| Firewall | ufw rules | Windows Firewall rule "MX mail server" | (macOS firewall unchanged) |

Secrets (database passwords, JWT key, key-encryption key, certificate password, initial admin
password) are generated once and readable by administrators only. **Back them up securely**:
losing the key-encryption key means regenerating DKIM keys.

## 4. Operations

| Task | Linux | Windows | macOS |
|---|---|---|---|
| Status | `systemctl status mx-mailhost mx-api mx-workers` | `Get-Service mx-*` | `sudo launchctl print system/com.mx.mailhost` |
| Restart | `systemctl restart mx-mailhost` | `Restart-Service mx-mailhost` | `sudo launchctl kickstart -k system/com.mx.mailhost` |
| Upgrade | re-run the installer | re-run the installer | re-run the installer |
| Certificate | `openssl s_client -connect mail.example.com:993 -brief </dev/null` | same | same |

Linux also installs a nightly backup (`mx-backup.timer`: database dump + compressed mail store in
`/var/backups/mx`, 14 days). On Windows and macOS, back up the data folder and the `mx` database
(`pg_dump`). **Copy backups off the server**; see [backup-recovery](../backup-recovery/postgresql-backup-recovery.md).

## 5. Security notes

- Linux services run as `mx` with `ProtectSystem=strict`, no new privileges, and only
  `CAP_NET_BIND_SERVICE`; Windows services run as per-service virtual accounts; macOS as `_mx`.
- PostgreSQL listens on localhost only; the services use the restricted `mx_app` role with RLS.
- The web UI sends a strict Content-Security-Policy, `X-Frame-Options: DENY` and HSTS.
- Port 80 serves only ACME challenges.

## 6. Going live

1. Sign in at `https://mail.example.com`, change the admin password.
2. Create the organization, your domain, mailboxes and their passwords.
3. Domains → generate the **DKIM** key and publish the records below.
4. Test sending and receiving with a mail app and webmail (`https://mail.example.com/mail`);
   [mail-tester.com](https://www.mail-tester.com) should score 9–10/10.

| Type | Name | Value |
|---|---|---|
| MX | `@` | `mail.example.com` (priority 10) |
| TXT | `@` | `v=spf1 mx -all` |
| TXT | `<selector>._domainkey` | value from the Domains page |
| TXT | `_dmarc` | `v=DMARC1; p=quarantine; rua=mailto:dmarc@example.com` (start with `p=none` if unsure) |

If your DNS is hosted elsewhere (e.g. Cloudflare), add these records **there**: only the DNS
provider named by your domain's nameservers is used.
