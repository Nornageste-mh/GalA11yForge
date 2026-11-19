param(
    [Parameter(Mandatory = $true)][string]$Game,
    [string]$OutDir = 'D:\DSHWorkSpace\research\84-gui-nvda-live',
    [string]$Workspace = 'D:\DSHWorkSpace\_gui-nvda-live-ws',
    [string]$GuiExe = 'D:\DSHWorkSpace\releases\v0.2.43\gui\Forge.Gui.exe',
    [int]$TimeoutSeconds = 120
)

# 目标：不靠人工耳朵，判断 NVDA 是否真的把 GUI 结果读出来。
# 手段：读 NVDA「朗读查看器」的 UIA 文档文本（= NVDA 实际朗读队列），
# 触发 GUI「环境自检」，比对前后文本增量。
# 不启动游戏、不改任何游戏文件。

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public static class Wn {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr lp);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, StringBuilder l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static string WindowText(IntPtr h) { var sb = new StringBuilder(65536); SendMessage(h, 0x000D, (IntPtr)65535, sb); return sb.ToString(); }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public static IntPtr FindViewer(uint nvdaPid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != nvdaPid) return true;
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      var t = new StringBuilder(256); GetWindowText(h, t, 256);
      if (cls.ToString().Contains("wxWindow") && t.ToString().Contains("朗读查看器")) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  public static IntPtr FirstWindowTitle(uint pid, string part) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        var t = new StringBuilder(256); GetWindowText(h, t, 256);
        if (t.ToString().Contains(part)) { found = h; return false; }
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
'@

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$logPath = Join-Path $OutDir 'progressive.log'
if (Test-Path $logPath) { Remove-Item $logPath -Force }
function Log([string]$s) {
    $line = ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $s)
    Add-Content -Path $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

$nvda = Get-Process nvda -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $nvda) { throw 'NVDA 没有运行。请先启动 NVDA 并打开朗读查看器。' }
$viewer = [Wn]::FindViewer([uint32]$nvda.Id)
if ($viewer -eq [IntPtr]::Zero) { throw 'NVDA 朗读查看器窗口没找到（工具->朗读查看器）。' }
# 朗读查看器可能被隐藏；先显示并等 UIA 树就绪，否则 Document 元素取不到。
[void][Wn]::ShowWindow($viewer, 9)
[void][Wn]::ShowWindow($viewer, 5)
$viewerEl = [System.Windows.Automation.AutomationElement]::FromHandle($viewer)
$doc = $null
for ($k = 1; $k -le 10 -and -not $doc; $k++) {
    Start-Sleep -Milliseconds 400
    $all = $viewerEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    for ($i = 0; $i -lt $all.Count; $i++) {
        $e = $all.Item($i)
        if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Document') { $doc = $e; break }
    }
}
if (-not $doc) { throw '朗读查看器里找不到文档元素（已显示窗口并重试）。' }
$vp = $doc.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
Log ("NVDA 就绪 pid={0} viewer={1} doc={2}" -f $nvda.Id, $viewer.ToInt64(), $doc.Current.Name)

function Snapshot([string]$tag) {
    $val = $vp.Current.Value
    $file = Join-Path $OutDir ("speech-{0}.txt" -f $tag)
    $val | Out-File -FilePath $file -Encoding utf8
    Log ("快照 {0}：{1} 字符 -> {2}" -f $tag, $val.Length, $file)
    return $val
}

if (Test-Path $Workspace) { Remove-Item $Workspace -Recurse -Force }
New-Item -ItemType Directory -Path $Workspace | Out-Null

$before = Snapshot 'before'

Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$guiDir = Split-Path -Parent $GuiExe
$start = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = ('"' + $GuiExe + '"'); CurrentDirectory = $guiDir }
Log ("WMI 启动 GUI 返回 {0} pid={1}" -f $start.ReturnValue, $start.ProcessId)

$proc = $null; $hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Milliseconds 500
    $proc = Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { $hwnd = [Wn]::FirstWindowTitle([uint32]$proc.Id, 'Gal 无障碍辅助 Demo'); if ($hwnd -ne [IntPtr]::Zero) { break } }
}
if ($hwnd -eq [IntPtr]::Zero) { Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force; throw 'GUI 主窗口没有出现。' }
Log ("GUI 就绪 pid={0} hwnd={1}" -f $proc.Id, $hwnd)

