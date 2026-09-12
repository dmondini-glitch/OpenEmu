<#
.SYNOPSIS
  Builds a self-contained OpenEmu for Windows x64 distribution with ALL console cores bundled.
  Output: dist\OpenEmu-Windows-x64\  and  dist\OpenEmu-Windows-x64.zip
#>
param([string]$Configuration = "Release", [switch]$SkipCores)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root "dist\OpenEmu-Windows-x64"
dotnet publish (Join-Path $root "src\OpenEmu.App\OpenEmu.App.csproj") -c $Configuration -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -o $out
dotnet publish (Join-Path $root "src\OpenEmu.Cli\OpenEmu.Cli.csproj") -c $Configuration -r win-x64 --self-contained false -o (Join-Path $out "cli")
if (-not $SkipCores) { & (Join-Path $PSScriptRoot "download-cores.ps1") -OutDir (Join-Path $out "cores") }
Copy-Item (Join-Path $root "README.md") $out -Force
Compress-Archive -Path "$out\*" -DestinationPath (Join-Path $root "dist\OpenEmu-Windows-x64.zip") -Force
Write-Host "Distribution ready: $out"
