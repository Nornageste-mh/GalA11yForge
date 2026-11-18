param(
  [string]$OutDir = "D:\DSHWorkSpace\research\41-kkz-translation\evidence",
  [int]$AdvanceClicks = 30,
  [int]$TimeoutSec = 90
)
# task-48 / R27: one controlled launch + menu entry + line advancing, driven only by
# the method task-47 proved to work (fullscreen launch, client-coordinate mouse_event).
$ErrorActionPreference = 'Continue'
$game   = 'D:\Steam\steamapps\common\SenrenBanka'
$outdir = 'D:\DSHWorkSpace\_kkz-trans2\out'
$probe  = Join-Path $outdir 'probe.txt'
$cap    = Join-Path $outdir 'capture.txt'
$tools  = 'D:\DSHWorkSpace\src\experiments\kkz-translation-probe\tools'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Remove-Item $probe, $cap -Force -ErrorAction SilentlyContinue

if (Get-Process SenrenBanka -ErrorAction SilentlyContinue) { "REFUSED GAME_RUNNING"; exit 2 }

$t0 = Get-Date
"START " + $t0.ToString('HH:mm:ss.fff')
& 'D:\Steam\steam.exe' -applaunch 1144400
"LAUNCH_CMD_EXIT=" + $LASTEXITCODE

$procId = 0
while (((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Milliseconds 400
    if ($procId -eq 0) {
        $c = @(Get-Process SenrenBanka -ErrorAction SilentlyContinue)
        if ($c.Count -gt 0) {
            $procId = ($c | Sort-Object StartTime | Select-Object -First 1).Id
            "PROC pid=$procId after=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"
        }
    }
    if ((Test-Path $probe) -and (Get-Item $probe).Length -gt 0) {
        "PROBE after=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"
        break
    }
}

# wait until our timer actually ticked (probe gets 'pump=ok' + 'first_tick')
$t1 = Get-Date
while (((Get-Date) - $t1).TotalSeconds -lt 30) {
    Start-Sleep -Milliseconds 500
    if ((Test-Path $probe) -and (Select-String -LiteralPath $probe -Pattern 'first_tick' -Quiet)) { "TICK_OK"; break }
}
Copy-Item $probe (Join-Path $OutDir 'probe-boot.txt') -Force -ErrorAction SilentlyContinue

$p = Get-Process -Id $procId -ErrorAction SilentlyContinue
if (-not $p) { "PROC_DIED"; exit 3 }

Start-Sleep -Seconds 7
& "$tools\shot.ps1" -Label "01-menu" -OutDir $OutDir | Out-String | Write-Output

# --- enter 新遊戲 (menu item at client ~(170,375) per task-47 evidence) ---
& "$tools\click.ps1" -CX 170 -CY 375 -Label "menu-newgame" -WaitMs 2500 | Out-String | Write-Output
& "$tools\shot.ps1" -Label "02-after-newgame" -OutDir $OutDir | Out-String | Write-Output

# --- task-47 proven grid round (covers a confirm/start button) ---
foreach ($y in 660,690,720,750) {
  foreach ($x in 300,400,500,600,700) {
    & "$tools\click.ps1" -CX $x -CY $y -Label "grid" -WaitMs 350 | Out-Null
  }
}
Start-Sleep -Milliseconds 800
& "$tools\shot.ps1" -Label "03-after-grid" -OutDir $OutDir | Out-String | Write-Output

# --- advance dialogue with center clicks ---
for ($i = 1; $i -le $AdvanceClicks; $i++) {
    & "$tools\click.ps1" -CX 960 -CY 540 -Label "adv" -WaitMs 900 | Out-Null
    if ($i % 10 -eq 0) {
        & "$tools\shot.ps1" -Label ("04-line-{0:D2}" -f $i) -OutDir $OutDir | Out-String | Write-Output
        if (Test-Path $cap) {
            $n = (Get-Content -LiteralPath $cap).Count
            "CAPTURE_LINES_AFTER_${i}=$n"
        }
    }
}
Start-Sleep -Milliseconds 1500
& "$tools\shot.ps1" -Label "05-final" -OutDir $OutDir | Out-String | Write-Output
if (Test-Path $cap) {
    "CAPTURE_LINES_FINAL=" + (Get-Content -LiteralPath $cap).Count
    "CAPTURE_BYTES_FINAL=" + (Get-Item $cap).Length
}
"ELAPSED=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"
