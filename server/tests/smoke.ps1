param(
    [string]$Base = 'https://fd.cherubino74.ru',
    [switch]$SkipTlsCheck
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ($SkipTlsCheck) {
    [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
}

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        $Body = $null,
        [string]$Token = ''
    )

    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }

    $params = @{
        Method      = $Method
        Uri         = ($Base.TrimEnd('/') + $Path)
        Headers     = $headers
        UseBasicParsing = $true
        TimeoutSec  = 30
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
            $stream = $resp.GetResponseStream()
            $text = (New-Object IO.StreamReader($stream)).ReadToEnd()
            throw "HTTP $([int]$resp.StatusCode) $Path -> $text"
        }
        throw
    }
}

function Step($label, $value) {
    Write-Host ("  [ok] {0}: {1}" -f $label, $value)
}

Write-Host "FreeDesk signaling smoke test -> $Base"

$health = Invoke-Api -Method GET -Path '/api/v1/health'
Step 'health' ("db={0} time={1}" -f $health.db, $health.server_time)

$a = Invoke-Api -Method POST -Path '/api/v1/device/register' -Body @{
    name = 'smoke-A'; os = 'windows'
}
$b = Invoke-Api -Method POST -Path '/api/v1/device/register' -Body @{
    name = 'smoke-B'; os = 'windows'
}
Step 'register A' $a.public_id
Step 'register B' $b.public_id

Invoke-Api -Method POST -Path '/api/v1/presence' -Token $a.device_token | Out-Null
Invoke-Api -Method POST -Path '/api/v1/presence' -Token $b.device_token | Out-Null
Step 'presence' 'both devices online'

$resolved = Invoke-Api -Method GET -Path "/api/v1/resolve/$($b.public_id)" -Token $a.device_token
Step 'resolve' ("name={0} online={1}" -f $resolved.name, $resolved.online)

$req = Invoke-Api -Method POST -Path '/api/v1/session/request' -Token $a.device_token -Body @{
    target_id = $b.public_id
}
$sid = $req.session_id
Step 'request' $sid

$inbox = Invoke-Api -Method GET -Path '/api/v1/session/incoming' -Token $b.device_token
$found = @($inbox.incoming | Where-Object { $_.session_id -eq $sid })
if ($found.Count -ne 1) { throw 'B did not see the incoming request' }
Step 'incoming' ("from={0} name={1}" -f $found[0].from.public_id, $found[0].from.name)

$accepted = Invoke-Api -Method POST -Path '/api/v1/session/answer' -Token $b.device_token -Body @{
    session_id = $sid; action = 'accept'
}
Step 'accept' $accepted.state

$state = Invoke-Api -Method GET -Path "/api/v1/session/poll?session_id=$sid&since=0" -Token $a.device_token
Step 'poll A' ("state={0} role={1}" -f $state.session.state, $state.session.role)

Invoke-Api -Method POST -Path '/api/v1/session/signal' -Token $a.device_token -Body @{
    session_id = $sid
    type       = 'offer'
    payload    = @{ sdp = 'v=0 fake-offer for smoke test'; type = 'offer' }
} | Out-Null

$pollB = Invoke-Api -Method GET -Path "/api/v1/session/poll?session_id=$sid&since=0" -Token $b.device_token
$offer = @($pollB.messages | Where-Object { $_.type -eq 'offer' })
if ($offer.Count -ne 1) { throw 'B did not receive the offer signal' }
Step 'signal offer' ("cursor={0} payload.type={1}" -f $pollB.cursor, $offer[0].payload.type)

# ── резервный канал: кадры и ввод мимо NAT, через сервер ──

function Invoke-Raw {
    param([string]$Method, [string]$Path, [byte[]]$Bytes, [string]$Token = '')
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    return Invoke-WebRequest -Method $Method -Uri ($Base.TrimEnd('/') + $Path) -Headers $headers `
        -Body $Bytes -ContentType 'image/jpeg' -UseBasicParsing -TimeoutSec 30
}

# Сервер тело кадра не декодирует — важен только размер (64…200 КБ).
$frameBytes = New-Object byte[] 100
(New-Object Random).NextBytes($frameBytes)

$posted = Invoke-Raw -Method POST -Path "/api/v1/relay/frame?session_id=$sid" -Bytes $frameBytes -Token $a.device_token
Step 'relay frame post' $posted.StatusCode

$img = Invoke-Raw -Method GET -Path "/api/v1/relay/image?session_id=$sid&since=0&wait=0" -Token $b.device_token
if ($img.Headers['Content-Type'] -notlike 'image/jpeg*') { throw 'relay image is not jpeg' }
if (@($img.Headers['X-Frame-Seq'])[0] -ne '1') { throw 'relay image seq mismatch' }
if ($img.RawContentLength -ne 100) { throw 'relay image body truncated' }
Step 'relay image' ("seq={0} bytes={1}" -f @($img.Headers['X-Frame-Seq'])[0], $img.RawContentLength)

$unchanged = Invoke-Api -Method GET -Path "/api/v1/relay/image?session_id=$sid&since=1&wait=0" -Token $b.device_token
if ($unchanged.changed) { throw 'relay frame should be unchanged' }
Step 'relay image unchanged' ("seq={0}" -f $unchanged.seq)

Invoke-Api -Method POST -Path "/api/v1/relay/control?session_id=$sid" -Token $b.device_token -Body @{
    event = @{ t = 'smoke'; x = 1; y = 2 }
} | Out-Null

$ctl = Invoke-Api -Method GET -Path "/api/v1/relay/control?session_id=$sid&offset=0&wait=0" -Token $a.device_token
if (@($ctl.events).Count -ne 1) { throw 'host did not receive the relay control event' }
Step 'relay control' ("events={0} offset={1}" -f @($ctl.events).Count, $ctl.offset)

Invoke-Api -Method POST -Path '/api/v1/session/answer' -Token $a.device_token -Body @{
    session_id = $sid; action = 'end'
} | Out-Null

$ended = Invoke-Api -Method GET -Path "/api/v1/session/poll?session_id=$sid&since=$($pollB.cursor)" -Token $b.device_token
Step 'end' $ended.session.state

Write-Host ''
Write-Host 'PASS — signaling работает end-to-end.'
Write-Host ("  A: {0}" -f $a.public_id)
Write-Host ("  B: {0}" -f $b.public_id)
