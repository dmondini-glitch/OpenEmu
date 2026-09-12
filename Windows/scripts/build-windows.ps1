<#
.SYNOPSIS
  Builds a self-contained OpenEmu for Windows distribution for one architecture, with all console cores bundled.
  Output: dist\OpenEmu-Windows-<arch>\  and  dist\OpenEmu-Windows-<arch>.zip

  x64   : app + cores\windows-x64 (+ OpenEmu.CoreHost.exe for optional process isolation)
  x86   : app + cores\windows-x86 (+ x86 core host)
  arm64 : native ARM64 app + host\x64 + host\x86 core hosts + cores\windows-x64 (Windows 11 ARM runs them through
          its x64 emulation; add -IncludeX86Cores for Windows 10 ARM, which only emulates x86)
.EXAMPLE
  .\scripts\build-windows.ps1 -Arch x64
  .\scripts\build-windows.ps1 -Arch arm64 -IncludeX86Cores
#>
param(
  [ValidateSet("x64","x86","arm64")] [string]$Arch = "x64",
  [string]$Configuration = "Release",
  [switch]$SkipCores,
  [switch]$IncludeX86Cores
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root "dist\OpenEmu-Windows-$Arch"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

function Publish($project, $rid, $dest, $selfContained = $true) {
  dotnet publish (Join-Path $root $project) -c $Configuration -r $rid --self-contained $selfContained -o $dest -p:DebugType=none
  if ($LASTEXITCODE -ne 0) { throw "publish failed: $project ($rid)" }
}

Write-Host "== app ($Arch)"
Publish "src\OpenEmu.App\OpenEmu.App.csproj" "win-$Arch" $out
Publish "src\OpenEmu.Cli\OpenEmu.Cli.csproj" "win-$Arch" (Join-Path $out "cli")

if ($Arch -eq "arm64") {
  Write-Host "== core hosts (x64 + x86) for ARM64"
  Publish "src\OpenEmu.CoreHost\OpenEmu.CoreHost.csproj" "win-x64" (Join-Path $out "host\x64")
  Publish "src\OpenEmu.CoreHost\OpenEmu.CoreHost.csproj" "win-x86" (Join-Path $out "host\x86")
  $corePlatforms = @("windows-x64"); if ($IncludeX86Cores) { $corePlatforms += "windows-x86" }
} else {
  Write-Host "== core host ($Arch)"
  Publish "src\OpenEmu.CoreHost\OpenEmu.CoreHost.csproj" "win-$Arch" (Join-Path $out "host\$Arch")
  $corePlatforms = @("windows-$Arch")
}

if (-not $SkipCores) {
  foreach ($p in $corePlatforms) { & (Join-Path $PSScriptRoot "download-cores.ps1") -OutDir (Join-Path $out "cores") -Platform $p }
}
Copy-Item (Join-Path $root "README.md") $out -Force
Copy-Item (Join-Path $root "docs\EMPACOTAMENTO-WINDOWS.md") $out -Force
$zip = Join-Path $root "dist\OpenEmu-Windows-$Arch.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Distribution ready: $out  ->  $zip"
