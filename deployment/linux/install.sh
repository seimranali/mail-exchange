#!/bin/bash
# MX installer / upgrader for Ubuntu 24.04 (run as root from the extracted release directory).
#
#   Dedicated server (MX owns ports 80/443, installs PostgreSQL 17):
#     sudo ./install.sh --domain mail.example.com --email admin@example.com [--acme-production]
#
#   Shared server (existing nginx on 80/443 and existing PostgreSQL; nothing global is changed):
#     sudo ./install.sh --domain mail.example.com --email admin@example.com --shared-host [--acme-production]
#
# Idempotent: re-running upgrades binaries, keeps secrets/data, applies new migrations.
# Let's Encrypt STAGING is used for the mail certificate unless --acme-production is given.
set -euo pipefail

DOMAIN=""
EMAIL=""
SHARED=0
ACME_DIR="https://acme-staging-v02.api.letsencrypt.org/directory"
RELEASE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --domain) DOMAIN="$2"; shift 2 ;;
    --email) EMAIL="$2"; shift 2 ;;
    --shared-host) SHARED=1; shift ;;
    --acme-production) ACME_DIR="https://acme-v02.api.letsencrypt.org/directory"; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
[[ -n "$DOMAIN" && -n "$EMAIL" ]] || { echo "Usage: $0 --domain <mail host name> --email <admin email> [--shared-host] [--acme-production]" >&2; exit 2; }
[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 1; }
. /etc/os-release
[[ "$ID" == "ubuntu" && "$VERSION_ID" == "24.04" ]] || echo "WARNING: tested on Ubuntu 24.04; this is $PRETTY_NAME"

log() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
VERSION="$(cat "$RELEASE_DIR/VERSION")"
MAIL_DOMAIN="$(echo "$DOMAIN" | cut -d. -f2-)"

# Ports used by MX itself. In shared mode the web UI and ACME responder stay on loopback behind nginx.
API_URL="https://0.0.0.0:443"; ACME_HTTP_PORT=80; ACME_LISTEN=0.0.0.0
if [[ $SHARED -eq 1 ]]; then API_URL="http://127.0.0.1:5080"; ACME_HTTP_PORT=8402; ACME_LISTEN=127.0.0.1; fi

