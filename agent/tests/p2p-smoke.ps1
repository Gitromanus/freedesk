param(
    [int]$Seconds = 25,
    [string]$HostArgs = '',
    [ValidateSet('h264', 'mjpeg')]
    [string]$Codec = 'h264',
    [string]$Password = '',
    [string]$AgentExe = "$PSScriptRoot\..\FreeDesk.Agent\bin\Debug\net9.0-windows\freedesk-agent.exe"
)

$ErrorActionPreference = 'Stop'

function Read-Log([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    try {
        $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        $reader = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose(); $fs.Dispose() }
    } catch {
        return ''
    }
}

if (-not (Test-Path $AgentExe)) { throw "agent not built: $AgentExe" }

# Пароль хоста — в его профиле: с ним проверяем unattended-доступ, без него — обычное подтверждение.
if ($Password) {
    & $AgentExe --profile host --set-password $Password | Out-Null
} else {
    & $AgentExe --profile host --clear-password | Out-Null
}

$hostLog = Join-Path $env:TEMP 'fd-host.log'
$viewLog = Join-Path $env:TEMP 'fd-viewer.log'
Remove-Item $hostLog, $viewLog, "$hostLog.err", "$viewLog.err" -ErrorAction SilentlyContinue

Write-Host 'starting host agent ...'
$hostArgList = @('--profile', 'host', '--name', 'host-pc', '--auto-accept', '--codec', $Codec)
if ($HostArgs) { $hostArgList += $HostArgs.Split(' ') }
$hostAgent = Start-Process -FilePath $AgentExe `
    -ArgumentList $hostArgList `
    -RedirectStandardOutput $hostLog -RedirectStandardError "$hostLog.err" `
    -PassThru -NoNewWindow

try {
    $hostId = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if ((Read-Log $hostLog) -match 'ID:\s+(\d{3})\s+(\d{3})\s+(\d{3})') {
            $hostId = $Matches[1] + $Matches[2] + $Matches[3]
            break
        }
        if ($hostAgent.HasExited) { throw "host exited: $(Read-Log "$hostLog.err")" }
    }

    if (-not $hostId) { throw "host did not report ID: $(Read-Log $hostLog)" }
    Write-Host ("  host id: {0}" -f $hostId)

    Write-Host 'starting viewer ...'
    $viewerArgs = @('--profile', 'viewer', '--name', 'viewer-pc', '--view', $hostId, '--codec', $Codec)
    if ($Password) { $viewerArgs += @('--password', $Password) }

    $viewer = Start-Process -FilePath $AgentExe `
        -ArgumentList $viewerArgs `
        -RedirectStandardOutput $viewLog -RedirectStandardError "$viewLog.err" `
        -PassThru -NoNewWindow

    Write-Host ("  watching for {0}s ..." -f $Seconds)
    Start-Sleep -Seconds $Seconds

    if (-not $viewer.HasExited) { Stop-Process -Id $viewer.Id -Force }
}
finally {
    if (-not $hostAgent.HasExited) { Stop-Process -Id $hostAgent.Id -Force }
}

Write-Host ''
Write-Host '--- host log ---'
Read-Log $hostLog
Write-Host '--- viewer log ---'
Read-Log $viewLog
Write-Host '--- viewer stderr ---'
Read-Log "$viewLog.err"
