param(
    [switch]$SkipDb,
    [switch]$WithTools
)

# Раскладка на этом хостинге: домашний каталог SSH-пользователя = каталог сайта.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$ssh = Join-Path $PSScriptRoot 'ssh.ps1'
$site = '/home/n/netesnl9/fd.cherubino74.ru'

function Send([string]$local, [string]$remote) {
    & powershell -ExecutionPolicy Bypass -File $ssh -Action put -Local $local -Remote $remote
    if ($LASTEXITCODE -ne 0) { throw "upload failed: $local" }
}

Write-Host 'uploading public_html ...'
Send (Join-Path $root 'server\public\index.php') "$site/public_html/index.php"
Send (Join-Path $root 'server\public\.htaccess') "$site/public_html/.htaccess"
Send (Join-Path $root 'server\public\test.html') "$site/public_html/test.html"
Send (Join-Path $root 'server\public\diag.php') "$site/public_html/diag.php"

Write-Host 'uploading app code ...'
Send (Join-Path $root 'server\bootstrap.php') "$site/bootstrap.php"
Send (Join-Path $root 'server\src') "$site/"
Send (Join-Path $root 'server\scripts') "$site/"
Send (Join-Path $root 'server\migrations') "$site/"

if ($WithTools) {
    Write-Host 'uploading migrations runner ...'
    Send (Join-Path $root 'tools\migrate.php') "$site/_migrate.php"
}

if (-not $SkipDb) {
    Write-Host 'checking health ...'
    $health = Invoke-RestMethod -Uri 'https://fd.cherubino74.ru/api/v1/health' -TimeoutSec 30
    Write-Host ("  db={0} time={1}" -f $health.db, $health.server_time)
}

Write-Host 'done'
