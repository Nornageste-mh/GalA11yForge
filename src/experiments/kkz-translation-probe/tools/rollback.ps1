param(
  [string]$GameDir = "D:\Steam\steamapps\common\SenrenBanka",
  [string]$Target  = "getSample.dll",
  [string]$ProxySha = "4A3008C4604BDE7D44DA6B600A15692A85E2933CBE71F7E38383E9D76294C936",
  [string]$OriginalSha = "E190AE1B17F82B106FA466E9AB985364E018C8ACD46B39B2B33B895133C2F30C"
)
# task-48 / R27: restore plugin\getSample.dll from plugin\getSample_orig.dll.
$ErrorActionPreference = "Stop"

if (Get-Process SenrenBanka -ErrorAction SilentlyContinue) { "REFUSED GAME_STILL_RUNNING"; exit 2 }

$pluginDir = Join-Path $GameDir "plugin"
$target = Join-Path $pluginDir $Target
$orig = [System.IO.Path]::Combine($pluginDir, [System.IO.Path]::GetFileNameWithoutExtension($Target) + "_orig.dll")
if (-not (Test-Path -LiteralPath $target)) { "REFUSED TARGET_MISSING"; exit 3 }
if (-not (Test-Path -LiteralPath $orig))   { "REFUSED ORIG_MISSING"; exit 4 }

$cur = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
$org = (Get-FileHash -LiteralPath $orig -Algorithm SHA256).Hash
"CURRENT_SHA256=" + $cur
"ORIG_SHA256=" + $org
if ($cur -ne $ProxySha) { "REFUSED CURRENT_IS_NOT_OUR_PROXY"; exit 5 }
if ($org -ne $OriginalSha) { "REFUSED ORIG_HASH_MISMATCH"; exit 6 }

Remove-Item -LiteralPath $target -Force
Rename-Item -LiteralPath $orig -Name $Target
$restored = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
"RESTORED_SHA256=" + $restored
if ($restored -ne $OriginalSha) { "REFUSED RESTORE_HASH_MISMATCH"; exit 7 }

$left = Get-ChildItem -LiteralPath $pluginDir -Filter "*_orig.dll" -File -ErrorAction SilentlyContinue
"ORIG_LEFTOVERS=" + (($left | ForEach-Object { $_.Name }) -join ',')
"ROLLBACK_OK"
