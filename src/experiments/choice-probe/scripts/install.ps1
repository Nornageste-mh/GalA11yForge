param(
  [string]$Game = 'D:\Steam\steamapps\common\Build',
  [string]$Package = 'D:\DSHWorkSpace\aliya_a11y\mod\package',
  [string]$Work = 'D:\DSHWorkSpace\src\experiments\choice-probe',
  [string]$Evidence = 'D:\DSHWorkSpace\research\23-choice\evidence\install'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Evidence | Out-Null
New-Item -ItemType Directory -Force -Path "$Evidence\backup" | Out-Null
$snapshot = Join-Path $Work 'scripts\snapshot.ps1'

Write-Output '=== 1) 安装前快照 ==='
& $snapshot -Root $Game -Out (Join-Path $Evidence 'before-tree.json')

Write-Output '=== 2) 检查将被覆盖的已有文件（必须全部不存在） ==='
$targets = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'BepInEx')
$overwritten = @()
foreach ($rel in $targets) {
  $p = Join-Path $Game $rel
  if (Test-Path -LiteralPath $p) {
    throw "目标已存在，拒绝安装（需先人工确认）: $rel"
  }
  $overwritten += [pscustomobject]@{ path = $rel; state = 'absent' }
}
$overwritten | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Evidence 'pre-existing.json') -Encoding UTF8

Write-Output '=== 3) 安装 BepInEx 5.4.23.5（只装 core） ==='
Copy-Item -LiteralPath (Join-Path $Package 'winhttp.dll') -Destination (Join-Path $Game 'winhttp.dll') -Force
Copy-Item -LiteralPath (Join-Path $Package 'doorstop_config.ini') -Destination (Join-Path $Game 'doorstop_config.ini') -Force
Copy-Item -LiteralPath (Join-Path $Package '.doorstop_version') -Destination (Join-Path $Game '.doorstop_version') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $Game 'BepInEx\core') | Out-Null
Copy-Item -Path (Join-Path $Package 'BepInEx\core\*') -Destination (Join-Path $Game 'BepInEx\core') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $Game 'BepInEx\plugins') | Out-Null

Write-Output '=== 4) 部署研究插件 ==='
$dll = Join-Path $Work 'ChoiceProbe\bin\Release\ChoiceProbe.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "plugin not built: $dll" }
Copy-Item -LiteralPath $dll -Destination (Join-Path $Game 'BepInEx\plugins\ChoiceProbe.dll') -Force

# 关闭 BepInEx 控制台、开启磁盘日志，便于无人值守
$cfg = Join-Path $Game 'BepInEx\config\BepInEx.cfg'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cfg) | Out-Null
@'
[Logging.Console]
Enabled = false

[Logging.Disk]
Enabled = true
'@ | Set-Content -LiteralPath $cfg -Encoding UTF8

Write-Output '=== 5) 安装后快照 + manifest ==='
& $snapshot -Root $Game -Out (Join-Path $Evidence 'after-install-tree.json')

$before = Get-Content -LiteralPath (Join-Path $Evidence 'before-tree.json') -Raw | ConvertFrom-Json
$after  = Get-Content -LiteralPath (Join-Path $Evidence 'after-install-tree.json') -Raw | ConvertFrom-Json
$beforeMap = @{}
foreach ($f in $before.files) { $beforeMap[$f.path] = $f.sha256 }
$added = @()
foreach ($f in $after.files) {
  if (-not $beforeMap.ContainsKey($f.path)) {
    $added += [pscustomobject]@{ path = $f.path; size = $f.size; sha256 = $f.sha256 }
  } elseif ($beforeMap[$f.path] -ne $f.sha256) {
    throw "existing file changed during install: $($f.path)"
  }
}
$addedDirs = @()
$beforeDirs = @{}
foreach ($d in $before.dirs) { $beforeDirs[$d] = $true }
foreach ($d in $after.dirs) { if (-not $beforeDirs.ContainsKey($d)) { $addedDirs += $d } }

$manifest = [pscustomobject]@{
  schema = 'gala11y.install-manifest/v1'
  game = $Game
  package = $Package
  bepinex_version = (Get-Item (Join-Path $Package 'BepInEx\core\BepInEx.dll')).VersionInfo.FileVersion
  installed_at_utc = (Get-Date).ToUniversalTime().ToString('o')
  plugin = $dll
  plugin_sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
  pre_existing = $overwritten
  added_files = $added
  added_dirs = $addedDirs
  added_file_count = $added.Count
  added_dir_count = $addedDirs.Count
  added_bytes = ($added | Measure-Object -Property size -Sum).Sum
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Evidence 'manifest.json') -Encoding UTF8
Write-Output ("manifest: added files={0} dirs={1} bytes={2}" -f $added.Count, $addedDirs.Count, $manifest.added_bytes)
