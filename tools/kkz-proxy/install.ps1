#requires -Version 5.1
<#
.SYNOPSIS
  KKZ 插件代理安装器（原型 v1，纯 PowerShell，无第三方依赖）。

.DESCRIPTION
  把 <TargetDll>（例如 plugin\menu.dll）替换成代理 DLL，并把原件改名为 <stem>_orig.dll；
  同时把原件备份到游戏目录之外，写 manifest.json 与逐文件 SHA256 快照，支持 -Rollback 还原。

  设计边界：只动 -TargetDll 指定的一个文件；不做 Steam DRM 绕过、不改 data.xp3、不碰存档。
  详见同目录 README.md。

.EXAMPLE
  .\install.ps1 -GameDir 'D:\Steam\steamapps\common\SenrenBanka' -TargetDll 'plugin\menu.dll' `
                -ProxyDll 'D:\...\kkr_proxy2.dll' -DryRun

.EXAMPLE
  .\uninstall.ps1 -GameDir 'D:\Steam\steamapps\common\SenrenBanka' -TargetDll 'plugin\menu.dll'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [Parameter(Mandatory = $true)][string]$TargetDll,
    [string]$ProxyDll,
    [string]$BackupDir,
    [switch]$DryRun,
    [switch]$Rollback,
    [string]$ProxyVersion = '0.1.0.0',
    [int]$MarkerTimeoutSec = 0,
    [string]$MarkerName = 'kkr_proxy2_probe.txt',
    [string]$ExeName = 'SenrenBanka.exe'
)

$ErrorActionPreference = 'Stop'
$schemaVersion = 1

function Write-Step([string]$msg) { Write-Host "[kkz] $msg" }
function Fail([string]$msg) { throw "KKZ_INSTALL_FAIL: $msg" }

# ---------- 1. 参数与目录校验 ----------
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $GameDir $ExeName))) {
    Fail "GameDir 下找不到 $ExeName：$GameDir"
}
$TargetPath = if ([IO.Path]::IsPathRooted($TargetDll)) { $TargetDll } else { Join-Path $GameDir $TargetDll }
if (-not (Test-Path -LiteralPath $TargetPath)) { Fail "目标 DLL 不存在：$TargetPath" }
$TargetDir  = Split-Path -Parent $TargetPath
$TargetName = Split-Path -Leaf $TargetPath
$Stem       = [IO.Path]::GetFileNameWithoutExtension($TargetName)
$OrigPath   = Join-Path $TargetDir ($Stem + '_orig.dll')
$OrigName   = $Stem + '_orig.dll'

# 只允许改 plugin\ 目录下、或 Plugins.link 里列出的插件
$pluginDir = Join-Path $GameDir 'plugin'
$inPlugin  = ($TargetDir.TrimEnd('\') -ieq $pluginDir.TrimEnd('\'))
$inLink    = $false
$linkPath  = Join-Path $GameDir 'Plugins.link'
if (Test-Path -LiteralPath $linkPath) {
    $inLink = (Get-Content -LiteralPath $linkPath -Raw) -match [regex]::Escape($TargetName)
}
if (-not $inPlugin -and -not $inLink) {
    Fail "目标不在 plugin\ 目录，也不在 Plugins.link 中：$TargetPath"
}

if ($ExeName -and (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue)) {
    Fail "游戏正在运行，请先退出：$ExeName"
}

if (-not $BackupDir) { $BackupDir = Join-Path $env:LOCALAPPDATA 'kkz-proxy-backup' }
$BackupRoot = Join-Path $BackupDir ($Stem + '-' + ($GameDir -replace '[^A-Za-z0-9]', '_'))
$manifestPath = Join-Path $BackupRoot 'manifest.json'

function Get-Sha256([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }

function Get-RelPath([string]$base, [string]$path) {
    # Windows PowerShell 5.1 没有 [IO.Path]::GetRelativePath，回退到 Uri 实现
    try { return [IO.Path]::GetRelativePath($base, $path) } catch { }
    $b = [Uri]($base.TrimEnd('\') + '\')
    return [Uri]::UnescapeDataString($b.MakeRelativeUri([Uri]$path).ToString()).Replace('/', '\')
}

function New-Snapshot([string]$dir) {
    # 逐文件 SHA256 清单（插件目录一般很小；大文件只记 name/size）
    $rows = Get-ChildItem -LiteralPath $dir -File | Sort-Object Name | ForEach-Object {
        $h = ''
        if ($_.Length -lt 20MB) { $h = Get-Sha256 $_.FullName }
        [pscustomobject]@{ name = $_.Name; size = $_.Length; sha256 = $h }
    }
    return $rows
}

# ---------- 2. Rollback 分支 ----------
if ($Rollback) {
    if (-not (Test-Path -LiteralPath $manifestPath)) { Fail "找不到 manifest，无法回滚：$manifestPath" }
    $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $curHash = Get-Sha256 $TargetPath
    if ($curHash -ne $m.proxy.sha256) {
        Fail ("当前目标哈希与 manifest 记录的代理不一致（可能已被 Steam 覆盖或手动替换）：$curHash")
    }
    if (-not (Test-Path -LiteralPath $OrigPath)) { Fail "找不到游戏内原件：$OrigPath" }

    if ($DryRun) {
        Write-Step "DryRun：将删除代理、把 $OrigName 改回 $TargetName（哈希 $($m.original.sha256)）"
        return
    }
    Remove-Item -LiteralPath $TargetPath -Force
    Move-Item -LiteralPath $OrigPath -Destination $TargetPath
    $restored = Get-Sha256 $TargetPath
    if ($restored -ne $m.original.sha256) {
        Fail "还原后哈希不匹配：$restored != $($m.original.sha256)（备份仍在 $BackupRoot）"
    }
    Remove-Item -LiteralPath $manifestPath -Force
    Write-Step "ROLLBACK_OK $TargetName sha256=$restored"
    return
}

# ---------- 3. Install 分支 ----------
if (-not $Rollback -and -not $ProxyDll) { Fail '安装模式必须提供 -ProxyDll' }
if (Test-Path -LiteralPath $OrigPath) { Fail "游戏内已存在 $OrigName，疑似上一轮未回滚" }
if (-not (Test-Path -LiteralPath $ProxyDll)) { Fail "代理 DLL 不存在：$ProxyDll" }

$origHash  = Get-Sha256 $TargetPath
$proxyHash = Get-Sha256 $ProxyDll
$origSize  = (Get-Item -LiteralPath $TargetPath).Length

Write-Step "GameDir   = $GameDir"
Write-Step "Target    = $TargetPath"
Write-Step "Original  = sha256 $origHash ($origSize B)"
Write-Step "Proxy     = $ProxyDll sha256 $proxyHash ver $ProxyVersion"
Write-Step "Backup    = $BackupRoot"

if ($DryRun) {
    Write-Step "DryRun：将备份原件、把 $TargetName 改名为 $OrigName、写入代理、写 manifest 与快照；不改任何文件。"
    return
}

New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null

# 逐文件快照（安装前）
$snapBefore = New-Snapshot $TargetDir
$snapPath = Join-Path $BackupRoot 'snapshot-before.json'
$snapBefore | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $snapPath -Encoding UTF8

# 备份原件到游戏目录外，并复核哈希
$backupFile = Join-Path $BackupRoot ($TargetName + '.' + $origHash.Substring(0, 16) + '.bak')
Copy-Item -LiteralPath $TargetPath -Destination $backupFile -Force
if ((Get-Sha256 $backupFile) -ne $origHash) { Fail "备份哈希校验失败：$backupFile" }

# 游戏内改名 + 放代理
Move-Item -LiteralPath $TargetPath -Destination $OrigPath
Copy-Item -LiteralPath $ProxyDll -Destination $TargetPath -Force
$installed = Get-Sha256 $TargetPath
if ($installed -ne $proxyHash) { Fail "代理写入后哈希不匹配：$installed" }

$manifest = [ordered]@{
    schemaVersion = $schemaVersion
    installedAtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    tool = 'kkz-proxy/install.ps1 v1'
    gameDir = $GameDir
    target = [ordered]@{ path = $TargetPath; relative = (Get-RelPath $GameDir $TargetPath); sha256Before = $origHash; sizeBefore = $origSize }
    original = [ordered]@{ name = $TargetName; sha256 = $origHash; size = $origSize; backupPath = $backupFile }
    renamedInGame = [ordered]@{ name = $OrigName; path = $OrigPath }
    proxy = [ordered]@{ sourcePath = (Resolve-Path -LiteralPath $ProxyDll).Path; name = [IO.Path]::GetFileName($ProxyDll); sha256 = $proxyHash; version = $ProxyVersion }
    snapshot = [ordered]@{ path = $snapPath; fileCount = @($snapBefore).Count; sha256OfList = (($snapBefore | ForEach-Object { $_.name + ':' + $_.size + ':' + $_.sha256 }) -join '|') }
    validation = [ordered]@{ inPluginDir = $inPlugin; inPluginsLink = $inLink; exeName = $ExeName }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Step "INSTALL_OK $TargetName -> proxy $proxyHash"
Write-Step "manifest: $manifestPath"

if ($MarkerTimeoutSec -gt 0) {
    Write-Step "等待 marker '$MarkerName'（最多 $MarkerTimeoutSec 秒，请现在启动游戏）"
    $marker = Join-Path $GameDir $MarkerName
    $deadline = (Get-Date).AddSeconds($MarkerTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $marker) {
            Start-Sleep -Milliseconds 500
            Write-Step "MARKER_OK $marker"
            Get-Content -LiteralPath $marker -Encoding UTF8 | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" }
            return
        }
        Start-Sleep -Milliseconds 500
    }
    Fail "等待 marker 超时；代理可能未被加载（检查 plugin 是否被引擎实际加载）"
}
