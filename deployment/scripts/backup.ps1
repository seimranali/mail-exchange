# Logical backup of the MX management database on Windows hosts.
# Usage: $env:PGPASSWORD = '...'; .\backup.ps1 -Destination D:\Backups\mx -DbHost db01 -User mx_backup
param(
    [Parameter(Mandatory)] [string] $Destination,
    [string] $DbHost = 'localhost',
    [int] $Port = 5432,
    [string] $User = 'mx_backup',
    [string] $Database = 'mx',
    [int] $RetentionDays = 30
)
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force $Destination | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$file = Join-Path $Destination "mx-$stamp.dump"

& pg_dump --format=custom --compress=9 --no-owner -h $DbHost -p $Port -U $User -d $Database -f $file
if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE" }
(Get-FileHash $file -Algorithm SHA256).Hash | Set-Content "$file.sha256" -Encoding ascii

& pg_restore --list $file | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Backup verification failed" }

Get-ChildItem $Destination -Filter 'mx-*.dump*' | Where-Object LastWriteTime -lt (Get-Date).AddDays(-$RetentionDays) | Remove-Item
Write-Output "Backup written: $file"
