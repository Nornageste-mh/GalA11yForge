#requires -Version 7.0
<#
.SYNOPSIS
    GalA11y Forge 真实游戏 E2E 驱动脚本（v0.1，默认 dry-run）。

.DESCRIPTION
    依次调用 forge detect / analyze / plan / generate / build [install] / verify / report，
    把每一步的命令行、退出码、耗时、产物路径和输出尾部写入
    <Workspace>\reports\e2e-v01-<yyyyMMdd-HHmmss>.log。

    安全边界（硬性）：
      - 默认不 install、不启动游戏；只有显式传 -Install 才会写游戏目录。
      - -Install 只允许 D:\Steam\ 下的目录，且必须 Read-Host 输入 INSTALL 二次确认。
      - install 前后各做一次文件 sha256 快照，写入 <Workspace>\reports\。
      - 任一步非 0 立即停止，打印最后 30 行输出，保留现场。

.PARAMETER GameDir
    真实游戏安装目录。默认 D:\Steam\steamapps\common\Build（只读；-Install 时才写）。

.PARAMETER Workspace
    工作区根目录。默认 %LOCALAPPDATA%\GalA11yForge\e2e-v01。

.PARAMETER ForgeExe
    forge.exe 或 forge.dll 路径。省略时自动找 src\Forge.Cli\bin\{Debug,Release}\net8.0\forge.dll。
    .dll 用 dotnet 运行，.exe 直接运行。

.PARAMETER Install
    显式开关：把补丁安装进 GameDir。默认关闭（dry-run）。

.PARAMETER TimeoutMin
    单步超时（分钟），默认 10。

.EXAMPLE
    pwsh -File tools\e2e-v01.ps1 -ForgeExe src\Forge.Cli\bin\Debug\net8.0\forge.dll

.EXAMPLE
    pwsh -File tools\e2e-v01.ps1 -GameDir 'D:\Steam\steamapps\common\Build' -Install
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'D:\Steam\steamapps\common\Build',

    [string]$Workspace = (Join-Path $env:LOCALAPPDATA 'GalA11yForge\e2e-v01'),

    [string]$ForgeExe,

    [switch]$Install,

    [ValidateRange(1, 240)]
    [int]$TimeoutMin = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:LogPath = $null
$script:Results = @()

# ---------------------------------------------------------------------------
# 基础工具
# ---------------------------------------------------------------------------

function Write-Log {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Gray
    )

    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message
    if ($script:LogPath) {
        Add-Content -LiteralPath $script:LogPath -Value $line -Encoding utf8NoBOM
    }
    Write-Host $line -ForegroundColor $Color
}

function Format-Duration {
    param([TimeSpan]$Value)
    return ('{0:mm\:ss\.fff}' -f $Value)
}

function Format-Arg {
    param([string]$Value)
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

function Resolve-ForgeCommand {
    param([string]$Requested)

    $path = $null
    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        if (-not (Test-Path -LiteralPath $Requested -PathType Leaf)) {
            throw "找不到 -ForgeExe：$Requested"
        }
        $path = (Resolve-Path -LiteralPath $Requested).Path
    }
    else {
        $candidates = @(
            (Join-Path $script:RepoRoot 'src\Forge.Cli\bin\Debug\net8.0\forge.dll'),
            (Join-Path $script:RepoRoot 'src\Forge.Cli\bin\Release\net8.0\forge.dll'),
            (Join-Path $script:RepoRoot 'src\Forge.Cli\bin\Debug\net8.0\Forge.Cli.dll')
        )
        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $path = $candidate; break }
        }
        if (-not $path) {
            throw '找不到 forge 可执行产物。请先运行：D:\dotnet\dotnet.exe build src\Forge.Cli -c Debug，或用 -ForgeExe 指定。'
        }
    }

    $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
    if ($extension -eq '.dll') {
        $dotnet = if ($env:FORGE_DOTNET) { $env:FORGE_DOTNET }
                  elseif (Test-Path -LiteralPath 'D:\dotnet\dotnet.exe') { 'D:\dotnet\dotnet.exe' }
                  else { (Get-Command dotnet -ErrorAction Stop).Source }
        if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
            throw "找不到 dotnet：$dotnet"
        }
        return [pscustomobject]@{ Command = $dotnet; Prefix = @($path); Display = "$dotnet $path" }
    }
    if ($extension -eq '.exe') {
        return [pscustomobject]@{ Command = $path; Prefix = @(); Display = $path }
    }
    throw "-ForgeExe 必须是 .dll 或 .exe：$path"
}

