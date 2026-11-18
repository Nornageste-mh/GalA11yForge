<#
.SYNOPSIS
  构建 KKZ 实验性适配器（x86 DLL）。task-49 / W2-Y。

.DESCRIPTION
  两种工具链：
    msvc : VS2022 BuildTools x86（首选，与 task-47 已验证的 /EHa + /MT 一致）
    gcc  : w64devkit 的 g++ -m32（备用；i686 下没有 SEH 异常，catch(...) 抓不到访问违例）
  构建后自动校验 PE 机器类型（必须 x86）与导出表（必须 V2Link/V2Unlink/GetModuleThreadModel），
  并打印 SHA256。

  参数会写进 DLL 的"编译期默认值"（KKZ_CFG_*）；运行期 kkz-adapter.ini 仍可覆盖。

.EXAMPLE
  .\build.ps1
  .\build.ps1 -TargetDll getSample.dll -PollMs 150 -EnableTts
  .\build.ps1 -Toolchain gcc -RequireExeVersion '1.2.*'

.NOTES
  产物：build\kkz-adapter.dll、dist\kkz-adapter-<ver>-x86.dll、dist\SHA256SUMS.txt
#>
[CmdletBinding()]
param(
    [ValidateSet('auto', 'msvc', 'gcc')][string]$Toolchain = 'auto',
    [string]$TargetDll = 'getSample.dll',
    [string]$OrigSuffix = '_orig',
    [int]$PollMs = 200,
    [int]$StabilizeMs = 200,
    [int]$MinChars = 2,
    [int]$MaxRows = 0,
    [string]$OutputFile = '%TEMP%\kkz_adapter_capture.txt',
    [string]$LogFile = '%TEMP%\kkz_adapter_probe.txt',
    [switch]$EnableTts,
    [string]$RequireExe = '',
    [string]$RequireExeVersion = '',
    [string]$RequireOrigSha256 = '',
    [ValidateSet('degraded', 'strict')][string]$FailMode = 'degraded',
    [string]$Version = '0.1.0.0',
    [switch]$Clean,
    [switch]$NoVerify
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $here 'src'
$buildDir = Join-Path $here 'build'
$distDir = Join-Path $here 'dist'
$cpp = Join-Path $src 'kkz_adapter.cpp'
$def = Join-Path $src 'kkz_adapter.def'
$obj = Join-Path $buildDir 'kkz_adapter.obj'
$dll = Join-Path $buildDir 'kkz-adapter.dll'
$map = Join-Path $buildDir 'kkz-adapter.map'
$buildLog = Join-Path $buildDir 'build.log'

function Say([string]$m) { Write-Host "[kkz-adapter] $m" }
function Die([string]$m) { throw "KKZ_BUILD_FAIL: $m" }

# ---------- 0. 清理 ----------
if ($Clean) {
    foreach ($p in @($buildDir, $distDir)) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }
    Say '已清理 build\ 与 dist\'
}
foreach ($p in @($buildDir, $distDir)) { if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p | Out-Null } }

