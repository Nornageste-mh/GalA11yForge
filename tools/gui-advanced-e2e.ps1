param(
    [Parameter(Mandatory = $true)][string]$Game,
    [string]$OutDir = 'D:\DSHWorkSpace\research\72-gui-advanced-run',
    [string]$Workspace = 'D:\DSHWorkSpace\_gui-adv-ws',
    [string]$GuiExe = 'D:\DSHWorkSpace\releases\v0.2.29\gui\Forge.Gui.exe',
    [int]$TimeoutSeconds = 420,
    [string]$GhidraExe = '',
    [string]$OutPrefix = 'gui-advanced-run'
)

# 用 Win32 窗口消息驱动 GUI（不依赖焦点/UIA）：
#   找子窗口 → WM_SETTEXT 填「游戏目录/工作区」 → BM_CLICK 勾「高级分析」 → BM_CLICK「分析文本」
# 证据靠工作区产物：analysis.json + analysis.json.ghidra（available=true 才证明 Ghidra 真跑了）。

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class WinMsg {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public static IntPtr FirstWindowTitle(uint pid, string titlePart) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        if (sb.ToString().Contains(titlePart)) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  public static List<IntPtr> Children(IntPtr parent) {
    var list = new List<IntPtr>();
    EnumChildWindows(parent, (h, l) => { if (IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }

  public static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
  public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
  public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
}
"@

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$log = New-Object System.Collections.Generic.List[string]
$logPath = Join-Path $OutDir 'progressive.log'
function Log([string]$s) { $line = ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $s); $log.Add($line); Add-Content -Path $logPath -Value $line -Encoding UTF8; Write-Host $line }

# 干净工作区
if (Test-Path $Workspace) { Remove-Item $Workspace -Recurse -Force }
New-Item -ItemType Directory -Path $Workspace | Out-Null

Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$startedAt = Get-Date
Log "用 WMI Win32_Process.Create 分离启动 GUI（不继承本进程管道）"
$guiCommand = '"' + $GuiExe + '"'
$guiDir = Split-Path -Parent $GuiExe
$start = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $guiCommand; CurrentDirectory = $guiDir }
Log ("WMI 启动返回 {0}，pid={1}，命令={2}" -f $start.ReturnValue, $start.ProcessId, $guiCommand)
$proc = $null
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 180; $i++) {
    Start-Sleep -Milliseconds 500
    $proc = Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) {
        $hwnd = [WinMsg]::FirstWindowTitle([uint32]$proc.Id, 'GalA11y Forge')
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
}
if ($hwnd -eq [IntPtr]::Zero) {
    Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
    throw 'GUI 主窗口没有出现（90 秒内）。'
}
Log ("GUI 就绪 pid={0} hwnd={1}" -f $proc.Id, $hwnd)

