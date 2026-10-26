param([string]$Out = '.tmp-side.png')

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class Pw {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  public struct RECT { public int L, T, R, B; public int W { get { return R - L; } } public int H { get { return B - T; } } }
  public static IntPtr FindBig(uint pid) {
    IntPtr best = IntPtr.Zero; int area = 0;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { RECT r; GetWindowRect(h, out r); int a = (r.R-r.L)*(r.B-r.T); if (a > area) { area = a; best = h; } }
      return true;
    }, IntPtr.Zero);
    return best;
  }
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(150);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(80);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(400);
  }
  public static void Raise(IntPtr h) { SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002); }
  public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
}
'@

[Pw]::SetProcessDPIAware() | Out-Null

$pids = (Get-Process FreeDesk -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
if (-not $pids) { throw 'FreeDesk не запущен' }

$h = [IntPtr]::Zero
foreach ($p in $pids) { $cand = [Pw]::FindBig([uint32]$p); if ($cand -ne [IntPtr]::Zero) { $h = $cand; break } }
if ($h -eq [IntPtr]::Zero) { throw 'окно FreeDesk не найдено' }
[Pw]::Raise($h)
Start-Sleep -Milliseconds 600

$r = [Pw]::Rect($h)
[Pw]::Click(($r.L + 28), ($r.T + 62))
Start-Sleep -Seconds 2

$bmp = New-Object System.Drawing.Bitmap $r.W, $r.H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Pw]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc)
$bmp.Save((Join-Path $PWD $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Output "снимок: $Out ($($r.W)x$($r.H))"