function Get-WorkspaceSnapshot {
    $map = @{}
    if (-not (Test-Path -LiteralPath $Workspace -PathType Container)) { return $map }

    Get-ChildItem -LiteralPath $Workspace -Recurse -File -Force -ErrorAction SilentlyContinue |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($Workspace, $_.FullName).Replace('\', '/')
            $map[$relative] = [pscustomobject]@{
                LastWriteTimeUtc = $_.LastWriteTimeUtc
                Length           = $_.Length
            }
        }
    return $map
}

function Get-SnapshotDiff {
    param(
        [hashtable]$Before,
        [hashtable]$After
    )

    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($key in $After.Keys) {
        if (-not $Before.ContainsKey($key)) {
            $changed.Add("新增 $key")
            continue
        }
        $old = $Before[$key]
        $new = $After[$key]
        if ($old.Length -ne $new.Length -or $old.LastWriteTimeUtc -ne $new.LastWriteTimeUtc) {
            $changed.Add("变更 $key")
        }
    }
    foreach ($key in $Before.Keys) {
        if (-not $After.ContainsKey($key)) { $changed.Add("删除 $key") }
    }
    return $changed
}

function Get-GameHashSnapshot {
    param(
        [Parameter(Mandatory)] [string]$Stage,
        [Parameter(Mandatory)] [string]$Timestamp
    )

    $fullGameDir = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
    Write-Log "正在生成游戏目录哈希快照（$Stage）：$fullGameDir" -Color Cyan

    $files = New-Object System.Collections.Generic.List[object]
    $root = [IO.Path]::GetFullPath($fullGameDir)
    foreach ($file in (Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction Stop)) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        $files.Add([ordered]@{
            path   = $relative
            size   = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }

    $snapshot = [ordered]@{
        schemaVersion = 1
        kind          = 'forge-e2e-install-hash'
        stage         = $Stage
        gameDir       = $root
        capturedAt    = (Get-Date).ToString('o')
        fileCount     = $files.Count
        files         = $files
    }

    $snapshotPath = Join-Path $Workspace ("reports\install-{0}-{1}.json" -f $Stage, $Timestamp)
    $snapshot | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $snapshotPath -Encoding utf8NoBOM
    Write-Log "哈希快照已写入：$snapshotPath（$($files.Count) 个文件）" -Color Cyan
    return $snapshotPath
}

function Assert-SafeInstallTarget {
    $full = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
    if ($full -notlike 'D:\Steam\*') {
        throw "-Install 只允许 D:\Steam\ 下的目录；当前为 $full。为防止误写，脚本中止。"
    }
    if ($full -eq 'D:\Steam') {
        throw '-Install 不允许直接指向 D:\Steam 根目录。'
    }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "游戏目录不存在：$full"
    }
    return $full
}

function Write-OutputTail {
    param(
        [string]$Text,
        [string]$Label,
        [int]$Count = 30
    )

    if ([string]::IsNullOrWhiteSpace($Text)) { return }
    $lines = @($Text -split "`r?`n")
    $tail = $lines | Select-Object -Last $Count
    Write-Host "--- $Label（最后 $($tail.Count) 行）---" -ForegroundColor DarkYellow
    $tail | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow }
    Write-Host '--- 输出结束 ---' -ForegroundColor DarkYellow
}

# ---------------------------------------------------------------------------
# 单步执行
# ---------------------------------------------------------------------------