$steps = [ordered]@{}
try {
    # 收集控件，按位置排序（阅读顺序）
    $children = [WinMsg]::Children($hwnd)
    $items = foreach ($h in $children) {
        [pscustomobject]@{
            H = $h
            Cls = [WinMsg]::Cls($h)
            Text = [WinMsg]::Text($h)
            Rect = [WinMsg]::Rect($h)
        }
    }
    $edits = @($items | Where-Object { $_.Cls -like '*EDIT*' } | Sort-Object { $_.Rect.Top }, { $_.Rect.Left })
    $buttons = @($items | Where-Object { $_.Cls -like '*BUTTON*' })
    Log ("子窗口：{0} 个（Edit={1}，Button={2}）" -f $items.Count, $edits.Count, $buttons.Count)
    if ($edits.Count -lt 2) { throw '找不到「游戏目录/工作区」输入框。' }

    # 第一个 Edit = 游戏目录，第二个 Edit = 工作区
    [void][WinMsg]::SendMessage($edits[0].H, 0x000C, [IntPtr]::Zero, $Game)
    [void][WinMsg]::SendMessage($edits[1].H, 0x000C, [IntPtr]::Zero, $Workspace)
    $readBack0 = [WinMsg]::Text($edits[0].H)
    $readBack1 = [WinMsg]::Text($edits[1].H)
    Log ("已填：游戏目录={0}；回读=[{1}]；工作区={2}；回读=[{3}]" -f $Game, $readBack0, $Workspace, $readBack1)
    if ($GhidraExe -ne '') {
        if ($edits.Count -lt 6) { throw ('找不到「分析程序」输入框（Edit 数={0}）。' -f $edits.Count) }
        [void][WinMsg]::SendMessage($edits[5].H, 0x000C, [IntPtr]::Zero, $GhidraExe)
        Log ("已填：分析程序={0}" -f $GhidraExe)
        $steps.ghidraExeFilled = $true
    }
    $steps.filled = $true

    # 勾「高级分析」（点一下 = 从默认未勾选变勾选）
    $advanced = $buttons | Where-Object { $_.Text -like '*高级分析*' } | Select-Object -First 1
    if (-not $advanced) { throw '找不到「高级分析」复选框。' }
    [void][WinMsg]::SendMessage($advanced.H, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) # BM_CLICK
    Start-Sleep -Milliseconds 500
    Log ("已点击高级分析复选框（文本：{0}）" -f $advanced.Text)
    $steps.advancedClicked = $true

    # 点「分析文本」
    $analyze = $buttons | Where-Object { $_.Text -like '*分析文本*' } | Select-Object -First 1
    if (-not $analyze) { throw '找不到「分析文本」按钮。' }
    [void][WinMsg]::SendMessage($analyze.H, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    Log ("已点击「分析文本」（文本：{0}）" -f $analyze.Text)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $analysisPath = $null
    $forgeSeen = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $found = Get-ChildItem -Path $Workspace -Recurse -Filter 'analysis.json' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $analysisPath = $found.FullName; break }
        $c = Get-CimInstance Win32_Process -Filter "Name = 'forge.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($c) { $forgeSeen = $c.CommandLine; Log ("forge.exe 正在跑：{0}" -f ($forgeSeen -replace '.*forge\.exe', 'forge.exe')) }
    }
    if (-not $analysisPath) { throw ('分析结果没有出现（{0} 秒）。' -f $TimeoutSeconds) }
    Log "发现 analysis.json：$analysisPath"

    $ghidraPath = Join-Path (Split-Path -Parent $analysisPath) 'analysis-ghidra.json'
    for ($i = 0; $i -lt 15 -and -not (Test-Path $ghidraPath); $i++) { Start-Sleep -Seconds 2 }
    $steps.analysisPath = $analysisPath
    $steps.ghidraPath = $ghidraPath
    $json = Get-Content $analysisPath -Encoding UTF8 -Raw | ConvertFrom-Json
    $steps.candidateCount = @($json.candidates).Count
    $steps.forgeCommandLine = $forgeSeen

    if (Test-Path $ghidraPath) {
        $ghidra = Get-Content $ghidraPath -Encoding UTF8 -Raw | ConvertFrom-Json
        $steps.ghidraAvailable = ($ghidra.available -eq $true)
        $steps.ghidraStatus = $ghidra.status
        $steps.ghidraElapsedSeconds = $ghidra.elapsedSeconds
        $steps.ghidraVersion = $ghidra.ghidraVersion
        $steps.ghidraCandidateCount = @($ghidra.candidates).Count
        $steps.ghidraExecutablePath = $ghidra.executablePath
        if ($GhidraExe -ne '') {
            $steps.ghidraExecutableMatches = ($ghidra.executablePath -eq $GhidraExe)
        } else {
            $steps.ghidraExecutableMatches = $null
        }
        Log ("Ghidra available={0} status={1} version={2} elapsed={3}s 候选={4} executable={5} 指定匹配={6}" -f $steps.ghidraAvailable, $steps.ghidraStatus, $steps.ghidraVersion, $steps.ghidraElapsedSeconds, $steps.ghidraCandidateCount, $steps.ghidraExecutablePath, $steps.ghidraExecutableMatches)
    } else {
        $steps.ghidraAvailable = $false
        Log '没有找到 analysis-ghidra.json：Ghidra 没跑（--advanced 可能没生效）。'
    }
    # 点「分析报告」，验证未知引擎也能在 GUI 里导出报告与人工待办
    $reportBtn = $buttons | Where-Object { $_.Text -like '*分析报告*' } | Select-Object -First 1
    if (-not $reportBtn) { throw '找不到「分析报告」按钮。' }
    [void][WinMsg]::SendMessage($reportBtn.H, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    Log ("已点击「分析报告」（文本：{0}）" -f $reportBtn.Text)
    $steps.reportClicked = $true
    $reportPath = $null
    $reportDeadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $reportDeadline) {
        Start-Sleep -Seconds 2
        $rp = Get-ChildItem -Path $Workspace -Recurse -Filter 'report.md' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($rp) { $reportPath = $rp.FullName; break }
    }
    $steps.reportPath = $reportPath
    $steps.reportExists = [bool]$reportPath
    if ($reportPath) {
        $reportsRoot = Split-Path -Parent $reportPath
        $todos = Get-ChildItem -Path $reportsRoot -Filter 'human-todos.md' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        $steps.humanTodosPath = if ($todos) { $todos.FullName } else { $null }
        Log ("发现报告：{0}；人工待办：{1}" -f $reportPath, $steps.humanTodosPath)
    } else {
        Log '报告没有出现（120 秒）：未知引擎的报告入口失败。'
    }

    Log ("候选数={0}；forge 命令行={1}" -f $steps.candidateCount, $steps.forgeCommandLine)
} finally {
    try { [void][WinMsg]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) } catch { }
    Start-Sleep -Milliseconds 1200
    Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
}

