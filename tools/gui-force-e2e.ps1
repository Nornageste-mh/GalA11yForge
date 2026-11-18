
param(
    [Parameter(Mandatory = $true)][string]$Game,
    [string]$Workspace = 'D:\DSHWorkSpace\_gui-force-ws',
    [string]$GuiExe = 'D:\DSHWorkSpace\src\Forge.Gui\bin\Release\net8.0-windows\Forge.Gui.exe',
    [string]$OutDir = 'D:\DSHWorkSpace\research\90-gui-force',
    [int]$TimeoutSeconds = 240,
    [string]$OutPrefix = 'gui-force'
)

# 用 Win32 窗口消息驱动 GUI：填游戏目录/工作区 → 勾「允许强制」→ 点「安装补丁」。
# 目标：R-106 闸门（计划里没有正文入口）在 GUI 里也能被 --force 越过，
# 且该 --force 真出现在 forge.exe 命令行里。
# PASS = 见过含 --force 的 install 命令行 且 结果区不是闸门拒绝文案。

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class GFWin {
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
  public static IntPtr FindDialog(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) { return true; }
      var sb = new StringBuilder(64); GetClassName(h, sb, 64);
      if (sb.ToString() != "#32770") { return true; }
      found = h; return false;
    }, IntPtr.Zero);
    return found;
  }
  public static IntPtr FindChildButton(IntPtr parent, string[] texts) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, (h, l) => {
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      var t = sb.ToString().Trim();
      foreach (var want in texts) {
        if (t.StartsWith(want, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static void PostClickCenter(IntPtr h) {
    RECT r = Rect(h);
    int cx = (r.Right - r.Left) / 2;
    int cy = (r.Bottom - r.Top) / 2;
    IntPtr lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
    PostMessage(h, 0x0201, (IntPtr)1, lp);
    System.Threading.Thread.Sleep(60);
    PostMessage(h, 0x0202, IntPtr.Zero, lp);
  }
  public static IntPtr FindChildByTextContains(IntPtr parent, string needle) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, (h, l) => {
      var sb = new StringBuilder(1024); GetWindowText(h, sb, 1024);
      if (sb.ToString().Contains(needle, StringComparison.Ordinal)) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
}
"@

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$logPath = Join-Path $OutDir ($OutPrefix + '.log')
function Log([string]$s) {
    $line = ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $s)
    Add-Content -Path $logPath -Value $line -Encoding UTF8
    Write-Host $line
}
Set-Content -Path $logPath -Value '' -Encoding UTF8

if (Test-Path $Workspace) { Remove-Item $Workspace -Recurse -Force }
New-Item -ItemType Directory -Path $Workspace | Out-Null

Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$startedAt = Get-Date
Log 'WMI 启动 GUI（分离管道）'
$guiDir = Split-Path -Parent $GuiExe
$start = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = ('"' + $GuiExe + '"'); CurrentDirectory = $guiDir }
Log ('WMI 返回 {0}' -f $start.ReturnValue)

$proc = $null
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Milliseconds 500
    $proc = Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { $hwnd = [GFWin]::FirstWindowTitle([uint32]$proc.Id, 'GalA11y Forge'); if ($hwnd -ne [IntPtr]::Zero) { break } }
}
if ($hwnd -eq [IntPtr]::Zero) { Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force; throw 'GUI 没出现。' }
Log ('GUI 就绪 pid={0} hwnd={1}' -f $proc.Id, $hwnd)

$steps = [ordered]@{}
try {
    $children = [GFWin]::Children($hwnd)
    $items = foreach ($h in $children) {
        [pscustomobject]@{ H = $h; Cls = [GFWin]::Cls($h); Text = [GFWin]::Text($h); Rect = [GFWin]::Rect($h) }
    }
    $edits = @($items | Where-Object { $_.Cls -like '*EDIT*' } | Sort-Object { $_.Rect.Top }, { $_.Rect.Left })
    $buttons = @($items | Where-Object { $_.Cls -like '*BUTTON*' })
    Log ('子窗口 {0} 个：Edit={1} Button={2}' -f $items.Count, $edits.Count, $buttons.Count)
    if ($edits.Count -lt 2) { throw '找不到游戏目录/工作区输入框。' }

    [void][GFWin]::SendMessage($edits[0].H, 0x000C, [IntPtr]::Zero, $Game)
    [void][GFWin]::SendMessage($edits[1].H, 0x000C, [IntPtr]::Zero, $Workspace)
    Log ('已填 游戏目录={0}；工作区={1}' -f $Game, $Workspace)

    $force = $buttons | Where-Object { $_.Text -like '*允许强制*' } | Select-Object -First 1
    if (-not $force) { throw '找不到「允许强制」复选框。' }
    # WinForms 的 CheckBox 不响应 BM_CLICK/BM_SETCHECK（探针实测），必须投真实鼠标消息。
    $fr = [GFWin]::Rect($force.H)
    $fcx = [int](($fr.Right - $fr.Left) / 2)
    $fcy = [int](($fr.Bottom - $fr.Top) / 2)
    $flp = [IntPtr](($fcy -shl 16) -bor $fcx)
    [void][GFWin]::PostMessage($force.H, 0x0201, [IntPtr]1, $flp)
    Start-Sleep -Milliseconds 80
    [void][GFWin]::PostMessage($force.H, 0x0202, [IntPtr]::Zero, $flp)
    Start-Sleep -Milliseconds 400
    Log ('已投鼠标点击「允许强制」（文本：{0}，尺寸 {1}x{2}）' -f $force.Text, ($fr.Right - $fr.Left), ($fr.Bottom - $fr.Top))
    $steps.forceClicked = $true

    # 通过提示行确认复选框真的勾上了（避免「点了但没生效」时静默跑出误判）。
    function Read-ForceHint {
        $hintH = [GFWin]::FindChildByTextContains($hwnd, '就绪')
        if ($hintH -eq [IntPtr]::Zero) { return '' }
        return [GFWin]::Text($hintH)
    }
    $forceChecked = $false
    for ($i = 0; $i -lt 6 -and -not $forceChecked; $i++) {
        Start-Sleep -Milliseconds 300
        $hintText = Read-ForceHint
        if ($hintText -like '*允许强制*') { $forceChecked = $true; break }
        [void][GFWin]::PostMessage($force.H, 0x0201, [IntPtr]1, $flp)
        Start-Sleep -Milliseconds 80
        [void][GFWin]::PostMessage($force.H, 0x0202, [IntPtr]::Zero, $flp)
    }
    Log ('允许强制勾选状态：{0}' -f $forceChecked)
    $steps.forceChecked = $forceChecked

    $install = $buttons | Where-Object { $_.Text -like '*安装补丁*' } | Select-Object -First 1
    if (-not $install) { throw '找不到「安装补丁」按钮。' }
    # 长命令按钮不要用 BM_CLICK（SendMessage 会阻塞到命令结束，实测 8 分钟）；投鼠标消息即可。
    $ir = [GFWin]::Rect($install.H)
    $icx = [int](($ir.Right - $ir.Left) / 2)
    $icy = [int](($ir.Bottom - $ir.Top) / 2)
    $ilp = [IntPtr](($icy -shl 16) -bor $icx)
    [void][GFWin]::PostMessage($install.H, 0x0201, [IntPtr]1, $ilp)
    Start-Sleep -Milliseconds 80
    [void][GFWin]::PostMessage($install.H, 0x0202, [IntPtr]::Zero, $ilp)
    Log ('已点击安装补丁（文本：{0}，尺寸 {1}x{2}）' -f $install.Text, ($ir.Right - $ir.Left), ($ir.Bottom - $ir.Top))
    $steps.installClicked = $true

    # 安装/回滚有二次确认模态框（默认「否」）；必须点「是」，否则 GUI 会一直挂着。
    $confirmed = $false
    for ($i = 0; $i -lt 60 -and -not $confirmed; $i++) {
        Start-Sleep -Milliseconds 250
        $dlg = [GFWin]::FindDialog([uint32]$proc.Id)
        if ($dlg -eq [IntPtr]::Zero) { continue }
        $yes = [GFWin]::FindChildButton($dlg, @('是(&Y)', '是', '&Yes', 'Yes'))
        if ($yes -ne [IntPtr]::Zero) { [GFWin]::PostClickCenter($yes); $confirmed = $true }
    }
    Log ('二次确认：confirmed={0}' -f $confirmed)
    $steps.confirmedDialog = $confirmed

    $seenCommands = New-Object System.Collections.Generic.List[string]
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastSeen = Get-Date
    $sawForce = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        $procs = @(Get-CimInstance Win32_Process -Filter "Name = 'forge.exe'" -ErrorAction SilentlyContinue)
        foreach ($c in $procs) {
            $cmd = ($c.CommandLine -replace '.*forge\.exe', 'forge.exe')
            if (-not $seenCommands.Contains($cmd)) {
                [void]$seenCommands.Add($cmd)
                Log ('forge.exe：{0}' -f $cmd)
                if ($cmd -like '*--force*') { $sawForce = $true }
            }
            $lastSeen = Get-Date
        }
        $resultProbe = $edits | Sort-Object { ($_.Rect.Bottom - $_.Rect.Top) } -Descending | Select-Object -First 1
        $probeText = if ($resultProbe) { [GFWin]::WindowText($resultProbe.H) } else { '' }
        if ($procs.Count -eq 0 -and $seenCommands.Count -gt 0 -and $probeText.Length -gt 40) {
            Start-Sleep -Milliseconds 800
            $probeText = if ($resultProbe) { [GFWin]::WindowText($resultProbe.H) } else { '' }
            if ($probeText.Length -gt 40) { break }
        }
    }

    $resultEdit = $edits | Sort-Object { ($_.Rect.Bottom - $_.Rect.Top) } -Descending | Select-Object -First 1
    $resultText = if ($resultEdit) { [GFWin]::WindowText($resultEdit.H) } else { '' }
    $steps.forgeCommands = @($seenCommands)
    $steps.sawForce = $sawForce
    $steps.resultMentionsGate = ($resultText -like '*没有识别出正文入口*')
    # 0.2.50：GUI 要能正确显示 CLI 的 stderr（中文不能乱码）。
    $steps.stderrReadable = ($resultText -like '*正文入口*') -or ($resultText -like '*[注意]*')
    $steps.resultSnippet = if ($resultText.Length -gt 800) { $resultText.Substring(0, 800) } else { $resultText }
    Log ('sawForce={0}；结果区提到闸门={1}；结果长度={2}' -f $sawForce, $steps.resultMentionsGate, $resultText.Length)
} finally {
    try { [void][GFWin]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) } catch { }
    Start-Sleep -Milliseconds 1200
    Get-Process -Name Forge.Gui -ErrorAction SilentlyContinue | Stop-Process -Force
}

$steps.seconds = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
$pass = ($steps.sawForce -eq $true) -and ($steps.forceChecked -eq $true) -and ($steps.resultMentionsGate -ne $true)
$summary = [ordered]@{
    game = $Game
    workspace = $Workspace
    gui = $GuiExe
    seconds = $steps.seconds
    stderrReadable = $steps.stderrReadable
    forceClicked = $steps.forceClicked
    forceChecked = $steps.forceChecked
    confirmedDialog = $steps.confirmedDialog
    sawForce = $steps.sawForce
    resultMentionsGate = $steps.resultMentionsGate
    forgeCommands = $steps.forgeCommands
    resultSnippet = $steps.resultSnippet
    pass = $pass
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutDir ($OutPrefix + '.json')) -Encoding UTF8
Write-Host ('PASS={0} seconds={1} sawForce={2} gateBlocked={3}' -f $pass, $steps.seconds, $steps.sawForce, $steps.resultMentionsGate)
if ($pass) { exit 0 } else { exit 2 }
