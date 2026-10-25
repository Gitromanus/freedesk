param(
    [ValidateSet('run', 'put', 'get', 'ls')][string]$Action = 'run',
    [string]$Command = '',
    [string]$Local = '',
    [string]$Remote = '',
    [string]$SshHost = 'netesnl9.beget.tech',
    [string]$SshUser = 'netesnl9_fd'
)

$ErrorActionPreference = 'Stop'

$passFile = Join-Path $env:TEMP 'fd_ssh_pass.txt'
if (-not (Test-Path $passFile)) { throw "password file not found: $passFile" }

$env:SSH_ASKPASS = Join-Path $PSScriptRoot 'askpass.exe'
$env:SSH_ASKPASS_REQUIRE = 'force'
$env:DISPLAY = ':0'

$sshOpts = @(
    '-o', 'StrictHostKeyChecking=no',
    '-o', "UserKnownHostsFile=$PSScriptRoot\known_hosts",
    '-o', 'PreferredAuthentications=password',
    '-o', 'PubkeyAuthentication=no',
    '-o', 'NumberOfPasswordPrompts=1'
)

$target = "$SshUser@$SshHost"

function Quote-Arg([string]$value) {
    if ($value -ne '' -and $value -notmatch '[\s"]') { return $value }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $backslashes = 0

    foreach ($ch in $value.ToCharArray()) {
        if ($ch -eq '\') { $backslashes++; continue }
        if ($ch -eq '"') {
            [void]$sb.Append('\' * ($backslashes * 2 + 1)).Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) { [void]$sb.Append('\' * $backslashes); $backslashes = 0 }
        [void]$sb.Append($ch)
    }

    if ($backslashes -gt 0) { [void]$sb.Append('\' * ($backslashes * 2)) }
    [void]$sb.Append('"')

    return $sb.ToString()
}

function Invoke-Native([string]$exe, [string[]]$argv) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = (($argv | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()

    $stdout = $outTask.Result
    $stderr = $errTask.Result

    if ($stdout) { [Console]::Out.Write($stdout) }

    if ($stderr) {
        $meaningful = $stderr -split "`r?`n" | Where-Object {
            $_ -and $_ -notmatch '^Welcome to LTD BeGet' -and $_ -notmatch '^Warning: Permanently added'
        }
        if ($proc.ExitCode -ne 0 -or $meaningful) {
            [Console]::Error.Write((($meaningful -join "`n") + "`n"))
        }
    }

    return $proc.ExitCode
}

switch ($Action) {
    'run' {
        exit (Invoke-Native 'ssh' ($sshOpts + $target + $Command))
    }

    'put' {
        if (-not (Test-Path $Local)) { throw "local path not found: $Local" }
        $code = Invoke-Native 'scp' (@('-r', '-q') + $sshOpts + $Local + "${target}:$Remote")
        if ($code -eq 0) { Write-Output "put $Local -> $Remote" }
        exit $code
    }

    'get' {
        $code = Invoke-Native 'scp' (@('-q') + $sshOpts + "${target}:$Remote" + $Local)
        if ($code -eq 0) { Write-Output "got $Remote -> $Local" }
        exit $code
    }

    'ls' {
        exit (Invoke-Native 'ssh' ($sshOpts + $target + "ls -la '$Remote'"))
    }
}
