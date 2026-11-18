param(
    [string]$ProcessName = 'Forge.Gui',
    [string]$Out = 'D:\DSHWorkSpace\research\64-gui-uia-check\gui-uia.json'
)
# 用法：先启动 Forge.Gui.exe，再运行本脚本。只读 UIA 控件树，不点击、不输入。
# 外层建议加超时（历史上 UiaDump.exe 会挂死）：Start-Job + Wait-Job -Timeout 45。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "process not found: $ProcessName" }
$main = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
if (-not $main) { throw "main window not found for pid $($proc.Id)" }

$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$rows = New-Object System.Collections.Generic.List[object]

function Walk($el, $depth) {
    if ($el -eq $null -or $rows.Count -ge 400) { return }
    try { $p = $el.Current } catch { $p = $null }
    if ($p -ne $null) {
        $r = $p.BoundingRectangle
        $rows.Add([pscustomobject]@{
            depth = $depth
            name = [string]$p.Name
            controlType = [string]$p.ControlType.ProgrammaticName
            automationId = [string]$p.AutomationId
            className = [string]$p.ClassName
            helpText = [string]$p.HelpText
            acceleratorKey = [string]$p.AcceleratorKey
            accessKey = [string]$p.AccessKey
            focusable = [bool]$p.IsKeyboardFocusable
            enabled = [bool]$p.IsEnabled
            left = [math]::Round($r.Left); top = [math]::Round($r.Top); width = [math]::Round($r.Width); height = [math]::Round($r.Height)
        })
    }
    try { $child = $walker.GetFirstChild($el) } catch { $child = $null }
    while ($child -ne $null) {
        Walk $child ($depth + 1)
        try { $child = $walker.GetNextSibling($child) } catch { $child = $null }
    }
}

Walk $main 0
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$rows | ConvertTo-Json -Depth 4 | Out-File -FilePath $Out -Encoding utf8
$rows | Where-Object { $_.name -ne '' } | ForEach-Object {
    '{0}{1} | {2} | autoId={3} | access={4} | focus={5} | enabled={6}' -f ('  ' * $_.depth), $_.controlType.Replace('ControlType.',''), $_.name, $_.automationId, $_.accessKey, $_.focusable, $_.enabled
} | Select-Object -First 120
'count=' + $rows.Count