function Invoke-ForgeStep {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [scriptblock]$BuildArguments,
        [string]$Prerequisite
    )

    $startedAt = Get-Date
    $outDir = Join-Path $Workspace ("out\" + $Name)
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    $extra = @(& $BuildArguments)
    $arguments = @($Name, '--game', $GameDir, '--workspace', $Workspace, '--out', $outDir, '--json') + $extra
    $allArguments = @($Forge.Prefix) + $arguments
    $commandLine = (@($Forge.Command) + ($allArguments | ForEach-Object { Format-Arg $_ })) -join ' '

    Write-Log ''
    Write-Log ("=== [{0}] {1} ===" -f ($script:Results.Count + 1), $Name) -Color White
    Write-Log "命令：$commandLine"
    if ($Prerequisite) { Write-Log "前置：$Prerequisite" }

    $before = Get-WorkspaceSnapshot
    $timeoutMs = $TimeoutMin * 60 * 1000

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Forge.Command
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $allArguments) { $psi.ArgumentList.Add([string]$argument) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    $finished = $process.WaitForExit($timeoutMs)
    $timedOut = $false
    if (-not $finished) {
        $timedOut = $true
        try { $process.Kill($true) } catch { }
        [void]$process.WaitForExit(10000)
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = if ($finished) { $process.ExitCode } else { 124 }
    $process.Dispose()

    $stdoutPath = Join-Path $outDir 'stdout.log'
    $stderrPath = Join-Path $outDir 'stderr.log'
    $stdout | Set-Content -LiteralPath $stdoutPath -Encoding utf8NoBOM
    $stderr | Set-Content -LiteralPath $stderrPath -Encoding utf8NoBOM

    $duration = (Get-Date) - $startedAt
    $after = Get-WorkspaceSnapshot
    $changes = @(Get-SnapshotDiff -Before $before -After $after | Where-Object { $_ -notmatch '^变更 reports/e2e-v01-.*\.log$' })

    $code = $null
    $message = $null
    $jsonLine = @(
        $stdout -split "`r?`n" |
            Where-Object { $_.TrimStart().StartsWith('{') } |
            Select-Object -Last 1
    )
    if ($jsonLine.Count -gt 0) {
        try {
            $envelope = $jsonLine[0] | ConvertFrom-Json -Depth 20 -ErrorAction Stop
            if ($envelope.PSObject.Properties['code']) { $code = [string]$envelope.code }
            if ($envelope.PSObject.Properties['message']) { $message = [string]$envelope.message }
        }
        catch {
            Write-Log "警告：无法解析 CLI JSON 信封：$($_.Exception.Message)" -Color Yellow
        }
    }
    if (-not $code -and $stdout -match '"code"\s*:\s*"([^"]+)"') { $code = $Matches[1] }

    $status = if ($timedOut) { '超时' }
              elseif ($exitCode -eq 0) { '通过' }
              elseif ($code -eq 'E_NOT_IMPLEMENTED') { 'blocked（E_NOT_IMPLEMENTED，接线未完成）' }
              else { '失败' }

    Write-Log ("退出码：{0}；耗时：{1}；状态：{2}" -f $exitCode, (Format-Duration $duration), $status) `
        -Color $(if ($exitCode -eq 0) { 'Green' } else { 'Red' })
    if ($code) { Write-Log "错误码：$code" -Color $(if ($code -eq 'E_NOT_IMPLEMENTED') { 'Yellow' } else { 'Red' }) }
    if ($message) { Write-Log "message：$message" -Color Gray }

    Write-Log '产物：'
    Write-Log "  - $stdoutPath"
    Write-Log "  - $stderrPath"
    if ($changes.Count -eq 0) {
        Write-Log '  - （工作区无新增/变更文件）'
    }
    else {
        $shown = $changes | Select-Object -First 60
        foreach ($change in $shown) { Write-Log ("  - {0}" -f $change) }
        if ($changes.Count -gt $shown.Count) {
            Write-Log ("  ... 其余 {0} 项省略" -f ($changes.Count - $shown.Count))
        }
    }

    if ($exitCode -ne 0 -or $timedOut) {
        Write-Log '步骤失败，停止后续步骤；以下为最后 30 行输出。' -Color Red
        Write-OutputTail -Text $stdout -Label 'stdout'
        Write-OutputTail -Text $stderr -Label 'stderr'
    }
    else {
        $tail = @($stdout -split "`r?`n" | Select-Object -Last 5)
        if ($tail.Count -gt 0) {
            Write-Log 'stdout 末尾：'
            foreach ($line in $tail) { if ($line) { Write-Log ("  " + $line) } }
        }
    }

    $script:Results += [pscustomobject]@{
        Step       = $Name
        ExitCode   = $exitCode
        Duration   = $duration
        Status     = $status
        Command    = $commandLine
        OutputDir  = $outDir
        TimedOut   = $timedOut
    }

    return $script:Results[-1]
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

$GameDir = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
$Workspace = [IO.Path]::GetFullPath($Workspace).TrimEnd('\')

New-Item -ItemType Directory -Path $Workspace -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Workspace 'reports') -Force | Out-Null
$script:LogPath = Join-Path $Workspace ("reports\e2e-v01-{0}.log" -f $script:Stamp)

$Forge = Resolve-ForgeCommand -Requested $ForgeExe

Write-Log 'GalA11y Forge E2E v0.1' -Color Cyan
Write-Log "时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Log "GameDir：$GameDir"
Write-Log "Workspace：$Workspace"
Write-Log "Forge：$($Forge.Display)"
Write-Log "Install：$(if ($Install) { '是（会写游戏目录）' } else { '否（默认 dry-run，不写游戏目录）' })"
Write-Log "单步超时：$TimeoutMin 分钟"
Write-Log "日志：$($script:LogPath)"
Write-Log '说明：本脚本不会启动游戏；detect 只读，install 需显式 -Install。' -Color DarkGray

if (-not (Test-Path -LiteralPath $GameDir -PathType Container)) {
    Write-Log "游戏目录不存在：$GameDir" -Color Red
    Write-Log '请用 -GameDir 指定真实游戏安装目录；本脚本不会创建游戏目录。' -Color Red
    exit 2
}

$installTarget = $null
$installTimestamp = $null
$beforeSnapshot = $null
$afterSnapshot = $null

$steps = @(
    @{ Name = 'detect';   Hint = '' },
    @{ Name = 'analyze';  Hint = '依赖 detect 的识别结果' },
    @{ Name = 'plan';     Hint = '依赖分析结果' },
    @{ Name = 'generate'; Hint = '生成补丁工程到工作区' },
    @{ Name = 'build';    Hint = '需要本地 BepInEx 包/离线缓存' },
    @{ Name = 'install';  Hint = '仅在 -Install 时执行；写入游戏目录' },
    @{ Name = 'verify';   Hint = '安装后初检' },
    @{ Name = 'report';   Hint = '汇总报告' }
)

foreach ($step in $steps) {
    $name = [string]$step.Name

    if ($name -eq 'install' -and -not $Install) {
        Write-Log ''
        Write-Log '=== [install] 已跳过（未指定 -Install；默认不写游戏目录）===' -Color Yellow
        $script:Results += [pscustomobject]@{
            Step      = 'install'
            ExitCode  = 0
            Duration  = [TimeSpan]::Zero
            Status    = '跳过（dry-run）'
            Command   = ''
            OutputDir = ''
            TimedOut  = $false
        }
        continue
    }

    if ($name -eq 'install') {
        $installTarget = Assert-SafeInstallTarget
        $installTimestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $beforeSnapshot = Get-GameHashSnapshot -Stage 'before' -Timestamp $installTimestamp

        Write-Log ''
        Write-Log "即将把补丁安装到：$installTarget" -Color Yellow
        Write-Log '这会修改真实游戏目录（新增/替换文件）。' -Color Yellow
        $answer = Read-Host '确认请输入 INSTALL（其它任何输入都会取消）'
        if ($answer -cne 'INSTALL') {
            Write-Log '已取消：未输入 INSTALL，脚本中止；游戏目录未被修改。' -Color Red
            exit 2
        }
    }

    $result = Invoke-ForgeStep -Name $name -BuildArguments { @() } -Prerequisite ([string]$step.Hint)

    if ($name -eq 'install' -and $result.ExitCode -eq 0) {
        $afterSnapshot = Get-GameHashSnapshot -Stage 'after' -Timestamp $installTimestamp
        Write-Log "install 前后快照：$beforeSnapshot / $afterSnapshot" -Color Cyan
        Write-Log '提示：可用两份快照对比差异；失败恢复请优先使用生成的 uninstall.ps1 或工作区备份。' -Color DarkGray
    }

    if ($result.TimedOut) {
        Write-Log "步骤 $name 超时（$TimeoutMin 分钟），已终止。现场保留在 $($result.OutputDir)。" -Color Red
        break
    }
    if ($result.ExitCode -ne 0) {
        Write-Log "步骤 $name 返回非 0（$($result.ExitCode)），按要求停止。" -Color Red
        break
    }
}

Write-Log ''
Write-Log '=== E2E 汇总 ===' -Color Cyan
$table = $script:Results | Format-Table Step, ExitCode, @{ Label = '耗时'; Expression = { Format-Duration $_.Duration } }, Status -AutoSize | Out-String
Write-Host $table
Add-Content -LiteralPath $script:LogPath -Value $table -Encoding utf8NoBOM
Write-Log "日志文件：$($script:LogPath)"

$failed = @($script:Results | Where-Object { $_.ExitCode -ne 0 -or $_.TimedOut })
if ($failed.Count -gt 0) {
    $blocked = @($failed | Where-Object { $_.Status -like 'blocked*' })
    if ($blocked.Count -gt 0) {
        Write-Log '结果：blocked —— 有步骤未实现（E_NOT_IMPLEMENTED），这不是脚本错误，等待接线后重跑。' -Color Yellow
    }
    else {
        Write-Log "结果：失败 —— $($failed[0].Step) 退出码 $($failed[0].ExitCode)。" -Color Red
    }
    exit [int]$failed[0].ExitCode
}

Write-Log '结果：全部步骤成功。' -Color Green
exit 0
