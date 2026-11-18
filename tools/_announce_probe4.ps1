
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$f = New-Object System.Windows.Forms.Form
$f.Text = 'A11yProbe4'
$f.Width = 460; $f.Height = 240
$b = New-Object System.Windows.Forms.Button
$b.Text = '按钮'; $b.Location = New-Object System.Drawing.Point(20, 20)
$f.Controls.Add($b)
$t = New-Object System.Windows.Forms.TextBox
$t.ReadOnly = $true; $t.TabStop = $false
$t.Location = New-Object System.Drawing.Point(20, 80); $t.Width = 400
$f.Controls.Add($t)
$f.Show()
$f.Activate()
$f.TopMost = $true
[System.Windows.Forms.Application]::DoEvents()
$b.Focus()
Start-Sleep -Milliseconds 1200
$t.Text = '结果播报：环境自检成功'
$t.Focus()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 2500
'probe4-done'
$f.Close()
