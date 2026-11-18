param(
  [string]$GameDir = "D:\Steam\steamapps\common\SenrenBanka",
  [string]$Target   = "getSample.dll",
  [Parameter(Mandatory=$true)][string]$ProxyDll,
  [string]$BackupDir = "D:\DSHWorkSpace\_kkz-trans2\backup"
)
# task-48 / R27: install our proxy as plugin\<Target>, moving the original to <stem>_orig.dll.
$ErrorActionPreference = "Stop"

if (Get-Process -Name SenrenBanka -ErrorAction SilentlyContinue) { "REFUSED GAME_RUNNING"; exit 2 }

$pluginDir = Join-Path $GameDir "plugin"
$dst = Join-Path $pluginDir $Target
$orig = [System.IO.Path]::Combine($pluginDir, [System.IO.Path]::GetFileNameWithoutExtension($Target) + "_orig.dll")
if (-not (Test-Path -LiteralPath $dst))  { "REFUSED TARGET_MISSING $dst"; exit 3 }
if (Test-Path -LiteralPath $orig)        { "REFUSED ORIG_EXISTS $orig"; exit 4 }
if (-not (Test-Path -LiteralPath $ProxyDll)) { "REFUSED PROXY_MISSING $ProxyDll"; exit 5 }

$before = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
$proxy  = (Get-FileHash -LiteralPath $ProxyDll -Algorithm SHA256).Hash
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
Copy-Item -LiteralPath $dst -Destination (Join-Path $BackupDir $Target) -Force
$backup = (Get-FileHash -LiteralPath (Join-Path $BackupDir $Target) -Algorithm SHA256).Hash
if ($backup -ne $before) { "REFUSED BACKUP_HASH_MISMATCH"; exit 6 }

Move-Item -LiteralPath $dst -Destination $orig
Copy-Item -LiteralPath $ProxyDll -Destination $dst -Force
$installed = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
if ($installed -ne $proxy) { "REFUSED INSTALL_HASH_MISMATCH"; exit 7 }

"TARGET_SHA256_BEFORE=" + $before
"ORIG_SHA256=" + (Get-FileHash -LiteralPath $orig -Algorithm SHA256).Hash
"PROXY_SHA256_INSTALLED=" + $installed
"DEPLOY_OK"
