# Ren'Py 最小样本自动回归（0.2.46）
#   把「安装覆盖层 → 启动 → 开始游戏 → 两句对白 → 选项（题干/编号/可选中）→ 确认框 → 回滚」跑成一条命令。
#   只对自造的 _renpy-mini 样本使用；不碰 Steam 游戏目录。
#   输入用真实鼠标事件（SetCursorPos + mouse_event），因为 PostMessage 合成消息在 Ren'Py/SDL 上不可靠；
#   启动用 WMI Win32_Process.Create，避免 Start-Process 让子进程继承 stdout 管道把调用方拖住。
param(
    [string]$GamePath = 'D:\DSHWorkSpace\_renpy-mini',
    [string]$Workspace = 'D:\DSHWorkSpace\_renpy-mini-ws',
    [string]$ForgeExe = 'D:\DSHWorkSpace\src\Forge.Cli\bin\Release\net8.0\forge.exe',
    [string]$OutDir = 'D:\DSHWorkSpace\research\85-renpy-minimal-e2e-auto',
    [int]$AppearTimeoutSeconds = 90,
    [switch]$KeepGameRunning
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$script:progressive = Join-Path $OutDir 'progressive.log'
Remove-Item $script:progressive -Force -ErrorAction SilentlyContinue
$logPath = Join-Path $GamePath 'a11y_speech.log'

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MR {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
"@

function Log([string]$text) {
    $line = ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $text)
    Write-Host $line
    Add-Content -Path $script:progressive -Value $line -Encoding UTF8
}
function Read-ForgeJson([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $lines = @(Get-Content $path -ErrorAction SilentlyContinue)
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $t = $lines[$i].Trim()
        if ($t.StartsWith('{')) {
            try { return ($t | ConvertFrom-Json) } catch { return $null }
        }
    }
    return $null
}
function Get-LogLines { if (Test-Path $logPath) { return @(Get-Content $logPath -ErrorAction SilentlyContinue) } else { return @() } }
function Wait-Log([string]$pattern, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $hit = @(Get-LogLines | Where-Object { $_ -match $pattern } | Select-Object -First 1)
        if ($hit.Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 250
    }
    Log ("等待超时（{0}s）：{1}（模式 {2}）" -f $seconds, $what, $pattern)
    return $false
}
function Wait-NewLog([int]$from, [string]$pattern, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $all = @(Get-LogLines)
        if ($all.Count -gt $from) {
            $hit = @($all[$from..($all.Count - 1)] | Where-Object { $_ -match $pattern } | Select-Object -First 1)
            if ($hit.Count -gt 0) { return $true }
        }
        Start-Sleep -Milliseconds 250
    }
    Log ("等待超时（{0}s）：{1}（模式 {2}）" -f $seconds, $what, $pattern)
    return $false
}
function Get-LiveGame {
    $deadline = (Get-Date).AddSeconds($AppearTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $p = @(Get-Process -Name DDLC -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1)
        if ($p.Count -gt 0) { return $p[0] }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
function Enter-Foreground([IntPtr]$h) {
    # 0.2.68：研究/131 暴露「只比较一次」会误报失败（系统要 1-2 秒才真的切过去，
    # 中间还可能被浏览器抢回）。这里改成多次尝试 + 短暂等待，成功了立刻返回。
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        if ([MR]::GetForegroundWindow() -eq $h) { return $true }
        [void][MR]::ShowWindow($h, 9)
        [void][MR]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
        $t = [MR]::GetWindowThreadProcessId($h, [IntPtr]::Zero)
        $fg = [MR]::GetForegroundWindow()
        $fgT = [MR]::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
        if ($fgT -ne 0 -and $t -ne 0 -and $fgT -ne $t) { [void][MR]::AttachThreadInput($fgT, $t, $true) }
        [void][MR]::SetForegroundWindow($h)
        [void][MR]::BringWindowToTop($h)
        Start-Sleep -Milliseconds 180
        if ($fgT -ne 0 -and $t -ne 0 -and $fgT -ne $t) { [void][MR]::AttachThreadInput($fgT, $t, $false) }
        if ([MR]::GetForegroundWindow() -eq $h) { return $true }
        Start-Sleep -Milliseconds 120
    }
    Log ("警告：试了 12 次仍没能把游戏窗口切到前台（当前前台 hwnd={0}）" -f [MR]::GetForegroundWindow())
    return $false
}
function Click-Client([IntPtr]$h, [int]$x, [int]$y, [int]$times = 1) {
    # 真实鼠标事件是系统级点击：窗口不在前台时点出去会落到别的程序上。
    # 0.2.47 回归暴露过「点了 8 次一次没生效」——就是游戏被挤到后面。所以每次点击前先确认前台。
    if ([MR]::GetForegroundWindow() -ne $h) {
        $ok = Enter-Foreground $h
        if (-not $ok) {
            Log '警告：游戏窗口不在前台，跳过本次点击（避免误点其它窗口）'
            return $false
        }
    }
    $pt = New-Object MR+POINT
    [void][MR]::ClientToScreen($h, [ref]$pt)
    $sx = $pt.X + $x; $sy = $pt.Y + $y
    for ($i = 1; $i -le $times; $i++) {
        [void][MR]::SetCursorPos($sx, $sy)
        Start-Sleep -Milliseconds 140
        [MR]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 70
        [MR]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 350
    }
    return $true
}
function Click-AndWait([IntPtr]$h, [int]$x, [int]$y, [string]$pattern, [string]$what, [int]$seconds = 20, [int]$times = 1) {
    $before = @(Get-LogLines).Count
    [void](Click-Client $h $x $y $times)
    return (Wait-NewLog $before $pattern $seconds $what)
}
function Wait-StateClick([IntPtr]$h, [int]$x, [int]$y, [string]$pattern, [string]$what, [int]$attempts = 8, [int]$waitSeconds = 4) {
    # 状态机式等待：先看当前累计日志是否已满足；不满足就点一下再等。
    # 这样容忍「点击生效有数秒延迟」「一步点击跨过两句话」等情况。
    for ($i = 1; $i -le $attempts; $i++) {
        if ((@(Get-LogLines) -join "`n") -match $pattern) { return $true }
        $clicked = Click-Client $h $x $y 1
        if (-not $clicked) { Log '提示：这次点击没发生（前台未拿回），下一轮重试' }
        for ($j = 1; $j -le $waitSeconds; $j++) {
            Start-Sleep -Milliseconds 1000
            if ((@(Get-LogLines) -join "`n") -match $pattern) { return $true }
        }
        Log ("等待中（第 {0}/{1} 次）：{2}" -f $i, $attempts, $what)
    }
    return $false
}
function Stop-Game {
    Get-Process -Name DDLC -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}
function Save-Summary($data) {
    $data | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutDir 'summary.json') -Encoding UTF8
}

