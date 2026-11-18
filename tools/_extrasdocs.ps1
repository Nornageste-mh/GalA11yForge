$ErrorActionPreference = 'Stop'
Set-Location 'D:\DSHWorkSpace'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$p = 'docs\26-外部读屏器原型结论-v0.md'
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
if ($t -notmatch 'external-reader-0.1.0') {
    $t += @"

## 打包记录（2026-11-15）

- 原型已发布为 **extras**（不属于主产品）：`releases/extras/external-reader-0.1.0/`。
- 内容：`Forge.ExternalReader.exe`（自包含单文件，162,876,605B，SHA256 F3348222…）+ WPF 原生库 + README + SHA256SUMS.txt。
- README 已写明“只保证标准控件/可访问文本；自绘读不到；OCR 美术字体不可用”；默认不启用 OCR。
- 发布包不随主 CLI；用户需要时单独下载/运行。
"@
    [IO.File]::WriteAllText($p, $t, $utf8)
}

$p = 'docs\33-0.2.4-计划.md'
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
$t = $t.Replace('| 3 | **外部读屏器 extras 归档** | R22 原型（src/Forge.ExternalReader）可运行但只保证标准控件 | `releases/v0.2.4/extras/` 或在 README 标注 | 不混入主链；标注"实验性、默认不启用" |', '| 3 | **外部读屏器 extras 归档** | ✅ 已完成（2026-11-15） | `releases/extras/external-reader-0.1.0/`（162MB 自包含 exe + README + SHA256SUMS） | 不混入主链；README 已标注"只保证标准控件" |')
[IO.File]::WriteAllText($p, $t, $utf8)

Write-Output 'docs updated'
Get-ChildItem 'research\40-kkz-mvp' -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 3 Name, LastWriteTime | Format-Table -AutoSize
Get-Process SenrenBanka -ErrorAction SilentlyContinue | Select-Object Id, StartTime
