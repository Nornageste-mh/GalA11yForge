param(
  [string]$Game = 'D:\Steam\steamapps\common\Build',
  [string]$Evidence = 'D:\DSHWorkSpace\research\19-re-runtime-confirm\evidence\install',
  [string]$Work = 'D:\DSHWorkSpace\src\experiments\runtime-confirm'
)
$ErrorActionPreference = 'Stop'
$snapshot = Join-Path $Work 'scripts\snapshot.ps1'
$gameFull = (Resolve-Path -LiteralPath $Game).Path.TrimEnd('\')

Write-Output '=== 1) 卸载后快照（记录新增文件） ==='
& $snapshot -Root $gameFull -Out (Join-Path $Evidence 'after-run-tree.json')

$before = Get-Content -LiteralPath (Join-Path $Evidence 'before-tree.json') -Raw | ConvertFrom-Json
$after  = Get-Content -LiteralPath (Join-Path $Evidence 'after-run-tree.json') -Raw | ConvertFrom-Json
$beforeFiles = @{}
foreach ($f in $before.files) { $beforeFiles[$f.path] = $f }
$beforeDirs = @{}
foreach ($d in $before.dirs) { $beforeDirs[$d] = $true }

$extraFiles = @($after.files | Where-Object { -not $beforeFiles.ContainsKey($_.path) })
$extraDirs  = @($after.dirs  | Where-Object { -not $beforeDirs.ContainsKey($_) })

Write-Output ("新增文件 {0}；新增目录 {1}" -f $extraFiles.Count, $extraDirs.Count)
$extraFiles | Select-Object -First 40 | ForEach-Object { Write-Output ("  F {0}" -f $_.path) }
$extraDirs  | Select-Object -First 20 | ForEach-Object { Write-Output ("  D {0}" -f $_) }

# 只允许删除本任务安装的路径：三个根文件 + BepInEx 子树
$allowedRootFiles = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')
foreach ($f in $extraFiles) {
  $ok = ($allowedRootFiles -contains $f.path) -or $f.path.StartsWith('BepInEx\', [StringComparison]::OrdinalIgnoreCase)
  if (-not $ok) { throw "拒绝删除未知新增文件: $($f.path)" }
}
foreach ($d in $extraDirs) {
  if (-not $d.StartsWith('BepInEx', [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝删除未知新增目录: $d"
  }
}
foreach ($rel in $allowedRootFiles) { $beforeFiles.ContainsKey($rel) | ForEach-Object { if ($_) { throw "原目录已有 $rel，拒绝删除" } } }

Write-Output '=== 2) 删除新增文件 ==='
foreach ($f in $extraFiles) {
  $p = Join-Path $gameFull $f.path
  if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
}
Write-Output '=== 3) 删除新增目录（深到浅） ==='
$dirsToDelete = @($extraDirs | Sort-Object Length -Descending)
foreach ($d in $dirsToDelete) {
  $p = Join-Path $gameFull $d
  if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}
# BepInEx 根目录若仍存在且安装前不存在，一并删除
if (-not $beforeDirs.ContainsKey('BepInEx') -and (Test-Path -LiteralPath (Join-Path $gameFull 'BepInEx'))) {
  Remove-Item -LiteralPath (Join-Path $gameFull 'BepInEx') -Recurse -Force
}

Write-Output '=== 4) 还原后快照与逐文件校验 ==='
& $snapshot -Root $gameFull -Out (Join-Path $Evidence 'after-restore-tree.json')
$restored = Get-Content -LiteralPath (Join-Path $Evidence 'after-restore-tree.json') -Raw | ConvertFrom-Json
$restoredFiles = @{}
foreach ($f in $restored.files) { $restoredFiles[$f.path] = $f }
$restoredDirs = @{}
foreach ($d in $restored.dirs) { $restoredDirs[$d] = $true }

$missing = @(); $changed = @(); $sizeChanged = @(); $extra = @()
foreach ($k in $beforeFiles.Keys) {
  if (-not $restoredFiles.ContainsKey($k)) { $missing += $k; continue }
  if ($restoredFiles[$k].sha256 -ne $beforeFiles[$k].sha256) { $changed += $k }
  if ($restoredFiles[$k].size -ne $beforeFiles[$k].size) { $sizeChanged += $k }
}
foreach ($k in $restoredFiles.Keys) { if (-not $beforeFiles.ContainsKey($k)) { $extra += $k } }
$missingDirs = @($beforeDirs.Keys | Where-Object { -not $restoredDirs.ContainsKey($_) })
$extraDirsAfter = @($restoredDirs.Keys | Where-Object { -not $beforeDirs.ContainsKey($_) })

$keyFiles = @('5omeday.exe', 'UnityPlayer.dll', '5omeday_Data\Managed\Assembly-CSharp.dll', '5omeday_Data\globalgamemanagers')
$keyCheck = @()
foreach ($k in $keyFiles) {
  $b = if ($beforeFiles.ContainsKey($k)) { $beforeFiles[$k].sha256 } else { '' }
  $a = if ($restoredFiles.ContainsKey($k)) { $restoredFiles[$k].sha256 } else { '' }
  $keyCheck += [pscustomobject]@{ path = $k; before = $b; after = $a; equal = ($b -eq $a -and $b -ne '') }
}

$verification = [pscustomobject]@{
  schema = 'gala11y.restore-verification/v1'
  game = $gameFull
  verified_at_utc = (Get-Date).ToUniversalTime().ToString('o')
  before_file_count = $before.file_count
  before_dir_count = $before.dir_count
  restored_file_count = $restored.file_count
  restored_dir_count = $restored.dir_count
  removed_file_count = $extraFiles.Count
  removed_dir_count = $extraDirs.Count
  missing = $missing
  changed = $changed
  size_changed = $sizeChanged
  extra = $extra
  missing_dirs = $missingDirs
  extra_dirs = $extraDirsAfter
  key_files = $keyCheck
  ok = ($missing.Count -eq 0 -and $changed.Count -eq 0 -and $sizeChanged.Count -eq 0 -and $extra.Count -eq 0 -and $missingDirs.Count -eq 0 -and $extraDirsAfter.Count -eq 0)
}
$verification | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Evidence 'restore-verification.json') -Encoding UTF8
Write-Output ("VERIFY ok={0} missing={1} changed={2} sizeChanged={3} extra={4} missingDirs={5} extraDirs={6}" -f $verification.ok, $missing.Count, $changed.Count, $sizeChanged.Count, $extra.Count, $missingDirs.Count, $extraDirsAfter.Count)
$keyCheck | Format-Table -AutoSize
if (-not $verification.ok) { exit 2 }
