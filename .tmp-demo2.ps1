param([string]$Exe = 'C:\Users\User\Desktop\freedesk\agent\publish\FreeDesk.exe')

$ErrorActionPreference = 'Stop'
$env:APPDATA = 'C:\Users\User\AppData\Roaming'
$env:LOCALAPPDATA = 'C:\Users\User\AppData\Local'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'

Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class Fr2 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; public int W { get { return R - L; } } public int H { get { return B - T; } } }
  public static IntPtr FindBig(uint pid) {
    IntPtr best = IntPtr.Zero; int area = 0;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        RECT r; GetWindowRect(h, out r); int a = (r.R - r.L) * (r.B - r.T);
        if (a > area) { area = a; best = h; }
      }
      return true;
    }, IntPtr.Zero);
    return best;
  }
  public static void Raise(IntPtr h) {
    SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002);
    SetWindowPos(h, (IntPtr)(-2), 0, 0, 0, 0, 0x0001 | 0x0002);
    SetForegroundWindow(h);
  }
  public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
}
'@

[Fr2]::SetProcessDPIAware() | Out-Null

$appData = 'C:\Users\User\AppData\Roaming\FreeDesk'
$logPath = Join-Path $appData 'freedesk.log'
$temp = $env:TEMP

Get-Process FreeDesk, freedesk-agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

& $Exe --profile hA --set-password alpha-111 | Out-Null
& $Exe --profile hB --set-password beta-222 | Out-Null

$outA = Join-Path $temp 'fd-hA.out'
$outB = Join-Path $temp 'fd-hB.out'
Remove-Item $outA, $outB -ErrorAction SilentlyContinue

Start-Process -FilePath $Exe -ArgumentList '--profile', 'hA', '--name', 'pc-alpha', '--codec', 'h264', '--fps', '10' `
    -RedirectStandardOutput $outA -RedirectStandardError (Join-Path $temp 'fd-hA.err') | Out-Null
Start-Process -FilePath $Exe -ArgumentList '--profile', 'hB', '--name', 'pc-beta', '--codec', 'h264', '--fps', '10' `
    -RedirectStandardOutput $outB -RedirectStandardError (Join-Path $temp 'fd-hB.err') | Out-Null

Start-Sleep -Seconds 10

$idA = ([regex]::Match((Get-Content $outA -Encoding UTF8 -Raw), '\d{3} \d{3} \d{3}')).Value.Replace(' ', '')
$idB = ([regex]::Match((Get-Content $outB -Encoding UTF8 -Raw), '\d{3} \d{3} \d{3}')).Value.Replace(' ', '')

if (-not $idA -or -not $idB) { throw "хосты не встали: '$idA' / '$idB'" }

$cfgA = Get-Content (Join-Path $appData 'hA.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$cfgB = Get-Content (Join-Path $appData 'hB.json') -Encoding UTF8 -Raw | ConvertFrom-Json

$viewPath = Join-Path $appData 'demo3.json'
Remove-Item $viewPath -ErrorAction SilentlyContinue

$creds = @{}
$creds[$idA] = [pscustomobject]@{ Salt = $cfgA.UnattendedSalt; Iterations = $cfgA.UnattendedIterations; Hash = $cfgA.UnattendedHash }
$creds[$idB] = [pscustomobject]@{ Salt = $cfgB.UnattendedSalt; Iterations = $cfgB.UnattendedIterations; Hash = $cfgB.UnattendedHash }

@{ Name = 'demo3-pc'; SavedCredentials = $creds } | ConvertTo-Json -Depth 6 | Set-Content $viewPath -Encoding UTF8

$viewer = Start-Process -FilePath $Exe -ArgumentList '--gui', '--profile', 'demo3', '--connect', $idA, '--connect', $idB -PassThru
Start-Sleep -Seconds 26

$handle = [Fr2]::FindBig($viewer.Id)
$r = [Fr2]::Rect($handle)
[Fr2]::Raise($handle)
Start-Sleep -Seconds 1

$bmp = New-Object System.Drawing.Bitmap $r.W, $r.H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save((Join-Path $PWD '.tmp-ready.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Output "клиент запущен: pid $($viewer.Id), окно $($r.L),$($r.T) $($r.W)x$($r.H)"
Write-Output "pc-alpha = $idA, pc-beta = $idB"
Get-Content $logPath -Encoding UTF8 -Tail 5 | Where-Object { $_ -match 'вкладка|панель' }
