<#
  speak.ps1 -- task-51 / R28  external TTS sidecar for the KKZ adapter experiment

  Independent process. Watches the adapter's capture TSV, speaks every newly appended
  dialogue line with SAPI (System.Speech, fallback SAPI.SpVoice COM), records what it
  actually spoke to spoken.tsv. It never touches the game process and never blocks it.
  Stops when the game process exits (after one final drain) or at -MaxSeconds.

  Run under Windows PowerShell 5.1 (powershell.exe) -- System.Speech is not available
  in PowerShell 7 on this machine; the COM fallback covers that case too.

  Capture TSV format (one line per utterance, UTF-8, CRLF):
      time <TAB> name <TAB> text
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$CaptureFile,
  [int]$GamePid = 0,
  [string]$SpokenOut = '',
  [string]$LogFile = '',
  [int]$MaxSeconds = 300,
  [int]$Rate = 0,
  [int]$PollMs = 350
)

$ErrorActionPreference = 'Continue'
if(-not $SpokenOut){ $SpokenOut = Join-Path (Split-Path -Parent $CaptureFile) 'spoken.tsv' }
if(-not $LogFile){ $LogFile = [System.IO.Path]::ChangeExtension($SpokenOut, '.log') }

function SLog([string]$m){
  $line = ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $m)
  try { Add-Content -LiteralPath $LogFile -Value $line -Encoding utf8 } catch { }
}
function WriteSpoken([string]$time,[string]$name,[string]$text,[string]$voice,[int]$ms,[string]$note){
  $safe = $text -replace "[`t`r`n]", ' '
  $n2   = $name -replace "[`t`r`n]", ' '
  $line = ('{0}`t{1}`t{2}`t{3}`t{4}`t{5}' -f (Get-Date -Format 'HH:mm:ss.fff'), $n2, $safe, $voice, $ms, $note)
  Add-Content -LiteralPath $SpokenOut -Value $line -Encoding utf8
}

SLog "=== speak.ps1 start ps=$($PSVersionTable.PSVersion) capture=$CaptureFile pid=$GamePid max=${MaxSeconds}s"
Add-Content -LiteralPath $SpokenOut -Value ("# speak.ps1 started {0} voice_select_pending" -f (Get-Date -Format 'o')) -Encoding utf8

# ------------------------------------------------------------- engine ------
$engine = 'none'; $synth = $null; $voiceName = '(none)'
try {
  Add-Type -AssemblyName System.Speech -ErrorAction Stop
  $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
  $engine = 'System.Speech'
  $voices = @()
  foreach($v in $synth.GetInstalledVoices()){
    $voices += [pscustomobject]@{ name=$v.VoiceInfo.Name; culture=$v.VoiceInfo.Culture.Name; enabled=$v.Enabled }
  }
  SLog ("voices=" + (($voices | ForEach-Object { "$($_.name)[$($_.culture)]" }) -join ' | '))
  $zh = @($voices | Where-Object { $_.enabled -and $_.culture -like 'zh*' }) | Select-Object -First 1
  if($zh){
    $synth.SelectVoice($zh.name); $voiceName = $zh.name
    SLog "voice_selected=$voiceName culture=$($zh.culture) zh_voice=1"
  } else {
    $voiceName = ($voices | Where-Object enabled | Select-Object -First 1).name
    if($voiceName){ $synth.SelectVoice($voiceName) }
    SLog "voice_selected=$voiceName zh_voice=0 (no zh-* voice installed; Chinese text may be unreadable)"
  }
  try { $synth.Rate = $Rate } catch { SLog "rate_set_failed $($_.Exception.Message)" }
} catch {
  SLog ("System.Speech unavailable: " + $_.Exception.Message + " -> trying SAPI.SpVoice COM")
  try {
    $synth = New-Object -ComObject SAPI.SpVoice
    $engine = 'SAPI.SpVoice(COM)'
    $toks = @($synth.GetVoices())
    $descs = @()
    $tokCount = 0
    try { $tokCount = [int]$toks.Count } catch { $tokCount = 0 }
    if($tokCount -gt 0){
      for($i=0; $i -lt $tokCount; $i++){ $descs += $toks.Item($i).GetDescription() }
    }
    SLog ("voices=" + ($descs -join ' | '))
    if($descs.Count -gt 0){
      $zhTok = $null
      for($i=0; $i -lt $descs.Count; $i++){ if($descs[$i] -match 'Chinese|中文|zh'){ $zhTok = $toks.Item($i); break } }
      if($zhTok){ $synth.Voice = $zhTok; $voiceName = $zhTok.GetDescription() } else { $voiceName = $descs[0] }
      SLog "voice_selected=$voiceName zh_voice=$([int]($zhTok -ne $null))"
    }
  } catch {
    SLog ("SAPI COM unavailable too: " + $_.Exception.Message)
  }
}
Add-Content -LiteralPath $SpokenOut -Value ("# engine={0} voice={1}" -f $engine, $voiceName) -Encoding utf8

