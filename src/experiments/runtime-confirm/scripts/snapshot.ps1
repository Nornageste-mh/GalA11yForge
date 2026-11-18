param(
  [Parameter(Mandatory=$true)][string]$Root,
  [Parameter(Mandatory=$true)][string]$Out
)
$ErrorActionPreference = 'Stop'
$rootFull = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
$files = New-Object System.Collections.Generic.List[object]
$dirs  = New-Object System.Collections.Generic.List[string]

Get-ChildItem -LiteralPath $rootFull -Recurse -Force -Directory | Sort-Object FullName | ForEach-Object {
  $dirs.Add($_.FullName.Substring($rootFull.Length + 1))
}
Get-ChildItem -LiteralPath $rootFull -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
  $rel = $_.FullName.Substring($rootFull.Length + 1)
  $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  $files.Add([pscustomobject]@{
    path = $rel
    size = $_.Length
    mtime_utc = $_.LastWriteTimeUtc.ToString('o')
    sha256 = $h
  })
}

$doc = [pscustomobject]@{
  schema = 'gala11y.tree-snapshot/v1'
  root = $rootFull
  taken_at_utc = (Get-Date).ToUniversalTime().ToString('o')
  file_count = $files.Count
  dir_count = $dirs.Count
  total_bytes = ($files | Measure-Object -Property size -Sum).Sum
  dirs = $dirs
  files = $files
}
$parent = Split-Path -Parent $Out
if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$doc | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath $Out -Encoding UTF8
Write-Output ("snapshot {0}: files={1} dirs={2} bytes={3}" -f $Out, $files.Count, $dirs.Count, $doc.total_bytes)
