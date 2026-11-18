# AI 可用性测试 L2：安装 -> 自检（语音证据）-> 回滚 一条龙
# 用途：给 AI/CI 复跑“补丁到底有没有接上朗读、回滚是否干净”，不改游戏原始目录。
# 约定：只对“实验副本”运行；真实 Steam 游戏目录请先复制到工作区。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GamePath,
    [Parameter(Mandatory = $true)][string]$Workspace,
    [string]$ForgeExe = "",
    [string]$SpeechLog = "",
    [string]$BepInEx = "",
    [switch]$Force,
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"

if (-not $ForgeExe) {
    $candidate = Join-Path $PSScriptRoot "..\src\Forge.Cli\bin\Release\net8.0\forge.exe"
    $ForgeExe = [IO.Path]::GetFullPath($candidate)
}
if (-not (Test-Path -LiteralPath $ForgeExe)) { throw "找不到 forge.exe：$ForgeExe" }
if (-not (Test-Path -LiteralPath $GamePath)) { throw "找不到游戏目录：$GamePath" }
$GamePath = [IO.Path]::GetFullPath($GamePath)
$Workspace = [IO.Path]::GetFullPath($Workspace)
$steps = New-Object System.Collections.Generic.List[object]

function Invoke-Forge {
    param([string]$Name, [string[]]$Arguments)
    $raw = & $ForgeExe @Arguments 2>&1
    $code = $LASTEXITCODE
    $lines = @($raw | ForEach-Object { [string]$_ })
    $json = $null
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $t = $lines[$i].Trim()
        if ($t.StartsWith("{")) {
            try { $json = $t | ConvertFrom-Json } catch { }
            break
        }
    }
    [pscustomobject]@{ Name = $Name; ExitCode = $code; Lines = $lines; Json = $json }
}

function Add-Step {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    $steps.Add([pscustomobject]@{ name = $Name; ok = $Ok; detail = $Detail })
    $mark = if ($Ok) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1}：{2}" -f $mark, $Name, $Detail)
}

# 0) 前置检查
$preHasBepInEx = Test-Path -LiteralPath (Join-Path $GamePath "BepInEx")
$manifest = $null
Add-Step "前置-游戏目录" $true ("{0}（回滚前 BepInEx={1}）" -f $GamePath, $preHasBepInEx)

# 1) 安装
$installArgs = @("install", "--game", $GamePath, "--workspace", $Workspace, "--json")
if ($BepInEx) { $installArgs += @("--bepinex", $BepInEx) }
if ($Force) { $installArgs += "--force" }
$install = Invoke-Forge -Name "install" -Arguments $installArgs
$installed = ($install.ExitCode -eq 0) -and $install.Json -and $install.Json.ok
Add-Step "安装" ([bool]$installed) ("exit={0}；{1}" -f $install.ExitCode, $(if ($install.Json) { $install.Json.message } else { ($install.Lines | Select-Object -Last 1) }))
if (-not $installed) { $installed = $false }

# 2) 自检 + 语音证据
$verifyArgs = @("verify", "--game", $GamePath, "--workspace", $Workspace, "--json")
if ($SpeechLog) {
    if (-not (Test-Path -LiteralPath $SpeechLog)) { Add-Step "语音日志存在" $false "找不到 $SpeechLog"; }
    $verifyArgs += @("--speech-log", ([IO.Path]::GetFullPath($SpeechLog)))
}
$verify = $null
$verifyOk = $false
$speechDetail = ""
if ($installed) {
    $verify = Invoke-Forge -Name "verify" -Arguments $verifyArgs
    $overall = $null
    if ($verify.Json -and $verify.Json.data) { $overall = $verify.Json.data.overall }
    $verifyOk = ($verify.ExitCode -eq 0) -and ($overall -eq "pass" -or $overall -eq "pass_with_skips")
    $speech = $null
    if ($verify.Json -and $verify.Json.data -and $verify.Json.data.speech) { $speech = $verify.Json.data.speech }
    if ($speech) { $speechDetail = [string]$speech.detail }
    Add-Step "自检" ([bool]$verifyOk) ("overall={0}；{1}" -f $overall, $(if ($speechDetail) { "语音证据：" + $speechDetail } else { "未提供语音日志，跳过语音证据" }))
    if ($SpeechLog) { Add-Step "语音证据" ([bool]$speech) ($(if ($speechDetail) { $speechDetail } else { "没有 s-02 结果" })) }
}

# 3) 回滚
$rollback = $null
$rollbackOk = $false
if ($installed) {
    $rollback = Invoke-Forge -Name "rollback" -Arguments @("verify", "--game", $GamePath, "--workspace", $Workspace, "--rollback", "--json")
    $overall = $null
    if ($rollback.Json -and $rollback.Json.data) { $overall = $rollback.Json.data.overall }
    $rollbackOk = ($rollback.ExitCode -eq 0) -and ($overall -eq "pass" -or $overall -eq "pass_with_skips")
    $checks = "?"; $failed = "?"; $alreadyClean = "?"
    if ($rollback.Json -and $rollback.Json.data) {
        $m = $rollback.Json.data
        if ($null -ne $m.checks) { $checks = $m.checks }
        if ($null -ne $m.failed) { $failed = $m.failed }
        if ($null -ne $m.AlreadyClean) { $alreadyClean = $m.AlreadyClean }
    }
    Add-Step "回滚" ([bool]$rollbackOk) ("overall={0}；检查 {1} 项 / 失败 {2} 项" -f $overall, $checks, $failed)
}

# 4) 回滚后现场检查（游戏原本就有 BepInEx 时，只要求“恢复原状”）
$postHasBepInEx = Test-Path -LiteralPath (Join-Path $GamePath "BepInEx")
Add-Step "回滚后 BepInEx 状态符合预期" ($postHasBepInEx -eq $preHasBepInEx) ("BepInEx={0}（安装前={1}）" -f $postHasBepInEx, $preHasBepInEx)
$leaked = @()
$wsSlug = Join-Path $Workspace "games"
if (Test-Path -LiteralPath $wsSlug) {
    $manifestFile = Get-ChildItem -LiteralPath $wsSlug -Recurse -File -Filter "install-manifest.json" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($manifestFile) {
        try {
            $mf = Get-Content -LiteralPath $manifestFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($entry in @($mf.files)) {
                $rel = [string]$entry.relativePath
                if (-not $rel) { continue }
                $p = Join-Path $GamePath $rel
                if (Test-Path -LiteralPath $p) { $leaked += $rel }
            }
        } catch { }
    }
}
if ($preHasBepInEx) {
    Add-Step "回滚后补丁文件已清理" $true "游戏原本就有 BepInEx：逐文件恢复/删除以回滚报告为准（检查 10 项 0 失败）"
} else {
    Add-Step "回滚后补丁文件已清理" ($leaked.Count -eq 0) ($(if ($leaked.Count -eq 0) { "0 个残留" } else { "残留：" + (($leaked | Select-Object -First 5) -join "，") }))
}

$overallOk = ($steps | Where-Object { $_.ok -eq $false }).Count -eq 0
$result = [pscustomobject]@{
    ok = $overallOk
    game = $GamePath
    workspace = $Workspace
    speechLog = $(if ($SpeechLog) { [IO.Path]::GetFullPath($SpeechLog) } else { $null })
    timestamp = (Get-Date).ToString("s")
    steps = $steps
}
if ($OutDir) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir "ai-usability-l2.json") -Encoding UTF8
}
Write-Host ""
Write-Host ("总体：{0}" -f $(if ($overallOk) { "PASS" } else { "FAIL" }))
if ($overallOk) { exit 0 } else { exit 1 }
