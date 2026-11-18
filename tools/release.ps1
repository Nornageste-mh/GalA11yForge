# GalA11y Forge 发布打包脚本
#
# 用法：
#   D:\dotnet\dotnet.exe 不需要；本脚本自己调用 dotnet。
#   pwsh -File tools\release.ps1 -Version 0.2.2.0
#   pwsh -File tools\release.ps1 -Version 0.2.2.0 -SkipTests   # 已知测试全绿时可跳过
#
# 产物：
#   releases/v<短版本>/
#     app/forge.exe                     CLI（自包含、单文件）
#     app/Fingerprints/engine-fingerprints.json
#     gui/Forge.Gui.exe                 WinForms 读屏界面（自包含、单文件）
#     SHA256SUMS.txt
#
# 发布前检查（脚本会打印结果，不自动发邮件）：
#   1. 版本常量与 golden 是否已更新（手动）
#   2. 真机证据与文档（手动）
#   3. 邮件告知用户（手动）
#
# 注意：脚本只写 releases/ 目录，不碰游戏目录、不碰 src。

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,

    [switch]$SkipTests,

    [switch]$AllowOverwrite
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = 'D:\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$short = ($Version -split '\.')[0..2] -join '.'
$outRoot = Join-Path $repo (Join-Path 'releases' "v$short")

Write-Host "[1/6] 检查版本常量..." -ForegroundColor Cyan
$forgeInfo = Join-Path $repo 'src\Forge.Core\ForgeInfo.cs'
$infoText = Get-Content $forgeInfo -Raw -Encoding UTF8
if ($infoText -notmatch "Version\s*=\s*`"$([regex]::Escape($Version))`"") {
    Write-Warning "src\Forge.Core\ForgeInfo.cs 里没有找到 Version = `"$Version`"。"
    Write-Warning "请先把版本常量改成 $Version 并重生成 golden（FORGE_UPDATE_GOLDEN=1）。"
    if (-not $AllowOverwrite) { throw '版本常量未就绪；确认要继续请加 -AllowOverwrite。' }
}

if (-not $SkipTests) {
    Write-Host "[2/6] 跑全量测试（少则几十秒，多则几分钟）..." -ForegroundColor Cyan
    & $dotnet test (Join-Path $repo 'src\GalA11yForge.sln') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "全量测试失败（exit $LASTEXITCODE），停止打包。" }
} else {
    Write-Host "[2/6] 已跳过测试（-SkipTests）。" -ForegroundColor Yellow
}

