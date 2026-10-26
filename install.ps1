<#
.SYNOPSIS
    Installs or upgrades the MX mail server (SMTP, IMAP, webmail, admin UI) on Linux, Windows or macOS.

.DESCRIPTION
    Downloads the release for this platform from GitHub (or uses -Package), then:
      Linux    runs the bundled install.sh: systemd services, PostgreSQL 17 from apt.
      Windows  installs Windows services, PostgreSQL 17 via winget (or uses an existing one).
      macOS    installs launchd daemons, PostgreSQL 17 via Homebrew (or uses an existing one).
    Re-running upgrades in place: secrets, settings and mail are kept; new migrations are applied.

    Run elevated: an Administrator PowerShell on Windows, `sudo pwsh` on Linux and macOS.

.EXAMPLE
    # One line, any platform (Windows: admin PowerShell; Linux/macOS: sudo pwsh -c "...")
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/seimranali/mail-exchange/main/install.ps1))) -Domain mail.example.com -Email admin@example.com

.EXAMPLE
    ./install.ps1 -Domain mail.example.com -Email admin@example.com -Version 1.2.0 -Staging

.EXAMPLE
    ./install.ps1 -Uninstall      # removes the services; keeps configuration, database and mail
#>
[CmdletBinding()]
param(
    # Public host name of the mail server (MX target, IMAP/SMTP server, admin UI), e.g. mail.example.com.
    [string] $Domain,
    # Contact address for Let's Encrypt; the first admin login is admin@<your domain>.
    [string] $Email,
    # Release to install ("latest" or e.g. 1.2.0).
    [string] $Version = 'latest',
    # Use Let's Encrypt staging certificates (for testing; browsers and mail apps will not trust them).
    [switch] $Staging,
    # Linux only: run behind an existing nginx/PostgreSQL instead of owning ports 80/443.
    [switch] $SharedHost,
    # Install from a local release archive (.zip/.tar.gz) or extracted folder instead of downloading.
    [string] $Package,
    # Existing PostgreSQL superuser (Windows default "postgres"; macOS default: the Homebrew user).
    [string] $PostgresUser,
    # Password of that superuser (not needed when this script installed PostgreSQL itself).
    [string] $PostgresPassword,
    [int] $PostgresPort = 5432,
    [string] $Repository = 'seimranali/mail-exchange',
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with the progress bar
$PortGiven = $PSBoundParameters.ContainsKey('PostgresPort')

# ---------------------------------------------------------------------------
# Platform
# ---------------------------------------------------------------------------
$OnWindows = $env:OS -eq 'Windows_NT'
$OnMac = (-not $OnWindows) -and $IsMacOS
$OnLinux = (-not $OnWindows) -and $IsLinux

function Write-Step([string] $Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Warn([string] $Text) { Write-Host "!! $Text" -ForegroundColor Yellow }

if (-not ($OnWindows -or $OnMac -or $OnLinux)) { throw "Unsupported operating system." }
if (-not $OnWindows -and $PSVersionTable.PSVersion.Major -lt 7) { throw "PowerShell 7 (pwsh) is required on Linux and macOS." }

if ($OnWindows) {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "Run this from an Administrator PowerShell." }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
elseif ((& id -u) -ne '0') { throw "Run as root: sudo pwsh ./install.ps1 ..." }

$arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($OnWindows) { $Rid = 'win-x64' }   # Windows on ARM runs the x64 build under emulation
elseif ($OnMac) { $Rid = if ($arch -eq 'arm64') { 'osx-arm64' } else { 'osx-x64' } }
else { $Rid = if ($arch -eq 'arm64') { 'linux-arm64' } else { 'linux-x64' } }

# ---------------------------------------------------------------------------
# Locations (Windows / macOS; Linux uses install.sh's /opt/mx, /etc/mx, /var/lib/mx)
# ---------------------------------------------------------------------------
if ($OnWindows) {
    $AppDir = Join-Path $env:ProgramFiles 'MX'
    $DataDir = Join-Path $env:ProgramData 'MX'
    $ConfigDir = Join-Path $DataDir 'config'
}
else {
    $AppDir = '/opt/mx'
    $DataDir = '/var/lib/mx'
    $ConfigDir = '/etc/mx'
}
$Services = @(
    @{ Name = 'mx-mailhost'; App = 'mailhost'; Exe = 'MX.MailHost'; Title = 'MX mail server (SMTP, IMAP, ACME)' },
    @{ Name = 'mx-workers'; App = 'workers'; Exe = 'MX.Workers'; Title = 'MX background workers' },
    @{ Name = 'mx-api'; App = 'api'; Exe = 'MX.Api'; Title = 'MX admin UI, webmail and API' }
)

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function New-Secret([int] $Bytes = 24, [switch] $Base64) {
    $buffer = New-Object byte[] $Bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    if ($Base64) { return [Convert]::ToBase64String($buffer) }
    return -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

function Invoke-Native([string] $File, [string[]] $Arguments, [string] $StdIn, [switch] $AllowFailure) {
    if ($PSBoundParameters.ContainsKey('StdIn')) { $output = $StdIn | & $File @Arguments 2>&1 }
    else { $output = & $File @Arguments 2>&1 }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) { throw "$File failed ($LASTEXITCODE): $($output | Out-String)" }
    return $output
}

function Read-JsonFile([string] $Path) {
    if (-not (Test-Path $Path)) { return @{} }
    $object = Get-Content $Path -Raw | ConvertFrom-Json
    $table = @{}
    foreach ($p in $object.PSObject.Properties) { $table[$p.Name] = $p.Value }
    return $table
}

function Write-PrivateFile([string] $Path, [string] $Content) {
    Set-Content -Path $Path -Value $Content -Encoding UTF8
    if ($OnWindows) { Invoke-Native icacls.exe @($Path, '/inheritance:r', '/grant:r', '*S-1-5-18:F', '*S-1-5-32-544:F') | Out-Null }
    else { Invoke-Native chmod @('600', $Path) | Out-Null }
}

# ---------------------------------------------------------------------------
# Release package
# ---------------------------------------------------------------------------
function Get-ReleasePackage {
    $work = Join-Path ([IO.Path]::GetTempPath()) ("mx-install-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $work | Out-Null
    $archive = $Package
    if ($archive -and (Test-Path $archive -PathType Container)) { return (Resolve-Path $archive).Path }

    if (-not $archive) {
        $api = if ($Version -eq 'latest') { "https://api.github.com/repos/$Repository/releases/latest" } else { "https://api.github.com/repos/$Repository/releases/tags/v$($Version.TrimStart('v'))" }
        Write-Step "Finding release ($Version) for $Rid"
        $release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'mx-installer'; Accept = 'application/vnd.github+json' }
        $asset = $release.assets | Where-Object { $_.name -match "-$([regex]::Escape($Rid))\.(zip|tar\.gz)$" } | Select-Object -First 1
        if (-not $asset) { throw "Release $($release.tag_name) has no package for $Rid." }
        $archive = Join-Path $work $asset.name
        Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)"
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing -Headers @{ 'User-Agent' = 'mx-installer' }
        $sums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
        if ($sums) {
            $expected = ((Invoke-WebRequest -Uri $sums.browser_download_url -UseBasicParsing -Headers @{ 'User-Agent' = 'mx-installer' }).Content -split "`n" |
                Where-Object { $_ -match [regex]::Escape($asset.name) } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
            $actual = (Get-FileHash $archive -Algorithm SHA256).Hash
            if (-not $expected -or $actual -ne $expected.ToUpperInvariant()) { throw "Checksum mismatch for $($asset.name): download corrupted or tampered with." }
            Write-Host "Checksum verified."
        }
    }

    $extract = Join-Path $work 'pkg'
    New-Item -ItemType Directory -Force $extract | Out-Null
    if ($archive -like '*.zip') { Expand-Archive -Path $archive -DestinationPath $extract -Force }
    else {
        Invoke-Native tar @('-xzf', $archive, '-C', $extract) | Out-Null
    }
    $versionFile = Get-ChildItem $extract -Recurse -Filter VERSION | Select-Object -First 1
    if (-not $versionFile) { throw "Not an MX release package: $archive" }
    return $versionFile.DirectoryName
}

# ---------------------------------------------------------------------------
# Linux: delegate to install.sh
# ---------------------------------------------------------------------------
function Install-Linux([string] $PackageDir) {
    if (-not $Domain -and (Test-Path /etc/mx/mailhost.env)) {
        $lines = Get-Content /etc/mx/mailhost.env
        $script:Domain = ($lines | Where-Object { $_ -like 'Acme__Domains__0=*' }) -replace '^[^=]+=', ''
        if (-not $Email) { $script:Email = ($lines | Where-Object { $_ -like 'Acme__Email=*' }) -replace '^[^=]+=', '' }
    }
    if (-not $Domain -or -not $Email) { throw "-Domain and -Email are required for the first install." }
    $arguments = @((Join-Path $PackageDir 'install.sh'), '--domain', $Domain, '--email', $Email)
    if ($SharedHost) { $arguments += '--shared-host' }
    if (-not $Staging) { $arguments += '--acme-production' }
    & bash @arguments
    if ($LASTEXITCODE -ne 0) { throw "install.sh failed ($LASTEXITCODE)" }
}

function Uninstall-Linux {
    foreach ($unit in 'mx-api', 'mx-mailhost', 'mx-workers', 'mx-backup.timer', 'mx-api-cert.path') { & systemctl disable --now $unit 2>$null }
    Remove-Item /etc/systemd/system/mx-* -Recurse -Force -ErrorAction SilentlyContinue
    & systemctl daemon-reload
    Write-Host "Services removed. Kept: /etc/mx (configuration), /var/lib/mx (mail), /opt/mx (binaries), database 'mx'."
}

# ---------------------------------------------------------------------------
# PostgreSQL (Windows / macOS)
# ---------------------------------------------------------------------------
function Find-Psql {
    if ($OnWindows) {
        $bases = Get-ItemProperty 'HKLM:\SOFTWARE\PostgreSQL\Installations\*' -ErrorAction SilentlyContinue | ForEach-Object { $_.'Base Directory' }
        $bases += Get-ChildItem (Join-Path $env:ProgramFiles 'PostgreSQL') -Directory -ErrorAction SilentlyContinue | Sort-Object { [int]($_.Name -replace '\D', '0') } -Descending | ForEach-Object { $_.FullName }
        foreach ($b in $bases) { if ($b -and (Test-Path (Join-Path $b 'bin\psql.exe'))) { return (Join-Path $b 'bin\psql.exe') } }
        return $null
    }
    foreach ($p in @('/opt/homebrew/opt/postgresql@17/bin/psql', '/usr/local/opt/postgresql@17/bin/psql', '/Applications/Postgres.app/Contents/Versions/latest/bin/psql')) { if (Test-Path $p) { return $p } }
    $cmd = Get-Command psql -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Test-PostgresUp([string] $Psql, [string] $User, [string] $Password) {
    $env:PGPASSWORD = $Password
    try {
        & $Psql -h 127.0.0.1 -p $PostgresPort -U $User -d postgres -tAc 'select 1' 2>$null | Out-Null
        return $LASTEXITCODE -eq 0
    }
    finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Initialize-Postgres([hashtable] $Secrets) {
    Write-Step "PostgreSQL"
    $psql = Find-Psql
    if ($OnWindows) {
        $user = if ($PostgresUser) { $PostgresUser } else { 'postgres' }
        $password = if ($PostgresPassword) { $PostgresPassword } else { $Secrets.PG_SUPERUSER_PASSWORD }
        if (-not $psql) {
            if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
                throw "PostgreSQL is not installed and winget is unavailable. Install PostgreSQL 17 (https://www.postgresql.org/download/windows/), then re-run with -PostgresPassword <superuser password>."
            }
            if (-not $password) { $password = New-Secret 18; $Secrets.PG_SUPERUSER_PASSWORD = $password }
            Write-Host "Installing PostgreSQL 17 with winget (a few minutes)"
            # The generated password is hex: no quoting needed (Windows PowerShell mangles embedded quotes).
            $override = "--mode unattended --unattendedmodeui none --superpassword $password --serverport $PostgresPort --enable-components server,commandlinetools"
            Invoke-Native winget @('install', '--id', 'PostgreSQL.PostgreSQL.17', '-e', '--silent', '--accept-package-agreements', '--accept-source-agreements', '--override', $override) | Out-Null
            $psql = Find-Psql
            if (-not $psql) { throw "PostgreSQL was installed but psql.exe was not found." }
        }
    }
    else {
        $brewUser = $env:SUDO_USER
        $user = if ($PostgresUser) { $PostgresUser } elseif ($brewUser) { $brewUser } else { 'postgres' }
        $password = if ($PostgresPassword) { $PostgresPassword } else { $Secrets.PG_SUPERUSER_PASSWORD }
        if (-not $psql -or -not (Test-PostgresUp $psql $user $password)) {
            $brew = @('/opt/homebrew/bin/brew', '/usr/local/bin/brew') | Where-Object { Test-Path $_ } | Select-Object -First 1
            if (-not $brew -or -not $brewUser) { throw "PostgreSQL 17 is not running. Install Homebrew (https://brew.sh) and run this with sudo from your user account, or start PostgreSQL and pass -PostgresUser/-PostgresPassword." }
            Write-Host "Installing PostgreSQL 17 with Homebrew (as $brewUser)"
            Invoke-Native sudo @('-u', $brewUser, $brew, 'install', 'postgresql@17') | Out-Null
            Invoke-Native sudo @('-u', $brewUser, $brew, 'services', 'start', 'postgresql@17') | Out-Null
            $psql = Find-Psql
        }
    }
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-PostgresUp $psql $user $password)) {
        if ((Get-Date) -gt $deadline) { throw "Cannot connect to PostgreSQL on 127.0.0.1:$PostgresPort as '$user'. Pass -PostgresUser and -PostgresPassword for an existing server." }
        Start-Sleep -Seconds 2
    }
    if ($PostgresPassword) { $Secrets.PG_SUPERUSER_PASSWORD = $PostgresPassword }

    Write-Host "Database 'mx' and roles (other databases are not touched)"
    $sql = @'
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
'@
    $env:PGPASSWORD = $password
    try {
        Invoke-Native $psql @('-h', '127.0.0.1', '-p', "$PostgresPort", '-U', $user, '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-q',
            '-v', "owner_pw=$($Secrets.MX_OWNER_PASSWORD)", '-v', "app_pw=$($Secrets.MX_APP_PASSWORD)", '-v', "backup_pw=$($Secrets.MX_BACKUP_PASSWORD)") -StdIn $sql | Out-Null
    }
    finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------------------
# Windows / macOS install
# ---------------------------------------------------------------------------
function Test-PortsFree {
    $ports = 25, 465, 587, 143, 993, 80, 443
    foreach ($port in $ports) {
        if ($OnWindows) {
            $owner = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($owner) {
                $process = Get-Process -Id $owner.OwningProcess -ErrorAction SilentlyContinue
                if ($process -and $process.ProcessName -notlike 'MX.*') { throw "Port $port is in use by '$($process.ProcessName)' (PID $($process.Id)). Stop it first." }
            }
        }
        else {
            $line = & lsof -nP -iTCP:$port -sTCP:LISTEN 2>$null | Select-Object -Skip 1 | Select-Object -First 1
            if ($line -and $line -notmatch '^MX\.') { throw "Port $port is in use: $line" }
        }
    }
}

function Get-ServiceEnvironment([hashtable] $Secrets, [string] $AcmeDirectory) {
    $tls = Join-Path $DataDir 'tls'
    $pfx = Join-Path $tls 'mail.pfx'
    $cs = "Host=127.0.0.1;Port=$PostgresPort;Database=mx;Username=mx_app;Password=$($Secrets.MX_APP_PASSWORD);Maximum Pool Size=50"
    $common = [ordered]@{
        'ConnectionStrings__Mx' = $cs
        'Secrets__KeyEncryptionKey' = $Secrets.KEY_ENCRYPTION_KEY
        'MailStore__Root' = (Join-Path $DataDir 'mailstore')
        'MailServer__Hostname' = $Domain
        'MailServer__CertificatePath' = $pfx
        'MailServer__CertificatePassword' = $Secrets.PFX_PASSWORD
        'DOTNET_ENVIRONMENT' = 'Production'
        'ASPNETCORE_ENVIRONMENT' = 'Production'
        'DOTNET_CLI_TELEMETRY_OPTOUT' = '1'
    }
    $api = [ordered]@{
        'Jwt__SigningKey' = $Secrets.JWT_SIGNING_KEY
        'Kestrel__Endpoints__Web__Url' = 'https://0.0.0.0:443'
        'Web__CertificatePath' = $pfx
        'Web__CertificatePassword' = $Secrets.PFX_PASSWORD
        'Cors__AllowedOrigins__0' = "https://$Domain"
        'Acme__Enabled' = 'true'
    }
    $mailhost = [ordered]@{
        'Acme__Enabled' = 'true'
        'Acme__DirectoryUrl' = $AcmeDirectory
        'Acme__Email' = $Email
        'Acme__AcceptTermsOfService' = 'true'
        'Acme__Domains__0' = $Domain
        'Acme__AccountKeyPath' = (Join-Path (Join-Path $DataDir 'acme') 'account.key')
        'Acme__HttpPort' = '80'
        'Acme__ListenAddress' = '0.0.0.0'
    }
    $workers = [ordered]@{ 'Backup__Location' = (Join-Path $DataDir 'backups') }
    $merge = {
        param($a, $b)
        $all = [ordered]@{}
        foreach ($kv in $a.GetEnumerator()) { $all[$kv.Key] = $kv.Value }
        foreach ($kv in $b.GetEnumerator()) { $all[$kv.Key] = $kv.Value }
        $all
    }
    return @{
        'mx-api' = (& $merge $common $api)
        'mx-mailhost' = (& $merge $common $mailhost)
        'mx-workers' = (& $merge $common $workers)
    }
}

function Install-WindowsServices([string] $Current, [hashtable] $Environment) {
    foreach ($s in $Services) {
        $exe = Join-Path $Current "$($s.App)\$($s.Exe).exe"
        $existing = Get-Service $s.Name -ErrorAction SilentlyContinue
        if (-not $existing) {
            New-Service -Name $s.Name -BinaryPathName "`"$exe`"" -DisplayName $s.Title -StartupType Automatic | Out-Null
        }
        else {
            Invoke-Native sc.exe @('config', $s.Name, 'binPath=', "`"$exe`"", 'start=', 'auto') | Out-Null
        }
        # Least privilege: each service runs as its own virtual account (NT SERVICE\<name>).
        Invoke-Native sc.exe @('config', $s.Name, 'obj=', "NT SERVICE\$($s.Name)") | Out-Null
        Invoke-Native sc.exe @('failure', $s.Name, 'reset=', '86400', 'actions=', 'restart/5000/restart/10000/restart/30000') | Out-Null
        Invoke-Native sc.exe @('description', $s.Name, $s.Title) | Out-Null
        $values = @($Environment[$s.Name].GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
        Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$($s.Name)" -Name Environment -Type MultiString -Value $values
    }
    # Data: services may write; configuration (secrets) is Administrators/SYSTEM only.
    $grants = $Services | ForEach-Object { "NT SERVICE\$($_.Name):(OI)(CI)M" }
    foreach ($dir in 'mailstore', 'tls', 'acme', 'backups') {
        $path = Join-Path $DataDir $dir
        $arguments = @($path, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
        foreach ($g in $grants) { $arguments += @('/grant:r', $g) }
        Invoke-Native icacls.exe $arguments | Out-Null
    }
    Invoke-Native icacls.exe @($ConfigDir, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F') | Out-Null

    $rule = Get-NetFirewallRule -DisplayName 'MX mail server' -ErrorAction SilentlyContinue
    if (-not $rule) {
        New-NetFirewallRule -DisplayName 'MX mail server' -Direction Inbound -Protocol TCP -LocalPort 25, 465, 587, 143, 993, 80, 443 -Action Allow | Out-Null
    }
}

function Install-MacServices([string] $Current, [hashtable] $Environment) {
    # Dedicated hidden system user _mx (UID/GID < 500).
    & dscl . -read /Users/_mx 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $used = @(& dscl . -list /Users UniqueID | ForEach-Object { [int](($_ -split '\s+')[-1]) }) + @(& dscl . -list /Groups PrimaryGroupID | ForEach-Object { [int](($_ -split '\s+')[-1]) })
        $id = (400..499 | Where-Object { $used -notcontains $_ } | Select-Object -First 1)
        Invoke-Native dscl @('.', '-create', '/Groups/_mx', 'PrimaryGroupID', "$id") | Out-Null
        foreach ($pair in @(@('UniqueID', "$id"), @('PrimaryGroupID', "$id"), @('UserShell', '/usr/bin/false'), @('NFSHomeDirectory', $DataDir), @('RealName', 'MX mail server'), @('IsHidden', '1'))) {
            Invoke-Native dscl @('.', '-create', '/Users/_mx', $pair[0], $pair[1]) | Out-Null
        }
    }
    Invoke-Native chown @('-R', '_mx:_mx', $DataDir) | Out-Null
    Invoke-Native chmod @('750', $DataDir) | Out-Null
    New-Item -ItemType Directory -Force '/var/log/mx' | Out-Null
    Invoke-Native chown @('_mx:_mx', '/var/log/mx') | Out-Null

    foreach ($s in $Services) {
        $label = "com.mx.$($s.App)"
        $plist = "/Library/LaunchDaemons/$label.plist"
        $vars = ($Environment[$s.Name].GetEnumerator() | ForEach-Object {
                "      <key>$([Security.SecurityElement]::Escape($_.Key))</key><string>$([Security.SecurityElement]::Escape([string]$_.Value))</string>"
            }) -join "`n"
        $xml = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$label</string>
  <key>ProgramArguments</key><array><string>$Current/$($s.App)/$($s.Exe)</string></array>
  <key>WorkingDirectory</key><string>$Current/$($s.App)</string>
  <key>UserName</key><string>_mx</string>
  <key>GroupName</key><string>_mx</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>5</integer>
  <key>StandardOutPath</key><string>/var/log/mx/$($s.App).log</string>
  <key>StandardErrorPath</key><string>/var/log/mx/$($s.App).log</string>
  <key>EnvironmentVariables</key>
  <dict>
$vars
  </dict>
</dict>
</plist>
"@
        & launchctl bootout "system/$label" 2>$null
        Set-Content -Path $plist -Value $xml -Encoding UTF8
        Invoke-Native chown @('root:wheel', $plist) | Out-Null
        Invoke-Native chmod @('600', $plist) | Out-Null   # holds secrets in EnvironmentVariables
    }
}

function Stop-MxServices {
    if ($OnWindows) {
        foreach ($s in $Services) { Stop-Service $s.Name -Force -ErrorAction SilentlyContinue }
    }
    else {
        foreach ($s in $Services) { & launchctl bootout "system/com.mx.$($s.App)" 2>$null }
    }
}

function Start-MxServices {
    if ($OnWindows) {
        foreach ($s in $Services) { Start-Service $s.Name }
    }
    else {
        foreach ($s in $Services) { Invoke-Native launchctl @('bootstrap', 'system', "/Library/LaunchDaemons/com.mx.$($s.App).plist") -AllowFailure | Out-Null }
    }
}

function Install-Desktop([string] $PackageDir) {
    $settingsPath = Join-Path $ConfigDir 'install.json'
    $secretsPath = Join-Path $ConfigDir 'secrets.json'
    New-Item -ItemType Directory -Force $ConfigDir | Out-Null
    $settings = Read-JsonFile $settingsPath
    if (-not $Domain) { $script:Domain = $settings.Domain }
    if (-not $Email) { $script:Email = $settings.Email }
    if (-not $PortGiven -and $settings.PostgresPort) { $script:PostgresPort = [int]$settings.PostgresPort }
    if (-not $Domain -or -not $Email) { throw "-Domain and -Email are required for the first install." }
    $acmeDirectory = if ($Staging) { 'https://acme-staging-v02.api.letsencrypt.org/directory' } else { 'https://acme-v02.api.letsencrypt.org/directory' }
    $version = (Get-Content (Join-Path $PackageDir 'VERSION') -Raw).Trim()
    $mailDomain = ($Domain -split '\.', 2)[1]

    Write-Step "Checking ports"
    Stop-MxServices
    Test-PortsFree

    Write-Step "Directories"
    foreach ($dir in 'mailstore', 'tls', 'acme', 'backups') { New-Item -ItemType Directory -Force (Join-Path $DataDir $dir) | Out-Null }
    New-Item -ItemType Directory -Force (Join-Path $AppDir 'releases') | Out-Null

    Write-Step "Secrets (generated once, kept on upgrade)"
    $secrets = Read-JsonFile $secretsPath
    $firstInstall = -not $secrets.MX_APP_PASSWORD
    foreach ($key in 'MX_OWNER_PASSWORD', 'MX_APP_PASSWORD', 'MX_BACKUP_PASSWORD', 'PFX_PASSWORD') { if (-not $secrets[$key]) { $secrets[$key] = New-Secret 24 } }
    if (-not $secrets.JWT_SIGNING_KEY) { $secrets.JWT_SIGNING_KEY = New-Secret 48 -Base64 }
    if (-not $secrets.KEY_ENCRYPTION_KEY) { $secrets.KEY_ENCRYPTION_KEY = New-Secret 32 -Base64 }
    if (-not $secrets.ADMIN_PASSWORD) { $secrets.ADMIN_PASSWORD = ((New-Secret 15 -Base64) -replace '[/+=]', '') + 'Aa1!' }

    Initialize-Postgres $secrets
    Write-PrivateFile $secretsPath ($secrets | ConvertTo-Json)
    Write-PrivateFile $settingsPath (@{ Domain = $Domain; Email = $Email; PostgresPort = $PostgresPort; Version = $version } | ConvertTo-Json)

    Write-Step "Installing release $version"
    $target = Join-Path (Join-Path $AppDir 'releases') $version
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Copy-Item -Recurse (Join-Path $PackageDir 'app') $target
    $current = Join-Path $AppDir 'current'
    if (Test-Path $current) { (Get-Item $current).Delete() }
    if ($OnWindows) { New-Item -ItemType Junction -Path $current -Target $target | Out-Null }
    else {
        Invoke-Native ln @('-sfn', $target, $current) | Out-Null
        Invoke-Native chown @('-R', 'root:wheel', $target) | Out-Null
        Invoke-Native chmod @('-R', 'u=rwX,go=rX', $target) | Out-Null
        foreach ($s in $Services) {
            $binary = Join-Path $target "$($s.App)/$($s.Exe)"
            Invoke-Native chmod @('755', $binary) | Out-Null
            if ($OnMac) {
                # Packages are cross-built on Linux: Apple silicon only runs signed code, so sign ad hoc here.
                Invoke-Native xattr @('-dr', 'com.apple.quarantine', (Join-Path $target $s.App)) -AllowFailure | Out-Null
                Invoke-Native codesign @('--force', '--sign', '-', $binary) | Out-Null
            }
        }
    }

    # Switching Let's Encrypt environment forces a fresh certificate.
    $directoryFile = Join-Path (Join-Path $DataDir 'acme') 'directory'
    if ((Test-Path $directoryFile) -and (Get-Content $directoryFile -Raw).Trim() -ne $acmeDirectory) {
        Remove-Item (Join-Path (Join-Path $DataDir 'tls') 'mail.pfx'), (Join-Path (Join-Path $DataDir 'acme') 'account.key') -ErrorAction SilentlyContinue
    }
    Set-Content $directoryFile $acmeDirectory -NoNewline

    Write-Step "Database migrations and initial data"
    $exe = Join-Path $current ("api/MX.Api" + $(if ($OnWindows) { '.exe' } else { '' }))
    $initEnv = @{
        'ConnectionStrings__MxOwner' = "Host=127.0.0.1;Port=$PostgresPort;Database=mx;Username=mx_owner;Password=$($secrets.MX_OWNER_PASSWORD)"
        'ConnectionStrings__Mx' = "Host=127.0.0.1;Port=$PostgresPort;Database=mx;Username=mx_app;Password=$($secrets.MX_APP_PASSWORD)"
        'Seed__AdminEmail' = "admin@$mailDomain"
        'Seed__AdminPassword' = $secrets.ADMIN_PASSWORD
        'Seed__MailServerHostname' = $Domain
        'Seed__IncludeDemoData' = 'false'
        'Jwt__SigningKey' = $secrets.JWT_SIGNING_KEY
        'Secrets__KeyEncryptionKey' = $secrets.KEY_ENCRYPTION_KEY
        'DOTNET_ENVIRONMENT' = 'Production'
        'ASPNETCORE_ENVIRONMENT' = 'Production'
    }
    foreach ($kv in $initEnv.GetEnumerator()) { [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value, 'Process') }
    try { Invoke-Native $exe @('--initialize-database') | Out-Null }
    finally { foreach ($kv in $initEnv.GetEnumerator()) { [Environment]::SetEnvironmentVariable($kv.Key, $null, 'Process') } }

    Write-Step "Services"
    $environment = Get-ServiceEnvironment $secrets $acmeDirectory
    if ($OnWindows) { Install-WindowsServices $current $environment } else { Install-MacServices $current $environment }
    Start-MxServices

    # Keep the current and previous release only.
    Get-ChildItem (Join-Path $AppDir 'releases') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 2 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Write-Step "Checks"
    Start-Sleep -Seconds 5
    foreach ($s in $Services) {
        if ($OnWindows) { $state = (Get-Service $s.Name).Status }
        else { $state = if ((& launchctl print "system/com.mx.$($s.App)" 2>$null) -match 'state = running') { 'running' } else { 'not running (see /var/log/mx)' } }
        Write-Host ("  {0,-12} {1}" -f $s.Name, $state)
    }
    try {
        $ip = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 5)
        $resolved = ([Net.Dns]::GetHostAddresses($Domain) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).IPAddressToString
        Write-Host "Public IPv4:  $ip"
        Write-Host "$Domain resolves to: $resolved"
        if ($resolved -ne $ip) { Write-Warn "Point an A record for $Domain to $ip (no proxy/CDN: mail ports must reach this machine)." }
    }
    catch { Write-Warn "Could not check public IP / DNS: $($_.Exception.Message)" }

    Write-Host ""
    Write-Host "MX $version installed." -ForegroundColor Green
    Write-Host "Admin UI:  https://$Domain   (webmail: https://$Domain/mail)"
    Write-Host "The HTTPS certificate is requested from Let's Encrypt once $Domain points here and port 80 is reachable."
    if ($firstInstall) {
        Write-Host "Admin login: admin@$mailDomain"
        Write-Host "Password:    $($secrets.ADMIN_PASSWORD)   (also in $secretsPath - change it after first login)"
    }
}

function Uninstall-Desktop {
    Stop-MxServices
    foreach ($s in $Services) {
        if ($OnWindows) { if (Get-Service $s.Name -ErrorAction SilentlyContinue) { Invoke-Native sc.exe @('delete', $s.Name) | Out-Null } }
        else { Remove-Item "/Library/LaunchDaemons/com.mx.$($s.App).plist" -ErrorAction SilentlyContinue }
    }
    if ($OnWindows) { Remove-NetFirewallRule -DisplayName 'MX mail server' -ErrorAction SilentlyContinue }
    Write-Host "Services removed. Kept: $ConfigDir (configuration), $DataDir (mail), $AppDir (binaries), database 'mx'."
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
if ($Uninstall) {
    if ($OnLinux) { Uninstall-Linux } else { Uninstall-Desktop }
    return
}
$packageDir = Get-ReleasePackage
Write-Host "Package: $packageDir ($((Get-Content (Join-Path $packageDir 'VERSION') -Raw).Trim()), $Rid)"
if ($OnLinux) { Install-Linux $packageDir }
else {
    if ($SharedHost) { Write-Warn "-SharedHost applies to Linux only; ignoring." }
    Install-Desktop $packageDir
}
