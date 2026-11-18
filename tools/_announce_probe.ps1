
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NvEv {
  [DllImport("user32.dll")] public static extern void NotifyWinEvent(uint ev, IntPtr hwnd, int objId, int childId);
}
"@
$f = New-Object System.Windows.Forms.Form
$f.Text = 'A11yAnnounceProbe'
$f.Width = 420; $f.Height = 220
$l = New-Object System.Windows.Forms.Label
$l.Text = '等待'
$l.Name = 'statusProbe'
$l.AutoSize = $true; $l.Location = New-Object System.Drawing.Point(20, 20)
$f.Controls.Add($l)
$f.Show()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 800
# 1) UIA 通知（WinForms RaiseAutomationNotification）
try {
  $l.AccessibilityObject.RaiseAutomationNotification(
    [System.Windows.Forms.Automation.AutomationNotificationKind]::ActionCompleted,
    [System.Windows.Forms.Automation.AutomationNotificationProcessing]::MostRecent,
    '探测UIA通知：环境自检成功')
  'uia-notify sent'
} catch { 'uia-notify error: ' + $_.Exception.GetType().Name }
Start-Sleep -Milliseconds 1500
# 2) WinEvent 名称改变（NVDA 对焦点控件名称变化通常会念）
$l.Text = '更新'
[void][NvEv]::NotifyWinEvent(0x800C, $l.Handle, -4, 0)
Start-Sleep -Milliseconds 1500
# 3) 直接把焦点给 label 再改文本（尽力念）
$l.Focus()
$l.Text = '最新播报：分析报告成功'
[void][NvEv]::NotifyWinEvent(0x800C, $l.Handle, -4, 0)
Start-Sleep -Milliseconds 2000
'probe-done'
$f.Close()
