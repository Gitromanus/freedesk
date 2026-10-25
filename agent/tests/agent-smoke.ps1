param(
    [string]$Base = 'https://fd.cherubino74.ru',
    [string]$AgentExe = "$PSScriptRoot\..\FreeDesk.Agent\bin\Debug\net9.0-windows\freedesk-agent.exe"
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body = $null, [string]$Token = '')

    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }

    $params = @{
        Method          = $Method
        Uri             = ($Base.TrimEnd('/') + $Path)
        Headers         = $headers
        UseBasicParsing = $true
        TimeoutSec      = 30
    }

    if ($null -ne $Body) {
        $params['ContentType'] = 'application/json; charset=utf-8'
        $params['Body'] = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8 -Compress))
    }

    try {
        return Invoke-RestMethod @params
    } catch {
        $resp = $_.Exception.Response
        if ($null -ne $resp) {
            $text = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd()
            throw "HTTP $([int]$resp.StatusCode) $Path -> $text"
        }
        throw
    }
}

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

$logFile = Join-Path $env:TEMP 'freedesk-agent-smoke.log'
Remove-Item $logFile -ErrorAction SilentlyContinue

Write-Host "starting agent ..."
$agent = Start-Process -FilePath $AgentExe -ArgumentList '--auto-accept' `
    -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err" `
    -PassThru -NoNewWindow

try {
    # Ждём, пока агент напечатает свой ID.
    $agentId = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        $log = Read-Log $logFile
        if ($log -match 'ID:\s+(\d{3})\s+(\d{3})\s+(\d{3})') {
            $agentId = $Matches[1] + $Matches[2] + $Matches[3]
            break
        }
        if ($agent.HasExited) { throw "agent exited early:`n$(Read-Log $logFile)`n$(Read-Log "$logFile.err")" }
    }

    if (-not $agentId) { throw "agent did not report an ID:`n$(Read-Log $logFile)" }
    Write-Host ("  [ok] agent online: {0}" -f $agentId)

    $peer = Invoke-Api -Method POST -Path '/api/v1/device/register' -Body @{ name = 'smoke-peer'; os = 'windows' }
    Write-Host ("  [ok] peer registered: {0}" -f $peer.public_id)

    Invoke-Api -Method POST -Path '/api/v1/presence' -Token $peer.device_token | Out-Null
    Invoke-Api -Method POST -Path '/api/v1/presence' -Token (Get-Content "$env:APPDATA\FreeDesk\agent.json" -Raw | ConvertFrom-Json).deviceToken | Out-Null

    $req = Invoke-Api -Method POST -Path '/api/v1/session/request' -Token $peer.device_token -Body @{ target_id = $agentId }
    $sid = $req.session_id
    Write-Host ("  [ok] request sent: {0}" -f $sid)

    # Агент должен принять сам (--auto-accept).
    $state = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        $poll = Invoke-Api -Method GET -Path "/api/v1/session/poll?session_id=$sid&since=0" -Token $peer.device_token
        $state = $poll.session.state
        if ($state -eq 'active') { break }
    }

    if ($state -ne 'active') { throw "agent did not accept, state=$state`n$(Read-Log $logFile)" }
    Write-Host '  [ok] agent accepted the session'

    Invoke-Api -Method POST -Path '/api/v1/session/signal' -Token $peer.device_token -Body @{
        session_id = $sid
        type       = 'offer'
        payload    = @{ type = 'offer'; sdp = 'smoke-test-offer' }
    } | Out-Null

    $answer = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        $poll = Invoke-Api -Method GET -Path "/api/v1/session/poll?session_id=$sid&since=0" -Token $peer.device_token
        $answer = @($poll.messages | Where-Object { $_.type -eq 'answer' })
        if ($answer.Count -ge 1) { break }
    }

    if ($answer.Count -lt 1) { throw "agent did not answer the offer`n$(Read-Log $logFile)" }
    Write-Host ("  [ok] agent answered: sdp={0}" -f $answer[0].payload.sdp)

    Invoke-Api -Method POST -Path '/api/v1/session/answer' -Token $peer.device_token -Body @{
        session_id = $sid; action = 'end'
    } | Out-Null

    Write-Host ''
    Write-Host 'PASS — агент регистрируется, принимает заявку и отвечает на offer.'
    Write-Host ("  agent: {0}" -f $agentId)
}
finally {
    if (-not $agent.HasExited) { Stop-Process -Id $agent.Id -Force }
}
