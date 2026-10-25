param(
    [string]$AgentExe = "$PSScriptRoot\..\FreeDesk.Agent\bin\Debug\net9.0-windows\freedesk-agent.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

function Read-Log([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    try {
        $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        $reader = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose(); $fs.Dispose() }
    } catch { return '' }
}

$hostLog = Join-Path $env:TEMP 'fd-input-host.log'
Remove-Item $hostLog, "$hostLog.err" -ErrorAction SilentlyContinue

$hostAgent = Start-Process -FilePath $AgentExe `
    -ArgumentList '--profile', 'host', '--name', 'host-pc', '--auto-accept' `
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
    }
    if (-not $hostId) { throw "host did not report ID" }

    $before = [System.Windows.Forms.Cursor]::Position
    Write-Host ("cursor before: {0},{1}" -f $before.X, $before.Y)

    & $AgentExe --profile viewer --name viewer-pc --input-test $hostId 2>&1 | ForEach-Object { Write-Host "  $_" }

    Start-Sleep -Milliseconds 500
    $after = [System.Windows.Forms.Cursor]::Position
    Write-Host ("cursor after:  {0},{1}" -f $after.X, $after.Y)

    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Write-Host ("expected:      ~{0},{1}" -f ([int]($screen.Width * 0.25)), ([int]($screen.Height * 0.25)))
}
finally {
    if (-not $hostAgent.HasExited) { Stop-Process -Id $hostAgent.Id -Force }
}

Write-Host ''
Write-Host '--- host log ---'
Read-Log $hostLog
