# R18 / task-21：安装并启动 Hook 探针，前台保持焦点跑一段时间，然后收集日志
# 用法：pwsh -File run_hook_test.ps1 [-Seconds 150] [-GameDir D:\_re\sh]
param(
    [int]$Seconds = 150,
    [string]$GameDir = 'D:\_re\sh',
    [string]$Label = 'v2',
    [switch]$AdvanceKeys
)

$ErrorActionPreference = 'Stop'
$probe = Join-Path $PSScriptRoot 'plugin\bin\Release\SiliconHeartReProbe.dll'
if (-not (Test-Path $probe)) { throw "probe dll missing: $probe" }

Get-Process SiliconHeart -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

New-Item -ItemType Directory -Force -Path (Join-Path $GameDir 'BepInEx\plugins') | Out-Null
Copy-Item $probe (Join-Path $GameDir 'BepInEx\plugins\') -Force
Remove-Item (Join-Path $GameDir 'BepInEx\re-probe.log') -ErrorAction SilentlyContinue

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    public static string ClickCenter(IntPtr h) {
        RECT r; if (!GetWindowRect(h, out r)) return "GetWindowRect failed";
        int x = (r.Left + r.Right) / 2, y = (r.Top + r.Bottom) / 2;
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        return x + "," + y;
    }
    public static void Space() {
        keybd_event(0x20, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        keybd_event(0x20, 0, 2, UIntPtr.Zero);
    }
}
'@
$script:advCount = 0

$p = Start-Process -FilePath (Join-Path $GameDir 'SiliconHeart.exe') -WorkingDirectory $GameDir -PassThru
"started pid={0} at {1}" -f $p.Id, (Get-Date -Format 'HH:mm:ss')

$deadline = (Get-Date).AddSeconds($Seconds)
$last = Get-Date
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $p.Refresh()
    if ($p.HasExited) { "PROCESS EXITED at " + (Get-Date -Format 'HH:mm:ss'); break }
    if ($p.MainWindowHandle -ne 0) {
        # 每 5 秒抢一次前台：默认 runInBackground=false 时失焦会暂停 player loop
        [Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
        # 对话推进：Naninovel 默认继续输入 = 左键点击 / Space；两者都发，避免本作只绑了鼠标
        if ($AdvanceKeys -and ((Get-Date) - $p.StartTime).TotalSeconds -gt 55) {
            $where = [Win]::ClickCenter($p.MainWindowHandle)
            [Win]::Space()
            if ($script:advCount++ -lt 3) { "advance click at $where" }
        }
    }
    $el = ((Get-Date) - $last).TotalSeconds
    if ($el -ge 30) {
        $last = Get-Date
        $probeLog = Join-Path $GameDir 'BepInEx\re-probe.log'
        $n = if (Test-Path $probeLog) { (Get-Content $probeLog).Count } else { 0 }
        "t+{0:N0}s cpu={1:N0}s ws={2}MB probe_lines={3}" -f ((Get-Date) - $p.StartTime).TotalSeconds, $p.CPU, ($p.WorkingSet64 / 1MB), $n
    }
}

# 收尾：请求退出（发 WM_CLOSE 走正常退出，让 OnApplicationQuit 写汇总；不行再强杀）
$p.Refresh()
if (-not $p.HasExited) {
    $p.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 8
    $p.Refresh()
    if (-not $p.HasExited) { "close timed out, killing"; $p.Kill() }
}

$logs = Join-Path $PSScriptRoot '..\..\..\research\20-il2cpp-re\evidence'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
Copy-Item (Join-Path $GameDir 'BepInEx\LogOutput.log') (Join-Path $logs "hook-$Label-bepinex.log") -Force
if (Test-Path (Join-Path $GameDir 'BepInEx\re-probe.log')) {
    Copy-Item (Join-Path $GameDir 'BepInEx\re-probe.log') (Join-Path $logs "hook-$Label-probe.log") -Force
}
if (Test-Path (Join-Path $GameDir 'BepInEx\re-lines.txt')) {
    Copy-Item (Join-Path $GameDir 'BepInEx\re-lines.txt') (Join-Path $logs "hook-$Label-lines.txt") -Force
}
"logs copied to $logs"