$script:started = Get-Date
$failed = @()
$steps = 0
try {
    Log ("开始：样本={0} 工作区={1} forge={2}" -f $GamePath, $Workspace, $ForgeExe)
    if (-not (Test-Path $ForgeExe)) { throw "找不到 forge.exe：$ForgeExe" }
    if (-not (Test-Path (Join-Path $GamePath 'game\script.rpy'))) { throw "样本不完整（缺 game\script.rpy）：$GamePath" }

    Stop-Game
    Remove-Item (Join-Path $GamePath 'game\a11y_platform') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $GamePath 'game\a11y_say.vbs') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $GamePath 'a11y_say.vbs') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $GamePath 'README-A11Y.txt') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $GamePath 'uninstall.ps1') -Force -ErrorAction SilentlyContinue
    Remove-Item $Workspace -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $logPath -Force -ErrorAction SilentlyContinue

    $installOut = Join-Path $OutDir 'install.json'
    & $ForgeExe install --game $GamePath --workspace $Workspace --json 2>&1 | Set-Content -Path $installOut -Encoding UTF8
    $install = Read-ForgeJson $installOut
    if (-not $install -or -not $install.ok) { throw ("安装失败：" + ($install.message) ) }
    Log ("步骤 PASS：安装覆盖层 " + $install.message)

    Log '启动游戏（WMI Win32_Process.Create）'
    $create = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = ('"{0}"' -f (Join-Path $GamePath 'DDLC.exe')); CurrentDirectory = $GamePath
    }
    if ($create.ReturnValue -ne 0) { throw ("启动失败，WMI 返回 " + $create.ReturnValue) }
    $steps++
    if (-not (Wait-Log '主菜单出现' $AppearTimeoutSeconds '主菜单出现')) { $failed += '主菜单出现' }
    $game = Get-LiveGame
    if (-not $game) { throw '等不到游戏窗口' }
    $hwnd = $game.MainWindowHandle
    Log ("步骤 PASS：游戏启动、覆盖层初始化（pid={0} hwnd={1}）" -f $game.Id, $hwnd)
    if (-not (Enter-Foreground $hwnd)) { Log '警告：游戏未能到前台，点击可能失效' }

    # 说明：点击生效有数秒延迟，且「激活开始按钮」的那次点击会顺带推进掉第一句，
    # 所以不再逐步断言第 1/2 句，而是用状态机：先等任意对白出现，再一路点到选项屏。
    $script:steps++
    if (Wait-StateClick $hwnd 40 9 '\[A11y对话\] ' '对白开始播报' 10 4) { Log '步骤 PASS：对白开始播报' } else { $failed += '对白开始播报' }

    $script:steps++
    if (Wait-StateClick $hwnd 400 300 '提示：问题是：你要去哪里？。选项 2 个' '选项屏：题干与选项分离' 12 3) { Log '步骤 PASS：选项屏：题干与选项分离' } else { $failed += '选项屏：题干与选项分离' }

    $steps++
    $choiceLines = @(Get-LogLines | Where-Object { $_ -match '\[A11y菜单\] .*提示：' })
    $choiceJoined = ($choiceLines -join "`n")
    if ($choiceLines.Count -gt 0 -and $choiceJoined -match '选项 2 个：1 去公园；2 回家' -and -not ($choiceJoined -match '选项 3 个')) {
        Log ('步骤 PASS：选项编号不含题干（' + ($choiceLines[-1].Trim()) + '）')
    } else { Log '步骤 FAIL：选项编号不含题干'; $failed += '选项编号不含题干' }

    $steps++
    if (Wait-StateClick $hwnd 30 37 '\[A11y对话\] .*到了公园' '选项可选中（进入公园分支）' 8 3) { Log '步骤 PASS：选项可选中（进入公园分支）' } else { $failed += '选项可选中（进入公园分支）' }

    $steps++
    if (Wait-StateClick $hwnd 400 300 '确认：这是一个确认框吗？' '确认框整屏播报' 8 3) { Log '步骤 PASS：确认框整屏播报' } else { $failed += '确认框整屏播报' }

    $steps++
    $errCount = @(Get-LogLines | Where-Object { $_ -match '\[A11y错误\]' }).Count
    if ($errCount -eq 0) { Log '步骤 PASS：运行期无 [A11y错误]' } else { Log ("步骤 FAIL：运行期有 {0} 条 [A11y错误]" -f $errCount); $failed += '运行期无 [A11y错误]' }

    if (-not $KeepGameRunning) { Stop-Game }
    $allLines = @(Get-LogLines)
    $allLines | Set-Content -Path (Join-Path $OutDir 'a11y-lines.txt') -Encoding UTF8
    if (Test-Path $logPath) { Copy-Item $logPath (Join-Path $OutDir 'a11y_speech.log') -Force }

    $rollbackOut = Join-Path $OutDir 'rollback.json'
    & $ForgeExe verify --game $GamePath --workspace $Workspace --rollback --force --json 2>&1 | Set-Content -Path $rollbackOut -Encoding UTF8
    $rollback = Read-ForgeJson $rollbackOut
    if ($rollback -and $rollback.ok) { Log '步骤 PASS：回滚成功' } else { Log '步骤 FAIL：回滚失败'; $failed += '回滚失败' }

    $steps++
    if (-not (Test-Path (Join-Path $GamePath 'game\a11y_platform'))) { Log '步骤 PASS：回滚后覆盖层已清掉' } else { Log '步骤 FAIL：回滚后覆盖层仍在'; $failed += '回滚后覆盖层已清掉' }

    $elapsed = [Math]::Round(((Get-Date) - $script:started).TotalSeconds, 1)
    $summary = [ordered]@{
        schema = 'gala11y.renpy-minimal-e2e/1'
        startedAt = $script:started.ToString('o')
        finishedAt = (Get-Date).ToString('o')
        elapsedSeconds = $elapsed
        gamePath = $GamePath
        workspace = $Workspace
        forgeExe = $ForgeExe
        steps = $steps
        failed = $failed
        pass = ($failed.Count -eq 0)
        speechLines = $allLines.Count
        errors = @($allLines | Where-Object { $_ -match '\[A11y错误\]' })
    }
    Save-Summary $summary
    Log ("结果：{0}，耗时 {1}s，失败步骤：{2}" -f ($(if ($summary.pass) { 'PASS' } else { 'FAIL' })), $elapsed, (($failed -join '、')))
    if (-not $summary.pass) { exit 1 }
    exit 0
} catch {
    Log ("异常：" + $_.Exception.Message)
    try { Stop-Game } catch { }
    try {
        & $ForgeExe verify --game $GamePath --workspace $Workspace --rollback --force --json 2>&1 | Set-Content -Path (Join-Path $OutDir 'rollback-error.json') -Encoding UTF8
    } catch { }
    Save-Summary ([ordered]@{ schema = 'gala11y.renpy-minimal-e2e/1'; pass = $false; failed = @('exception'); error = $_.Exception.Message })
    exit 9
}
