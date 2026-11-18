#requires -Version 7.0
<#
  KKZ 适配器 uninstall.ps1 的沙箱回归测试（不碰真实游戏目录）。

  背景：task-51 事故——install.ps1 写的快照表头是 scope,relpath,... 而行值是相对路径，
  旧 uninstall.ps1 读 $r.rel → 键退化成 "plugin|" → 把所有文件当“多余文件”删除。
  本测试锁住三条行为：
    T1 默认不带 -RemoveExtras：任何“快照外文件”都不删（安全失败，退出码 22）。
    T2 显式 -RemoveExtras：只删白名单扩展名，.sig/.exe/.xp3 永远保留。
    T3 快照行 rel 为空：立即中止（退出码 23），一个文件都不删。

  用法：pwsh -NoProfile -File src\experiments\kkz-adapter\tests\uninstall-sandbox.ps1
  成功时最后一行输出 SANDBOX=PASS。
#>
[CmdletBinding()]
param([string]$Root = '')

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Join-Path $env:TEMP ('kkz-uninstall-sandbox-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
$Root = [IO.Path]::GetFullPath($Root)
if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Root | Out-Null

$uninstall = Join-Path $PSScriptRoot '..\uninstall.ps1'
if (-not (Test-Path -LiteralPath $uninstall)) { throw "找不到 uninstall.ps1：$uninstall" }

function Write-TextFile([string]$p, [string]$text) {
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $p) | Out-Null
  [IO.File]::WriteAllText($p, $text, (New-Object System.Text.UTF8Encoding($true)))
}
function New-Case([string]$name) {
  $caseRoot = Join-Path $Root $name
  $game = Join-Path $caseRoot 'game'
  $work = Join-Path $caseRoot 'work'
  $plugin = Join-Path $game 'plugin'
  New-Item -ItemType Directory -Force -Path $plugin, $work | Out-Null
  return [pscustomobject]@{ Name = $name; Root = $caseRoot; Game = $game; Work = $work; Plugin = $plugin }
}
function Snapshot([string]$path, [string[]]$lines) {
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine('scope,relpath,bytes,sha256,mtime_utc')
  foreach ($l in $lines) { [void]$sb.AppendLine($l) }
  Write-TextFile $path $sb.ToString()
}
function Sha([string]$p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant() } else { '' } }

function Seed-Case($c) {
  Write-TextFile (Join-Path $c.Plugin 'a.dll') 'unchanged-snapshot-file'
  Write-TextFile (Join-Path $c.Plugin 'getSample_orig.dll') 'original-plugin-dll'
  Write-TextFile (Join-Path $c.Plugin 'getSample.dll') 'proxy-plugin-dll'
  Write-TextFile (Join-Path $c.Plugin 'kkz-adapter.ini') 'proxy-ini'
  Write-TextFile (Join-Path $c.Plugin 'extra.dll') 'stray-dll'
  Write-TextFile (Join-Path $c.Game 'SenrenBanka.exe') 'game-exe'
  Write-TextFile (Join-Path $c.Game 'data.xp3') 'big-archive'
  Write-TextFile (Join-Path $c.Game 'logo.sig') 'sig-file'
  Write-TextFile (Join-Path $c.Game 'extra.ini') 'stray-ini'
}

function Run-Uninstall($c, [switch]$RemoveExtras) {
  $log = Join-Path $c.Work 'uninstall.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $ps = @(
    '-NoProfile','-ExecutionPolicy','Bypass','-File', $uninstall,
    '-GameDir', $c.Game,
    '-RepoRoot', $c.Root,
    '-WorkDir', $c.Work,
    '-KeepGameRunning'
  )
  if ($RemoveExtras) { $ps += '-RemoveExtras' }
  $out = & pwsh @ps 2>&1
  $code = $LASTEXITCODE
  return [pscustomobject]@{ ExitCode = $code; Output = ($out -join "`n"); Log = $log }
}

function Assert([bool]$cond, [string]$msg) {
  if (-not $cond) { throw "断言失败：$msg" }
  Write-Host "  ok  $msg"
}