# Refuse to start if another program holds the mail ports (e.g. Postfix/Dovecot).
for port in 25 465 587 143 993; do
  holder="$(ss -ltnpH "( sport = :$port )" 2>/dev/null | grep -v MX.MailHost | grep -oP 'users:\(\("\K[^"]+' | head -1 || true)"
  [[ -z "$holder" ]] || { echo "Port $port is in use by '$holder'. Stop/remove it first." >&2; exit 1; }
done
if [[ $SHARED -eq 0 ]]; then
  for port in 80 443; do
    holder="$(ss -ltnpH "( sport = :$port )" 2>/dev/null | grep -v -e MX.MailHost -e MX.Api | grep -oP 'users:\(\("\K[^"]+' | head -1 || true)"
    [[ -z "$holder" ]] || { echo "Port $port is in use by '$holder'. Use --shared-host to run behind it." >&2; exit 1; }
  done
fi

# ---------------------------------------------------------------------------
log "Packages"
export DEBIAN_FRONTEND=noninteractive
# Never let needrestart restart services on a shared server (it would bounce databases, SSH, ...).
export NEEDRESTART_MODE=l NEEDRESTART_SUSPEND=1
apt-get update -qq
# libicu74 + libgssapi-krb5-2: native libraries the self-contained .NET binaries and Npgsql load.
apt-get install -y -qq ca-certificates curl gnupg zstd openssl lsb-release libicu74 libgssapi-krb5-2 >/dev/null
if pg_lsclusters -h 2>/dev/null | awk '$4=="online"' | grep -q .; then
  PG_PORT="$(pg_lsclusters -h | awk '$4=="online"{print $3; exit}')"
  echo "Using existing PostgreSQL $(pg_lsclusters -h | awk '$4=="online"{print $1; exit}') on port $PG_PORT"
else
  if [[ ! -f /etc/apt/sources.list.d/pgdg.list ]]; then
    install -d /usr/share/postgresql-common/pgdg
    curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list
    apt-get update -qq
  fi
  apt-get install -y -qq postgresql-17 >/dev/null
  systemctl enable --now postgresql >/dev/null
  PG_PORT=5432
fi
[[ $SHARED -eq 1 ]] || apt-get install -y -qq ufw unbound >/dev/null

# ---------------------------------------------------------------------------
log "User and directories"
[[ $SHARED -eq 1 ]] || hostnamectl set-hostname "$DOMAIN"
id mx >/dev/null 2>&1 || useradd --system --home-dir /var/lib/mx --shell /usr/sbin/nologin mx
install -d -o mx -g mx -m 0750 /var/lib/mx /var/lib/mx/mailstore /var/lib/mx/tls /var/lib/mx/acme /var/lib/mx/backups
install -d -o root -g mx -m 0750 /etc/mx
install -d -m 0755 /opt/mx/releases
install -d -o postgres -g postgres -m 0750 /var/backups/mx

# ---------------------------------------------------------------------------
log "Secrets (generated once, kept on upgrade)"
SECRETS=/etc/mx/secrets.env
if [[ ! -f "$SECRETS" ]]; then
  umask 077
  {
    echo "MX_OWNER_PASSWORD=$(openssl rand -hex 24)"
    echo "MX_APP_PASSWORD=$(openssl rand -hex 24)"
    echo "MX_BACKUP_PASSWORD=$(openssl rand -hex 24)"
    echo "JWT_SIGNING_KEY=$(openssl rand -base64 48 | tr -d '\n')"
    echo "KEY_ENCRYPTION_KEY=$(openssl rand -base64 32 | tr -d '\n')"
    echo "PFX_PASSWORD=$(openssl rand -hex 16)"
    echo "ADMIN_PASSWORD=$(openssl rand -base64 18 | tr -d '\n/+=' | cut -c1-20)Aa1!"
  } > "$SECRETS"
  umask 022
  FIRST_INSTALL=1
else
  FIRST_INSTALL=0
fi
chmod 600 "$SECRETS"
# shellcheck disable=SC1090
. "$SECRETS"

# ---------------------------------------------------------------------------
log "Database 'mx' and roles (other databases are not touched)"
sudo -u postgres psql -p "$PG_PORT" -v ON_ERROR_STOP=1 -q \
  -v owner_pw="$MX_OWNER_PASSWORD" -v app_pw="$MX_APP_PASSWORD" -v backup_pw="$MX_BACKUP_PASSWORD" <<'SQL'
SELECT 'CREATE DATABASE mx' WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'mx') \gexec
\c mx
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_owner') THEN CREATE ROLE mx_owner LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB NOBYPASSRLS; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_app') THEN CREATE ROLE mx_app LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB NOBYPASSRLS; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mx_backup') THEN CREATE ROLE mx_backup LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB BYPASSRLS; END IF;
END $$;
ALTER ROLE mx_owner PASSWORD :'owner_pw';
ALTER ROLE mx_app PASSWORD :'app_pw';
ALTER ROLE mx_backup PASSWORD :'backup_pw';
REVOKE CONNECT ON DATABASE mx FROM PUBLIC;
GRANT CONNECT ON DATABASE mx TO mx_owner, mx_app, mx_backup;
CREATE SCHEMA IF NOT EXISTS mx AUTHORIZATION mx_owner;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA mx TO mx_app, mx_backup;
ALTER DEFAULT PRIVILEGES FOR ROLE mx_owner IN SCHEMA mx GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mx_app;
ALTER DEFAULT PRIVILEGES FOR ROLE mx_owner IN SCHEMA mx GRANT SELECT ON TABLES TO mx_backup;
GRANT SELECT ON ALL TABLES IN SCHEMA mx TO mx_backup;
SQL

