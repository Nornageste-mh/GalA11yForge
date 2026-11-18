param(
  [string]$ShotDir = "D:\DSHWorkSpace\research\41-kkz-translation\evidence",
  [int]$AdvanceClicks = 26,
  [int]$BootTimeoutSec = 90
)
# task-48 / R27 run2: proxy already installed; this script only drives the game.
# Run1 findings baked in: (1) a11y patch shows a modal "NVDA 朗讀查看器" dialog at boot which
# swallows every synthetic click, (2) TJS must not be entered while that modal is up.
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
public class W48 {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  public static List<IntPtr> Dialogs(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr lp) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        var sb = new StringBuilder(64); GetClassNameW(h, sb, 64);
        if (sb.ToString() == "#32770") {
          var tb = new StringBuilder(160); GetWindowTextW(h, tb, 160);
          if (tb.Length > 0) list.Add(h);
        }
      }
      return true;
    }, IntPtr.Zero);
    return list;
  }
  public static string Title(IntPtr h) { var tb = new StringBuilder(160); GetWindowTextW(h, tb, 160); return tb.ToString(); }
}
"@

function Close-GameDialogs([int]$rounds = 6) {
  for ($i = 0; $i -lt $rounds; $i++) {
    $p = @(Get-Process SenrenBanka -ErrorAction SilentlyContinue) | Select-Object -First 1
    if (-not $p) { return }
    $ds = [W48]::Dialogs([uint32]$p.Id)
    if ($ds.Count -eq 0) { if ($i -eq 0) { "DIALOGS=none" } else { "DIALOGS=cleared_after_$i" }; return }
    foreach ($d in $ds) {
      "DIALOG_CLOSE title=[" + [W48]::Title($d) + "]"
      [void][W48]::SendMessageW($d, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 900
  }
  "DIALOGS=still_there"
}

$t0 = Get-Date
"START " + $t0.ToString('HH:mm:ss.fff')
& 'D:\Steam\steam.exe' -applaunch 1144400
"LAUNCH_CMD_EXIT=" + $LASTEXITCODE

$procId = 0
while (((Get-Date) - $t0).TotalSeconds -lt $BootTimeoutSec) {
    Start-Sleep -Milliseconds 400
    if ($procId -eq 0) {
        $c = @(Get-Process SenrenBanka -ErrorAction SilentlyContinue)
        if ($c.Count -gt 0) {
            $procId = ($c | Sort-Object StartTime | Select-Object -First 1).Id
            "PROC pid=$procId after=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"
        }
    }
    if ((Test-Path $probe) -and (Get-Item $probe).Length -gt 0) {
        "PROBE after=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"; break
    }
}

# wait for the probe's 25 s native warmup to pass (no TJS is entered before that)
$tw = Get-Date
while (((Get-Date) - $tw).TotalSeconds -lt 45) {
    Start-Sleep -Milliseconds 700
    if ((Test-Path $probe) -and (Select-String -LiteralPath $probe -Pattern 'warmup_done' -Quiet)) {
        "WARMUP_DONE after=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"; break
    }
}
Copy-Item $probe (Join-Path $ShotDir 'probe-warmup.txt') -Force -ErrorAction SilentlyContinue

Start-Sleep -Seconds 2
& "$tools\shot.ps1" -Label "r2-01-menu" -OutDir $ShotDir | Out-String | Write-Output
Close-GameDialogs
& "$tools\shot.ps1" -Label "r2-02-menu-clean" -OutDir $ShotDir | Out-String | Write-Output

# 新遊戲  (client ~170,375 per task-47 menu evidence)
& "$tools\click.ps1" -CX 170 -CY 375 -Label "newgame" -WaitMs 2500 | Out-String | Write-Output
& "$tools\shot.ps1" -Label "r2-03-after-newgame" -OutDir $ShotDir | Out-String | Write-Output

foreach ($y in 660,690,720,750) {
  foreach ($x in 300,400,500,600,700) {
    & "$tools\click.ps1" -CX $x -CY $y -Label "grid" -WaitMs 350 | Out-Null
  }
}
Start-Sleep -Milliseconds 900
Close-GameDialogs 2
& "$tools\shot.ps1" -Label "r2-04-after-grid" -OutDir $ShotDir | Out-String | Write-Output

for ($i = 1; $i -le $AdvanceClicks; $i++) {
    & "$tools\click.ps1" -CX 960 -CY 520 -Label "adv" -WaitMs 850 | Out-Null
    if ($i % 8 -eq 0) {
        Close-GameDialogs 1
        & "$tools\shot.ps1" -Label ("r2-05-line-{0:D2}" -f $i) -OutDir $ShotDir | Out-String | Write-Output
        if (Test-Path $cap) { "CAPTURE_LINES_AFTER_${i}=" + (Get-Content -LiteralPath $cap).Count }
        if (Test-Path $probe) { "PROBE_BYTES_AFTER_${i}=" + (Get-Item $probe).Length }
    }
}
Start-Sleep -Milliseconds 1500
& "$tools\shot.ps1" -Label "r2-06-final" -OutDir $ShotDir | Out-String | Write-Output
if (Test-Path $cap) {
    "CAPTURE_LINES_FINAL=" + (Get-Content -LiteralPath $cap).Count
    "CAPTURE_BYTES_FINAL=" + (Get-Item $cap).Length
}
"ELAPSED=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"
