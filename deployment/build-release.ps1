# Builds self-contained MX release packages (no .NET runtime needed on the server).
#
#   pwsh deployment/build-release.ps1 -Version 1.2.0                       # every platform
#   pwsh deployment/build-release.ps1 -Version 1.2.0 -Runtime linux-x64    # one platform
#
# Output: release/mx-<version>-<runtime>.tar.gz (Linux/macOS) or .zip (Windows), each containing
# install.ps1 (all platforms), plus install.sh and systemd units on Linux.
param(
    [string] $Version = (Get-Date -Format 'yyyy.MM.dd.HHmm'),
    [ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'osx-x64', 'osx-arm64')]
    [string[]] $Runtime = @('linux-x64', 'linux-arm64', 'win-x64', 'osx-x64', 'osx-arm64'),
    [switch] $SkipFrontend
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseRoot = Join-Path $root 'release'
New-Item -ItemType Directory -Force $releaseRoot | Out-Null
$dist = Join-Path $root 'frontend/dist'

if (-not $SkipFrontend) {
    Write-Host "==> Frontend"
    Push-Location (Join-Path $root 'frontend')
    try {
        # Native commands don't throw on failure; check every exit code so a stale dist/ is never shipped.
        if (Test-Path dist) { Remove-Item -Recurse -Force dist }
        npm ci --no-audit --no-fund | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed (is a dev server still running and locking node_modules?)" }
        $env:MX_BUILD_ID = $Version   # stamped into asset names: new URLs every release, no stale CDN/browser caches
        npm run build | Out-Null
        $built = $LASTEXITCODE
        Remove-Item Env:MX_BUILD_ID
        if ($built -ne 0 -or -not (Test-Path dist/index.html)) { throw "frontend build failed" }
    }
    finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $dist 'index.html'))) { throw "frontend/dist is missing; build without -SkipFrontend" }

function Write-Lf([string] $Path) {
    $text = [IO.File]::ReadAllText($Path) -replace "`r`n", "`n"
    [IO.File]::WriteAllText($Path, $text, (New-Object Text.UTF8Encoding $false))
}

foreach ($rid in $Runtime) {
    $name = "mx-$Version-$rid"
    $out = Join-Path $releaseRoot $name
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Force (Join-Path $out 'app/scripts') | Out-Null

    foreach ($app in @(@{ Name = 'api'; Project = 'MX.Api' }, @{ Name = 'mailhost'; Project = 'MX.MailHost' }, @{ Name = 'workers'; Project = 'MX.Workers' })) {
        Write-Host "==> $rid $($app.Project)"
        dotnet publish (Join-Path $root "backend/$($app.Project)/$($app.Project).csproj") -c Release -r $rid --self-contained true `
            -p:InvariantGlobalization=false -p:DebugType=none -o (Join-Path $out "app/$($app.Name)") --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $($app.Project) ($rid)" }
        # Development settings never ship to servers.
        Remove-Item (Join-Path $out "app/$($app.Name)/appsettings.Development.json") -ErrorAction SilentlyContinue
    }
    Copy-Item -Recurse $dist (Join-Path $out 'app/api/wwwroot')
    Copy-Item (Join-Path $root 'install.ps1') $out
    Set-Content (Join-Path $out 'VERSION') $Version -NoNewline -Encoding ascii

    if ($rid -like 'linux-*') {
        Copy-Item (Join-Path $root 'deployment/linux/install.sh') $out
        Copy-Item (Join-Path $root 'deployment/linux/backup-all.sh'), (Join-Path $root 'deployment/scripts/backup.sh'), (Join-Path $root 'deployment/scripts/restore.sh') (Join-Path $out 'app/scripts')
        New-Item -ItemType Directory -Force (Join-Path $out 'systemd') | Out-Null
        Copy-Item (Join-Path $root 'deployment/linux/systemd/*') (Join-Path $out 'systemd')
        # Shell scripts and unit files must have LF line endings.
        Get-ChildItem $out -Recurse -Include *.sh, *.service, *.timer, *.path | ForEach-Object { Write-Lf $_.FullName }
    }

    Write-Host "==> $rid archive"
    if ($rid -like 'win-*') {
        $archive = Join-Path $releaseRoot "$name.zip"
        Remove-Item $archive -ErrorAction SilentlyContinue
        Compress-Archive -Path $out -DestinationPath $archive -CompressionLevel Optimal
    }
    else {
        $archive = Join-Path $releaseRoot "$name.tar.gz"
        tar -C $releaseRoot -czf $archive $name
        if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    }
    Remove-Item -Recurse -Force $out
    Write-Host "Release: $archive ($([math]::Round((Get-Item $archive).Length / 1MB, 1)) MB)"
}
