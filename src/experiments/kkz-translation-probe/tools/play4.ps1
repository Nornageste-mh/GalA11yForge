param([string]$ShotDir = "D:\DSHWorkSpace\research\41-kkz-translation\evidence", [string]$Tag = "r4")
# task-48 / R27 run4: no for-in anywhere; verified focus; fraction clicks.
$ErrorActionPreference = 'Continue'
$outdir = 'D:\DSHWorkSpace\_kkz-trans2\out'
$probe = Join-Path $outdir 'probe.txt'; $cap = Join-Path $outdir 'capture.txt'
$tools = 'D:\DSHWorkSpace\src\experiments\kkz-translation-probe\tools'
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
if (Get-Process SenrenBanka -ErrorAction SilentlyContinue) { "REFUSED GAME_RUNNING"; exit 2 }
Remove-Item $probe, $cap -Force -ErrorAction SilentlyContinue
$t0 = Get-Date
"START " + $t0.ToString('HH:mm:ss.fff')
& 'D:\Steam\steam.exe' -applaunch 1144400
$pid0 = 0
while (((Get-Date) - $t0).TotalSeconds -lt 90) {
  Start-Sleep -Milliseconds 400
  if ($pid0 -eq 0) { $c = @(Get-Process SenrenBanka -EA SilentlyContinue); if ($c.Count) { $pid0 = ($c | Sort-Object StartTime | Select-Object -First 1).Id; "PROC pid=$pid0" } }
  if ((Test-Path $probe) -and (Select-String -LiteralPath $probe -Pattern 'warmup_done' -Quiet)) { "WARMUP_DONE t=" + [math]::Round(((Get-Date)-$t0).TotalSeconds,1); break }
}
Start-Sleep -Seconds 3
"PROBE_STAGES:"; Get-Content -LiteralPath $probe | Select-Object -Last 14
& "$tools\shot.ps1" -Label "$Tag-01-menu" -OutDir $ShotDir | Out-String | Write-Output
& "$tools\clickf.ps1" -FX 0.06 -FY 0.345 -Label "newgame" -WaitMs 3000 | Out-String | Write-Output
& "$tools\shot.ps1" -Label "$Tag-02-after-newgame" -OutDir $ShotDir | Out-String | Write-Output
for ($i = 1; $i -le 12; $i++) {
  & "$tools\clickf.ps1" -FX 0.5 -FY 0.75 -Label "adv$i" -WaitMs 700 | Select-String 'CLICKF|FOCUS' | Out-String | Write-Output
  if ($i -in 3, 7) { & "$tools\shot.ps1" -Label "$Tag-03-adv$i" -OutDir $ShotDir | Out-String | Write-Output }
}
Start-Sleep -Milliseconds 1500
& "$tools\shot.ps1" -Label "$Tag-04-final" -OutDir $ShotDir | Out-String | Write-Output
"PROBE_TAIL:"; Get-Content -LiteralPath $probe | Select-Object -Last 8
if (Test-Path $cap) { "CAPTURE_LINES=" + (Get-Content -LiteralPath $cap).Count; Get-Content -LiteralPath $cap | Select-Object -Last 6 }
Copy-Item $probe (Join-Path $ShotDir "probe-$Tag.txt") -Force -EA SilentlyContinue
Copy-Item $cap (Join-Path $ShotDir "capture-$Tag.txt") -Force -EA SilentlyContinue
"ELAPSED=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"