$steps.seconds = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
$summary = [ordered]@{
    game = $Game
    workspace = $Workspace
    gui = $GuiExe
    seconds = $steps.seconds
    ghidraAvailable = $steps.ghidraAvailable
    ghidraStatus = $steps.ghidraStatus
    ghidraVersion = $steps.ghidraVersion
    ghidraElapsedSeconds = $steps.ghidraElapsedSeconds
    ghidraCandidateCount = $steps.ghidraCandidateCount
    ghidraExecutablePath = $steps.ghidraExecutablePath
    ghidraExecutableMatches = $steps.ghidraExecutableMatches
    requestedGhidraExe = $GhidraExe
    candidateCount = $steps.candidateCount
    analysisPath = $steps.analysisPath
    reportPath = $steps.reportPath
    reportExists = $steps.reportExists
    humanTodosPath = $steps.humanTodosPath
    pass = (($steps.ghidraAvailable -eq $true) -and ($steps.reportExists -eq $true) -and (($GhidraExe -eq '') -or ($steps.ghidraExecutableMatches -eq $true)))
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $OutDir ($OutPrefix + '.json')) -Encoding UTF8
$log | Set-Content -Path (Join-Path $OutDir ($OutPrefix + '.log')) -Encoding UTF8
if ($steps.analysisPath -and (Test-Path $steps.analysisPath)) {
    Copy-Item $steps.analysisPath (Join-Path $OutDir 'analysis.json') -Force
    if (Test-Path $steps.ghidraPath) { Copy-Item $steps.ghidraPath (Join-Path $OutDir 'analysis.json.ghidra') -Force }
}
if ($steps.reportPath -and (Test-Path $steps.reportPath)) { Copy-Item $steps.reportPath (Join-Path $OutDir 'report.md') -Force }
if ($steps.humanTodosPath -and (Test-Path $steps.humanTodosPath)) { Copy-Item $steps.humanTodosPath (Join-Path $OutDir 'human-todos.md') -Force }
Write-Host ("PASS={0} seconds={1} ghidraAvailable={2}" -f $summary.pass, $summary.seconds, $summary.ghidraAvailable)
if ($summary.pass) { exit 0 } else { exit 2 }
