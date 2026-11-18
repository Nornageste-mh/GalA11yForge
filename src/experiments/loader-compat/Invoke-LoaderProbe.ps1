# Invoke-LoaderProbe.ps1 — 在"副本"里启动游戏，等 N 秒，收集加载器日志后杀掉进程
# 只在 research/26-loader-compat/copies/ 下的副本上使用；不要把 -GameDir 指向 D:\Steam。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$EvidenceDir,
    [int]$WaitSeconds = 30,
    [string]$Tag = ''
)

$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if ($GameDir -match '^D:\\Steam\\') { throw '拒绝在 Steam 原目录运行：请使用 copies/ 下的副本' }
$exePath = Join-Path $GameDir $Exe
if (-not (Test-Path -LiteralPath $exePath)) { throw "找不到 exe: $exePath" }

New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
$stamp = if ($Tag) { $Tag } else { Get-Date -Format 'yyyyMMdd-HHmmss' }

Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $exePath -WorkingDirectory $GameDir -PassThru
$exited = $false
$exitCode = $null
$elapsed = 0
for ($i = 0; $i -lt $WaitSeconds; $i++) {
    Start-Sleep -Seconds 1
    if ($p.HasExited) { $exited = $true; $exitCode = $p.ExitCode; $elapsed = [int]$sw.Elapsed.TotalSeconds; break }
}
$elapsed = if ($elapsed) { $elapsed } else { [int]$sw.Elapsed.TotalSeconds }

$alive = -not $p.HasExited
if ($alive) {
    # 杀进程树（Unity 可能派生 crash handler）
    try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

$files = @()
foreach ($rel in @('BepInEx\LogOutput.log', 'BepInEx\preloader_error.log', 'forge-probe5.txt', 'forge-probe6.txt',
                   'output_log.txt', 'Player.log', 'doorstop_config.ini')) {
    $src = Join-Path $GameDir $rel
    if (Test-Path -LiteralPath $src) {
        $dst = Join-Path $EvidenceDir ($stamp + '-' + ($rel -replace '[\\/:*?"<>|]', '_'))
        Copy-Item -LiteralPath $src -Destination $dst -Force
        $files += $dst
    }
}
# BepInEx 目录里的其它日志/配置（排除 core dll）
foreach ($f in Get-ChildItem (Join-Path $GameDir 'BepInEx') -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.log', '.txt' -or $_.Name -like '*.log' }) {
    $dst = Join-Path $EvidenceDir ($stamp + '-BepInEx-' + ($f.FullName.Substring((Join-Path $GameDir 'BepInEx').Length).TrimStart('\') -replace '[\\/:*?"<>|]', '_'))
    Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
    $files += $dst
}

$summary = [pscustomobject]@{
    tag          = $stamp
    gameDir      = $GameDir
    exe          = $Exe
    started      = $true
    exitedEarly  = $exited
    exitCode     = $exitCode
    aliveAfterSec = $elapsed
    killed       = $alive
    logs         = ($files | ForEach-Object { Split-Path $_ -Leaf }) -join '; '
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDir "$stamp-summary.json") -Encoding utf8
$summary | Format-List
foreach ($f in $files) {
    "----- $(Split-Path $f -Leaf) : $((Get-Item $f).Length) B"
}
