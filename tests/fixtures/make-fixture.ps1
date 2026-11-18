#requires -Version 7.0
<#
.SYNOPSIS
    一键重建「假 Unity Mono 游戏」夹具（合成样本，不是真实游戏）。

.DESCRIPTION
    在 tests/fixtures/fake-game-unity-mono/ 下生成：
      5omeday.exe                                net8.0 空 Main 控制台程序（单文件发布；失败时回退 apphost）
      5omeday_Data/Managed/Assembly-CSharp.dll  netstandard2.1 类库，含 FakeDialogueManager 与 Unity 存根类型
      5omeday_Data/globalgamemanagers            前 512 字节含 Unity 版本号 2021.3.16f1 的合成二进制
      UnityPlayer.dll                            有效 PE 占位（复用 5omeday.exe 字节，不含真实 Unity 代码）
      MonoBleedingEdge/EmbedRuntime/.keep        目录占位
      fixture-manifest.json                      文件清单（大小 + SHA256），便于核对夹具是否被改动

    夹具只用于「引擎识别 / 静态分析 / 生成器 / E2E 骨架」的离线测试：
      * 不包含任何商业游戏资源、台词、图片或音频；
      * 不是可运行游戏，不可注入、不可启动；
      * FakeDialogueManager 只提供静态分析可识别的 set_text 调用点。

    脚本可重复运行：每次重建上述生成物，保留手写的 README.md 与 .gitignore。
    生成的二进制默认不提交（见夹具目录下 .gitignore）。

.PARAMETER DotnetPath
    dotnet 可执行文件路径，默认 D:\dotnet\dotnet.exe；找不到时回退 PATH 中的 dotnet。

.PARAMETER OutDir
    输出目录，默认脚本旁的 fake-game-unity-mono/。

.PARAMETER Configuration
    Debug 或 Release，默认 Release。

.PARAMETER KeepWork
    保留 %TEMP% 下的临时编译目录，便于排查编译失败。

.EXAMPLE
    pwsh -NoProfile -File tests/fixtures/make-fixture.ps1

.EXAMPLE
    pwsh -NoProfile -File tests/fixtures/make-fixture.ps1 -OutDir D:\temp\fake-game -KeepWork
#>
[CmdletBinding()]
param(
    [string]$DotnetPath = 'D:\dotnet\dotnet.exe',
    [string]$OutDir,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$KeepWork
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $scriptDir 'fake-game-unity-mono'
}
$OutDir = [IO.Path]::GetFullPath($OutDir)

if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCmd) {
        throw "找不到 dotnet：$DotnetPath；请用 -DotnetPath 指定 D:\dotnet\dotnet.exe"
    }
    $DotnetPath = $dotnetCmd.Source
}
$DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
$dotnetVersion = (& $DotnetPath --version).Trim()

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('forge-fixture-' + [Guid]::NewGuid().ToString('N').Substring(0, 10))
$publishMode = 'single-file'
$publishWarning = $null

function Write-Utf8BomFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )
    $parent = Split-Path -Parent $Path
    if ($parent) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($true))
}

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$What
    )
    $output = & $DotnetPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $tail = ($output | Select-Object -Last 40) -join [Environment]::NewLine
        throw "$What 失败（dotnet 退出码 $exitCode）：`n$tail"
    }
    return ($output -join [Environment]::NewLine)
}

