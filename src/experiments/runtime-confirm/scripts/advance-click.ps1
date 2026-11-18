param(
  [string]$ProcName = '5omeday',
  [int]$Count = 130,
  [int]$IntervalMs = 2200,
  [int]$X = 1600,
  [int]$Y = 980,
  [string]$ShotDir = 'D:\DSHWorkSpace\research\19-re-runtime-confirm\evidence\logs',
  [string]$Tag = 'click'
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class ClkB {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  public static IntPtr FindUnity(uint want) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != want) return true;
      var c = new StringBuilder(256); GetClassNameW(h, c, 256);
      if (c.ToString() == "UnityWndClass") { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

$p = Get-Process -Name $ProcName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "GAME NOT RUNNING"; exit 1 }
$h = [ClkB]::FindUnity([uint32]$p.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output "NO UNITY WINDOW"; exit 1 }
Write-Output ("start pid={0} hwnd={1} count={2} interval={3}ms click=({4},{5})" -f $p.Id, $h, $Count, $IntervalMs, $X, $Y)
[ClkB]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
[ClkB]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 600

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$shotEvery = 20
$focusLoss = 0
for ($i = 1; $i -le $Count; $i++) {
  if ($p.HasExited) { Write-Output ("game exited at i={0}" -f $i); break }
  if ($i % 10 -eq 1) { [ClkB]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null }
  [ClkB]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 120
  if ([ClkB]::GetForegroundWindow() -ne $h) { $focusLoss++ }
  [ClkB]::SetCursorPos($X, $Y) | Out-Null
  Start-Sleep -Milliseconds 60
  [ClkB]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 90
  [ClkB]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds ($IntervalMs - 270)
  if ($i % $shotEvery -eq 0) {
    $bmp = New-Object System.Drawing.Bitmap($screen.Width, $screen.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($screen.X, $screen.Y, 0, 0, $bmp.Size)
    $g.Dispose()
    $path = Join-Path $ShotDir ("{0}-{1:d3}.png" -f $Tag, $i)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("shot {0}" -f $path)
  }
  if ($i % 10 -eq 0) { Write-Output ("i={0} alive={1} focusLoss={2} {3:HH:mm:ss}" -f $i, (-not $p.HasExited), $focusLoss, (Get-Date)) }
}
Write-Output "done"
