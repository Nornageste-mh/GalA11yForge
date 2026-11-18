# 复跑 4 款校准游戏的实测证据（只读游戏目录，只写 evidence/）
# 用法：pwsh -File tools\run-evidence.ps1 [-Configuration Release]
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = 'D:\dotnet\dotnet.exe'
$evidence = Join-Path $projectRoot 'evidence'
$runsDir = Join-Path $evidence 'runs'
New-Item -ItemType Directory -Force -Path $runsDir | Out-Null

Write-Host "== 构建（$Configuration）==" -ForegroundColor Cyan
& $dotnet build (Join-Path $projectRoot 'UnityMonoAnalyzer.csproj') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }
$exe = Join-Path $projectRoot "bin\$Configuration\net8.0\UnityMonoAnalyzer.exe"

$steam = 'D:\Steam\steamapps\common'
$games = @(
    @{ Slug = 'seven-days'; Name = '我与你共度的七日'; Path = Join-Path $steam '我与你共度的七日'; Note = '根目录是启动器，真游戏在 game\ 子目录' }
    @{ Slug = 'needy'; Name = 'NEEDY GIRL OVERDOSE'; Path = Join-Path $steam 'NEEDY GIRL OVERDOSE'; Note = 'exe 名 Windose.exe 与数据目录同名，旁边有 UnityCrashHandler64.exe' }
    @{ Slug = 'galmaster'; Name = 'GalMaster'; Path = Join-Path $steam 'GalMaster'; Note = '目录名与 exe 名都带空格' }
    @{ Slug = 'to-the-moon'; Name = 'To the Moon'; Path = Join-Path $steam 'To the Moon'; Note = '顶层无 exe，嵌套 3 个 Unity Mono 作' }
)

$rows = @()
$totalSw = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($g in $games) {
    if (-not (Test-Path -LiteralPath $g.Path)) {
        Write-Host "跳过（未安装）：$($g.Name)" -ForegroundColor Yellow
        continue
    }

    $runDir = Join-Path $runsDir $g.Slug
    Remove-Item -Recurse -Force -LiteralPath $runDir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null

    Write-Host "== $($g.Name) ==" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $exe --game $g.Path --out $runDir *>&1 | Tee-Object -FilePath (Join-Path $runDir 'console.txt')
    $code = $LASTEXITCODE
    $sw.Stop()

    $jsonPath = Join-Path $runDir 'analysis.json'
    if (-not (Test-Path $jsonPath)) { throw "$($g.Slug) 没有产出 analysis.json（exit $code）" }
    $j = Get-Content -Raw -Encoding utf8 $jsonPath | ConvertFrom-Json

    $tmp = ($j.textSinks | Where-Object { $_.api -eq 'TMPro.TMP_Text::set_text' } | Select-Object -First 1)
    $ui = ($j.textSinks | Where-Object { $_.api -eq 'UnityEngine.UI.Text::set_text' } | Select-Object -First 1)
    $top = ($j.candidates | Select-Object -First 1)
    $framework = (($j.frameworkTraces | ForEach-Object { "$($_.displayName)×$($_.typeCount)" }) -join '；')

    $rows += [pscustomobject]@{
        slug             = $g.Slug
        game             = $j.game.name
        input            = $g.Path
        dataDir          = $j.game.dataDirName
        exe              = Split-Path -Leaf $j.game.exePath
        unity            = $j.unity.version
        unitySource      = $j.unity.versionSource
        types            = $j.stats.types
        methods          = $j.stats.methods
        ldstr            = $j.stats.ldstrTotal
        sinkCalls        = $j.stats.textSinkCallSites
        tmpCalls         = if ($tmp) { $tmp.calls } else { 0 }
        uiTextCalls      = if ($ui) { $ui.calls } else { 0 }
        sinkCallerMethods= $j.stats.textSinkCallerMethods
        tierA            = $j.stats.tierA
        tierB            = $j.stats.tierB
        tierC            = $j.stats.tierC
        included         = $j.stats.candidatesIncluded
        frameworks       = $framework
        top1Method       = $top.methodFullName
        top1Score        = $top.score
        top1Tier         = $top.tier
        exitCode         = $code
        elapsedMs        = [math]::Round($sw.Elapsed.TotalMilliseconds)
        note             = $g.Note
    }
}

$totalSw.Stop()

if ($rows.Count -eq 0) { throw '没有找到任何游戏目录，未生成证据' }

$csv = Join-Path $evidence '4games-summary.csv'
$rows | Export-Csv -Path $csv -Encoding utf8 -NoTypeInformation
Write-Host "已写 $csv" -ForegroundColor Green

# ---------- run-log.md ----------
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('# 4 款校准游戏实测记录')
[void]$sb.AppendLine()
[void]$sb.AppendLine("> 生成时间（含时区）：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'))")
[void]$sb.AppendLine("> 机器：Windows 10 · dotnet $(& $dotnet --version) · 工具 $((Get-Item $exe).VersionInfo.FileVersion)")
[void]$sb.AppendLine('> 全部输入来自本机已安装游戏；游戏目录只读，产物只写在 evidence/ 下。')
[void]$sb.AppendLine('> 复跑：`pwsh -File tools\run-evidence.ps1`')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 汇总表')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 游戏 | Unity | 类型/方法 | 文本写入点（TMP / UI.Text） | 候选 A/B/C | Top1 | 耗时 | 退出码 |')
[void]$sb.AppendLine('|---|---|---|---|---|---|---|---|')
foreach ($r in $rows) {
    [void]$sb.AppendLine("| $($r.game) | $($r.unity) | $($r.types)/$($r.methods) | $($r.sinkCalls)（$($r.tmpCalls) / $($r.uiTextCalls)） | $($r.tierA)/$($r.tierB)/$($r.tierC) | ``$($r.top1Method)`` $($r.top1Score) $($r.top1Tier) | $($r.elapsedMs) ms | $($r.exitCode) |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 每款游戏的输入与入口判定')
[void]$sb.AppendLine()
foreach ($r in $rows) {
    [void]$sb.AppendLine("### $($r.game)")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- 输入：``$($r.input)``（$($r.note)）")
    [void]$sb.AppendLine("- 数据目录：``$($r.dataDir)``；启动入口：``$($r.exe)``")
    [void]$sb.AppendLine("- Unity：$($r.unity)（来源 $($r.unitySource)）")
    [void]$sb.AppendLine("- ldstr $($r.ldstr) 条；写入点 $($r.sinkCalls) 处 / $($r.sinkCallerMethods) 个方法；框架痕迹：$($r.frameworks)")
    [void]$sb.AppendLine("- 产物：``runs/$($r.slug)/REPORT.md``、``runs/$($r.slug)/analysis.json``、``runs/$($r.slug)/console.txt``")
    [void]$sb.AppendLine()
}
[void]$sb.AppendLine('## 总耗时')
[void]$sb.AppendLine()
[void]$sb.AppendLine("4 款游戏串行跑完共 $([math]::Round($totalSw.Elapsed.TotalSeconds, 1)) s（含进程启动、写盘；不含构建）。")
$logPath = Join-Path $evidence 'run-log.md'
Set-Content -LiteralPath $logPath -Value $sb.ToString() -Encoding utf8
Write-Host "已写 $logPath" -ForegroundColor Green

$rows | Format-Table game, unity, types, methods, sinkCalls, tierA, exitCode, elapsedMs -AutoSize
