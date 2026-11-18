param([string]$Key = "SPACE", [int]$WaitMs = 700)
# task-48 / R27: keyboard injection with the same verified-focus handshake as clickf.ps1.
# KAG maps "advance" to left-click AND space/enter, and key events go through a different
# path than synthetic mouse events, so this is the fallback when clicks are swallowed.
$ErrorActionPreference = "Continue"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class KB49 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  public static IntPtr FindByPid(uint pid) {
    IntPtr best = IntPtr.Zero; long bestArea = 0;
    EnumWindows(delegate(IntPtr h, IntPtr lp) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { RECT r;
        if (GetClientRect(h, out r)) { long a = (long)(r.Right-r.Left)*(r.Bottom-r.Top); if (a > bestArea) { bestArea = a; best = h; } } }
      return true; }, IntPtr.Zero);
    return best;
  }
  public static bool ForceForeground(IntPtr h) {
    if (GetForegroundWindow() == h) return true;
    ShowWindow(h, 5); BringWindowToTop(h); SetForegroundWindow(h);
    if (GetForegroundWindow() == h) return true;
    uint fgPid; uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
    uint my = GetCurrentThreadId();
    if (fgThread != my) { AttachThreadInput(my, fgThread, true); SetForegroundWindow(h); BringWindowToTop(h); AttachThreadInput(my, fgThread, false); }
    return GetForegroundWindow() == h;
  }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
  [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint n, INPUT[] p, int cb);
  public static uint Key(ushort vk) {
    INPUT[] a = new INPUT[2];
    a[0].type = 1; a[0].u.ki.wVk = vk; a[0].u.ki.dwFlags = 0;
    a[1].type = 1; a[1].u.ki.wVk = vk; a[1].u.ki.dwFlags = 2;
    return SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
  }
}
"@
$p = @(Get-Process SenrenBanka -EA SilentlyContinue) | Select-Object -First 1
if (-not $p) { "KEY_SKIP GAME_NOT_RUNNING"; exit 0 }
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { $h = [KB49]::FindByPid([uint32]$p.Id) }
if ($h -eq [IntPtr]::Zero) { "KEY_SKIP NO_HWND"; exit 0 }
$ok = [KB49]::ForceForeground($h)
Start-Sleep -Milliseconds 140
$fg = [KB49]::GetForegroundWindow()
$vk = 0
switch ($Key.ToUpper()) {
  'SPACE' { $vk = 0x20 } 'RETURN' { $vk = 0x0D } 'ENTER' { $vk = 0x0D }
  'ESC' { $vk = 0x1B } 'DOWN' { $vk = 0x28 } 'UP' { $vk = 0x26 }
  'LEFT' { $vk = 0x25 } 'RIGHT' { $vk = 0x27 } default { $vk = 0x20 }
}
$n = [KB49]::Key([uint16]$vk)
# belt-and-braces: also post the messages straight to the window queue
[void][KB49]::PostMessageW($h, 0x0100, [IntPtr]$vk, [IntPtr]1)
[void][KB49]::PostMessageW($h, 0x0101, [IntPtr]$vk, [IntPtr]0xC0000001)
Start-Sleep -Milliseconds $WaitMs
"KEY $Key vk=0x{0:X2} focus=$ok match={1} sendinput_n={2} alive={3}" -f $vk, ($fg -eq $h), $n, (-not $p.HasExited)
