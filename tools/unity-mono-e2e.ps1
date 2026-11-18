#requires -Version 7.0
<#
  一条命令回归：Unity Mono 补丁真机冒烟（安装 → 启动 → 文本兜底轮询日志 → 回滚 → 复查）
  只对实验副本运行；不碰 Steam 原目录。用法：
    pwsh -NoProfile -ExecutionPolicy Bypass -File tools\unity-mono-e2e.ps1
#>
param(
    [string]$SourceGame = 'D:\Steam\steamapps\common\Build',
    [string]$Root = 'D:\DSHWorkSpace\_unity-e2e',
    [string]$ForgeExe = 'D:\DSHWorkSpace\releases\v0.2.54\app\forge.exe',
    [string]$OutDir = 'D:\DSHWorkSpace\research\95-unity-mono-e2e',
    [int]$FirstLaunchSeconds = 40,
    [int]$WatchWaitSeconds = 120,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$started = Get-Date
$steps = New-Object System.Collections.Generic.List[object]

function Step([string]$name, [string]$state, [string]$detail = '') {
    $steps.Add([pscustomobject]@{ step = $name; state = $state; detail = $detail; at = (Get-Date).ToString('HH:mm:ss') })
    Write-Host ("[{0}] {1}：{2}{3}" -f (Get-Date).ToString('HH:mm:ss'), $state, $name, $(if ($detail) { " — $detail" } else { '' }))
}

function Fail([string]$name, [string]$detail) {
    Step $name 'FAIL' $detail
    throw "$name：$detail"
}

function Stop-Game([string]$procName) {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$game = Join-Path $Root 'game'
$ws = Join-Path $Root 'ws'
$tag = Split-Path $SourceGame -Leaf
Write-Host "=== Unity Mono E2E：$tag ==="
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
try {
    # 1. 复制实验副本
    if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $game | Out-Null
    $copy = robocopy $SourceGame $game /E /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { Fail '复制游戏副本' "robocopy exit $LASTEXITCODE" }
    Step '复制游戏副本' 'PASS' $game

    # 2. 安装补丁
    $install = & $ForgeExe install --game $game --workspace $ws --json 2>&1 | Select-Object -Last 1
    $installJson = $install | ConvertFrom-Json
    if (-not $installJson.ok) { Fail '安装补丁' $installJson.message }
    Step '安装补丁' 'PASS' ("deploymentId=" + $installJson.data.deploymentId)

    # 3. 找主程序
    $exe = Get-ChildItem $game -Filter *.exe |
        Where-Object { $_.Name -notmatch 'UnityCrashHandler|unins|setup|vcredist' } |
        Sort-Object Length -Descending | Select-Object -First 1
    if (-not $exe) { Fail '找主程序' '游戏目录里没有 exe' }
    $procName = [IO.Path]::GetFileNameWithoutExtension($exe.Name)
    Step '找主程序' 'PASS' ($exe.Name + ' / ' + $procName)

    # 4. 首次启动（生成插件配置）
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = ('"' + $exe.FullName + '"'); CurrentDirectory = $game } | Out-Null
    Start-Sleep -Seconds $FirstLaunchSeconds
    Stop-Game $procName
    Start-Sleep -Seconds 2
    $cfgDir = Join-Path $game 'BepInEx\config'
    if (-not (Test-Path $cfgDir)) { Fail '等待插件配置' "没有生成 $cfgDir（游戏可能启动太慢或崩溃）" }
    Step '首次启动并生成配置' 'PASS' $cfgDir

    # 5. 打开详细日志（找含「诊断」的那个 cfg，不是 BepInEx.cfg）
    $cfg = Get-ChildItem $cfgDir -Filter *.cfg |
        Where-Object { (Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue) -match '诊断' } |
        Select-Object -First 1
    if (-not $cfg) { Fail '打开详细日志' '没有找到含「诊断」的插件配置' }
    $patchScript = Join-Path $OutDir 'patch-diag.py'
    @"
import io, re, sys
p = sys.argv[1]
s = io.open(p, encoding='utf-8-sig').read()
s2, n = re.subn(r'(?m)^(\s*详细日志\s*=\s*).*$', r'\1true', s)
if n == 0:
    print('NO_MATCH'); sys.exit(2)
io.open(p, 'w', encoding='utf-8', newline='').write(s2)
print('PATCHED')
"@ | Set-Content -Path $patchScript -Encoding utf8NoBOM
    $patchOut = & 'D:\python\python.exe' -X utf8 $patchScript $cfg.FullName 2>&1
    if (($patchOut -join "`n") -notmatch 'PATCHED') { Fail '打开详细日志' ("配置改写失败：" + ($patchOut -join ' ')) }
    Step '打开详细日志' 'PASS' $cfg.Name
    $log = Join-Path $game 'BepInEx\LogOutput.log'
    Remove-Item $log -Force -ErrorAction SilentlyContinue

    # 6. 第二次启动，等兜底轮询真读到文本
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = ('"' + $exe.FullName + '"'); CurrentDirectory = $game } | Out-Null
    $procDeadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $procDeadline -and -not (Get-Process -Name $procName -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
    if (-not (Get-Process -Name $procName -ErrorAction SilentlyContinue)) { Fail '第二次启动' '游戏进程没有起来' }
    $deadline = (Get-Date).AddSeconds($WatchWaitSeconds)
    $readLines = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if (Test-Path $log) {
            $readLines = @(Select-String -Path $log -Pattern '兜底读到' -ErrorAction SilentlyContinue).Count
            if ($readLines -ge 1) { break }
        }
    }
    Start-Sleep -Seconds 3
    Stop-Game $procName
    Start-Sleep -Seconds 2
    if (-not (Test-Path $log)) { Fail '等兜底轮询' '没有生成 LogOutput.log' }
    Copy-Item $log (Join-Path $OutDir 'LogOutput.log') -Force
    $watchLines = @(Select-String -Path $log -Pattern '\[Speech:watch\]' -ErrorAction SilentlyContinue).Count
    $readLines = @(Select-String -Path $log -Pattern '兜底读到' -ErrorAction SilentlyContinue).Count
    $speakLines = @(Select-String -Path $log -Pattern '\[朗读\]' -ErrorAction SilentlyContinue).Count
    $errorLines = @(Select-String -Path $log -Pattern '\[A11y错误\]' -ErrorAction SilentlyContinue).Count
    if ($watchLines -lt 1) { Fail '等兜底轮询' '日志里没有 [Speech:watch] 行' }
    if ($readLines -lt 1) { Fail '等兜底轮询' '日志里没有「兜底读到」行' }
    Step '兜底轮询真读到文本' 'PASS' ("watch={0} read={1} speak={2}" -f $watchLines, $readLines, $speakLines)
    if ($errorLines -ne 0) { Fail '运行期无错误' ("[A11y错误] 出现 {0} 行，见 LogOutput.log" -f $errorLines) }
    Step '运行期无错误' 'PASS'

    # 7. 回滚
    $rollback = & $ForgeExe verify --game $game --workspace $ws --rollback --force --json 2>&1 | Select-Object -Last 1
    $rbJson = $rollback | ConvertFrom-Json
    if (-not $rbJson.ok) { Fail '回滚' $rbJson.message }
    Step '回滚' 'PASS' ("overall=" + $rbJson.data.overall)

    # 8. 回滚后复查：插件与注入文件都不在
    $leftovers = @()
    foreach ($rel in @('BepInEx\plugins', 'doorstop_config.ini', 'winhttp.dll', 'BepInEx\core')) {
        if (Test-Path (Join-Path $game $rel)) {
            # BepInEx 目录本身可能被保留（空目录），这里只关心插件 dll 与注入文件
            if ($rel -eq 'BepInEx\plugins') {
                $dlls = Get-ChildItem (Join-Path $game $rel) -Filter *.dll -ErrorAction SilentlyContinue
                if ($dlls) { $leftovers += ($rel + ' → ' + ($dlls.Name -join ',')) }
            } elseif ($rel -ne 'BepInEx\core') {
                $leftovers += $rel
            }
        }
    }
    if ($leftovers.Count -gt 0) { Fail '回滚后复查' ('仍有残留：' + ($leftovers -join '; ')) }
    Step '回滚后复查' 'PASS' '插件与注入文件已清掉'

    $summary = [pscustomobject]@{
        schema = 'gala11y.unity-mono-e2e/1'
        sourceGame = $SourceGame
        root = $Root
        forgeExe = $ForgeExe
        startedAt = $started.ToString('o')
        finishedAt = (Get-Date).ToString('o')
        elapsedSeconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
        watchLines = $watchLines
        readLines = $readLines
        speakLines = $speakLines
        errorLines = $errorLines
        steps = $steps
        pass = $true
    }
    $summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'unity-mono-e2e.json') -Encoding utf8NoBOM
    Write-Host ("结果：PASS，耗时 {0}s" -f $summary.elapsedSeconds)
    exit 0
} catch {
    $msg = $_.Exception.Message
    Write-Host ("结果：FAIL  {0}" -f $msg)
    $summary = [pscustomobject]@{
        schema = 'gala11y.unity-mono-e2e/1'
        sourceGame = $SourceGame
        forgeExe = $ForgeExe
        startedAt = $started.ToString('o')
        finishedAt = (Get-Date).ToString('o')
        elapsedSeconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
        steps = $steps
        pass = $false
        failed = $msg
    }
    try {
        New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
        $summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'unity-mono-e2e.json') -Encoding utf8NoBOM
    } catch { }
    try { if (Test-Path $log) { Copy-Item $log (Join-Path $OutDir 'LogOutput.log') -Force } } catch { }
    exit 1
} finally {
    if (-not $Keep -and (Test-Path $Root)) {
        try { Stop-Game $procName } catch { }
        Start-Sleep -Seconds 2
        for ($i = 0; $i -lt 5 -and (Test-Path $Root); $i++) {
            Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path $Root) { Start-Sleep -Seconds 3 }
        }
    }
}
