# Make-HardlinkCopy.ps1 — 只读游戏的"实验副本"生成器（同一卷用硬链接，不占空间）
# 用途：在 research/26-loader-compat/copies/ 下复制一份能启动的游戏目录，
#       把 BepInEx/MelonLoader 装进副本，绝不动 D:\Steam 原目录。
# 硬链接要求源与目标在同一 NTFS 卷；跨卷时自动退化为复制（会检查剩余空间）。
# 注意：硬链接共享文件内容——游戏运行时不写自己的安装目录（存档在 AppData），所以是安全的；
#       但任何"修改游戏文件"的实验都必须在副本上做（写入会同时改到源，禁止）。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination,
    [string[]]$ExcludeDirs = @(),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
$Destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')

if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "源目录不存在: $Source" }
if ($Destination -eq $Source) { throw "目标不能等于源" }
if ($Destination.StartsWith($Source + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "目标不能位于源目录内部" }

$srcRoot = [IO.Path]::GetPathRoot($Source)
$dstRoot = [IO.Path]::GetPathRoot($Destination)
$sameVolume = $srcRoot -eq $dstRoot

if (Test-Path -LiteralPath $Destination) {
    if (-not $Force) { throw "目标已存在（加 -Force 覆盖）: $Destination" }
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$files = Get-ChildItem -LiteralPath $Source -Recurse -File -Force
$made = 0; $linked = 0; $bytes = 0L; $skipped = 0
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Source.Length).TrimStart('\')
    $top = ($rel -split '\\')[0]
    if ($ExcludeDirs -contains $top) { $skipped++; continue }
    $dst = Join-Path $Destination $rel
    $dir = Split-Path -Parent $dst
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    if ($sameVolume) {
        New-Item -ItemType HardLink -Path $dst -Target $f.FullName | Out-Null
        $linked++
    }
    else {
        Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
        $made++
    }
    $bytes += $f.Length
}

[pscustomobject]@{
    source      = $Source
    destination = $Destination
    sameVolume  = $sameVolume
    hardlinked  = $linked
    copied      = $made
    skipped     = $skipped
    logicalBytes = $bytes
    method      = if ($sameVolume) { 'hardlink' } else { 'copy' }
} | Format-List
