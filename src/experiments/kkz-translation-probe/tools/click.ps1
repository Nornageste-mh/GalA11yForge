param([int]$CX = 0, [int]$CY = 0, [string]$Label = "click", [int]$WaitMs = 1200)
# task-48 / R27: synthetic click in *client* coordinates via mouse_event (task-47 proven).
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CK48 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  public struct POINT { public int X, Y; }
}
"@
$p = @(Get-Process SenrenBanka -ErrorAction SilentlyContinue) | Select-Object -First 1
if (-not $p) { "CLICK_SKIP GAME_NOT_RUNNING"; exit 0 }
$h = $p.MainWindowHandle
[void][CK48]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 150
$pt = New-Object CK48+POINT
$pt.X = $CX; $pt.Y = $CY
[void][CK48]::ClientToScreen($h, [ref]$pt)
[void][CK48]::SetCursorPos($pt.X, $pt.Y)
Start-Sleep -Milliseconds 100
[CK48]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[CK48]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds $WaitMs
"CLICK client=({0},{1}) screen=({2},{3}) label={4} alive={5}" -f $CX, $CY, $pt.X, $pt.Y, $Label, (-not $p.HasExited)