try {
    New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

    # ---------- 1. 生成临时工程源码（只在 %TEMP% 里，不污染仓库） ----------
    $hostDir = Join-Path $workRoot 'host'
    $managedDir = Join-Path $workRoot 'managed'

    $hostProj = Join-Path $hostDir 'FakeGameHost.csproj'
    Write-Utf8BomFile -Path $hostProj -Content @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>5omeday</AssemblyName>
    <RootNamespace>FakeGameHost</RootNamespace>
    <Version>0.0.0.0</Version>
    <FileVersion>0.0.0.0</FileVersion>
    <AssemblyVersion>0.0.0.0</AssemblyVersion>
    <Deterministic>true</Deterministic>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
'@

    Write-Utf8BomFile -Path (Join-Path $hostDir 'Program.cs') -Content @'
namespace FakeGameHost;

/// <summary>
/// 合成夹具入口：空 Main，不做任何事。
/// 检测层只关心 5omeday.exe 是否存在、名字是否与 5omeday_Data 配对。
/// </summary>
internal static class Program
{
    private static void Main()
    {
        // 故意留空：这不是可运行的游戏，不允许启动。
    }
}
'@

    $managedProj = Join-Path $managedDir 'FakeManaged.csproj'
    Write-Utf8BomFile -Path $managedProj -Content @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AssemblyName>Assembly-CSharp</AssemblyName>
    <RootNamespace>FakeGame</RootNamespace>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <Deterministic>true</Deterministic>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
'@

    Write-Utf8BomFile -Path (Join-Path $managedDir 'FakeTypes.cs') -Content @'
using System;

// 合成夹具：自造 UnityEngine.UI.Text / TMPro.TMP_Text 存根，仅用于让静态分析器
// 看到真实的 set_text 调用点。它们不是 Unity 程序集，不能加载进 Unity，也不能注入。
namespace UnityEngine.UI
{
    /// <summary>合成存根：对应 UnityEngine.UI.Text。</summary>
    public class Text
    {
        private string _value = string.Empty;

        public string text
        {
            get { return _value; }
            set { _value = value; }
        }
    }
}

namespace TMPro
{
    /// <summary>合成存根：对应 TMPro.TMP_Text。</summary>
    public class TMP_Text
    {
        private string _value = string.Empty;

        public string text
        {
            get { return _value; }
            set { _value = value; }
        }
    }
}

namespace FakeGame
{
    /// <summary>
    /// 合成对话管理器：方法名、字段类型都按真实 Galgame 常见形态构造，
    /// 供 Unity Mono 分析器识别文本汇点（sink）。
    /// </summary>
    public sealed class FakeDialogueManager
    {
        private UnityEngine.UI.Text _text;
        private TMPro.TMP_Text _tmp;

        public FakeDialogueManager()
        {
            _text = new UnityEngine.UI.Text();
            _tmp = new TMPro.TMP_Text();
        }

        /// <summary>合成台词入口，内部写入两种 UI 文本组件。</summary>
        public void ShowText(string text)
        {
            _text.text = text;
            _tmp.text = text;
        }

        /// <summary>合成选项入口。</summary>
        public void ShowChoice(string[] options)
        {
            if (options == null || options.Length == 0)
            {
                return;
            }

            _tmp.text = options[0];
        }
    }

    /// <summary>
    /// 合成样例台本：把哨兵字符串喂给汇点，供「生成物不得包含游戏原文」的 grep 断言使用。
    /// 这两句是自造测试文本，不是任何游戏的台词。
    /// </summary>
    public static class FakeDialogueScript
    {
        public const string SyntheticLine = "FORGE_FIXTURE_LINE_DO_NOT_SHIP_5OMEDAY";
        public const string SyntheticChineseLine = "【合成夹具】这句测试台词不得进入补丁工程。";

        public static void PlaySyntheticScript()
        {
            var manager = new FakeDialogueManager();
            manager.ShowText(SyntheticLine);
            manager.ShowText(SyntheticChineseLine);
            manager.ShowChoice(new[] { "OPTION_A", "OPTION_B" });
        }
    }
}
'@

    # 注意：不要手写 AssemblyInfo.cs —— SDK 默认 GenerateAssemblyInfo=true，会与本文件重复。

    # ---------- 2. 编译宿主 exe（优先单文件发布，失败回退 apphost） ----------
    $publishDir = Join-Path $workRoot 'publish'
    try {
        [void](Invoke-Dotnet -What '编译 5omeday.exe（单文件发布）' -Arguments @(
            'publish', $hostProj, '-c', $Configuration, '-r', 'win-x64',
            '--self-contained', 'false',
            '-p:PublishSingleFile=true',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            '-o', $publishDir, '--nologo', '-v', 'quiet'
        ))
    }
    catch {
        $publishWarning = $_.Exception.Message
        $publishMode = 'apphost-fallback'
    }

    $publishedExe = Join-Path $publishDir '5omeday.exe'
    if (Test-Path -LiteralPath $publishedExe -PathType Leaf) {
        $hostExe = $publishedExe
    }
    else {
        $publishMode = 'apphost-fallback'
        [void](Invoke-Dotnet -What '编译 5omeday.exe（普通构建回退）' -Arguments @(
            'build', $hostProj, '-c', $Configuration, '--nologo', '-v', 'quiet'
        ))
        $hostExe = Join-Path $hostDir "bin/$Configuration/net8.0/5omeday.exe"
        if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) {
            throw "找不到编译产物：$hostExe"
        }
    }

    # ---------- 3. 编译 Assembly-CSharp.dll ----------
    [void](Invoke-Dotnet -What '编译 Assembly-CSharp.dll' -Arguments @(
        'build', $managedProj, '-c', $Configuration, '--nologo', '-v', 'quiet'
    ))
    $managedDll = Join-Path $managedDir "bin/$Configuration/netstandard2.1/Assembly-CSharp.dll"
    if (-not (Test-Path -LiteralPath $managedDll -PathType Leaf)) {
        throw "找不到编译产物：$managedDll"
    }

    # 抽样核对元数据字符串，避免「编译成功但类型被裁掉」的假夹具。
    $dllBytes = [IO.File]::ReadAllBytes($managedDll)
    $dllText = [Text.Encoding]::UTF8.GetString($dllBytes)
    foreach ($needle in @('FakeDialogueManager', 'ShowText', 'ShowChoice', 'set_text', 'TMP_Text')) {
        if (-not $dllText.Contains($needle, [StringComparison]::Ordinal)) {
            throw "Assembly-CSharp.dll 缺少预期元数据字符串：$needle"
        }
    }

    # ---------- 4. 写入夹具目录（保留 README.md / .gitignore） ----------
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    foreach ($generated in @('5omeday.exe', '5omeday_Data', 'UnityPlayer.dll', 'MonoBleedingEdge', 'fixture-manifest.json')) {
        $target = Join-Path $OutDir $generated
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }

    $dataDir = Join-Path $OutDir '5omeday_Data'
    $managedTargetDir = Join-Path $dataDir 'Managed'
    New-Item -ItemType Directory -Force -Path $managedTargetDir | Out-Null

    Copy-Item -LiteralPath $hostExe -Destination (Join-Path $OutDir '5omeday.exe') -Force
    Copy-Item -LiteralPath $managedDll -Destination (Join-Path $managedTargetDir 'Assembly-CSharp.dll') -Force
    # UnityPlayer.dll 只要求「存在且是有效 PE」；这里复用宿主的 PE 字节，明确不含真实 Unity 代码。
    Copy-Item -LiteralPath $hostExe -Destination (Join-Path $OutDir 'UnityPlayer.dll') -Force

    # globalgamemanagers：前 512 字节含版本号，供 UnityVersionDetector 正则匹配。
    $ggm = [byte[]]::new(1024)
    $versionBytes = [Text.Encoding]::ASCII.GetBytes('2021.3.16f1')
    [Array]::Copy($versionBytes, 0, $ggm, 64, $versionBytes.Length)
    $tagBytes = [Text.Encoding]::ASCII.GetBytes('5omeday synthetic fixture')
    [Array]::Copy($tagBytes, 0, $ggm, 128, $tagBytes.Length)
    [IO.File]::WriteAllBytes((Join-Path $dataDir 'globalgamemanagers'), $ggm)

    $monoDir = Join-Path $OutDir 'MonoBleedingEdge/EmbedRuntime'
    New-Item -ItemType Directory -Force -Path $monoDir | Out-Null
    Set-Content -LiteralPath (Join-Path $monoDir '.keep') -Value 'synthetic placeholder' -Encoding utf8BOM

    # ---------- 5. 写文件清单，便于核对夹具完整性 ----------
    $generatedAt = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $files = Get-ChildItem -LiteralPath $OutDir -Recurse -File |
        Where-Object {
            $_.Name -ne 'fixture-manifest.json' -and
            $_.Name -ne 'README.md' -and
            $_.Name -ne '.gitignore'
        } |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path   = [IO.Path]::GetRelativePath($OutDir, $_.FullName).Replace('\', '/')
                size   = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }

    $manifest = [ordered]@{
        schemaVersion = 1
        kind          = 'synthetic-fixture'
        game          = '5omeday'
        unityVersion  = '2021.3.16f1'
        generator     = 'tests/fixtures/make-fixture.ps1'
        dotnetVersion = $dotnetVersion
        publishMode   = $publishMode
        generatedAt   = $generatedAt
        files         = @($files)
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $OutDir 'fixture-manifest.json'), $manifestJson, [Text.UTF8Encoding]::new($false))

    # ---------- 6. 输出摘要 ----------
    Write-Host ''
    Write-Host "夹具已生成：$OutDir" -ForegroundColor Green
    Write-Host "  宿主 exe 模式：$publishMode（dotnet $dotnetVersion，$Configuration）"
    if ($publishWarning) {
        Write-Host "  单文件发布回退原因：$($publishWarning.Split([Environment]::NewLine)[0])" -ForegroundColor Yellow
    }
    Write-Host ''
    $files | ForEach-Object {
        Write-Host ("  {0,-46} {1,9}  {2}" -f $_.path, $_.size, $_.sha256.Substring(0, 12))
    }
    Write-Host ''
    Write-Host '提示：夹具是合成样本，不可运行、不可注入；重建后请运行 dotnet test 验证。' -ForegroundColor DarkGray
}
finally {
    if ($KeepWork) {
        Write-Host "临时编译目录已保留：$workRoot" -ForegroundColor DarkGray
    }
    elseif (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
