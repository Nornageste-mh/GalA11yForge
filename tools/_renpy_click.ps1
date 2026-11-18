param([int]$TargetPid = 0, [int]$X = 0, [int]$Y = 0, [int]$Times = 1, [switch]$Show)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class RC {
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@
$p = if ($TargetPid -ne 0) { Get-Process -Id $TargetPid -ErrorAction Stop } else { Get-Process | Where-Object { $_.ProcessName -eq 'DDLC' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { Write-Error "no main window for pid=$($p.Id)"; exit 2 }
if ($Show) {
  [void][RC]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
  [void][RC]::SetForegroundWindow($h)
  Start-Sleep -Milliseconds 400
}
$pt = New-Object RC+POINT; $pt.X = 0; $pt.Y = 0
[void][RC]::ClientToScreen($h, [ref]$pt)
$sx = $pt.X + $X; $sy = $pt.Y + $Y
for ($i = 1; $i -le $Times; $i++) {
  [void][RC]::SetCursorPos($sx, $sy)
  Start-Sleep -Milliseconds 120
  [RC]::mouse_event(0x0002 -bor 0x0001, 0, 0, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [RC]::mouse_event(0x0004 -bor 0x0001, 0, 0, 0, [IntPtr]::Zero)
  Write-Output ("click#$i pid=$($p.Id) hwnd=$h client=($X,$Y) screen=($sx,$sy)")
  if ($i -lt $Times) { Start-Sleep -Milliseconds 700 }
}
