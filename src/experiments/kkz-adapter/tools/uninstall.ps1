<#
.SYNOPSIS
  卸载 / 回滚 KKZ 实验性适配器（task-49 / W2-Y）。默认 dry-run。

.DESCRIPTION
  回滚顺序（全部在 <GameDir>\plugin\ 里）：
    1) 检查"当前那个 DLL"还是不是我们装的代理（哈希必须等于清单里的 proxySha256）
       —— 不等于说明被 Steam 校验覆盖过或被别人换过，默认拒绝，避免误删原插件
    2) 删掉代理 DLL，把 <TargetDll 去扩展名><OrigSuffix>.dll 改回原名
    3) 删掉我们写的 kkz-adapter.ini
    4) 清单移到 %LOCALAPPDATA%\kkz-adapter\<指纹>.uninstalled.json

  不加 -Rollback 时只做体检（找清单、查哈希、报告能否安全回滚），不写盘。

.EXAMPLE
  .\uninstall.ps1 -GameDir 'D:\Games\sometitle'                 # 体检
  .\uninstall.ps1 -GameDir 'D:\Games\sometitle' -Rollback -Apply # 真回滚
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$TargetDll = 'getSample.dll',
    [string]$OrigSuffix = '_orig',
    [switch]$Rollback,
    [switch]$Apply,
    [switch]$PurgeBackup,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
function Say([string]$m) { Write-Host "[kkz-uninstall] $m" }
function Die([string]$m) { throw "KKZ_UNINSTALL_FAIL: $m" }

$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath.TrimEnd('\')
$pluginDir = Join-Path $GameDir 'plugin'

$manifestDir = Join-Path $env:LOCALAPPDATA 'kkz-adapter'
$finger = [BitConverter]::ToString(([Security.Cryptography.SHA1]::Create()).ComputeHash(
    [Text.Encoding]::UTF8.GetBytes($GameDir.ToLowerInvariant()))).Replace('-', '').Substring(0, 16)
$manifestPath = Join-Path $manifestDir ("$finger.json")

if (-not (Test-Path -LiteralPath $manifestPath)) { Die "找不到安装清单: $manifestPath（这个目录没装过，或清单被删了）" }
$m = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json

$targetPath = $m.proxyPath
$origPath = $m.origPath
If ($m.targetDll -and $m.targetDll -ne $TargetDll) { Say "注意: 清单里的目标插件是 $($m.targetDll)，本次参数是 $TargetDll，以清单为准" }

function Get-Sha256([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }

Say "游戏目录 : $GameDir"
Say "安装时间 : $($m.installedAt)"
Say "目标插件 : $targetPath"
Say "原 DLL   : $origPath"

if (-not (Test-Path -LiteralPath $targetPath)) { Die "代理 DLL 不在了: $targetPath（Steam 校验可能已还原？）" }

$curSha = Get-Sha256 $targetPath
$isOurs = ($curSha -eq $m.proxySha256)
Say "当前 DLL 是否我们的代理: $isOurs ($curSha)"
if (-not $isOurs -and -not $Force) {
    Die '当前 DLL 不是我们装的版本，拒绝自动回滚。若你确认它就是代理（例如重新编译过），加 -Force。'
}

if (Test-Path -LiteralPath $origPath) {
    $origShaNow = Get-Sha256 $origPath
    Say "原 DLL 备份是否完好: $($origShaNow -eq $m.origSha256) ($origShaNow)"
    if (($origShaNow -ne $m.origSha256) -and -not $Force) {
        Die '原 DLL 备份的哈希和清单不符（被改过？），拒绝自动还原。请人工核对后加 -Force。'
    }
}
else {
    Say '警告: 原 DLL 备份不存在（<name>_orig.dll 已被删除/改名）——回滚会把代理 DLL 删掉而没有原文件可还原！'
    if (-not $Force) { Die '缺少原 DLL 备份，拒绝回滚。确认要清理请加 -Force' }
}

$iniPath = $m.iniPath
$running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($m.gameExe)) -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) { Die "游戏正在运行（PID $($running[0].Id)），请先退出游戏" }

if (-not $Rollback) {
    Say ''
    Say '体检完成。要执行回滚请加 -Rollback -Apply：'
    Say "  删除 $targetPath"
    Say "  改名 $origPath -> $targetPath"
    Say "  删除 $iniPath"
    return
}
if (-not $Apply) { Say ''; Say '这是 dry-run：加 -Apply 才会真的回滚。-Rollback -Apply 一起用。'; return }

# ---------- 执行 ----------
$bak = Join-Path $manifestDir 'removed'
New-Item -ItemType Directory -Path $bak -Force | Out-Null
Copy-Item -LiteralPath $targetPath -Destination (Join-Path $bak ("$finger-" + [IO.Path]::GetFileName($targetPath))) -Force

Remove-Item -LiteralPath $targetPath -Force
if (Test-Path -LiteralPath $origPath) { Move-Item -LiteralPath $origPath -Destination $targetPath }
if ($iniPath -and (Test-Path -LiteralPath $iniPath)) { Remove-Item -LiteralPath $iniPath -Force }

$nowSha = Get-Sha256 $targetPath
Say "回滚后插件哈希: $nowSha"
if ($nowSha -ne $m.origSha256) { Say '注意: 回滚后的哈希与原 DLL 清单不一致！请把这一行反馈给作者' }
else { Say '回滚后插件哈希 = 原 DLL 清单哈希，OK' }

Move-Item -LiteralPath $manifestPath -Destination (Join-Path $manifestDir ("$finger.uninstalled.json")) -Force
Say "清单已归档: $(Join-Path $manifestDir ("$finger.uninstalled.json"))"

if ($PurgeBackup -and (Test-Path -LiteralPath $bak)) {
    Remove-Item -LiteralPath $bak -Recurse -Force
    Say '已清理 removed\ 备份'
}
Say '完成。'
