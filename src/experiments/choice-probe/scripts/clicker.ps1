param(
  [ValidateSet('advance','select')][string]$Mode = 'advance',
  [string]$ProcName = '5omeday',
  [int]$Count = 40,
  [int]$IntervalMs = 1600,
  [int]$X = 1600,
  [int]$Y = 980,
  [string]$Events = 'D:\Steam\steamapps\common\Build\BepInEx\choice-probe\events.tsv',
  [string]$ShotDir = 'D:\DSHWorkSpace\research\23-choice\evidence\shots',
  [string]$Tag = 'advance',
  [int]$StopAt = 1,
  [switch]$NoStop
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Clk23 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  public static IntPtr FindUnity(uint want) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != want) return true;
      var c = new StringBuilder(256); GetClassNameW(h, c, 256);
      if (c.ToString() == "UnityWndClass") { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

function Read-EventsText {
  if (-not (Test-Path -LiteralPath $Events)) { return $null }
  try { return (Get-Content -LiteralPath $Events -Raw -ErrorAction Stop) } catch {
    try {
      $fs = [System.IO.File]::Open($Events, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
      $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
      $t = $sr.ReadToEnd(); $sr.Dispose(); $fs.Dispose(); return $t
    } catch { Write-Output ("READ-ERR " + $_.Exception.Message); return $null }
  }
}
function Get-ChoiceCount {
  $t = Read-EventsText
  if ($null -eq $t) { return 0 }
  return ([regex]::Matches($t, "`tCHOICE`t")).Count
}
function Get-EventCount([string]$kind) {
  $t = Read-EventsText
  if ($null -eq $t) { return 0 }
  return ([regex]::Matches($t, "`t$kind`t")).Count
}
function Save-Shot([string]$name) {
  New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
  $g.Dispose()
  $path = Join-Path $ShotDir ($name + '.png')
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Output ("shot {0}" -f $path)
}

$p = Get-Process -Name $ProcName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "GAME NOT RUNNING"; exit 1 }
$h = [Clk23]::FindUnity([uint32]$p.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output "NO UNITY WINDOW"; exit 1 }
Write-Output ("mode={0} pid={1} hwnd={2} count={3} interval={4}ms click=({5},{6})" -f $Mode, $p.Id, $h, $Count, $IntervalMs, $X, $Y)
[Clk23]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
[Clk23]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 700

$focusLoss = 0
for ($i = 1; $i -le $Count; $i++) {
  if ($p.HasExited) { Write-Output ("game exited at i={0}" -f $i); break }
  if ($Mode -eq 'advance' -and -not $NoStop) {
    $c = Get-ChoiceCount
    if ($c -ge $StopAt) {
      Write-Output ("STOP: CHOICE count>={2} before click i={0} count={1}" -f $i, $c, $StopAt)
      Save-Shot ("{0}-stopped-{1:d2}" -f $Tag, $i)
      break
    }
  }
  [Clk23]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
  [Clk23]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 120
  if ([Clk23]::GetForegroundWindow() -ne $h) { $focusLoss++ }
  [Clk23]::SetCursorPos($X, $Y) | Out-Null
  Start-Sleep -Milliseconds 60
  [Clk23]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 90
  [Clk23]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds ([Math]::Max(200, $IntervalMs - 270))
  if ($i % 5 -eq 0) {
    Write-Output ("i={0} alive={1} focusLoss={2} choices={3} sels={4} {5:HH:mm:ss}" -f $i, (-not $p.HasExited), $focusLoss, (Get-ChoiceCount), (Get-EventCount 'SELECT'), (Get-Date))
  }
}
if ($Mode -eq 'select' -and $Count -le 4) {
  Start-Sleep -Milliseconds 1200
  Save-Shot ("{0}-after" -f $Tag)
}
Write-Output "done"

