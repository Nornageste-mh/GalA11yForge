param([double]$FX = 0.5, [double]$FY = 0.5, [string]$Label = "clickf", [int]$WaitMs = 900, [switch]$NoFocus)
# task-48 / R27: click by fraction of the client area, with a *verified* foreground activation.
# Run1-3 lesson: SetForegroundWindow alone silently fails from a background process, so the
# synthetic click lands on whatever is focused instead of the game.
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class CKF49 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  public struct POINT { public int X, Y; }
  public struct RECT { public int Left, Top, Right, Bottom; }

  public static IntPtr FindByPid(uint pid) {
    IntPtr best = IntPtr.Zero; long bestArea = 0;
    EnumWindows(delegate(IntPtr h, IntPtr lp) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        RECT r; if (GetClientRect(h, out r)) {
          long a = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
          if (a > bestArea) { bestArea = a; best = h; }
        }
      }
      return true;
    }, IntPtr.Zero);
    return best;
  }
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);

  public static bool ForceForeground(IntPtr h) {
    if (GetForegroundWindow() == h) return true;
    ShowWindow(h, 5);
    BringWindowToTop(h);
    SetForegroundWindow(h);
    if (GetForegroundWindow() == h) return true;
    uint fgPid; uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
    uint myThread = GetCurrentThreadId();
    if (fgThread != myThread) {
      AttachThreadInput(myThread, fgThread, true);
      SetForegroundWindow(h);
      BringWindowToTop(h);
      AttachThreadInput(myThread, fgThread, false);
    }
    return GetForegroundWindow() == h;
  }
}
"@
$p = @(Get-Process SenrenBanka -ErrorAction SilentlyContinue) | Select-Object -First 1
if (-not $p) { "CLICKF_SKIP GAME_NOT_RUNNING"; exit 0 }
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { $h = [CKF49]::FindByPid([uint32]$p.Id) }
if ($h -eq [IntPtr]::Zero) { "CLICKF_SKIP NO_HWND"; exit 0 }

if (-not $NoFocus) {
  $ok = [CKF49]::ForceForeground($h)
  Start-Sleep -Milliseconds 180
  $fg = [CKF49]::GetForegroundWindow()
  "FOCUS ok=$ok fg=" + $fg.ToInt64() + " target=" + $h.ToInt64() + " match=" + ($fg -eq $h)
}

$r = New-Object CKF49+RECT
[void][CKF49]::GetClientRect($h, [ref]$r)
$cw = $r.Right - $r.Left; $ch = $r.Bottom - $r.Top
if ($cw -le 0 -or $ch -le 0) { "CLICKF_SKIP BAD_CLIENT"; exit 0 }
$cx = [int][math]::Round($FX * $cw)
$cy = [int][math]::Round($FY * $ch)
$pt = New-Object CKF49+POINT
$pt.X = $cx; $pt.Y = $cy
[void][CKF49]::ClientToScreen($h, [ref]$pt)
[void][CKF49]::SetCursorPos($pt.X, $pt.Y)
Start-Sleep -Milliseconds 80
[CKF49]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 55
[CKF49]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds $WaitMs
"CLICKF f=({0},{1}) client=({2},{3}) of {4}x{5} label={6} alive={7}" -f $FX, $FY, $cx, $cy, $cw, $ch, $Label, (-not $p.HasExited)
