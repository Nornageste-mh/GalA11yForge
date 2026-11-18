param(
    [int]$TargetPid,
    [switch]$Bring
)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class W {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$found = @()
$cb = [W+EnumProc]{
    param($h, $l)
    $pid2 = 0
    [W]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
    if ($pid2 -eq $TargetPid) {
        $t = New-Object System.Text.StringBuilder 256
        [W]::GetWindowText($h, $t, 256) | Out-Null
        $c = New-Object System.Text.StringBuilder 256
        [W]::GetClassName($h, $c, 256) | Out-Null
        $r = New-Object W+RECT
        [W]::GetWindowRect($h, [ref]$r) | Out-Null
        $script:found += [pscustomobject]@{
            hwnd = [int64]$h
            title = $t.ToString()
            cls = $c.ToString()
            visible = [W]::IsWindowVisible($h)
            rect = ('{0},{1},{2},{3}' -f $r.Left, $r.Top, $r.Right, $r.Bottom)
        }
    }
    return $true
}
[W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($found.Count -eq 0) { "no windows for pid $TargetPid"; return }
$found | ForEach-Object { 'hwnd={0} vis={1} cls={2} rect={3} title=[{4}]' -f $_.hwnd, $_.visible, $_.cls, $_.rect, $_.title }
if ($Bring) {
    $best = $found | Where-Object { $_.visible -and $_.title -ne '' } | Select-Object -First 1
    if (-not $best) { $best = $found | Select-Object -First 1 }
    $h = [IntPtr]$best.hwnd
    [W]::ShowWindow($h, 9) | Out-Null
    [W]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0053) | Out-Null
    [W]::SetForegroundWindow($h) | Out-Null
    'brought hwnd=' + $best.hwnd
}