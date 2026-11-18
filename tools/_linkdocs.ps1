$ErrorActionPreference = 'Stop'
Set-Location 'D:\DSHWorkSpace'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$p = 'docs\30-GUI真实读屏验收结论-v0.md'
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
if ($t -notmatch 'docs/32') {
    $t += @"

## 补充：给用户的人工验收清单

- 已整理成 10 分钟版清单：`docs/32-GUI人工验收清单.md`（含记录表与“不用当 bug 报”的已知限制）。
- 用户回来后可自行跑一遍，把“过/不过/备注”发回邮箱；阻断项按 P0 处理。
"@
    [IO.File]::WriteAllText($p, $t, $utf8)
}

$p = 'docs\22-打回预案-v0.2.md'
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
if ($t -notmatch 'docs/31') {
    $t += @"

## 演练记录

- 12 类场景的最小验证与结论：`docs/31-打回预案演练-v0.md`（8 类有实测可扛、3 类部分验证、1 类范围外）。
- GUI 场景的人工步骤：`docs/32-GUI人工验收清单.md`。
"@
    [IO.File]::WriteAllText($p, $t, $utf8)
}

Write-Output 'docs linked'
Get-ChildItem 'research\40-kkz-mvp' -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 3 Name, LastWriteTime |
    Format-Table -AutoSize