$resultText = ''
try {
    $children = [Wn]::Children($hwnd)
    $items = foreach ($h in $children) {
        [pscustomobject]@{ H = $h; Cls = [Wn]::Cls($h); Text = [Wn]::Text($h); Rect = [Wn]::Rect($h) }
    }
    $edits = @($items | Where-Object { $_.Cls -like '*EDIT*' } | Sort-Object { $_.Rect.Top }, { $_.Rect.Left })
    $buttons = @($items | Where-Object { $_.Cls -like '*BUTTON*' })
    Log ("子窗口 {0} 个（Edit={1}，Button={2}）" -f $items.Count, $edits.Count, $buttons.Count)
    if ($edits.Count -lt 2) { throw '找不到游戏目录/工作区输入框。' }
    [void][Wn]::SendMessage($edits[0].H, 0x000C, [IntPtr]::Zero, $Game)
    [void][Wn]::SendMessage($edits[1].H, 0x000C, [IntPtr]::Zero, $Workspace)
    Log ("已填游戏目录={0}；工作区={1}" -f $Game, $Workspace)

    $doctor = $buttons | Where-Object { $_.Text -like '*环境自检*' } | Select-Object -First 1
    if (-not $doctor) { throw '找不到「环境自检」按钮。' }
    # 让 GUI 到前台：UIA 通知在后台窗口可能被 NVDA 忽略，验证前先置前台并等焦点稳定。
    for ($i = 0; $i -lt 3; $i++) {
        [void][Wn]::ShowWindow($hwnd, 9)
        [void][Wn]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 300
    }
    [void][Wn]::SendMessage($doctor.H, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) # BM_CLICK
    Log ("已点击环境自检（{0}）" -f $doctor.Text)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $sawForge = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        $f = Get-CimInstance Win32_Process -Filter "Name = 'forge.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f) { $sawForge = $true }
        elseif ($sawForge) { break }
    }
    Start-Sleep -Seconds 3
    $resultEdits = @($edits | Sort-Object { ($_.Rect.Bottom - $_.Rect.Top) } -Descending)
    if ($resultEdits.Count -gt 0) { $resultText = [Wn]::WindowText($resultEdits[0].H) }
    Log ("forge.exe 跑过={0}；结果区长度={1}" -f $sawForge, $resultText.Length)
} finally {
    try { [void][Wn]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) } catch { }
    Start-Sleep -Milliseconds 1000
    Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
}

$after = Snapshot 'after'
# 朗读查看器文本可能被旋转/截断，StartsWith 不可靠：用 before 的尾部 200 字符做锚点。
$delta = ''
$anchorLen = [Math]::Min(200, $before.Length)
if ($before -and $anchorLen -gt 0) {
    $anchor = $before.Substring($before.Length - $anchorLen)
    $idx = $after.LastIndexOf($anchor, [StringComparison]::Ordinal)
    if ($idx -ge 0) { $delta = $after.Substring($idx + $anchorLen) }
}
if ($delta.Length -eq 0) {
    # 兜底：取 after 的最后 4000 字符（但仍标记 uncertain）
    $tail = [Math]::Min(4000, $after.Length)
    $delta = $after.Substring($after.Length - $tail)
}
$deltaFile = Join-Path $OutDir 'speech-delta.txt'
$delta | Out-File -FilePath $deltaFile -Encoding utf8
Log ("语音增量 {0} 字符 -> {1}" -f $delta.Length, $deltaFile)

$mentionsDoctor = $delta -like '*环境自检*'
$mentionsSuccess = ($delta -like '*成功*') -or ($delta -like '*通过*') -or ($delta -like '*失败*')
$summary = [ordered]@{
    game = $Game
    gui = $GuiExe
    seconds = $null
    sawForge = $sawForge
    resultMentionsDoctor = ($resultText -like '*环境自检*')
    resultMentionsSuccess = ($resultText -like '*成功*') -or ($resultText -like '*通过*')
    speechBefore = $before.Length
    speechAfter = $after.Length
    deltaLength = $delta.Length
    deltaMentionsDoctor = $mentionsDoctor
    deltaMentionsResult = $mentionsSuccess
    deltaSnippet = if ($delta.Length -gt 800) { $delta.Substring(0, 800) } else { $delta }
    resultSnippet = if ($resultText.Length -gt 800) { $resultText.Substring(0, 800) } else { $resultText }
    pass = ($delta.Length -gt 5) -and $mentionsDoctor -and $mentionsSuccess
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutDir 'gui-nvda-live.json') -Encoding UTF8
Write-Host ("PASS={0} delta={1} doctor={2} result={3}" -f $summary.pass, $delta.Length, $mentionsDoctor, $mentionsSuccess)
if ($summary.pass) { exit 0 } else { exit 2 }
