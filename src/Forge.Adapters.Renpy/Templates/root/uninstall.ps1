# GalA11y Forge 卸载脚本（生成文件）
# 1) 只删除 a11y_manifest.json 里登记、且 SHA256 与清单一致的文件；
#    任何被改动过的文件都不会被删（先 -Force 才覆盖该保护）。
# 2) 再清理 game/a11y_platform 下由 Ren'Py 生成的 *.rpyc（.rpy 的编译缓存），
#    最后只在目录已空时删除目录本身。
# 只认 game/a11y_platform 这一层；game/ 下其他目录的 .rpy/.rpyc 一概不碰。
#
# 幂等：清单已不存在（例如重复运行）时不再报错，继续清理补丁目录并 exit 0；
# 显式传入 -Manifest 且文件不存在时才 exit 2。
param(
    [string]$Manifest = (Join-Path $PSScriptRoot "a11y_manifest.json"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# 优先用 Get-FileHash；某些环境（例如从 PowerShell 7 里再启动 5.1，
# 继承的 PSModulePath 指不到系统模块）会找不到该命令，此时退回 .NET SHA256。
function Get-A11ySha256([string]$Path) {
    if ($null -ne (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLower()
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $bytes = $sha.ComputeHash($stream)
        } finally {
            $stream.Dispose()
        }
    } finally {
        $sha.Dispose()
    }
    return ([System.BitConverter]::ToString($bytes) -replace "-", "").ToLower()
}

$platformRel = "game\a11y_platform"
$platformDir = Join-Path $root $platformRel
$selfPath = Join-Path $root "uninstall.ps1"
$manifestWasSpecified = $PSBoundParameters.ContainsKey("Manifest")
$removed = 0
$skipped = 0
$failed = 0
$manifestExists = Test-Path -LiteralPath $Manifest -PathType Leaf

if ($manifestExists) {
    try {
        $plan = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        Write-Host "清单无法解析: $Manifest ($($_.Exception.Message))" -ForegroundColor Red
        exit 2
    }

    foreach ($entry in @($plan.files)) {
        if ($null -eq $entry -or [string]::IsNullOrWhiteSpace($entry.path)) { continue }
        $rel = $entry.path -replace '/', '\'
        $full = Join-Path $root $rel
        # 卸载脚本自己留到最后删：中途有文件被保留时，用户还要用它再跑一次 -Force。
        if ($full -ieq $selfPath) { continue }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            Write-Host "已不存在: $rel"
            continue
        }
        try {
            $hash = Get-A11ySha256 $full
        } catch {
            Write-Host "无法读取哈希: $rel ($($_.Exception.Message))" -ForegroundColor Yellow
            $failed++
            continue
        }
        if ($hash -ne ([string]$entry.sha256).ToLower() -and -not $Force) {
            Write-Host "被改动过，保留: $rel" -ForegroundColor Yellow
            $skipped++
            continue
        }
        try {
            Remove-Item -LiteralPath $full -Force
            Write-Host "已删除: $rel"
            $removed++
        } catch {
            Write-Host "删除失败: $rel ($($_.Exception.Message))" -ForegroundColor Red
            $failed++
        }
    }
} elseif ($manifestWasSpecified) {
    Write-Host "找不到清单: $Manifest" -ForegroundColor Red
    exit 2
} else {
    Write-Host "清单已不存在（可能已卸载过），继续清理补丁目录。" -ForegroundColor Yellow
}

# 清 Ren'Py 编译缓存：只删 game/a11y_platform 顶层、扩展名为 .rpyc 的文件
if (Test-Path -LiteralPath $platformDir -PathType Container) {
    try {
        $rpycs = @(Get-ChildItem -LiteralPath $platformDir -Filter "*.rpyc" -File -Force)
    } catch {
        Write-Host "无法枚举目录: $platformRel ($($_.Exception.Message))" -ForegroundColor Red
        $failed++
        $rpycs = @()
    }
    if ($rpycs.Count -gt 0) {
        Write-Host "清理 Ren'Py 编译缓存（$($rpycs.Count) 个 .rpyc）"
    }
    foreach ($file in $rpycs) {
        try {
            Remove-Item -LiteralPath $file.FullName -Force
            Write-Host "已删除: $platformRel\$($file.Name)"
            $removed++
        } catch {
            Write-Host "删除失败: $platformRel\$($file.Name) ($($_.Exception.Message))" -ForegroundColor Red
            $failed++
        }
    }

    # 只在目录已空时删除目录本身；残留文件（用户改动/未登记）一律保留
    try {
        $left = @(Get-ChildItem -LiteralPath $platformDir -Force)
    } catch {
        $left = @($null)
        $failed++
    }
    if ($left.Count -eq 0) {
        try {
            Remove-Item -LiteralPath $platformDir -Force
            Write-Host "已删除空目录: $platformRel"
        } catch {
            Write-Host "删除目录失败: $platformRel ($($_.Exception.Message))" -ForegroundColor Red
            $failed++
        }
    } else {
        $names = ($left | ForEach-Object { $_.Name }) -join ", "
        Write-Host "目录非空，保留: $platformRel（残留: $names）" -ForegroundColor Yellow
    }
} else {
    Write-Host "已不存在: $platformRel"
}

Write-Host ""
Write-Host "完成：删除 $removed 个，跳过 $skipped 个，失败 $failed 个。"

# 全部清干净（含目录已删）时，清单本身也一起删；有跳过/失败/残留则保留，便于排查
$platformStillThere = Test-Path -LiteralPath $platformDir
if ($manifestExists -and $skipped -eq 0 -and $failed -eq 0 -and -not $platformStillThere) {
    # 运行期日志也是补丁产物；只在确认全部清干净时删，排查失败时保留。
    $logPath = Join-Path $root "a11y_speech.log"
    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        Remove-Item -LiteralPath $logPath -Force
        Write-Host "已删除: a11y_speech.log"
    }
    $selfManifest = Join-Path $root "a11y_manifest.json"
    if ((Test-Path -LiteralPath $selfManifest -PathType Leaf) -and
        ((Resolve-Path -LiteralPath $selfManifest).Path -eq (Resolve-Path -LiteralPath $Manifest).Path)) {
        Remove-Item -LiteralPath $selfManifest -Force
        Write-Host "已删除: a11y_manifest.json"
        # 0.2.1 fix (W2-P): deploy_plan.json 是部署清单副本，未登记在 a11y_manifest 中，
        # 干净卸载时一并删除，避免在游戏根目录留下补丁痕迹。
        $planPath = Join-Path $root "deploy_plan.json"
        if (Test-Path -LiteralPath $planPath -PathType Leaf) {
            Remove-Item -LiteralPath $planPath -Force
            Write-Host "已删除: deploy_plan.json"
        }
        if (Test-Path -LiteralPath $selfPath -PathType Leaf) {
            Remove-Item -LiteralPath $selfPath -Force
            Write-Host "已删除: uninstall.ps1"
        }
    }
}
if ($failed -gt 0) { exit 1 }
exit 0
