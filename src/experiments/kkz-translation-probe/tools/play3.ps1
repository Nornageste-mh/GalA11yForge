param(
  [string]$ShotDir = "D:\DSHWorkSpace\research\41-kkz-translation\evidence",
  [int]$BootTimeoutSec = 90
)
# task-48 / R27 run3: launch -> wait warmup -> close a11y viewer -> click 新遊戲 by client FRACTION
# -> a few center clicks -> dump probe/capture.  Fractions make it mode-independent.
$ErrorActionPreference = 'Continue'
$outdir = 'D:\DSHWorkSpace\_kkz-trans2\out'
$probe  = Join-Path $outdir 'probe.txt'
$cap    = Join-Path $outdir 'capture.txt'
$tools  = 'D:\DSHWorkSpace\src\experiments\kkz-translation-probe\tools'
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

if (Get-Process SenrenBanka -ErrorAction SilentlyContinue) { "REFUSED GAME_RUNNING"; exit 2 }
Remove-Item $probe, $cap -Force -ErrorAction SilentlyContinue

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W48B {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public struct POINT { public int X, Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  public static IntPtr Main(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr lp) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
        if (sb.Length > 0 && found == IntPtr.Zero) found = h;
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static List<string> ChildrenWithTitle(IntPtr parent) {
    var list = new List<string>();
    EnumChildWindows(parent, delegate(IntPtr h, IntPtr lp) {
      if (IsWindowVisible(h)) {
        var tb = new StringBuilder(160); GetWindowTextW(h, tb, 160);
        var cb = new StringBuilder(64); GetClassNameW(h, cb, 64);
        if (tb.Length > 0) {
          list.Add(cb.ToString() + "|" + tb.ToString() + "|" + h.ToInt64());
        }
      }
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@

$t0 = Get-Date
"START " + $t0.ToString('HH:mm:ss.fff')
& 'D:\Steam\steam.exe' -applaunch 1144400
"LAUNCH_CMD_EXIT=" + $LASTEXITCODE

$procId = 0
while (((Get-Date) - $t0).TotalSeconds -lt $BootTimeoutSec) {
    Start-Sleep -Milliseconds 400
    if ($procId -eq 0) {
        $c = @(Get-Process SenrenBanka -ErrorAction SilentlyContinue)
        if ($c.Count -gt 0) { $procId = ($c | Sort-Object StartTime | Select-Object -First 1).Id; "PROC pid=$procId" }
    }
    if ((Test-Path $probe) -and (Get-Item $probe).Length -gt 0) { "PROBE ok"; break }
}
$tw = Get-Date
while (((Get-Date) - $tw).TotalSeconds -lt 50) {
    Start-Sleep -Milliseconds 700
    if ((Test-Path $probe) -and (Select-String -LiteralPath $probe -Pattern 'warmup_done' -Quiet)) { "WARMUP_DONE t=" + [math]::Round(((Get-Date)-$t0).TotalSeconds,1); break }
}
Start-Sleep -Seconds 2
Copy-Item $probe (Join-Path $ShotDir 'probe-run3-boot.txt') -Force -ErrorAction SilentlyContinue

$main = [W48B]::Main([uint32]$procId)
"MAIN_HWND=" + $main.ToInt64()
$r = New-Object W48B+RECT
[void][W48B]::GetClientRect($main, [ref]$r)
"CLIENT=" + ($r.Right - $r.Left) + "x" + ($r.Bottom - $r.Top)
& "$tools\shot.ps1" -Label "r3-01-menu" -OutDir $ShotDir | Out-String | Write-Output

# close every titled child window of the game (this is where the a11y viewer lives)
foreach ($round in 1..3) {
  $kids = [W48B]::ChildrenWithTitle($main)
  "CHILDREN r$round n=" + $kids.Count
  foreach ($k in $kids) {
     "CHILD " + $k
     $h = [IntPtr][int64]($k.Split('|')[2])
     [void][W48B]::SendMessageW($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
  }
  Start-Sleep -Milliseconds 700
}
& "$tools\shot.ps1" -Label "r3-02-after-closeviewer" -OutDir $ShotDir | Out-String | Write-Output

# 新遊戲 : deliberately far left so it cannot fall inside the viewer panel
& "$tools\clickf.ps1" -FX 0.055 -FY 0.345 -Label "newgame" -WaitMs 2600 | Out-String | Write-Output
& "$tools\shot.ps1" -Label "r3-03-after-newgame" -OutDir $ShotDir | Out-String | Write-Output

for ($i = 1; $i -le 10; $i++) {
    & "$tools\clickf.ps1" -FX 0.5 -FY 0.75 -Label "adv" -WaitMs 850 | Out-Null
    if ($i -eq 4) { & "$tools\shot.ps1" -Label "r3-04-mid" -OutDir $ShotDir | Out-String | Write-Output }
}
Start-Sleep -Milliseconds 1200
& "$tools\shot.ps1" -Label "r3-05-final" -OutDir $ShotDir | Out-String | Write-Output
if (Test-Path $cap) { "CAPTURE_LINES=" + (Get-Content -LiteralPath $cap).Count }
Copy-Item $probe (Join-Path $ShotDir 'probe-run3.txt') -Force -ErrorAction SilentlyContinue
Copy-Item $cap   (Join-Path $ShotDir 'capture-run3.txt') -Force -ErrorAction SilentlyContinue
"ELAPSED=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"
