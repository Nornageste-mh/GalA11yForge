<#
  uninstall.ps1 -- task-51 / R28  KKZ adapter v1: full rollback + 0-diff verification

  Steps
    1. stop the game (and the TTS sidecar spawned by install.ps1)
    2. undo exactly what install.ps1 did: delete proxy getSample.dll, rename getSample_orig.dll
       back to getSample.dll, delete plugin\kkz-adapter.ini
    3. remove game-created stray files inside plugin\ + game root that appeared during the run
       (never under savedata\, never *.xp3, never anything older than the snapshot)
    4. re-hash every file of snapshot-before.csv and compare; report extras/missing/mismatch
    5. write _kkz-adapter-e2e\rollback.log, copy evidence, exit 0 only when every diff is 0

  Exit codes: 0 clean / 21 restore error / 22 diff found
#>
[CmdletBinding()]
param(
  [string]$GameDir  = 'D:\Steam\steamapps\common\SenrenBanka',
  [string]$RepoRoot = '',
  [string]$WorkDir  = '',
  [switch]$KeepGameRunning,
  [switch]$NoRemoveExtra,
  # 默认不删除任何“安装前快照里没有”的散落文件；显式给 -RemoveExtras 才删除，
  # 且只删白名单扩展名、只删 plugin\ 与游戏根、绝不删 .sig/.exe/.xp3/.cf。
  # （task-51 事故后加固：快照键必须先解析成功，否则一律中止。）
  [switch]$RemoveExtras
)

$ErrorActionPreference = 'Continue'
if(-not $RepoRoot){ $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }
if(-not $WorkDir){ $WorkDir = Join-Path $RepoRoot '_kkz-adapter-e2e' }
$EvDir     = Join-Path $RepoRoot 'research\45-kkz-adapter-e2e\evidence'
$RunLog    = Join-Path $WorkDir 'uninstall.log'
$SnapBefore= Join-Path $WorkDir 'snapshot-before.csv'
$SnapAfter = Join-Path $WorkDir 'snapshot-after.csv'
$RollLog   = Join-Path $WorkDir 'rollback.log'
$StatePath = Join-Path $WorkDir 'state.json'
$PluginDir = Join-Path $GameDir 'plugin'
$TargetDll = Join-Path $PluginDir 'getSample.dll'
$OrigDll   = Join-Path $PluginDir 'getSample_orig.dll'
$IniPath   = Join-Path $PluginDir 'kkz-adapter.ini'
$CapDirLocal = Join-Path $env:LOCALAPPDATA 'GalA11yForge\kkz-adapter'
New-Item -ItemType Directory -Force -Path $WorkDir, $EvDir | Out-Null

$script:lines = New-Object System.Collections.Generic.List[string]
function Log([string]$m){
  $line = ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $m)
  Write-Host $line
  $script:lines.Add($line)
  Add-Content -LiteralPath $RunLog -Value $line -Encoding utf8
}
function Sha256([string]$p){
  if(-not (Test-Path -LiteralPath $p -PathType Leaf)){ return '' }
  try { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant() } catch { '' }
}
function Get-PeMachine([string]$p){
  try {
    $fs=[System.IO.File]::OpenRead($p)
    $br=New-Object System.IO.BinaryReader($fs)
    try {
      if($br.ReadUInt16() -ne 0x5A4D){ return -1 }
      $fs.Position=0x3C; $e=$br.ReadUInt32(); $fs.Position=$e
      if($br.ReadUInt32() -ne 0x00004550){ return -1 }
      return [int]$br.ReadUInt16()
    } finally { $fs.Dispose() }
  } catch { return -1 }
}

Log "=== task-51 kkz-adapter uninstall: begin"
$state = $null
if(Test-Path -LiteralPath $StatePath){
  try { $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json } catch { Log ("state_parse_fail " + $_.Exception.Message) }
}
if($state){ Log "state: pid=$($state.game_pid) cap=$($state.capture_file) speak_pid=$($state.speak_pid)" }

