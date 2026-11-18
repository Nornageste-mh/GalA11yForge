param([string]$Label = "shot", [string]$OutDir = "D:\DSHWorkSpace\research\41-kkz-translation\evidence")
# task-48 / R27: capture the game window rect from the screen (fullscreen borderless).
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WSH48 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$p = @(Get-Process SenrenBanka -ErrorAction SilentlyContinue) | Select-Object -First 1
if (-not $p) { "SHOT_SKIP GAME_NOT_RUNNING"; exit 0 }
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { "SHOT_SKIP NO_MAIN_WINDOW"; exit 0 }
$r = New-Object WSH48+RECT
if (-not [WSH48]::GetWindowRect($h, [ref]$r)) { "SHOT_SKIP GET_RECT_FAILED"; exit 0 }
$w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
if ($w -le 0 -or $hh -le 0) { "SHOT_SKIP BAD_RECT $w x $hh"; exit 0 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
$g.Dispose()
$path = Join-Path $OutDir ("shot-{0}.png" -f $Label)
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"SHOT $path {0}x{1} rect=({2},{3}) title=[{4}]" -f $w, $hh, $r.Left, $r.Top, $p.MainWindowTitle
