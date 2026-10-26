$ErrorActionPreference = 'Stop'

$env:APPDATA = 'C:\Users\User\AppData\Roaming'
$env:LOCALAPPDATA = 'C:\Users\User\AppData\Local'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'

$exe = 'C:\Users\User\Desktop\freedesk\agent\publish\FreeDesk.exe'
$appData = 'C:\Users\User\AppData\Roaming\FreeDesk'
$temp = $env:TEMP

# старые экземпляры не должны мешать
Get-Process FreeDesk, freedesk-agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# ── два хоста с паролями ──
& $exe --profile hA --set-password alpha-111 | Out-Null
& $exe --profile hB --set-password beta-222 | Out-Null

$outA = Join-Path $temp 'fd-hostA.out'
$outB = Join-Path $temp 'fd-hostB.out'
Remove-Item $outA, $outB -ErrorAction SilentlyContinue

$hostA = Start-Process -FilePath $exe `
    -ArgumentList '--profile', 'hA', '--name', 'pc-alpha', '--codec', 'h264', '--fps', '10' `
    -PassThru -RedirectStandardOutput $outA -RedirectStandardError (Join-Path $temp 'fd-hostA.err')

$hostB = Start-Process -FilePath $exe `
    -ArgumentList '--profile', 'hB', '--name', 'pc-beta', '--codec', 'h264', '--fps', '10' `
    -PassThru -RedirectStandardOutput $outB -RedirectStandardError (Join-Path $temp 'fd-hostB.err')

Start-Sleep -Seconds 10

$idA = ([regex]::Match((Get-Content $outA -Encoding UTF8 -Raw), '\d{3} \d{3} \d{3}')).Value.Replace(' ', '')
$idB = ([regex]::Match((Get-Content $outB -Encoding UTF8 -Raw), '\d{3} \d{3} \d{3}')).Value.Replace(' ', '')

if (-not $idA -or -not $idB) { throw "хосты не зарегистрировались: '$idA' / '$idB'" }

# ── пароли хостов кладём зрителю как сохранённые ──
$cfgA = Get-Content (Join-Path $appData 'hA.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$cfgB = Get-Content (Join-Path $appData 'hB.json') -Encoding UTF8 -Raw | ConvertFrom-Json

$viewPath = Join-Path $appData 'demo2.json'
Remove-Item $viewPath -ErrorAction SilentlyContinue

$creds = @{}
$creds[$idA] = [pscustomobject]@{ Salt = $cfgA.UnattendedSalt; Iterations = $cfgA.UnattendedIterations; Hash = $cfgA.UnattendedHash }
$creds[$idB] = [pscustomobject]@{ Salt = $cfgB.UnattendedSalt; Iterations = $cfgB.UnattendedIterations; Hash = $cfgB.UnattendedHash }

@{
    Name = 'demo2-pc'
    SavedCredentials = $creds
} | ConvertTo-Json -Depth 6 | Set-Content $viewPath -Encoding UTF8

# ── клиент сразу с двумя подключениями ──
$viewer = Start-Process -FilePath $exe `
    -ArgumentList '--gui', '--profile', 'demo2', '--connect', $idA, '--connect', $idB `
    -PassThru

Start-Sleep -Seconds 26

# окно на передний план
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class Fg {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  public static IntPtr Find(uint pid) {
    IntPtr best = IntPtr.Zero; int bestW = 0;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { best = h; }
      return true;
    }, IntPtr.Zero);
    return best;
  }
  public static void Raise(IntPtr h) {
    SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002);
    SetWindowPos(h, (IntPtr)(-2), 0, 0, 0, 0, 0x0001 | 0x0002);
    SetForegroundWindow(h);
  }
}
'@

$h = [Fg]::Find($viewer.Id)
[Fg]::Raise($h)

Write-Output "хосты: pc-alpha = $idA, pc-beta = $idB"
Write-Output "клиент pid $($viewer.Id)"

Start-Sleep -Seconds 2
Select-String -Path (Join-Path $appData 'freedesk.log') -Pattern 'вкладка активна' |
    Select-Object -Last 4 | ForEach-Object { $_.Line }
