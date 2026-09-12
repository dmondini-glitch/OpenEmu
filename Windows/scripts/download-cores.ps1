<#
.SYNOPSIS
  Downloads every libretro core listed in cores.json for one platform into <OutDir>\<Platform>\.
  Platforms: windows-x64 (default), windows-x86. (There are no native windows-arm64 libretro builds; the ARM64
  package ships the x64/x86 cores and runs them through the core host under Windows' emulation.)
.EXAMPLE
  .\scripts\download-cores.ps1 -OutDir .\dist\OpenEmu-Windows-x64\cores -Platform windows-x64
#>
param(
  [string]$OutDir = (Join-Path $PSScriptRoot "..\dist\cores"),
  [ValidateSet("windows-x64","windows-x86")] [string]$Platform = "windows-x64",
  [switch]$Force
)
$ErrorActionPreference = "Stop"
$manifest = Get-Content (Join-Path $PSScriptRoot "..\src\OpenEmu.Core\Resources\cores.json") -Raw | ConvertFrom-Json
$dir = Join-Path $OutDir $Platform
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$ok = 0; $fail = @(); $skipped = @()
foreach ($core in $manifest.cores) {
  if ($core.id -eq "2048") { continue }
  $url = $core.download.$Platform
  if (-not $url) { $skipped += $core.id; continue }
  $target = Join-Path $dir ("{0}_libretro.dll" -f $core.id)
  if ((Test-Path $target) -and -not $Force) { $ok++; continue }
  $zip = "$target.zip"
  try {
    Write-Host ("  {0,-28} {1}" -f $core.id, $url)
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $dir -Force
    Remove-Item $zip -Force
    Set-Content -Path "$target.version" -Value (Get-Date).ToUniversalTime().ToString("o")
    $ok++
  } catch { $fail += $core.id; Write-Warning "  $($core.id) failed: $($_.Exception.Message)" }
}
Write-Host "[$Platform] cores ready: $ok; not available for this platform: $($skipped -join ', '); failed: $($fail -join ', ')"
if ($fail.Count -gt 0) { exit 1 }