$failures = @()
try {
  # ---------------- T1: 默认不删除任何快照外文件 ----------------
  Write-Host '[T1] 默认（不带 -RemoveExtras）：安全失败、一个都不删'
  $c1 = New-Case 't1-default'
  Seed-Case $c1
  $origGetSampleSha = Sha (Join-Path $c1.Plugin 'getSample_orig.dll')
  Snapshot (Join-Path $c1.Work 'snapshot-before.csv') @(
    ('plugin,getSample.dll,16,{0},2026-01-01T00:00:00.0000000Z' -f 'AAAA'),
    ('plugin,a.dll,22,{0},2026-01-01T00:00:00.0000000Z' -f 'BBBB'),
    ('root,data.xp3,11,{0},2026-01-01T00:00:00.0000000Z' -f 'CCCC')
  )
  Write-TextFile (Join-Path $c1.Work 'state.json') ('{{"game_pid":0,"proxy_sha256":"{0}","speak_pid":0}}' -f (Sha (Join-Path $c1.Plugin 'getSample.dll')))
  $r1 = Run-Uninstall $c1
  Write-Host ("  exit=" + $r1.ExitCode)
  Assert ($r1.ExitCode -eq 22) 'T1 退出码 22（有差异、安全失败）'
  Assert (Test-Path (Join-Path $c1.Plugin 'extra.dll')) 'T1 extra.dll 未被删除'
  Assert (Test-Path (Join-Path $c1.Game 'extra.ini')) 'T1 extra.ini 未被删除'
  Assert (Test-Path (Join-Path $c1.Game 'logo.sig')) 'T1 logo.sig 未被删除'
  Assert (Test-Path (Join-Path $c1.Plugin 'getSample.dll')) 'T1 getSample.dll 已从 orig 还原'
  Assert ((Sha (Join-Path $c1.Plugin 'getSample.dll')) -eq $origGetSampleSha) 'T1 还原后的 getSample.dll 哈希与 orig 一致'
  Assert (-not (Test-Path (Join-Path $c1.Plugin 'kkz-adapter.ini'))) 'T1 kkz-adapter.ini 被删除（安装产物）'

  # ---------------- T2: -RemoveExtras 只删白名单 ----------------
  Write-Host '[T2] -RemoveExtras：只删白名单，.sig/.exe/.xp3 保留'
  $c2 = New-Case 't2-remove-extras'
  Seed-Case $c2
  Snapshot (Join-Path $c2.Work 'snapshot-before.csv') @(
    ('plugin,getSample.dll,16,{0},2026-01-01T00:00:00.0000000Z' -f 'AAAA'),
    ('plugin,a.dll,22,{0},2026-01-01T00:00:00.0000000Z' -f 'BBBB'),
    ('root,data.xp3,11,{0},2026-01-01T00:00:00.0000000Z' -f 'CCCC')
  )
  Write-TextFile (Join-Path $c2.Work 'state.json') ('{{"game_pid":0,"proxy_sha256":"{0}","speak_pid":0}}' -f (Sha (Join-Path $c2.Plugin 'getSample.dll')))
  $r2 = Run-Uninstall $c2 -RemoveExtras
  Write-Host ("  exit=" + $r2.ExitCode)
  Assert (-not (Test-Path (Join-Path $c2.Plugin 'extra.dll'))) 'T2 extra.dll 被删除（白名单）'
  Assert (-not (Test-Path (Join-Path $c2.Game 'extra.ini'))) 'T2 extra.ini 被删除（白名单）'
  Assert (Test-Path (Join-Path $c2.Game 'logo.sig')) 'T2 logo.sig 保留（保护扩展名）'
  Assert (Test-Path (Join-Path $c2.Game 'SenrenBanka.exe')) 'T2 SenrenBanka.exe 保留'
  Assert (Test-Path (Join-Path $c2.Game 'data.xp3')) 'T2 data.xp3 保留'
  Assert ($r2.ExitCode -eq 22) 'T2 仍因保护文件算“多余”而安全失败（22）'

  # ---------------- T3: 快照损坏 -> 中止，一个动作都不做 ----------------
  Write-Host '[T3] 快照 rel 为空：中止、不杀进程/不还原/不删任何文件'
  $c3 = New-Case 't3-bad-snapshot'
  Seed-Case $c3
  $proxyShaBefore = Sha (Join-Path $c3.Plugin 'getSample.dll')
  Snapshot (Join-Path $c3.Work 'snapshot-before.csv') @(
    'plugin,,16,AAAA,2026-01-01T00:00:00.0000000Z',
    'root,,11,CCCC,2026-01-01T00:00:00.0000000Z'
  )
  $r3 = Run-Uninstall $c3
  Write-Host ("  exit=" + $r3.ExitCode)
  Assert ($r3.ExitCode -eq 23) 'T3 退出码 23（快照无效）'
  assert ($r3.Output -match 'snapshot_rows_invalid=\d+') 'T3 日志含 snapshot_rows_invalid=N'
  foreach ($f in @('extra.dll','a.dll','getSample_orig.dll','getSample.dll','kkz-adapter.ini')) {
    Assert (Test-Path (Join-Path $c3.Plugin $f)) "T3 plugin\$f 未被删除"
  }
  foreach ($f in @('SenrenBanka.exe','data.xp3','logo.sig','extra.ini')) {
    Assert (Test-Path (Join-Path $c3.Game $f)) "T3 root\$f 未被删除"
  }
  Assert ((Sha (Join-Path $c3.Plugin 'getSample.dll')) -eq $proxyShaBefore) 'T3 代理 DLL 未被还原/改动（fail fast）'
}
catch {
  $failures += $_.Exception.Message
}
finally {
  if ($failures.Count -eq 0) {
    Write-Host "SANDBOX=PASS root=$Root"
    exit 0
  } else {
    Write-Host ("SANDBOX=FAIL " + ($failures -join ' | '))
    Write-Host "证据目录保留：$Root"
    exit 1
  }
}
