param([string]$ShotDir = "D:\DSHWorkSpace\research\41-kkz-translation\evidence", [string]$Tag = "r5")
# task-48 / R27 run5: keyboard-driven advance, self-verifying through the probe capture file.
# Success condition (no OCR needed): capture.txt grows past its 3 header lines.
$ErrorActionPreference = 'Continue'
$outdir = 'D:\DSHWorkSpace\_kkz-trans2\out'
$probe = Join-Path $outdir 'probe.txt'; $cap = Join-Path $outdir 'capture.txt'
$tools = 'D:\DSHWorkSpace\src\experiments\kkz-translation-probe\tools'
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
if (Get-Process SenrenBanka -EA SilentlyContinue) { "REFUSED GAME_RUNNING"; exit 2 }
Remove-Item $probe, $cap -Force -EA SilentlyContinue
function CapLines { if (Test-Path $cap) { @(Get-Content -LiteralPath $cap).Count } else { 0 } }
$t0 = Get-Date
"START " + $t0.ToString('HH:mm:ss.fff')
& 'D:\Steam\steam.exe' -applaunch 1144400
while (((Get-Date) - $t0).TotalSeconds -lt 90) {
  Start-Sleep -Milliseconds 400
  if ((Test-Path $probe) -and (Select-String -LiteralPath $probe -Pattern 'warmup_done' -Quiet)) { "WARMUP_DONE t=" + [math]::Round(((Get-Date)-$t0).TotalSeconds,1); break }
}
Start-Sleep -Seconds 3
& "$tools\shot.ps1" -Label "$Tag-01-menu" -OutDir $ShotDir | Out-String | Write-Output
# one mouse click on 新遊戲, then fall back to keys
& "$tools\clickf.ps1" -FX 0.06 -FY 0.345 -Label "newgame" -WaitMs 2200 | Select-String 'CLICKF|FOCUS' | Out-String | Write-Output
$hit = 0
for ($i = 1; $i -le 24; $i++) {
  $k = if ($i % 3 -eq 0) { 'RETURN' } else { 'SPACE' }
  & "$tools\key.ps1" -Key $k -WaitMs 650 | Select-String 'KEY ' | Out-String | Write-Output
  $n = CapLines
  if ($n -gt 3) { "CAPTURE_GREW n=$n at i=$i key=$k"; $hit = 1; break }
  if ($i -in 4, 10) { & "$tools\shot.ps1" -Label "$Tag-02-k$i" -OutDir $ShotDir | Out-String | Write-Output }
}
Start-Sleep -Milliseconds 1200
& "$tools\shot.ps1" -Label "$Tag-03-final" -OutDir $ShotDir | Out-String | Write-Output
"CAPTURE_LINES=" + (CapLines) + " hit=$hit"
"PROBE_TAIL:"; Get-Content -LiteralPath $probe | Select-Object -Last 10
if (Test-Path $cap) { Get-Content -LiteralPath $cap | Select-Object -Last 6 }
Copy-Item $probe (Join-Path $ShotDir "probe-$Tag.txt") -Force -EA SilentlyContinue
Copy-Item $cap (Join-Path $ShotDir "capture-$Tag.txt") -Force -EA SilentlyContinue
"ELAPSED=" + [math]::Round(((Get-Date) - $t0).TotalSeconds,1) + "s"
