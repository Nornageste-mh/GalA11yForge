# GalA11y Forge 卸载脚本（生成文件）
# 只删除 a11y_manifest.json 里登记、且 SHA256 与清单一致的文件；
# 任何被改动过的文件都不会被删（先 -Force 才覆盖该保护）。
param(
    [string]$Manifest = (Join-Path $PSScriptRoot "a11y_manifest.json"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if (-not (Test-Path -LiteralPath $Manifest)) {
    Write-Host "找不到清单: $Manifest" -ForegroundColor Red
    exit 2
}

$plan = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$removed = 0
$skipped = 0
$failed = 0

foreach ($entry in $plan.files) {
    $rel = $entry.path -replace '/', '\'
    $full = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $full)) {
        Write-Host "已不存在: $rel"
        continue
    }
    try {
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLower()
    } catch {
        Write-Host "无法读取哈希: $rel" -ForegroundColor Yellow
        $failed++
        continue
    }
    if ($hash -ne $entry.sha256.ToLower() -and -not $Force) {
        Write-Host "被改动过，保留: $rel" -ForegroundColor Yellow
        $skipped++
        continue
    }
    try {
        Remove-Item -LiteralPath $full -Force
        Write-Host "已删除: $rel"
        $removed++
    } catch {
        Write-Host "删除失败: $rel ($($_.Exception.Message))" -ForegroundColor Red
        $failed++
    }
}

# 只清理"空了且属于补丁"的目录
foreach ($dir in @("game\a11y_platform")) {
    $full = Join-Path $root $dir
    if (Test-Path -LiteralPath $full) {
        $left = Get-ChildItem -LiteralPath $full -Force
        if ($left.Count -eq 0) {
            Remove-Item -LiteralPath $full -Force
            Write-Host "已删除空目录: $dir"
        }
    }
}

Write-Host ""
Write-Host "完成：删除 $removed 个，跳过 $skipped 个，失败 $failed 个。"
# 全部清干净时，清单本身也一起删（有跳过/失败则保留，便于排查）
if ($skipped -eq 0 -and $failed -eq 0) {
    $selfManifest = Join-Path $root "a11y_manifest.json"
    if ((Test-Path -LiteralPath $selfManifest) -and
        ((Resolve-Path -LiteralPath $selfManifest).Path -eq (Resolve-Path -LiteralPath $Manifest).Path)) {
        Remove-Item -LiteralPath $selfManifest -Force
        Write-Host "已删除: a11y_manifest.json"
    }
}
if ($failed -gt 0) { exit 1 }
exit 0
