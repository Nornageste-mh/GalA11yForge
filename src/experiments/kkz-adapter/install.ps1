<#
  install.ps1 -- task-51 / R28  KKZ adapter v1: install + launch + capture (experimental)

  What it does
    1. Preflight gates  : game exe version 1.2.0.3, plugin\getSample.dll hash, clean state, no running game
    2. Snapshot         : per-file SHA-256 of plugin\ (all files) and game root (files) -> _kkz-adapter-e2e\snapshot-before.csv
    3. Install          : getSample.dll -> getSample_orig.dll (rename), proxy DLL -> plugin\getSample.dll,
                          plugin\kkz-adapter.ini (UTF-16LE), nothing else in the game dir is touched
    4. Launch           : steam://rungameid/1144400, wait for process + main window + capture file
    5. Capture          : run N seconds, 2 screenshots, count Chinese TSV rows, optionally spawn speak.ps1
    6. Close            : WM_CLOSE, then forced kill if still alive (rollback is uninstall.ps1's job)

  Exit codes: 0 ok / 10 gate / 11 snapshot / 12 install / 13 launch / 14 no capture rows / 15 screenshot
  All writes outside the game dir go to _kkz-adapter-e2e\ and %LOCALAPPDATA%\GalA11yForge\kkz-adapter\.
#>
[CmdletBinding()]
param(
  [string]$GameDir      = 'D:\Steam\steamapps\common\SenrenBanka',
  [string]$DllPath      = '',
  [string]$RepoRoot     = '',
  [string]$WorkDir      = '',
  [string]$ExpectedExeVersion  = '1.2.0.3',
  [string]$ExpectedOrigSha256  = 'E190AE1B17F82B106FA466E9AB985364E018C8ACD46B39B2B33B895133C2F30C',
  [int]$RunSeconds      = 210,
  [int]$MinRows         = 3,
  [switch]$NoLaunch,
  [switch]$KeepRunning,
  [bool]$WithTts        = $true
)

$ErrorActionPreference = 'Stop'
$script:fail = 0

# ------------------------------------------------------------------ paths ---
if(-not $RepoRoot){ $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }
if(-not $WorkDir){ $WorkDir = Join-Path $RepoRoot '_kkz-adapter-e2e' }
if(-not $DllPath){ $DllPath = Join-Path $RepoRoot 'tools\kkz-proxy\build\kkz_proxy.dll' }
$EvDir = Join-Path $RepoRoot 'research\45-kkz-adapter-e2e\evidence'
New-Item -ItemType Directory -Force -Path $WorkDir, $EvDir | Out-Null
$RunLog      = Join-Path $WorkDir 'install.log'
$StatePath   = Join-Path $WorkDir 'state.json'
$SnapBefore  = Join-Path $WorkDir 'snapshot-before.csv'
$CapDirLocal = Join-Path $env:LOCALAPPDATA 'GalA11yForge\kkz-adapter'
$PluginDir   = Join-Path $GameDir 'plugin'
$TargetDll   = Join-Path $PluginDir 'getSample.dll'
$OrigDll     = Join-Path $PluginDir 'getSample_orig.dll'
$IniPath     = Join-Path $PluginDir 'kkz-adapter.ini'
$ExePath     = Join-Path $GameDir 'SenrenBanka.exe'

function Log([string]$msg){
  $line = ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
  Write-Host $line
  Add-Content -LiteralPath $RunLog -Value $line -Encoding utf8
}
function Die([int]$code, [string]$why){
  Log ("FAIL exit=$code $why")
  exit $code
}
function Sha256([string]$p){
  if(-not (Test-Path -LiteralPath $p -PathType Leaf)){ return '' }
  (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant()
}

# ------------------------------------------------------- native helpers -----
$cs = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class KkzW {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static string TitleOf(IntPtr h){ var sb=new StringBuilder(512); GetWindowTextW(h,sb,512); return sb.ToString(); }
  public static string ClassOf(IntPtr h){ var sb=new StringBuilder(256); GetClassNameW(h,sb,256); return sb.ToString(); }
  public static List<IntPtr> WindowsOf(uint pid){
    var list=new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l){
      uint p; GetWindowThreadProcessId(h, out p);
      if(p==pid) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
  public static IntPtr BestWindow(uint pid, out int wid, out int hei){
    wid=0; hei=0; IntPtr best=IntPtr.Zero; int bestArea=0;
    foreach(var h in WindowsOf(pid)){
      if(!IsWindowVisible(h)) continue;
      RECT r; if(!GetWindowRect(h, out r)) continue;
      int w=r.Right-r.Left, hh=r.Bottom-r.Top;
      if(w<=0||hh<=0) continue;
      if(w*hh > bestArea){ bestArea=w*hh; best=h; wid=w; hei=hh; }
    }
    return best;
  }
}
'@
try { Add-Type -TypeDefinition $cs -Language CSharp -ErrorAction Stop } catch {
  if($_.Exception.Message -notmatch 'already exists'){ Die 20 ("Add-Type failed: " + $_.Exception.Message) }
}
[void][KkzW]::SetProcessDPIAware()

# --------------------------------------------------------------- helpers ----
function Get-PeMachine([string]$p){
  $fs=[System.IO.File]::OpenRead($p)
  try {
    $br=New-Object System.IO.BinaryReader($fs)
    if($br.ReadUInt16() -ne 0x5A4D){ return -1 }
    $fs.Position=0x3C; $e=$br.ReadUInt32()
    $fs.Position=$e
    if($br.ReadUInt32() -ne 0x00004550){ return -1 }
    return [int]$br.ReadUInt16()
  } finally { $fs.Dispose() }
}
function Test-ProxyExports([string]$p){
  $bytes=[System.IO.File]::ReadAllBytes($p)
  $txt=[System.Text.Encoding]::Latin1.GetString($bytes)
  return @{
    V2Link   = $txt.Contains("V2Link`0")
    V2Unlink = $txt.Contains("V2Unlink`0")
  }
}
function Get-CaptureFile([int]$procId){
  $f = Join-Path $CapDirLocal ("capture-{0}.tsv" -f $procId)
  if(Test-Path -LiteralPath $f){ return $f }
  return $null
}
function Get-CaptureRows([string]$file){
  if(-not $file -or -not (Test-Path -LiteralPath $file)){ return @() }
  $rows=@()
  foreach($ln in [System.IO.File]::ReadAllLines($file, [System.Text.Encoding]::UTF8)){
    if($ln.StartsWith('#')){ continue }
    if($ln.Trim().Length -eq 0){ continue }
    $p=$ln.Split("`t")
    if($p.Count -ge 3){ $rows += [pscustomobject]@{ time=$p[0]; name=$p[1]; text=$p[2] } }
  }
  return $rows
}
function Test-Cjk([string]$s){ return ($s -match '[\u4e00-\u9fff]') }
function Save-WindowShot([string]$path, [IntPtr]$hwnd, [int]$w, [int]$h, [string]$tag){
  try {
    Add-Type -AssemblyName System.Drawing
    [void][KkzW]::ShowWindow($hwnd, 9)   # SW_RESTORE
    [void][KkzW]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 900
    $r=New-Object KkzW+RECT
    [void][KkzW]::GetWindowRect($hwnd, [ref]$r)
    $rw=$r.Right-$r.Left; $rh=$r.Bottom-$r.Top
    if($rw -le 0 -or $rh -le 0){ Log "shot_skip tag=$tag reason=bad_rect"; return $false }
    $bmp=New-Object System.Drawing.Bitmap($rw, $rh)
    $g=[System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    # brightness sanity: sample a grid, count near-black pixels
    $dark=0; $n=0; $sum=0L
    for($x=0; $x -lt $rw; $x+=[Math]::Max(1,[int]($rw/32))){
      for($y=0; $y -lt $rh; $y+=[Math]::Max(1,[int]($rh/18))){
        $c=$bmp.GetPixel($x,$y); $lum=[int](($c.R+$c.G+$c.B)/3); $sum+=$lum; $n++
        if($lum -lt 8){ $dark++ }
      }
    }
    $mean=[math]::Round($sum/[math]::Max(1,$n),1); $darkPct=[math]::Round(100.0*$dark/[math]::Max(1,$n),1)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Log "shot_ok tag=$tag file=$path ${rw}x${rh} mean_lum=$mean dark_pct=$darkPct"
    if($darkPct -ge 99.5){ Log "shot_warn tag=$tag nearly_black=true (window may be occluded/DPR/overlay)" }
    return $true
  } catch {
    Log ("shot_fail tag=$tag err=" + $_.Exception.Message)
    return $false
  }
}

# ============================================================== 1. gates ====
Log "=== task-51 kkz-adapter install: begin"
Log "repo=$RepoRoot"
Log "game=$GameDir"
Log "dll=$DllPath"
Log ("ps=" + $PSVersionTable.PSVersion + " os=" + [Environment]::OSVersion.VersionString)

if(-not (Test-Path -LiteralPath $ExePath)){ Die 10 "gate_exe_missing $ExePath" }
$vi=[System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
Log ("exe_fileversion=" + $vi.FileVersion + " product=" + $vi.ProductVersion)
if($vi.FileVersion -ne $ExpectedExeVersion){ Die 10 ("gate_exe_version actual=" + $vi.FileVersion + " expected=" + $ExpectedExeVersion) }
Log "gate_exe_version_ok=1"

if(-not (Test-Path -LiteralPath $TargetDll)){ Die 10 "gate_target_missing $TargetDll" }
if(Test-Path -LiteralPath $OrigDll){ Die 10 "gate_dirty_state getSample_orig.dll already exists (run uninstall.ps1 first)" }
$curSha = Sha256 $TargetDll
Log "gate_target_sha256=$curSha"
if($curSha -ne $ExpectedOrigSha256){ Die 10 ("gate_target_hash_mismatch actual=$curSha") }
Log "gate_target_hash_ok=1"

$busy = @(Get-Process -Name 'SenrenBanka*' -ErrorAction SilentlyContinue)
if($busy.Count -gt 0){ Die 10 ("gate_game_running pids=" + (($busy | ForEach-Object Id) -join ',')) }
Log "gate_no_game_running=1"

if(-not (Test-Path -LiteralPath $DllPath)){ Die 10 "gate_proxy_missing $DllPath" }
$mach = Get-PeMachine $DllPath
$exp  = Test-ProxyExports $DllPath
$proxySha = Sha256 $DllPath
Log "proxy_bytes=$((Get-Item -LiteralPath $DllPath).Length) proxy_machine=0x$('{0:X}' -f $mach) proxy_sha256=$proxySha"
Log ("proxy_exports V2Link=" + $exp.V2Link + " V2Unlink=" + $exp.V2Unlink)
if($mach -ne 0x14C){ Die 10 ("gate_proxy_arch machine=0x" + ('{0:X}' -f $mach) + " expected=0x14C") }
if(-not ($exp.V2Link -and $exp.V2Unlink)){ Die 10 "gate_proxy_exports_missing" }
Log "gate_proxy_ok=1"

# =========================================================== 2. snapshot ====
function New-Snapshot([string]$path, $items){
  $sb=New-Object System.Text.StringBuilder
  [void]$sb.AppendLine('scope,rel,bytes,sha256,mtime_utc')
  foreach($it in $items){
    [void]$sb.AppendLine(('{0},{1},{2},{3},{4}' -f $it.scope, $it.rel, $it.bytes, $it.sha, $it.mtime))
  }
  [System.IO.File]::WriteAllText($path, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
}
function Get-Items([string]$root, [string]$scope, [switch]$Recurse){
  $out=@()
  $gciArgs=@{ LiteralPath=$root; File=$true; ErrorAction='SilentlyContinue' }
  if($Recurse){ $gciArgs.Recurse=$true }
  foreach($f in Get-ChildItem @gciArgs){
    $rel=$f.FullName.Substring($root.Length).TrimStart('\')
    $out += [pscustomobject]@{ scope=$scope; rel=$rel; bytes=$f.Length; sha=(Sha256 $f.FullName); mtime=$f.LastWriteTimeUtc.ToString('o') }
  }
  return $out
}
$snapItems = @()
$snapItems += Get-Items $PluginDir 'plugin' -Recurse
$snapItems += Get-Items $GameDir   'root'
$savedataDir = Join-Path $GameDir 'savedata'
if(Test-Path -LiteralPath $savedataDir){ $snapItems += Get-Items $savedataDir 'savedata' -Recurse }
New-Snapshot $SnapBefore $snapItems
$nPlugin = @($snapItems | Where-Object scope -eq 'plugin').Count
$nRoot   = @($snapItems | Where-Object scope -eq 'root').Count
$nSave   = @($snapItems | Where-Object scope -eq 'savedata').Count
Log "snapshot=$SnapBefore plugin_files=$nPlugin root_files=$nRoot savedata_files=$nSave"
if($nPlugin -ne 59){ Log "snapshot_warn plugin_files=$nPlugin expected=59" }
if($nRoot   -ne 33){ Log "snapshot_warn root_files=$nRoot expected=33" }
Copy-Item -LiteralPath $SnapBefore -Destination (Join-Path $EvDir 'snapshot-before.csv') -Force

# ============================================================ 3. install ====
try {
  Move-Item -LiteralPath $TargetDll -Destination $OrigDll -ErrorAction Stop
  Log "install_rename getSample.dll -> getSample_orig.dll"
  Copy-Item -LiteralPath $DllPath -Destination $TargetDll -Force -ErrorAction Stop
  Log "install_copy proxy -> plugin\getSample.dll"
} catch { Die 12 ("install_action_failed: " + $_.Exception.Message) }

$origSha = Sha256 $OrigDll
$newSha  = Sha256 $TargetDll
Log "install_orig_sha256=$origSha"
Log "install_proxy_sha256=$newSha"
if($origSha -ne $ExpectedOrigSha256){ Die 12 ("install_orig_hash_changed actual=$origSha") }
if($newSha -ne $proxySha){ Die 12 "install_proxy_copy_mismatch" }

$ini = @"
[adapter]
Name=kkz-adapter
Version=0.1.0.0
TargetDll=getSample.dll
OrigSuffix=_orig
WarmupMs=25000
PollMs=300
StabilizeMs=300
MinChars=2
AutoClick=1
ClickIntervalMs=2000
MaxClicks=70
ClickRowsGoal=10
QuietAfterClicksMs=45000
CaptureDir=%LOCALAPPDATA%\GalA11yForge\kkz-adapter
LogFile=%LOCALAPPDATA%\GalA11yForge\kkz-adapter\kkz-adapter.log

[gate]
RequireExe=SenrenBanka.exe
RequireExeVersion=1.2.0.3
RequireOrigSha256=$ExpectedOrigSha256
"@
[System.IO.File]::WriteAllText($IniPath, $ini, (New-Object System.Text.UnicodeEncoding($false,$true)))
Log "install_ini=$IniPath bytes=$((Get-Item -LiteralPath $IniPath).Length) encoding=utf16le"
$logFile = Join-Path $CapDirLocal 'kkz-adapter.log'
$captureGlob = Join-Path $CapDirLocal 'capture-*.tsv'
if(Test-Path -LiteralPath $logFile){ Move-Item -LiteralPath $logFile -Destination (Join-Path $WorkDir ('kkz-adapter.prev-{0}.log' -f (Get-Date -Format 'HHmmss'))) -Force }
Get-ChildItem -Path $captureGlob -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

$state = [ordered]@{
  task='task-51'; started_at=(Get-Date).ToString('o'); game_dir=$GameDir; exe_version=$vi.FileVersion
  proxy_sha256=$proxySha; orig_sha256=$origSha; install_ini=$IniPath
  capture_dir=$CapDirLocal; log_file=$logFile; work_dir=$WorkDir
  game_pid=$null; capture_file=$null; speak_pid=$null; rows=0; chinese_rows=0
}
$state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $StatePath -Encoding utf8
Log "state=$StatePath"

if($NoLaunch){ Log "NoLaunch set: stopping after install"; exit 0 }

# ============================================================= 4. launch ====
Log "launch steam://rungameid/1144400"
cmd /c "start steam://rungameid/1144400" | Out-Null
$deadline=(Get-Date).AddSeconds(120); $proc=$null
while((Get-Date) -lt $deadline){
  Start-Sleep -Milliseconds 700
  $proc = @(Get-Process -Name 'SenrenBanka*' -ErrorAction SilentlyContinue) | Sort-Object StartTime -Descending | Select-Object -First 1
  if($proc){ break }
}
if(-not $proc){ Die 13 "launch_timeout no SenrenBanka process within 120s (Steam DRM / launcher?)" }
$pidGame=[int]$proc.Id
$state.game_pid=$pidGame
Log "launch_pid=$pidGame path=$($proc.Path) start=$($proc.StartTime.ToString('HH:mm:ss'))"

$capFile=$null; $t0=Get-Date
$deadline=(Get-Date).AddSeconds(120)
while((Get-Date) -lt $deadline){
  Start-Sleep -Milliseconds 500
  $capFile = Get-CaptureFile $pidGame
  if($capFile){ break }
  if($proc.HasExited){ Die 13 ("game_exited_before_capture exit=" + $proc.ExitCode) }
}
if(-not $capFile){ Die 13 "capture_file_timeout no capture-<pid>.tsv within 120s (proxy not loaded?)" }
Log "capture_file=$capFile after_ms=$([int]((Get-Date)-$t0).TotalMilliseconds)"
$state.capture_file=$capFile

# main window
$hwnd=[IntPtr]::Zero; $ww=0; $wh=0
$deadline=(Get-Date).AddSeconds(60)
while((Get-Date) -lt $deadline){
  $hwnd=[KkzW]::BestWindow([uint32]$pidGame, [ref]$ww, [ref]$wh)
  if($hwnd -ne [IntPtr]::Zero){ break }
  Start-Sleep -Milliseconds 500
}
if($hwnd -ne [IntPtr]::Zero){
  $title=[KkzW]::TitleOf($hwnd); $cls=[KkzW]::ClassOf($hwnd)
  Log ("window_ok hwnd=0x{0:X} title='{1}' class='{2}' ${ww}x${wh}" -f $hwnd.ToInt64(), $title, $cls)
  $all=@()
  foreach($h in [KkzW]::WindowsOf([uint32]$pidGame)){
    $r=New-Object KkzW+RECT; [void][KkzW]::GetWindowRect($h,[ref]$r)
    $all += [pscustomobject]@{ hwnd=('0x{0:X}' -f $h.ToInt64()); visible=[KkzW]::IsWindowVisible($h); class=[KkzW]::ClassOf($h); title=[KkzW]::TitleOf($h); w=($r.Right-$r.Left); h=($r.Bottom-$r.Top) }
  }
  $all | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $WorkDir 'windows.json') -Encoding utf8
} else {
  Log "window_warn no visible top-level window found within 60s"
}
$state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $StatePath -Encoding utf8

# TTS sidecar (independent process, watches the capture file)
if($WithTts){
  $speak = Join-Path $PSScriptRoot 'speak.ps1'
  if(Test-Path -LiteralPath $speak){
    $spokenOut = Join-Path $WorkDir 'spoken.tsv'
    $spLog     = Join-Path $WorkDir 'speak.log'
    $spArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$speak,'-CaptureFile',$capFile,'-GamePid',"$pidGame",
                '-SpokenOut',$spokenOut,'-LogFile',$spLog,'-MaxSeconds',"$($RunSeconds+90)")
    $sp = Start-Process -FilePath 'powershell.exe' -ArgumentList $spArgs -PassThru -WindowStyle Hidden
    $state.speak_pid=[int]$sp.Id
    Log "tts_spawned pid=$($sp.Id) script=$speak spoken_out=$spokenOut"
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $StatePath -Encoding utf8
  } else { Log "tts_skip speak.ps1 not found" }
}

# ============================================================ 5. capture ====
$shot1=Join-Path $EvDir 'shot-01.png'
$shot2=Join-Path $EvDir 'shot-02.png'
$shot1Done=$false; $shot2Done=$false
$t1=Get-Date; $lastLog=Get-Date
$deadline=$t1.AddSeconds($RunSeconds)
while((Get-Date) -lt $deadline){
  Start-Sleep -Milliseconds 800
  $rows=@(Get-CaptureRows $capFile)
  $cjk=@($rows | Where-Object { Test-Cjk $_.text })
  $state.rows=$rows.Count; $state.chinese_rows=$cjk.Count
  $elapsed=[int]((Get-Date)-$t1).TotalSeconds
  if(((Get-Date)-$lastLog).TotalSeconds -ge 20){
    Log "capture_tick elapsed=${elapsed}s rows=$($rows.Count) cjk=$($cjk.Count)"
    $lastLog=Get-Date
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $StatePath -Encoding utf8
  }
  if(-not $shot1Done -and $rows.Count -ge 1){
    $shot1Done = Save-WindowShot $shot1 $hwnd $ww $wh 'row1'
  }
  if(-not $shot2Done -and $cjk.Count -ge 2){
    $shot2Done = Save-WindowShot $shot2 $hwnd $ww $wh 'cjk2'
  }
  if($proc.HasExited){ Log "game_exited during capture exit=$($proc.ExitCode)"; break }
}
$rows=@(Get-CaptureRows $capFile)
$cjk=@($rows | Where-Object { Test-Cjk $_.text })
$state.rows=$rows.Count; $state.chinese_rows=$cjk.Count
Log "capture_done rows=$($rows.Count) cjk_rows=$($cjk.Count) file=$capFile"
$rows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $WorkDir 'capture-rows.json') -Encoding utf8
if($rows.Count -gt 0){ Copy-Item -LiteralPath $capFile -Destination (Join-Path $EvDir 'capture.tsv') -Force }
if(-not $shot1Done){ $shot1Done = Save-WindowShot $shot1 $hwnd $ww $wh 'fallback1' }
if(-not $shot2Done){ $shot2Done = Save-WindowShot $shot2 $hwnd $ww $wh 'fallback2' }
Copy-Item -LiteralPath $logFile -Destination (Join-Path $EvDir 'kkz-adapter.log') -Force -ErrorAction SilentlyContinue

# ============================================================== 6. close ====
if(-not $KeepRunning){
  if(-not $proc.HasExited){
    Log "close_try WM_CLOSE"
    [void][KkzW]::PostMessageW($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if($proc.WaitForExit(12000)){ Log "close_ok graceful exit=$($proc.ExitCode)" }
    else {
      Log "close_force after 12s"
      try { Stop-Process -Id $pidGame -Force -ErrorAction Stop; Log "close_forced_kill=1" } catch { Log ("close_forced_kill_fail " + $_.Exception.Message) }
      Start-Sleep -Milliseconds 1500
    }
  } else { Log "close_skip already_exited exit=$($proc.ExitCode)" }
  # give the TTS sidecar a moment to notice the exit
  $spw=(Get-Date).AddSeconds(45)
  while((Get-Date) -lt $spw){
    $spPid = $state.speak_pid
    if(-not $spPid){ break }
    if(-not (Get-Process -Id $spPid -ErrorAction SilentlyContinue)){ Log "tts_exited pid=$spPid"; break }
    Start-Sleep -Milliseconds 800
  }
}
$spoken = Join-Path $WorkDir 'spoken.tsv'
$nSpoken = 0
if(Test-Path -LiteralPath $spoken){ $nSpoken = @(Get-Content -LiteralPath $spoken | Where-Object { $_ -and -not $_.StartsWith('#') }).Count }
$state.rows=$rows.Count; $state.chinese_rows=$cjk.Count; $state.spoken_lines=$nSpoken
$state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $StatePath -Encoding utf8
Log "spoken_lines=$nSpoken"

if($cjk.Count -lt $MinRows){
  Log "RESULT=FAIL reason=insufficient_rows cjk=$($cjk.Count) min=$MinRows"
  exit 14
}
Log "RESULT=OK rows=$($rows.Count) cjk=$($cjk.Count) spoken=$nSpoken shots=$([int]$shot1Done)+$([int]$shot2Done)"
exit 0