# --------------------------------------------- 0. snapshot gate (fail fast) --
# task-51 事故根因是快照键解析失败后仍继续执行删除。这里把“解析 + 有效性检查”
# 提到所有动作之前：快照无效就一个动作都不做（不杀进程、不还原、不删文件）。
$snapRaw = @()
$snap = @()
$snapInvalid = 0
if(Test-Path -LiteralPath $SnapBefore){
  $snapRaw = @(Import-Csv -LiteralPath $SnapBefore)
  # 兼容两种表头：旧安装脚本写 scope,relpath,...（行值其实是相对路径），
  # 早期版本读 $r.rel 取不到 → 键退化成 "plugin|"，会把所有文件当多余文件删掉（task-51 事故根因）。
  foreach($row in $snapRaw){
    $relText = ''
    if($row.PSObject.Properties.Name -contains 'relpath'){ $relText = [string]$row.PSObject.Properties['relpath'].Value }
    elseif($row.PSObject.Properties.Name -contains 'rel'){ $relText = [string]$row.PSObject.Properties['rel'].Value }
    $relText = $relText.Trim()
    if([string]::IsNullOrWhiteSpace($relText)){ $snapInvalid++; continue }
    $snap += [pscustomobject]@{
      scope  = [string]$row.scope
      rel    = $relText
      bytes  = [int64]$row.bytes
      sha256 = [string]$row.sha256
      mtime  = [string]$row.mtime_utc
    }
  }
} else {
  Log "snapshot_before_missing -> cannot verify 0-diff (ABORT, no action)"
  $script:lines.Add('RESULT=FAIL snapshot_missing')
  $script:lines | Add-Content -LiteralPath $RollLog -Encoding utf8
  exit 23
}
if($snapInvalid -gt 0){
  Log "snapshot_rows_invalid=$snapInvalid -> ABORT (no action taken)"
  $script:lines.Add('RESULT=FAIL snapshot_invalid')
  $script:lines | Add-Content -LiteralPath $RollLog -Encoding utf8
  exit 23
}
Log "snapshot_rows_loaded=$($snap.Count) (raw=$($snapRaw.Count))"
$snapKey = @{}
foreach($r in $snap){ $snapKey[($r.scope + '|' + $r.rel)] = $r }

# ------------------------------------------------------------- 1. stop ------
if(-not $KeepGameRunning){
  $procs = @(Get-Process -Name 'SenrenBanka*' -ErrorAction SilentlyContinue)
  foreach($p in $procs){
    Log "stop_game pid=$($p.Id)"
    try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { Log ("stop_game_fail pid=" + $p.Id + " " + $_.Exception.Message) }
  }
  Start-Sleep -Milliseconds 1500
  $still = @(Get-Process -Name 'SenrenBanka*' -ErrorAction SilentlyContinue)
  Log "game_processes_after_kill=$($still.Count)"
  if($state -and $state.speak_pid){
    $sp = Get-Process -Id ([int]$state.speak_pid) -ErrorAction SilentlyContinue
    if($sp){ Log "stop_tts pid=$($sp.Id)"; try { Stop-Process -Id $sp.Id -Force } catch {} }
  }
}

# ---------------------------------------------------------- 2. restore ------
$restoreOk = $true
if(Test-Path -LiteralPath $OrigDll){
  if(Test-Path -LiteralPath $TargetDll){
    $m = Get-PeMachine $TargetDll
    $h = Sha256 $TargetDll
    Log "remove_proxy getSample.dll machine=0x$('{0:X}' -f $m) sha=$h"
    $hmm = $h.Substring(0, [Math]::Min(16,$h.Length))
    if($state -and $state.proxy_sha256){
      $stm = ([string]$state.proxy_sha256).Substring(0,[Math]::Min(16,([string]$state.proxy_sha256).Length))
      if($hmm -ne $stm){ Log "warn_proxy_hash_differs_from_state actual=$h state=$($state.proxy_sha256) (removing anyway: orig exists)" }
    }
    try { Remove-Item -LiteralPath $TargetDll -Force -ErrorAction Stop } catch { Log ("remove_proxy_fail " + $_.Exception.Message); $restoreOk=$false }
  }
  try {
    Move-Item -LiteralPath $OrigDll -Destination $TargetDll -ErrorAction Stop
    Log "restore_rename getSample_orig.dll -> getSample.dll"
  } catch { Log ("restore_rename_fail " + $_.Exception.Message); $restoreOk=$false }
} else {
  Log "restore_skip getSample_orig.dll absent (already rolled back or never installed)"
}
if(Test-Path -LiteralPath $IniPath){
  try { Remove-Item -LiteralPath $IniPath -Force -ErrorAction Stop; Log "remove_ini kkz-adapter.ini" } catch { Log ("remove_ini_fail " + $_.Exception.Message); $restoreOk=$false }
}

