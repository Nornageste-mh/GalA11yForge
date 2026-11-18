namespace GalA11yForge.Generators.Templates;

/// <summary>
/// 生成构建脚本。只负责编译，不复制、不写游戏目录；安装动作留给用户按说明手动完成。
/// </summary>
internal static class BuildScriptTemplate
{
    internal const string Text = """
#requires -Version 5.1
<#
  本脚本由 GalA11y Forge（无障碍补丁工坊）生成，生成器版本 {{GENERATOR_VERSION}}。
  只做一件事：把当前目录的补丁工程编译成 dll。不会写入游戏目录。
  用法：
    powershell -ExecutionPolicy Bypass -File .\build.ps1
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -GameDir "D:\Games\xxx" -BepInExDir "D:\Games\xxx\BepInEx\core"
#>
[CmdletBinding()]
param(
    [string]$GameDir = '{{GAME_PATH_PS}}',
    [string]$BepInExDir = '',
    [string]$Configuration = 'Release',
    [string]$Dotnet = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    throw '没有游戏目录：请用 -GameDir "游戏安装目录" 指定。'
}
if (-not (Test-Path -LiteralPath $GameDir)) {
    throw "游戏目录不存在：$GameDir"
}

if ([string]::IsNullOrWhiteSpace($BepInExDir)) {
    $BepInExDir = Join-Path $GameDir 'BepInEx\core'
}
$bepInExDll = Join-Path $BepInExDir 'BepInEx.dll'
$harmonyDll = Join-Path $BepInExDir '0Harmony.dll'
if (-not (Test-Path -LiteralPath $bepInExDll) -or -not (Test-Path -LiteralPath $harmonyDll)) {
    throw "BepInEx 5 的核心文件不在 $BepInExDir。本补丁不附带 BepInEx，请自行下载 BepInEx 5.4.x x64 并解压到游戏目录。"
}

$managed = Join-Path $GameDir '{{DATADIR}}\Managed'
if (-not (Test-Path -LiteralPath $managed)) {
    throw "找不到 Unity 托管目录：$managed。若数据目录不叫 {{DATADIR}}，请加 -p:ManagedDir 覆盖，或在 csproj 里改 ManagedDir。"
}

if ([string]::IsNullOrWhiteSpace($Dotnet)) {
    $Dotnet = 'dotnet'
}
if ($null -eq (Get-Command $Dotnet -ErrorAction SilentlyContinue)) {
    throw "找不到 dotnet（$Dotnet）。请安装 .NET SDK 8，或用 -Dotnet 指定 dotnet.exe 的完整路径。"
}

$project = Join-Path $root '{{ASSEMBLY}}.csproj'
Write-Host "构建 $project"
& $Dotnet build $project -c $Configuration "-p:GameDir=$GameDir" "-p:BepInExDir=$BepInExDir" -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "构建失败（退出码 $LASTEXITCODE）。请把上面的报错原文发给补丁作者，不要自己改生成的代码。"
}

$dll = Join-Path $root "bin\$Configuration\{{ASSEMBLY}}.dll"
if (-not (Test-Path -LiteralPath $dll)) {
    throw "构建命令成功，但没有找到产物：$dll"
}

Write-Host ''
Write-Host '构建成功。生成的插件：'
Write-Host "  $dll"
Write-Host ''
Write-Host '安装（手动两步，脚本不替你写游戏目录）：'
Write-Host "  1) 在游戏目录下新建 BepInEx\plugins\{{ASSEMBLY}}\"
Write-Host "  2) 把上面这个 dll 复制进去，然后启动游戏"
Write-Host '卸载：删掉 BepInEx\plugins\{{ASSEMBLY}}\ 这个文件夹；配置文件是 BepInEx\config\{{GUID}}.cfg'
""";
}
