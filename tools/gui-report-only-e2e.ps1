param(
    [Parameter(Mandatory = $true)][string]$Game,
    [string]$OutDir = 'D:\DSHWorkSpace\research\83-gui-report-only',
    [string]$Workspace = 'D:\DSHWorkSpace\_gui-report-only-ws',
    [string]$GuiExe = 'D:\DSHWorkSpace\src\Forge.Gui\bin\Release\net8.0-windows\Forge.Gui.exe',
    [int]$TimeoutSeconds = 480,
    [string]$OutPrefix = 'gui-report-only'
)

# 用 Win32 窗口消息驱动 GUI（不依赖焦点/UIA）：
#   找子窗口 → WM_SETTEXT 填「游戏目录/工作区」 → BM_CLICK「一键」。
# 目标：GUI 单独点「分析报告」、工作区没有缓存时，CLI 会自动跑一次只读
# Ghidra 高级分析再导出报告（0.2.43 行为），GUI 结果区给出报告路径与人工待办。
# 证据：工作区 report.md/human-todos.md + 结果区文本 + forge.exe 命令行里出现 --advanced。

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class WinMsg2 {
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
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, StringBuilder l);
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
  public static string WindowText(IntPtr h) { var sb = new StringBuilder(65536); SendMessage(h, 0x000D, (IntPtr)65535, sb); return sb.ToString(); }
  public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
  public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
}
"@

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$log = New-Object System.Collections.Generic.List[string]
$logPath = Join-Path $OutDir 'progressive.log'
function Log([string]$s) {
    $line = ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $s)
    $log.Add($line)
    Add-Content -Path $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

if (Test-Path $Workspace) { Remove-Item $Workspace -Recurse -Force }
New-Item -ItemType Directory -Path $Workspace | Out-Null

Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$startedAt = Get-Date
Log '用 WMI Win32_Process.Create 分离启动 GUI（不继承本进程管道）'
$guiCommand = '"' + $GuiExe + '"'
$guiDir = Split-Path -Parent $GuiExe
$start = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $guiCommand; CurrentDirectory = $guiDir }
Log ("WMI 启动返回 {0}，pid={1}" -f $start.ReturnValue, $start.ProcessId)

$proc = $null
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 180; $i++) {
    Start-Sleep -Milliseconds 500
    $proc = Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) {
        $hwnd = [WinMsg2]::FirstWindowTitle([uint32]$proc.Id, 'GalA11y Forge')
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
}
if ($hwnd -eq [IntPtr]::Zero) {
    Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
    throw 'GUI 主窗口没有出现（90 秒内）。'
}
Log ("GUI 就绪 pid={0} hwnd={1}" -f $proc.Id, $hwnd)