# ------------------------------------------------------- 3. stray files -----
# only inside plugin\ and the game root; never savedata\; never archives
function Get-Items([string]$root, [string]$scope, [switch]$Recurse){
  $out=@()
  $gciArgs=@{ LiteralPath=$root; File=$true; ErrorAction='SilentlyContinue' }
  if($Recurse){ $gciArgs.Recurse=$true }
  foreach($f in Get-ChildItem @gciArgs){
    $rel=$f.FullName.Substring($root.Length).TrimStart('\')
    $out += [pscustomobject]@{ scope=$scope; rel=$rel; full=$f.FullName; bytes=$f.Length; sha=(Sha256 $f.FullName); mtime=$f.LastWriteTimeUtc.ToString('o') }
  }
  return $out
}
$snapAlreadyLoaded = $true   # 快照已在“0. snapshot gate”解析并校验过，这里直接复用

$extras = @()
if($snap.Count -gt 0){
  foreach($it in @((Get-Items $PluginDir 'plugin' -Recurse) + (Get-Items $GameDir 'root'))){
    if(-not $snapKey.ContainsKey(($it.scope + '|' + $it.rel))){ $extras += $it }
  }
}
Log "extras_found=$($extras.Count)"
$removed = 0
$kept = 0
# 允许删除的“多余文件”扩展名白名单；.sig 是 Steam/游戏完整性文件，永远不碰。
$extraAllowedExt = @('.dll','.ini','.log','.txt','.json','.tmp')
foreach($e in $extras){
  $ext = [System.IO.Path]::GetExtension($e.rel).ToLowerInvariant()
  if($NoRemoveExtra -or -not $RemoveExtras){ Log "extra_keep $($e.scope)|$($e.rel) bytes=$($e.bytes)"; $kept++; continue }
  if($ext -eq '.xp3' -or $ext -eq '.sig' -or $ext -eq '.exe' -or $ext -eq '.cf'){ Log "extra_keep_protected $($e.scope)|$($e.rel) bytes=$($e.bytes)"; $kept++; continue }
  if($extraAllowedExt -notcontains $ext){ Log "extra_keep_ext $($e.scope)|$($e.rel) ext=$ext bytes=$($e.bytes)"; $kept++; continue }
  if($e.bytes -gt 5MB){ Log "extra_keep_big $($e.scope)|$($e.rel) bytes=$($e.bytes)"; $kept++; continue }
  try { Remove-Item -LiteralPath $e.full -Force -ErrorAction Stop; $removed++; Log "extra_removed $($e.scope)|$($e.rel) bytes=$($e.bytes) sha=$($e.sha)" }
  catch { Log ("extra_remove_fail " + $e.full + " " + $_.Exception.Message); $kept++ }
}
Log "extras_removed=$removed extras_kept=$kept remove_extras=$([int]$RemoveExtras)"

# -------------------------------------------------------- 4. verify ---------
$after = @()
$after += Get-Items $PluginDir 'plugin' -Recurse
$after += Get-Items $GameDir   'root'
$afterKey = @{}
foreach($a in $after){ $afterKey[($a.scope + '|' + $a.rel)] = $a }
$sb=New-Object System.Text.StringBuilder
[void]$sb.AppendLine('scope,rel,bytes,sha256,mtime_utc')
foreach($a in $after){ [void]$sb.AppendLine(('{0},{1},{2},{3},{4}' -f $a.scope,$a.rel,$a.bytes,$a.sha,$a.mtime)) }
[System.IO.File]::WriteAllText($SnapAfter, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))

$missing=0; $mismatch=0; $extra=0; $ok=0
$diffLines = New-Object System.Collections.Generic.List[string]
foreach($r in $snap){
  $k = $r.scope + '|' + $r.rel
  if(-not $afterKey.ContainsKey($k)){
    $missing++; $diffLines.Add("MISSING  $k (before sha=$($r.sha256) bytes=$($r.bytes))"); continue
  }
  $a=$afterKey[$k]
  if($a.sha -ne $r.sha256){
    $mismatch++; $diffLines.Add("MISMATCH $k before=$($r.sha256) after=$($a.sha)")
  } elseif($a.bytes -ne [int64]$r.bytes){
    $mismatch++; $diffLines.Add("SIZEDIFF $k before=$($r.bytes) after=$($a.bytes)")
  } else { $ok++ }
}
foreach($a in $after){
  $k = $a.scope + '|' + $a.rel
  if(-not ($snap | Where-Object { ($_.scope + '|' + $_.rel) -eq $k })){
    $extra++; $diffLines.Add("EXTRA    $k bytes=$($a.bytes) sha=$($a.sha)")
  }
}
$savedataLines = @()
$sd = Join-Path $GameDir 'savedata'
if(Test-Path -LiteralPath $sd){
  foreach($s in Get-Items $sd 'savedata' -Recurse){
    $old = $snap | Where-Object { $_.scope -eq 'savedata' -and $_.rel -eq $s.rel } | Select-Object -First 1
    if(-not $old){ $savedataLines.Add("NEW      savedata|$($s.rel) bytes=$($s.bytes)") }
    elseif($old.sha256 -ne $s.sha){ $savedataLines.Add("CHANGED  savedata|$($s.rel) before=$($old.sha256) after=$($s.sha)") }
  }
}

