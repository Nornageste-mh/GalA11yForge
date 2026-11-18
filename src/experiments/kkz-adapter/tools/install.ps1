<#
.SYNOPSIS
  安装 / 预览安装 KKZ 实验性适配器（task-49 / W2-Y）。默认 dry-run。

.DESCRIPTION
  做的事只有三件（都在 <GameDir>\plugin\ 里）：
    1) 把原插件 <TargetDll> 改名成 <TargetDll 去扩展名><OrigSuffix>.dll
    2) 把我们的代理 DLL 放成 <TargetDll>
    3) 在旁边写 kkz-adapter.ini，并把清单（含哈希）存到
       %LOCALAPPDATA%\kkz-adapter\<GameDir 指纹>.json

  安全约束（脚本层面，任何一条不满足就拒绝，不写盘）：
    * 必须显式给 -GameDir 和 -GameExe；脚本里没有任何默认游戏路径
    * 游戏进程正在运行 => 拒绝
    * 目标插件不存在 / 已经装过（_orig 已存在）=> 拒绝
    * 代理 DLL 不是 x86 PE 或是空壳 => 拒绝
    * 给了 -ExpectOrigSha256 但与实际不符 => 拒绝
    * 默认只打印计划（dry-run）；真正写盘必须加 -Apply

.EXAMPLE
  # 1) 先看计划（不写任何文件）
  .\install.ps1 -GameDir 'D:\Games\sometitle' -GameExe 'sometitle.exe'

.EXAMPLE
  # 2) 确认无误后真正安装（哈希也一起卡住）
  .\install.ps1 -GameDir 'D:\Games\sometitle' -GameExe 'sometitle.exe' `
      -ExpectOrigSha256 'E190AE1B17F82B106FA466E9AB985364E018C8ACD46B39B2B33B895133C2F30C' `
      -EnableTts -Apply

.NOTES
  本脚本不联网、不提权、不碰 data.xp3、不碰 Steam 客户端配置。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [Parameter(Mandatory = $true)][string]$GameExe,
    [string]$TargetDll = 'getSample.dll',
    [string]$OrigSuffix = '_orig',
    [string]$ProxyDll = '',
    [string]$ExpectOrigSha256 = '',
    [string]$RequireExeVersion = '',
    [string]$PollMs = '200',
    [switch]$EnableTts,
    [switch]$Apply,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
function Say([string]$m) { Write-Host "[kkz-install] $m" }
function Die([string]$m) { throw "KKZ_INSTALL_FAIL: $m" }

$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath.TrimEnd('\')
$pluginDir = Join-Path $GameDir 'plugin'
$targetPath = Join-Path $pluginDir $TargetDll
$exePath = Join-Path $GameDir $GameExe

if (-not $Apply) { Say '模式: dry-run（只打印计划，不写盘）；确认后加 -Apply' }
else { Say '模式: APPLY（会真的改名/复制）' }

# ---------- 1. 路径与前置条件 ----------
if (-not (Test-Path -LiteralPath $pluginDir -PathType Container)) { Die "找不到 plugin 目录: $pluginDir" }
if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { Die "找不到目标插件: $targetPath" }
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { Die "找不到游戏主程序: $exePath（-GameExe 要写文件名，不含路径）" }

$baseName = [IO.Path]::GetFileNameWithoutExtension($TargetDll)
$ext = [IO.Path]::GetExtension($TargetDll)
$origPath = Join-Path $pluginDir ($baseName + $OrigSuffix + $ext)

if (Test-Path -LiteralPath $origPath) {
    Die "已存在 $origPath —— 看起来装过了（或上次回滚没做完）。请先用 uninstall.ps1 -Rollback。"
}

$running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($GameExe)) -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) { Die "游戏正在运行（PID $($running[0].Id)），请先退出游戏" }

if (-not $ProxyDll) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $cand = @(Get-ChildItem -LiteralPath (Join-Path (Split-Path -Parent $here) 'dist') -Filter 'kkz-adapter-*-x86.dll' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending)
    if ($cand.Count -eq 0) { Die '没找到 dist\kkz-adapter-*-x86.dll；请先跑 build.ps1，或用 -ProxyDll 指定' }
    $ProxyDll = $cand[0].FullName
}
if (-not (Test-Path -LiteralPath $ProxyDll -PathType Leaf)) { Die "找不到代理 DLL: $ProxyDll" }