function Speak-Text([string]$text){
  if(-not $synth){ return @{ ok=0; ms=0; note='no_tts_engine' } }
  $sw=[System.Diagnostics.Stopwatch]::StartNew()
  try {
    if($engine -eq 'System.Speech'){ $synth.Speak($text) }
    else { [void]$synth.Speak($text, 0) }
    $sw.Stop()
    return @{ ok=1; ms=[int]$sw.ElapsedMilliseconds; note='calling_succeeded_not_recorded' }
  } catch {
    $sw.Stop()
    return @{ ok=0; ms=[int]$sw.ElapsedMilliseconds; note=('error:' + $_.Exception.Message) }
  }
}

# ------------------------------------------------------------ main loop ----
$consumed = 0L        # bytes of complete lines already handled
$spokenCount = 0
$t0 = Get-Date
$deadline = $t0.AddSeconds($MaxSeconds)
$gameAlive = { param($gp) if($gp -le 0){ return $true } return [bool](Get-Process -Id $gp -ErrorAction SilentlyContinue) }
$lastSeen = Get-Date

while((Get-Date) -lt $deadline){
  Start-Sleep -Milliseconds $PollMs
  $alive = & $gameAlive $GamePid
  $fs=$null
  try {
    if(Test-Path -LiteralPath $CaptureFile){
      $fs=[System.IO.File]::Open($CaptureFile, 'Open', 'Read', 'ReadWrite')
      $len=$fs.Length
      if($len -gt $consumed){
        $fs.Position=$consumed
        $buf=New-Object byte[] ($len-$consumed)
        $read=$fs.Read($buf, 0, $buf.Length)
        # only consume up to the last CRLF so partial writes are never half-parsed
        $lastNl=-1
        for($i=$read-1; $i -ge 1; $i--){ if($buf[$i] -eq 10 -and $buf[$i-1] -eq 13){ $lastNl=$i; break } }
        if($lastNl -ge 0){
          $chunk=[System.Text.Encoding]::UTF8.GetString($buf, 0, $lastNl+1)
          $consumed += ($lastNl+1)
          foreach($ln in ($chunk -split "`r`n")){
            if(-not $ln -or $ln.StartsWith('#')){ continue }
            $p=$ln.Split("`t")
            if($p.Count -lt 3){ continue }
            $name=$p[1]; $text=$p[2]
            if(-not $text -or $text.Trim().Length -eq 0){ continue }
            $say = $text
            if($name -and $name.Trim().Length -gt 0){ $say = $name + '：' + $text }
            $r = Speak-Text $say
            WriteSpoken $p[0] $name $text $voiceName $r.ms ('ok={0};{1}' -f $r.ok, $r.note)
            $spokenCount++
            SLog ("spoke #{0} ms={1} ok={2} text={3}" -f $spokenCount, $r.ms, $r.ok, ($text.Substring(0, [Math]::Min(40, $text.Length))))
          }
          $lastSeen = Get-Date
        }
      }
    }
  } catch {
    SLog ("read_error " + $_.Exception.Message)
  } finally {
    if($fs){ $fs.Dispose() }
  }

  if(-not $alive -and ((Get-Date)-$lastSeen).TotalSeconds -ge 2){
    SLog "game_exited -> stop (spoken_total=$spokenCount)"
    break
  }
  if(-not (Test-Path -LiteralPath $CaptureFile) -and -not $alive){
    SLog "capture_missing_and_game_exited -> stop"
    break
  }
}
SLog "=== speak.ps1 end spoken_total=$spokenCount elapsed_s=$([int](New-TimeSpan -Start $t0 -End (Get-Date)).TotalSeconds)"
if($synth -and $engine -eq 'System.Speech'){ try { $synth.Dispose() } catch {} }
exit 0