[void]$sb.Clear()
[void]$sb.AppendLine('# task-51 kkz-adapter rollback.log')
[void]$sb.AppendLine(('generated={0}' -f (Get-Date).ToString('o')))
[void]$sb.AppendLine(('game_dir={0}' -f $GameDir))
[void]$sb.AppendLine(('snapshot_before={0}' -f $SnapBefore))
[void]$sb.AppendLine(('snapshot_after={0}' -f $SnapAfter))
[void]$sb.AppendLine(('scope=plugin+root (savedata tracked separately, game-written)'))
[void]$sb.AppendLine(('restore_actions_ok={0}' -f [int]$restoreOk))
[void]$sb.AppendLine(('files_ok={0}' -f $ok))
[void]$sb.AppendLine(('files_missing={0}' -f $missing))
[void]$sb.AppendLine(('files_mismatch={0}' -f $mismatch))
[void]$sb.AppendLine(('files_extra={0}' -f $extra))
[void]$sb.AppendLine(('extras_removed={0}' -f $removed))
[void]$sb.AppendLine(('savedata_diffs={0} (informational: written by the game itself)' -f $savedataLines.Count))
[void]$sb.AppendLine('--- per-file differences (plugin+root) ---')
if($diffLines.Count -eq 0){ [void]$sb.AppendLine('(none)') } else { foreach($d in $diffLines){ [void]$sb.AppendLine($d) } }
[void]$sb.AppendLine('--- savedata inventory ---')
if($savedataLines.Count -eq 0){ [void]$sb.AppendLine('(unchanged)') } else { foreach($d in $savedataLines){ [void]$sb.AppendLine($d) } }
[System.IO.File]::WriteAllText($RollLog, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Log "verify ok=$ok missing=$missing mismatch=$mismatch extra=$extra savedata_diffs=$($savedataLines.Count)"
Log "rollback.log=$RollLog"

# -------------------------------------------------------- 5. evidence -------
Get-ChildItem -Path (Join-Path $CapDirLocal 'capture-*.tsv') -ErrorAction SilentlyContinue | ForEach-Object {
  Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $EvDir ('capture-evidence-' + $_.Name.Replace('capture-',''))) -Force
}
foreach($f in @('kkz-adapter.log')){
  $p = Join-Path $CapDirLocal $f
  if(Test-Path -LiteralPath $p){ Copy-Item -LiteralPath $p -Destination (Join-Path $EvDir $f) -Force }
}
foreach($f in @($SnapBefore,$SnapAfter,$RollLog,$RunLog,(Join-Path $WorkDir 'install.log'),(Join-Path $WorkDir 'spoken.tsv'),(Join-Path $WorkDir 'speak.log'),(Join-Path $WorkDir 'capture-rows.json'),(Join-Path $WorkDir 'windows.json'),(Join-Path $StatePath))){
  if($f -and (Test-Path -LiteralPath $f)){ Copy-Item -LiteralPath $f -Destination $EvDir -Force }
}

$totalDiff = $missing + $mismatch + $extra
if(-not $restoreOk){ $script:lines.Add('RESULT=FAIL restore_actions_failed'); exit 21 }
if($totalDiff -ne 0){ $script:lines.Add("RESULT=FAIL diff=$totalDiff (missing=$missing mismatch=$mismatch extra=$extra)"); exit 22 }
$left = @(Get-Process -Name 'SenrenBanka*' -ErrorAction SilentlyContinue).Count
$script:lines.Add("RESULT=OK zero_diff=1 files_ok=$ok game_processes=$left")
$script:lines | Add-Content -LiteralPath $RollLog -Encoding utf8
Write-Host ("RESULT=OK zero_diff=1 files_ok=$ok game_processes=$left")
exit 0