# ---------- 1. 生成编译期配置头 ----------
# 不把 KKZ_CFG_* 放命令行 /D：cl 对含 % 的 /D 值直接报 D8038（实测），
# 而我们的默认输出路径正是 %TEMP%\...。写头文件可原样保留 %、空格、反斜杠、中文。
function EscCppW([string]$s) { return ([string]$s).Replace('\', '\\').Replace('"', '\"') }
$cfgHeader = Join-Path $buildDir 'kkz_build_config.h'
$hdr = New-Object System.Collections.ArrayList
$hdr.Add('// 由 build.ps1 自动生成，请勿手工编辑（改参数请重跑 build.ps1）。')
$hdr.Add('#pragma once')
$hdr.Add("#define KKZ_CFG_VERSION              L`"$(EscCppW $Version)`"")
$hdr.Add("#define KKZ_CFG_TARGET_DLL           L`"$(EscCppW $TargetDll)`"")
$hdr.Add("#define KKZ_CFG_ORIG_SUFFIX          L`"$(EscCppW $OrigSuffix)`"")
$hdr.Add("#define KKZ_CFG_POLL_MS              $PollMs")
$hdr.Add("#define KKZ_CFG_STABILIZE_MS         $StabilizeMs")
$hdr.Add("#define KKZ_CFG_MIN_CHARS            $MinChars")
$hdr.Add("#define KKZ_CFG_MAX_ROWS             $MaxRows")
$hdr.Add("#define KKZ_CFG_OUTPUT_FILE          L`"$(EscCppW $OutputFile)`"")
$hdr.Add("#define KKZ_CFG_LOG_FILE             L`"$(EscCppW $LogFile)`"")
$hdr.Add("#define KKZ_CFG_ENABLE_TTS           $(if ($EnableTts) { 1 } else { 0 })")
$hdr.Add("#define KKZ_CFG_REQUIRE_EXE          L`"$(EscCppW $RequireExe)`"")
$hdr.Add("#define KKZ_CFG_REQUIRE_EXE_VERSION  L`"$(EscCppW $RequireExeVersion)`"")
$hdr.Add("#define KKZ_CFG_REQUIRE_ORIG_SHA256  L`"$(EscCppW $RequireOrigSha256)`"")
$hdr.Add("#define KKZ_CFG_FAIL_MODE            L`"$(EscCppW $FailMode)`"")
Set-Content -LiteralPath $cfgHeader -Value $hdr -Encoding utf8
Say "配置头: $cfgHeader"
Say "版本=$Version 目标=$TargetDll 轮询=${PollMs}ms 稳定=${StabilizeMs}ms TTS=$([int][bool]$EnableTts) FailMode=$FailMode"
Say "默认输出=$OutputFile"

# ---------- 2. 选工具链 ----------
function Resolve-Msvc {
    $vcvarsCandidates = @(
        $env:VCVARS,
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat',
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    return $vcvarsCandidates | Select-Object -First 1
}

function Resolve-Gcc {
    $c = @(
        (Get-Command 'g++.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
        'D:\w64devkit\bin\g++.exe',
        'C:\w64devkit\bin\g++.exe'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    return $c | Select-Object -First 1
}

$msvcVcvars = Resolve-Msvc
$gccPath = Resolve-Gcc
if ($Toolchain -eq 'auto') {
    if ($msvcVcvars) { $useMsvc = $true }
    elseif ($gccPath) { $useMsvc = $false }
    else { Die '找不到 MSVC (vcvarsall.bat) 也找不到 g++；请装 VS BuildTools 或 w64devkit' }
}
elseif ($Toolchain -eq 'msvc') {
    if (-not $msvcVcvars) { Die '指定了 msvc 但找不到 vcvarsall.bat' }
    $useMsvc = $true
}
else {
    if (-not $gccPath) { Die '指定了 gcc 但找不到 g++' }
    $useMsvc = $false
}

# ---------- 3. 编译 ----------
if ($useMsvc) {
    Say "工具链: MSVC ($msvcVcvars)"
    # 用 response file 传参数：避免 cmd 展开 %TEMP%、也避免引号嵌套
    $clRsp = Join-Path $buildDir '_cl.rsp'
    $lnRsp = Join-Path $buildDir '_link.rsp'
    $rspCl = @('/nologo', '/c', '/EHa', '/MT', '/O1', '/DNDEBUG', '/DUNICODE', '/D_UNICODE', '/utf-8', '/W3')
    $rspCl += "/I`"$buildDir`""
    $rspCl += @("/Fo`"$obj`"", "`"$cpp`"")
    Set-Content -LiteralPath $clRsp -Value $rspCl -Encoding ascii

    $rspLn = @('/nologo', '/DLL', '/MACHINE:X86', '/Brepro', "/DEF:`"$def`"", "/MAP:`"$map`"", "/OUT:`"$dll`"", "`"$obj`"")
    Set-Content -LiteralPath $lnRsp -Value $rspLn -Encoding ascii

    $tmpCmd = Join-Path $buildDir '_build-msvc.cmd'
    $lines = @(
        '@echo off'
        "call `"$msvcVcvars`" x86 >nul"
        'if /i not "%VSCMD_ARG_TGT_ARCH%"=="x86" (echo ARCH_NOT_X86 & exit /b 2)'
        "cl @`"$clRsp`""
        'if errorlevel 1 exit /b 3'
        "link @`"$lnRsp`""
        'if errorlevel 1 exit /b 4'
        "dumpbin /nologo /exports /headers `"$dll`" > `"$(Join-Path $buildDir 'dumpbin.txt')`""
        'exit /b 0'
    )
    Set-Content -LiteralPath $tmpCmd -Value $lines -Encoding ascii
    Say 'cl 编译中…'
    & cmd.exe /c "`"$tmpCmd`"" 2>&1 | Tee-Object -FilePath $buildLog | ForEach-Object { Write-Host "  $_" }
    $code = $LASTEXITCODE
    Remove-Item -LiteralPath $tmpCmd -Force -ErrorAction SilentlyContinue
    if ($code -ne 0) { Die "MSVC 构建失败 (exit=$code)，日志见 $buildLog" }
    # 坑位：.def 里出现非 ASCII（中文注释）时 link 只发 LNK4017 警告就丢弃全部导出，
    # 产物看起来"构建成功"但引擎加载不了。这里主动提示根因。
    $logText = Get-Content -LiteralPath $buildLog -Raw -ErrorAction SilentlyContinue
    if ($logText -and $logText -match 'LNK4017') {
        Say '警告：link 报了 LNK4017 => src\kkz_adapter.def 可能含非 ASCII 字符，导出会被静默丢弃'
    }
}
else {
    Say "工具链: gcc ($gccPath)"
    # 直接调用（不经 cmd）：%TEMP% 之类的字面量不会被 shell 展开
    $gcArgs = @('-m32', '-shared', '-O1', '-s',
        '-static', '-static-libgcc', '-static-libstdc++',
        '-DNDEBUG', '-DUNICODE', '-D_UNICODE',
        '-fno-strict-aliasing', '-fexceptions',
        '-Wall', '-Wno-unknown-pragmas', "-I$buildDir")
    $gcArgs += @($cpp, $def, '-o', $dll,
        '-Wl,--enable-stdcall-fixup', '-Wl,--no-insert-timestamp',
        '-luser32', '-lkernel32', '-ladvapi32', '-lversion', '-lsapi', '-lole32', '-loleaut32', '-luuid')

    Say 'g++ 编译中…'
    & $gccPath @gcArgs 2>&1 | Tee-Object -FilePath $buildLog | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll)) {
        $lt = Get-Content -LiteralPath $buildLog -Raw -ErrorAction SilentlyContinue
        if ($lt -and $lt -match 'cannot find -l(kernel32|msvcrt|gcc)') {
            Die 'gcc 构建失败：本机 w64devkit 是纯 64 位发行版，没有 i686 库（-m32 只能编不能链）。请改用 -Toolchain msvc。'
        }
        Die 'gcc 构建失败，见 build.log'
    }
}

if (-not (Test-Path -LiteralPath $dll)) { Die "没有产出 DLL：$dll" }
$size = (Get-Item -LiteralPath $dll).Length
if ($size -lt 4096) { Die "DLL 太小，疑似空壳：$size B" }
Say "产物: $dll ($size B)"

# ---------- 4. 校验 PE（机器类型 + 导出） ----------
$verifyPy = Join-Path $buildDir '_verify_pe.py'
@'
import json, sys
import pefile
path = sys.argv[1]
pe = pefile.PE(path, fast_load=False)
machine = pe.FILE_HEADER.Machine
exp = []
if hasattr(pe, "DIRECTORY_ENTRY_EXPORT") and pe.DIRECTORY_ENTRY_EXPORT:
    for s in pe.DIRECTORY_ENTRY_EXPORT.symbols:
        if s.name:
            exp.append(s.name.decode("ascii", "replace"))
want = ["V2Link", "V2Unlink", "GetModuleThreadModel"]
out = {
    "path": path,
    "machine": hex(machine),
    "is_x86": machine == 0x14C,
    "is_dll": bool(pe.FILE_HEADER.Characteristics & 0x2000),
    "exports": exp,
    "missing": [w for w in want if w not in exp],
    "extra": [e for e in exp if e not in want],
    "subsystem": pe.OPTIONAL_HEADER.Subsystem,
}
print(json.dumps(out, ensure_ascii=False, indent=2))
sys.exit(0 if (out["is_x86"] and out["is_dll"] and not out["missing"]) else 1)
'@ | Set-Content -LiteralPath $verifyPy -Encoding ascii

$py = 'D:\python\python.exe'
if (-not (Test-Path -LiteralPath $py)) {
    $pyCmd = Get-Command python -ErrorAction SilentlyContinue
    if ($pyCmd) { $py = $pyCmd.Source } else { $py = $null }
}
if ($NoVerify) {
    Say '已跳过 PE 校验 (-NoVerify)'
}
elseif ($py) {
    $json = & $py $verifyPy $dll 2>&1
    $code = $LASTEXITCODE
    $json | ForEach-Object { Write-Host "  $_" }
    if ($code -ne 0) { Die 'PE 校验失败：不是 x86 DLL 或导出符号缺失' }
    try {
        $info = ($json -join "`n") | ConvertFrom-Json
        Say ("PE 校验通过: machine=$($info.machine) exports=" + ($info.exports -join ','))
        if ($info.extra -and $info.extra.Count -gt 0) { Say ("注意：多出导出 " + ($info.extra -join ',')) }
    } catch { Say 'PE 校验通过（JSON 解析略过）' }
}
else {
    Say '未找到 python/pefile，跳过 PE 校验（请手动 dumpbin /exports 确认）'
}
# dumpbin 交叉验证（MSVC 构建时已生成）
$dumpbinTxt = Join-Path $buildDir 'dumpbin.txt'
if (Test-Path -LiteralPath $dumpbinTxt) {
    $txt = Get-Content -LiteralPath $dumpbinTxt -Raw
    $missing = @()
    foreach ($sym in @('V2Link', 'V2Unlink', 'GetModuleThreadModel')) {
        if ($txt -notmatch ("\b" + $sym + "\b")) { $missing += $sym }
    }
    if ($txt -match 'machine \(x86\)') { Say 'dumpbin: machine (x86) OK' }
    if ($missing.Count -gt 0) { Die ("dumpbin 未见导出：" + ($missing -join ',')) }
    Say 'dumpbin: 三个导出齐全 OK'
}

# ---------- 5. 发布副本 + SHA256 ----------
$distName = "kkz-adapter-$Version-x86.dll"
$distDll = Join-Path $distDir $distName
Copy-Item -LiteralPath $dll -Destination $distDll -Force
$hash = (Get-FileHash -LiteralPath $distDll -Algorithm SHA256).Hash
"$hash  $distName" | Set-Content -LiteralPath (Join-Path $distDir 'SHA256SUMS.txt') -Encoding ascii
Say "SHA256: $hash"
Say "发布副本: $distDll"
Say 'BUILD_OK'
