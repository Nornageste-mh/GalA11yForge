#requires -Version 5.1
<#
.SYNOPSIS
  KKZ 插件代理卸载/回滚（install.ps1 -Rollback 的薄包装 + 可选清理备份）。

.EXAMPLE
  .\uninstall.ps1 -GameDir 'D:\Steam\steamapps\common\SenrenBanka' -TargetDll 'plugin\menu.dll'

.EXAMPLE
  .\uninstall.ps1 -GameDir 'D:\Steam\steamapps\common\SenrenBanka' -TargetDll 'plugin\menu.dll' -PurgeBackup
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [Parameter(Mandatory = $true)][string]$TargetDll,
    [string]$BackupDir,
    [switch]$DryRun,
    [switch]$PurgeBackup,
    [string]$ExeName = 'SenrenBanka.exe'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$install = Join-Path $here 'install.ps1'
if (-not (Test-Path -LiteralPath $install)) { throw "找不到 install.ps1：$install" }

& $install -GameDir $GameDir -TargetDll $TargetDll -BackupDir $BackupDir -Rollback -DryRun:$DryRun -ExeName $ExeName

if ($PurgeBackup -and -not $DryRun) {
    $stem = [IO.Path]::GetFileNameWithoutExtension($TargetDll)
    if (-not $BackupDir) { $BackupDir = Join-Path $env:LOCALAPPDATA 'kkz-proxy-backup' }
    $dir = Join-Path $BackupDir ($stem + '-' + ((Resolve-Path -LiteralPath $GameDir).Path -replace '[^A-Za-z0-9]', '_'))
    if (Test-Path -LiteralPath $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force
        Write-Host "[kkz] 已清理备份目录 $dir"
    }
}
