<#
.SYNOPSIS
  Downloads every libretro core listed in cores.json (all 42 OpenEmu systems) for Windows x64
  into a folder that is shipped next to OpenEmu.exe as "cores\".
.EXAMPLE
  .\scripts\download-cores.ps1 -OutDir .\dist\cores
#>
param(
  [string]$OutDir = (Join-Path $PSScriptRoot "..\dist\cores"),
  [string]$Platform = "windows-x64",
  [switch]$Force
)
$ErrorActionPreference = "Stop"
$manifest = Get-Content (Join-Path $PSScriptRoot "..\src\OpenEmu.Core\Resources\cores.json") -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$ext = if ($Platform -like "windows*") { ".dll" } else { ".dylib" }
$ok = 0; $fail = @()
foreach ($core in $manifest.cores) {
  if ($core.id -eq "2048") { continue }
  $url = $core.download.$Platform
  if (-not $url) { continue }
  $target = Join-Path $OutDir ("{0}_libretro{1}" -f $core.id, $ext)
  if ((Test-Path $target) -and -not $Force) { $ok++; continue }
  $zip = "$target.zip"
  try {
    Write-Host ("  {0,-28} {1}" -f $core.id, $url)
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $OutDir -Force
    Remove-Item $zip -Force
    Set-Content -Path "$target.version" -Value (Get-Date).ToUniversalTime().ToString("o")
    $ok++
  } catch { $fail += $core.id; Write-Warning "  $($core.id) failed: $($_.Exception.Message)" }
}
Write-Host "cores ready: $ok; failed: $($fail -join ', ')"
if ($fail.Count -gt 0) { exit 1 }
