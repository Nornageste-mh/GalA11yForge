param(
  [string]$GameDir = "D:\Steam\steamapps\common\SenrenBanka",
  [Parameter(Mandatory=$true)][string]$OutCsv
)
# task-48 / R27: per-file SHA256 snapshot of the game dir (root + plugin + savedata).
$ErrorActionPreference = "Stop"
$rows = New-Object System.Collections.Generic.List[object]

function Add-Files([string]$scope, [string]$baseRel, [string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { return }
  Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    $rel = $_.FullName.Substring($path.Length).TrimStart('\')
    if ($baseRel -ne "") { $rel = $baseRel + "/" + $rel }
    $rows.Add([pscustomobject]@{
      scope  = $scope
      rel    = $rel
      size   = $_.Length
      sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
  }
}

# root, non-recursive
Get-ChildItem -LiteralPath $GameDir -File | ForEach-Object {
  $rows.Add([pscustomobject]@{
    scope  = "root"
    rel    = $_.Name
    size   = $_.Length
    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
  })
}
Add-Files "plugin"  "plugin"  (Join-Path $GameDir "plugin")
Add-Files "savedata" "savedata" (Join-Path $GameDir "savedata")

$rows | Export-Csv -LiteralPath $OutCsv -NoTypeInformation -Encoding UTF8
$rootN = ($rows | Where-Object { $_.scope -eq "root" }).Count
$plugN = ($rows | Where-Object { $_.scope -eq "plugin" }).Count
$saveN = ($rows | Where-Object { $_.scope -eq "savedata" }).Count
"ROWS=" + $rows.Count
"ROOT_FILES=" + $rootN
"PLUGIN_FILES=" + $plugN
"SAVEDATA_FILES=" + $saveN
$m = ($rows | Sort-Object scope, rel | ForEach-Object { $_.scope + "|" + $_.rel + "|" + $_.size + "|" + $_.sha256 }) -join "`n"
$sha = [System.BitConverter]::ToString(
  [System.Security.Cryptography.SHA256]::Create().ComputeHash(
    [System.Text.Encoding]::UTF8.GetBytes($m))).Replace("-","")
"MANIFEST_SHA256=" + $sha