# ---------------------------------------------------------------------------
log "Installing release $VERSION"
TARGET="/opt/mx/releases/$VERSION"
rm -rf "$TARGET"
cp -a "$RELEASE_DIR/app" "$TARGET"
chown -R root:root "$TARGET"
# Archives built on Windows carry no Unix modes (files arrive world-writable): normalize so only
# root can modify what the services execute.
chmod -R u=rwX,go=rX "$TARGET"
chmod 0755 "$TARGET"/{api/MX.Api,mailhost/MX.MailHost,workers/MX.Workers} "$TARGET"/scripts/*.sh
PREVIOUS="$(readlink -f /opt/mx/current 2>/dev/null || true)"
ln -sfn "$TARGET" /opt/mx/current

# ---------------------------------------------------------------------------
log "Configuration (/etc/mx)"
APP_CS="Host=127.0.0.1;Port=$PG_PORT;Database=mx;Username=mx_app;Password=$MX_APP_PASSWORD;Maximum Pool Size=50"
OWNER_CS="Host=127.0.0.1;Port=$PG_PORT;Database=mx;Username=mx_owner;Password=$MX_OWNER_PASSWORD"
# DNS for mail checks: query real resolvers directly, never the systemd-resolved stub (seen
# returning corrupted cached TXT answers). Dedicated: local unbound. Shared: the system's upstreams.
if [[ $SHARED -eq 1 ]]; then
  mapfile -t DNS_SERVERS < <(awk '/^nameserver/{print $2}' /run/systemd/resolve/resolv.conf 2>/dev/null | grep -v '^127\.' | head -3)
else
  DNS_SERVERS=(127.0.0.1)
fi
umask 027
cat > /etc/mx/common.env <<EOF
ConnectionStrings__Mx=$APP_CS
Secrets__KeyEncryptionKey=$KEY_ENCRYPTION_KEY
MailStore__Root=/var/lib/mx/mailstore
MailServer__Hostname=$DOMAIN
MailServer__CertificatePath=/var/lib/mx/tls/mail.pfx
MailServer__CertificatePassword=$PFX_PASSWORD
DOTNET_ENVIRONMENT=Production
ASPNETCORE_ENVIRONMENT=Production
DOTNET_CLI_TELEMETRY_OPTOUT=1
EOF
for i in "${!DNS_SERVERS[@]}"; do echo "Dns__Servers__$i=${DNS_SERVERS[$i]}" >> /etc/mx/common.env; done
{
  echo "Jwt__SigningKey=$JWT_SIGNING_KEY"
  echo "Kestrel__Endpoints__Web__Url=$API_URL"
  if [[ $SHARED -eq 1 ]]; then
    echo "ReverseProxy__Enabled=true"
  else
    echo "Kestrel__Endpoints__Web__Certificate__Path=/var/lib/mx/tls/mail.pfx"
    echo "Kestrel__Endpoints__Web__Certificate__Password=$PFX_PASSWORD"
  fi
  echo "Cors__AllowedOrigins__0=https://$DOMAIN"
  echo "Acme__Enabled=true"   # informational for the API (certificate card); renewal runs in the mail host
} > /etc/mx/api.env
cat > /etc/mx/mailhost.env <<EOF
Acme__Enabled=true
Acme__DirectoryUrl=$ACME_DIR
Acme__Email=$EMAIL
Acme__AcceptTermsOfService=true
Acme__Domains__0=$DOMAIN
Acme__AccountKeyPath=/var/lib/mx/acme/account.key
Acme__HttpPort=$ACME_HTTP_PORT
Acme__ListenAddress=$ACME_LISTEN
EOF
cat > /etc/mx/workers.env <<EOF
Backup__Location=/var/lib/mx/backups
EOF
cat > /etc/mx/backup.env <<EOF
PGHOST=127.0.0.1
PGPORT=$PG_PORT
PGUSER=mx_backup
PGPASSWORD=$MX_BACKUP_PASSWORD
PGDATABASE=mx
BACKUP_DIR=/var/backups/mx
MAILSTORE=/var/lib/mx/mailstore
RETENTION_DAYS=14
EOF
umask 022
chown root:mx /etc/mx/*.env
chmod 640 /etc/mx/*.env
chmod 600 /etc/mx/secrets.env /etc/mx/backup.env

# Switching Let's Encrypt environment (staging -> production) forces a fresh mail certificate.
if [[ -f /var/lib/mx/acme/directory && "$(cat /var/lib/mx/acme/directory)" != "$ACME_DIR" ]]; then
  rm -f /var/lib/mx/tls/mail.pfx /var/lib/mx/acme/account.key
fi
echo "$ACME_DIR" > /var/lib/mx/acme/directory
chown mx:mx /var/lib/mx/acme/directory

# ---------------------------------------------------------------------------
log "Database migrations and initial data"
ConnectionStrings__MxOwner="$OWNER_CS" ConnectionStrings__Mx="$APP_CS" \
Seed__AdminEmail="admin@$MAIL_DOMAIN" Seed__AdminPassword="$ADMIN_PASSWORD" \
Seed__MailServerHostname="$DOMAIN" Seed__IncludeDemoData=false \
Jwt__SigningKey="$JWT_SIGNING_KEY" Secrets__KeyEncryptionKey="$KEY_ENCRYPTION_KEY" \
DOTNET_ENVIRONMENT=Production ASPNETCORE_ENVIRONMENT=Production \
  /opt/mx/current/api/MX.Api --initialize-database

# ---------------------------------------------------------------------------
if [[ $SHARED -eq 0 ]]; then
  log "Local DNS resolver (DNS blocklists refuse queries from large public resolvers)"
  cat > /etc/unbound/unbound.conf.d/mx.conf <<'EOF'
server:
  interface: 127.0.0.1
  access-control: 127.0.0.0/8 allow
  prefetch: yes
  qname-minimisation: yes
  hide-identity: yes
  hide-version: yes
EOF
  systemctl enable --now unbound >/dev/null
  systemctl restart unbound
  mkdir -p /etc/systemd/resolved.conf.d
  printf '[Resolve]\nDNS=127.0.0.1\nDomains=~.\n' > /etc/systemd/resolved.conf.d/mx-unbound.conf
  systemctl restart systemd-resolved
fi

# ---------------------------------------------------------------------------
log "Firewall"
if command -v ufw >/dev/null; then
  if [[ $SHARED -eq 0 ]]; then
    ufw --force default deny incoming >/dev/null
    ufw --force default allow outgoing >/dev/null
    for port in 22/tcp 80/tcp 443/tcp; do ufw allow "$port" >/dev/null; done
  fi
  for port in 25/tcp 465/tcp 587/tcp 143/tcp 993/tcp; do ufw allow "$port" comment 'MX mail' >/dev/null; done
  [[ $SHARED -eq 1 ]] || ufw --force enable >/dev/null
  ufw status | grep -E '^(25|143|465|587|993)/tcp' | sed 's/^/  /'
fi

# ---------------------------------------------------------------------------
log "Services"
cp "$RELEASE_DIR"/systemd/*.service "$RELEASE_DIR"/systemd/*.path "$RELEASE_DIR"/systemd/*.timer /etc/systemd/system/
if [[ $SHARED -eq 0 ]]; then
  # Dedicated: the API serves HTTPS with the ACME certificate and restarts when it renews.
  mkdir -p /etc/systemd/system/mx-api.service.d
  printf '[Unit]\nConditionPathExists=/var/lib/mx/tls/mail.pfx\n' > /etc/systemd/system/mx-api.service.d/tls.conf
  ENABLE_PATH=mx-api-cert.path
else
  rm -rf /etc/systemd/system/mx-api.service.d
  ENABLE_PATH=""
fi
systemctl daemon-reload
systemctl enable mx-mailhost mx-workers mx-api mx-backup.timer $ENABLE_PATH >/dev/null
systemctl restart mx-mailhost mx-workers
systemctl start mx-backup.timer $ENABLE_PATH
if [[ $SHARED -eq 1 || -f /var/lib/mx/tls/mail.pfx ]]; then systemctl restart mx-api; fi

# ---------------------------------------------------------------------------
if [[ $SHARED -eq 1 ]] && command -v nginx >/dev/null; then
  log "nginx site for $DOMAIN (admin UI + ACME challenge forwarding)"
  install -d /var/www/certbot
  SITE=/etc/nginx/sites-available/$DOMAIN
  # Step 1: HTTP only. certbot's webroot files are served first; anything else under the
  # challenge path goes to the MX ACME responder (mail certificate).
  cat > "$SITE" <<EOF
# Managed by the MX installer.
server {
    listen 80;
    listen [::]:80;
    server_name $DOMAIN;

    location ^~ /.well-known/acme-challenge/ {
        root /var/www/certbot;
        try_files \$uri @mx_acme;
    }
    location @mx_acme {
        proxy_pass http://127.0.0.1:$ACME_HTTP_PORT;
        proxy_set_header Host \$host;
    }
    location / {
        return 301 https://\$host\$request_uri;
    }
}
EOF
  ln -sfn "$SITE" /etc/nginx/sites-enabled/$DOMAIN
  nginx -t && systemctl reload nginx

  # Step 2: certificate for the web UI (nginx), then the HTTPS server block.
  # A renewal hiccup (e.g. a transient network error reaching Let's Encrypt) must never drop the
  # HTTPS site: write the block whenever a certificate exists, even if this certbot run failed.
  if command -v certbot >/dev/null; then
    certbot certonly --webroot -w /var/www/certbot -d "$DOMAIN" --email "$EMAIL" --agree-tos --non-interactive \
      --keep-until-expiring >/tmp/mx-certbot.log 2>&1 || echo "  !! certbot run failed (see /tmp/mx-certbot.log); using the existing certificate if present"
  fi
  if [[ -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem ]]; then
    cat >> "$SITE" <<EOF

server {
    # 'listen ... http2' works on every nginx version ('http2 on;' needs 1.25.1+).
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name $DOMAIN;

    ssl_certificate /etc/letsencrypt/live/$DOMAIN/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;
    client_max_body_size 40m;

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;     # SignalR WebSockets
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 3600;
    }
}
EOF
    if nginx -t 2>/dev/null; then systemctl reload nginx; echo "  HTTPS site enabled with certbot certificate (renewed by certbot.timer)"
    else
      # Never leave a config that breaks the next reload for every site on the server.
      rm -f "/etc/nginx/sites-enabled/$DOMAIN"; nginx -t && systemctl reload nginx
      echo "  !! nginx rejected the HTTPS block; site $DOMAIN disabled. Check $SITE"; fi
  else
    echo "  !! No certificate for $DOMAIN yet (see /tmp/mx-certbot.log)."
    echo "     Usually DNS: $DOMAIN must point to this server (Cloudflare: DNS only). Re-run the installer afterwards."
  fi
fi

# Keep the current and previous release only.
{ ls -1dt /opt/mx/releases/* | tail -n +3 | grep -v -e "$TARGET" -e "${PREVIOUS:-/nonexistent}" || true; } | xargs -r rm -rf

# ---------------------------------------------------------------------------
log "Checks"
PUBLIC_IP="$(curl -fsS4 --max-time 5 https://api.ipify.org || echo unknown)"
RESOLVED="$(getent ahostsv4 "$DOMAIN" | awk 'NR==1{print $1}')"
echo "Public IPv4:        $PUBLIC_IP"
echo "$DOMAIN resolves to: ${RESOLVED:-<nothing>}"
[[ "$RESOLVED" == "$PUBLIC_IP" ]] || echo "!! DNS: point an A record for $DOMAIN to $PUBLIC_IP (Cloudflare: DNS only / grey cloud)."
PTR="$(getent hosts "$PUBLIC_IP" | awk '{print $2}')"
echo "Reverse DNS (PTR):  ${PTR:-<none>}"
[[ "$PTR" == "$DOMAIN" ]] || echo "!! Set the PTR / reverse DNS of $PUBLIC_IP to $DOMAIN at your VPS provider."
if timeout 6 bash -c 'exec 3<>/dev/tcp/gmail-smtp-in.l.google.com/25' 2>/dev/null; then echo "Outbound port 25:   open"; else echo "!! Outbound port 25 is BLOCKED - ask your VPS provider to unblock it (outbound mail cannot be delivered until then)."; fi
sleep 3
for s in mx-mailhost mx-api mx-workers; do printf '  %-12s %s\n' "$s" "$(systemctl is-active $s)"; done
ss -ltnH | awk '{print $4}' | grep -E ':(25|465|587|143|993|5080|8402)$' | sort -t: -k2 -n | sed 's/^/  listening /'

echo
echo "MX $VERSION installed$([[ $SHARED -eq 1 ]] && echo ' (shared-host mode)')."
echo "Admin UI:  https://$DOMAIN"
if [[ $FIRST_INSTALL -eq 1 ]]; then
  echo "Admin login: admin@$MAIL_DOMAIN"
  echo "Password:    $ADMIN_PASSWORD   (also in /etc/mx/secrets.env - change it after first login)"
fi