# ---------- 2. 读哈希与 PE 头 ----------
function Get-Sha256([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
function Test-X86Dll([string]$p) {
    $fs = [IO.File]::OpenRead($p)
    try {
        $br = New-Object IO.BinaryReader($fs)
        if ($br.ReadUInt16() -ne 0x5A4D) { return '不是 MZ' }
        $fs.Position = 0x3C
        $peOff = $br.ReadUInt32()
        $fs.Position = $peOff
        if ($br.ReadUInt32() -ne 0x00004550) { return 'PE 签名不对' }
        $machine = $br.ReadUInt16()
        if ($machine -ne 0x014C) { return ("不是 x86（machine=0x{0:X4}）" -f $machine) }
        return ''
    }
    finally { $fs.Dispose() }
}

$proxySize = (Get-Item -LiteralPath $ProxyDll).Length
if ($proxySize -lt 4096) { Die "代理 DLL 太小（$proxySize B），疑似空壳" }
$peBad = Test-X86Dll $ProxyDll
if ($peBad) { Die "代理 DLL 校验失败: $peBad" }
$proxySha = Get-Sha256 $ProxyDll
$origSha = Get-Sha256 $targetPath
$exeVer = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion

if ($ExpectOrigSha256 -and ($ExpectOrigSha256.Trim().ToUpper() -ne $origSha)) {
    Die "原插件哈希不符！期望 $($ExpectOrigSha256.ToUpper()) 实际 $origSha —— 版本不对或文件被改过，拒绝安装"
}

Say "游戏目录 : $GameDir"
Say "主程序   : $GameExe (FileVersion=$exeVer)"
Say "目标插件 : $TargetDll ($origSha, $((Get-Item -LiteralPath $targetPath).Length) B)"
Say "代理 DLL : $ProxyDll ($proxySha, $proxySize B)"
Say "原 DLL 将改名为: $([IO.Path]::GetFileName($origPath))"

$manifestDir = Join-Path $env:LOCALAPPDATA 'kkz-adapter'
$finger = [BitConverter]::ToString(([Security.Cryptography.SHA1]::Create()).ComputeHash(
    [Text.Encoding]::UTF8.GetBytes($GameDir.ToLowerInvariant()))).Replace('-', '').Substring(0, 16)
$manifestPath = Join-Path $manifestDir ("$finger.json")
if ((Test-Path -LiteralPath $manifestPath) -and -not $Force) {
    Die "已有安装清单 $manifestPath；要覆盖请加 -Force（或先 uninstall.ps1）"
}

if (-not $Apply) {
    Say ''
    Say '计划（dry-run，未写任何文件）:'
    Say "  1) 改名 $targetPath -> $origPath"
    Say "  2) 复制 $ProxyDll -> $targetPath"
    Say "  3) 写 $(Join-Path $pluginDir 'kkz-adapter.ini')  (TargetDll=$TargetDll, OrigSuffix=$OrigSuffix, RequireExe=$GameExe, FailMode=degraded)"
    Say "  4) 写清单 $manifestPath"
    Say ''
    Say '确认后把上面那条命令加上 -Apply 重跑。'
    return
}

# ---------- 3. 真正写盘 ----------
New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
Copy-Item -LiteralPath $targetPath -Destination $origPath
Copy-Item -LiteralPath $ProxyDll -Destination $targetPath -Force

$iniPath = Join-Path $pluginDir 'kkz-adapter.ini'
$tts = if ($EnableTts) { 1 } else { 0 }
$ini = @(
    '; 由 install.ps1 生成。UTF-16LE 编码，Windows 的 GetPrivateProfileStringW 才能正确读中文。'
    '[adapter]'
    "TargetDll=$TargetDll"
    "OrigSuffix=$OrigSuffix"
    "PollMs=$PollMs"
    'StabilizeMs=200'
    'MinChars=2'
    "EnableTts=$tts"
    'RequireExe=' + $GameExe
    'RequireExeVersion=' + $RequireExeVersion
    'RequireOrigSha256=' + $origSha
    'FailMode=degraded'
    'OutputFile=%TEMP%\kkz_adapter_capture.txt'
    'LogFile=%TEMP%\kkz_adapter_probe.txt'
    ''
    '[text]'
    'ExprName=global.kag.historyLayer.currentInfo.disp'
    'ExprText=global.kag.historyLayer.currentInfo.text'
    'ExprPlain=global.kag.historyLayer.currentInfo.plaintext'
)
Set-Content -LiteralPath $iniPath -Value $ini -Encoding Unicode

$manifest = [ordered]@{
    schemaVersion    = 1
    tool             = 'kkz-adapter install.ps1'
    installedAt      = (Get-Date).ToString('o')
    gameDir          = $GameDir
    gameExe          = $GameExe
    gameExeVersion   = $exeVer
    gameExeSha256    = Get-Sha256 $exePath
    targetDll        = $TargetDll
    origPath         = $origPath
    origSha256       = $origSha
    origBytes        = (Get-Item -LiteralPath $origPath).Length
    proxyPath        = $targetPath
    proxySource      = $ProxyDll
    proxySha256      = $proxySha
    proxyBytes       = $proxySize
    iniPath          = $iniPath
    iniSha256        = Get-Sha256 $iniPath
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8

# ---------- 4. 装完立刻复核 ----------
$okProxy = (Get-Sha256 $targetPath) -eq $proxySha
$okOrig = (Get-Sha256 $origPath) -eq $origSha
Say "复核: 代理位置哈希一致=$okProxy / 原 DLL 备份哈希一致=$okOrig"
if (-not ($okProxy -and $okOrig)) {
    Die '装完复核失败 —— 请立刻用 uninstall.ps1 -Rollback -Apply 回滚，并把上面的信息反馈给作者'
}

Say ''
Say "装好了。清单: $manifestPath"
Say '下一步: 启动游戏进到有台词的场景，看日志:'
Say "  %TEMP%\kkz_adapter_probe.txt   （gate_all_ok=1、inject=1、rows 递增才算真工作）"
Say "  %TEMP%\kkz_adapter_capture.txt （台词，一行一条）"
Say '回滚: .\uninstall.ps1 -GameDir <同一个目录> -Rollback -Apply'