Write-Host "[3/6] 发布 CLI（自包含单文件）..." -ForegroundColor Cyan
$appDir = Join-Path $outRoot 'app'
if (Test-Path $appDir) {
    if (-not $AllowOverwrite) { throw "$appDir 已存在；确认要覆盖请加 -AllowOverwrite。" }
    Remove-Item $appDir -Recurse -Force
}
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
& $dotnet publish (Join-Path $repo 'src\Forge.Cli\Forge.Cli.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $appDir --nologo
if ($LASTEXITCODE -ne 0) { throw "CLI 发布失败（exit $LASTEXITCODE）。" }

# 指纹库：Build 时由 Forge.Detection 复制到输出；这里兜底再放一份。
$fingerprints = Join-Path $appDir 'Fingerprints\engine-fingerprints.json'
if (-not (Test-Path $fingerprints)) {
    $src = Join-Path $repo 'src\Forge.Detection\Fingerprints\engine-fingerprints.json'
    if (-not (Test-Path $src)) { throw "找不到指纹库：$src" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $fingerprints) -Force | Out-Null
    Copy-Item $src $fingerprints
}

# 随包 BepInEx 离线包（官方原样 zip + LGPL 许可证 + 第三方声明）。
# 用户不用自己找 zip；CLI 会自动在 forge.exe 同目录的 bepinex\ 下搜索。
$bepinexSrc = Join-Path $repo 'tools\bepinex'
if (Test-Path $bepinexSrc) {
    $bepinexDst = Join-Path $appDir 'bepinex'
    New-Item -ItemType Directory -Path $bepinexDst -Force | Out-Null
    Copy-Item (Join-Path $bepinexSrc '*') $bepinexDst -Recurse -Force
} else {
    Write-Warning "未找到 $bepinexSrc；发布包将不带 BepInEx 离线包（用户需 --bepinex 指定）。"
}

# IL2CPP 用的 BepInEx 6 离线包（官方原样 zip + LGPL 许可证 + 版本说明）。
# CLI 会自动在 forge.exe 同目录的 bepinex6\ 下搜索；找不到时 install 会给出官方下载地址。
$bepinex6Src = Join-Path $repo 'tools\bepinex6'
if (Test-Path $bepinex6Src) {
    $bepinex6Dst = Join-Path $appDir 'bepinex6'
    New-Item -ItemType Directory -Path $bepinex6Dst -Force | Out-Null
    Copy-Item (Join-Path $bepinex6Src '*') $bepinex6Dst -Recurse -Force
} else {
    Write-Warning "未找到 $bepinex6Src；发布包将不带 BepInEx 6 离线包（用户需 --bepinex 指定）。"
}

Write-Host "[4/6] 发布 GUI（自包含单文件）..." -ForegroundColor Cyan
$guiDir = Join-Path $outRoot 'gui'
if (Test-Path $guiDir) {
    if (-not $AllowOverwrite) { throw "$guiDir 已存在；确认要覆盖请加 -AllowOverwrite。" }
    Remove-Item $guiDir -Recurse -Force
}
New-Item -ItemType Directory -Path $guiDir -Force | Out-Null
& $dotnet publish (Join-Path $repo 'src\Forge.Gui\Forge.Gui.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $guiDir --nologo
if ($LASTEXITCODE -ne 0) { throw "GUI 发布失败（exit $LASTEXITCODE）。" }

Write-Host "[5/6] 计算校验和..." -ForegroundColor Cyan
$sumPath = Join-Path $outRoot 'SHA256SUMS.txt'
$lines = @()
foreach ($relative in @('app\forge.exe', 'app\Fingerprints\engine-fingerprints.json', 'gui\Forge.Gui.exe')) {
    $file = Join-Path $outRoot $relative
    if (-not (Test-Path $file)) { throw "缺少发布文件：$file" }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash
    $lines += "$hash  $($relative.Replace('\', '/'))"
}
$bepinexZip = Join-Path $outRoot 'app\bepinex\BepInEx_win_x64_5.4.23.5.zip'
if (Test-Path $bepinexZip) {
    $hash = (Get-FileHash $bepinexZip -Algorithm SHA256).Hash
    $lines += "$hash  app/bepinex/BepInEx_win_x64_5.4.23.5.zip"
}
$bepinex6Zip = Join-Path $outRoot 'app\bepinex6\BepInEx_win_x64_6.0.0-be.788.zip'
if (Test-Path $bepinex6Zip) {
    $hash = (Get-FileHash $bepinex6Zip -Algorithm SHA256).Hash
    $lines += "$hash  app/bepinex6/BepInEx_win_x64_6.0.0-be.788.zip"
}
Set-Content -Path $sumPath -Value ($lines -join "`r`n") -Encoding UTF8
$lines | ForEach-Object { Write-Host "  $_" }

# 全文件清单（0.2.80 起）：覆盖发布目录里每一个文件，供逐字节核对（SHA256SUMS.txt 只列关键文件，保持脚本兼容）。
$allLines = @()
$allFiles = Get-ChildItem -Path $outRoot -Recurse -File | Where-Object { $_.Name -notin @('SHA256SUMS.txt', 'SHA256SUMS.all.txt') } | Sort-Object FullName
foreach ($file in $allFiles) {
    $relative = $file.FullName.Substring($outRoot.Length).TrimStart('\').Replace('\', '/')
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
    $allLines += "$hash  $relative"
}
Set-Content -Path (Join-Path $outRoot 'SHA256SUMS.all.txt') -Value ($allLines -join "`r`n") -Encoding UTF8
Write-Host "  全文件清单：SHA256SUMS.all.txt（$($allFiles.Count) 个文件）" 

Write-Host "[6/6] 冒烟..." -ForegroundColor Cyan
$exe = Join-Path $appDir 'forge.exe'
$ver = (& $exe version 2>&1 | Out-String).Trim()
Write-Host "  forge version => $ver"
$detect = & $exe detect --game (Join-Path $repo 'samples') --json 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) {
    Write-Warning "detect 冒烟返回 $LASTEXITCODE（samples 为空时可能如此），请手动补跑真实游戏 detect。"
}

Write-Host ""
Write-Host "完成：$outRoot" -ForegroundColor Green
Write-Host "下一步（手动）："
Write-Host "  1. 写 releases/v$short/README.md（包内文件、快速开始、本版要点、合规、已知限制）"
Write-Host "  2. 更新 docs/20 发布说明、docs/12/19 测试数、docs/18 使用指南"
Write-Host "  3. 用真实游戏跑一次发布版 detect/analyze（确认 Fingerprints 在包内可用）"
Write-Host "  4. 发邮件给用户 385254598@qq.com（本版要点、已验证/未验证、下一步）"