$steps = [ordered]@{}
$seenCommands = New-Object System.Collections.Generic.List[string]
try {
    $children = [WinMsg2]::Children($hwnd)
    $items = foreach ($h in $children) {
        [pscustomobject]@{ H = $h; Cls = [WinMsg2]::Cls($h); Text = [WinMsg2]::Text($h); Rect = [WinMsg2]::Rect($h) }
    }
    $edits = @($items | Where-Object { $_.Cls -like '*EDIT*' } | Sort-Object { $_.Rect.Top }, { $_.Rect.Left })
    $buttons = @($items | Where-Object { $_.Cls -like '*BUTTON*' })
    Log ("子窗口：{0} 个（Edit={1}，Button={2}）" -f $items.Count, $edits.Count, $buttons.Count)
    if ($edits.Count -lt 2) { throw '找不到「游戏目录/工作区」输入框。' }

    [void][WinMsg2]::SendMessage($edits[0].H, 0x000C, [IntPtr]::Zero, $Game)
    [void][WinMsg2]::SendMessage($edits[1].H, 0x000C, [IntPtr]::Zero, $Workspace)
    Log ("已填：游戏目录={0}；工作区={1}" -f $Game, $Workspace)
    $steps.filled = $true

    $reportButton = $buttons | Where-Object { $_.Text -like '*分析报告*' } | Select-Object -First 1
    if (-not $reportButton) { throw '找不到「分析报告」按钮。' }
    [void][WinMsg2]::SendMessage($reportButton.H, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) # BM_CLICK
    Log ("已点击分析报告（文本：{0}）" -f $reportButton.Text)
    $steps.reportClicked = $true

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $reportPath = $null
    $lastCommandTime = Get-Date
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $c = Get-CimInstance Win32_Process -Filter "Name = 'forge.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($c) {
            $lastCommandTime = Get-Date
            $cmd = ($c.CommandLine -replace '.*forge\.exe', 'forge.exe')
            if (-not $seenCommands.Contains($cmd)) {
                [void]$seenCommands.Add($cmd)
                Log ("forge.exe 正在跑：{0}" -f $cmd)
            }
        }
        $reportPath = Get-ChildItem -Path $Workspace -Recurse -Filter 'report.md' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($reportPath -and ((Get-Date) - $lastCommandTime).TotalSeconds -gt 3) { break }
    }

    $steps.reportPath = if ($reportPath) { $reportPath.FullName } else { $null }
    $steps.reportExists = [bool]$reportPath
    $steps.forgeCommands = @($seenCommands)
    $steps.sawReportCommand = @($seenCommands | Where-Object { $_ -like '*report*' }).Count -gt 0
    Log ("reportExists={0}，sawReportCommand={1}" -f $steps.reportExists, $steps.sawReportCommand)

    if ($reportPath) {
        $reportsRoot = Split-Path -Parent $reportPath
        $todos = Get-ChildItem -Path $reportsRoot -Filter 'human-todos.md' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        $steps.humanTodosPath = if ($todos) { $todos.FullName } else { $null }
        Log ("报告：{0}；人工待办：{1}" -f $steps.reportPath, $steps.humanTodosPath)
    }

    # 读结果区（最大的 EDIT，通常是只读多行文本框）
    $resultEdit = $edits | Sort-Object { ($_.Rect.Bottom - $_.Rect.Top) } -Descending | Select-Object -First 1
    $resultText = if ($resultEdit) { [WinMsg2]::WindowText($resultEdit.H) } else { '' }
    $steps.resultHeight = if ($resultEdit) { $resultEdit.Rect.Bottom - $resultEdit.Rect.Top } else { 0 }
    $steps.resultMentionsAdapterless = ($resultText -like '*适配器尚未接线*') -or ($resultText -like '*没有内置适配器*')
    $steps.resultMentionsReport = ($resultText -like '*分析报告*') -or ($resultText -like '*report*')
    $steps.resultSnippet = if ($resultText.Length -gt 600) { $resultText.Substring(0, 600) } else { $resultText }
    Log ("结果区高度={0}，提到无适配器={1}，提到报告={2}" -f $steps.resultHeight, $steps.resultMentionsAdapterless, $steps.resultMentionsReport)
} finally {
    try { [void][WinMsg2]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) } catch { }
    Start-Sleep -Milliseconds 1200
    Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
}

$steps.seconds = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
$summary = [ordered]@{
    game = $Game
    workspace = $Workspace
    gui = $GuiExe
    mode = 'report-only'
    seconds = $steps.seconds
    reportExists = $steps.reportExists
    reportPath = $steps.reportPath
    humanTodosPath = $steps.humanTodosPath
    sawReportCommand = $steps.sawReportCommand
    forgeCommands = $steps.forgeCommands
    resultMentionsAdapterless = $steps.resultMentionsAdapterless
    resultMentionsReport = $steps.resultMentionsReport
    resultHeight = $steps.resultHeight
    resultSnippet = $steps.resultSnippet
    pass = (($steps.reportExists -eq $true) -and ($steps.sawReportCommand -eq $true))
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutDir ($OutPrefix + '.json')) -Encoding UTF8
$log | Set-Content -Path (Join-Path $OutDir ($OutPrefix + '.log')) -Encoding UTF8
if ($steps.reportPath -and (Test-Path $steps.reportPath)) { Copy-Item $steps.reportPath (Join-Path $OutDir 'report.md') -Force }
if ($steps.humanTodosPath -and (Test-Path $steps.humanTodosPath)) { Copy-Item $steps.humanTodosPath (Join-Path $OutDir 'human-todos.md') -Force }
$analysis = Get-ChildItem -Path $Workspace -Recurse -Filter 'analysis.json' -File -ErrorAction SilentlyContinue | Select-Object -First 1
if ($analysis) {
    Copy-Item $analysis.FullName (Join-Path $OutDir 'analysis.json') -Force
    $ghidra = Join-Path (Split-Path -Parent $analysis.FullName) 'analysis-ghidra.json'
    if (Test-Path $ghidra) { Copy-Item $ghidra (Join-Path $OutDir 'analysis-ghidra.json') -Force }
}
Write-Host ("PASS={0} seconds={1} report={2} reportCommand={3}" -f $summary.pass, $summary.seconds, $summary.reportExists, $summary.sawReportCommand)
if ($summary.pass) { exit 0 } else { exit 2 }
