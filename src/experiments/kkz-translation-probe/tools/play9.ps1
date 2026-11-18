param([int]$MaxSec = 150, [string]$ShotDir = "D:\DSHWorkSpace\research\41-kkz-translation\evidence", [string]$Tag = "r9")
# task-48 / R27 run9: let the in-process click injector drive the game, stop as soon as capture grows.
$ErrorActionPreference = 'Continue'
$outdir = 'D:\DSHWorkSpace\_kkz-trans2\out'
$probe = Join-Path $outdir 'probe.txt'; $cap = Join-Path $outdir 'capture.txt'
$tools = 'D:\DSHWorkSpace\src\experiments\kkz-translation-probe\tools'
if (Get-Process SenrenBanka -EA SilentlyContinue) { "REFUSED GAME_RUNNING"; exit 2 }
Remove-Item $probe, $cap -Force -EA SilentlyContinue
$t0 = Get-Date
"START " + $t0.ToString('HH:mm:ss.fff')
& 'D:\Steam\steam.exe' -applaunch 1144400
while (((Get-Date) - $t0).TotalSeconds -lt 90) {
  Start-Sleep -Milliseconds 400
  if ((Test-Path $probe) -and (Select-String -LiteralPath $probe -Pattern 'warmup_done' -Quiet)) { "WARMUP_DONE t=" + [math]::Round(((Get-Date)-$t0).TotalSeconds,1); break }
}
$rows = 0
while (((Get-Date) - $t0).TotalSeconds -lt $MaxSec) {
  Start-Sleep -Seconds 3
  if (Test-Path $cap) { $rows = @(Get-Content -LiteralPath $cap).Count }
  $el = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
  "T+$el rows=$rows"
  if ($rows -gt 15) { "ROWS_GREW rows=$rows at T+$el"; & "$tools\shot.ps1" -Label "$Tag-line" -OutDir $ShotDir | Out-String | Write-Output; break }
  if (-not (Get-Process SenrenBanka -EA SilentlyContinue)) { "GAME_EXITED T+$el"; break }
}
& "$tools\shot.ps1" -Label "$Tag-end" -OutDir $ShotDir | Out-String | Write-Output
"POSTCLICK_SAMPLE:"; Get-Content -LiteralPath $probe -EA SilentlyContinue | Select-String 'postclick|where=' | Select-Object -Last 4
"CAPTURE_LINES=" + $rows
if (Test-Path $cap) { Get-Content -LiteralPath $cap | Select-Object -Last 10 }
Copy-Item $probe (Join-Path $ShotDir "probe-$Tag.txt") -Force -EA SilentlyContinue
Copy-Item $cap (Join-Path $ShotDir "capture-$Tag.txt") -Force -EA SilentlyContinue
"ELAPSED=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"

