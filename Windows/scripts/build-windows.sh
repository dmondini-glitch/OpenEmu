#!/usr/bin/env bash
# Cross-builds a Windows distribution from macOS/Linux. Usage: scripts/build-windows.sh [x64|x86|arm64]
# Env: SKIP_CORES=1 to skip core downloads; INCLUDE_X86_CORES=1 (arm64 only) to also bundle x86 cores.
set -euo pipefail
ARCH="${1:-x64}"
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/.." && pwd)"
OUT="$ROOT/dist/OpenEmu-Windows-$ARCH"; rm -rf "$OUT"
pub() { dotnet publish "$ROOT/$1" -c Release -r "$2" --self-contained "${4:-true}" -o "$3" -p:DebugType=none; }
echo "== app ($ARCH)"
pub src/OpenEmu.App/OpenEmu.App.csproj "win-$ARCH" "$OUT"
pub src/OpenEmu.Cli/OpenEmu.Cli.csproj "win-$ARCH" "$OUT/cli"
if [ "$ARCH" = "arm64" ]; then
  pub src/OpenEmu.CoreHost/OpenEmu.CoreHost.csproj win-x64 "$OUT/host/x64"
  pub src/OpenEmu.CoreHost/OpenEmu.CoreHost.csproj win-x86 "$OUT/host/x86"
  PLATFORMS="windows-x64"; [ "${INCLUDE_X86_CORES:-0}" = "1" ] && PLATFORMS="$PLATFORMS windows-x86"
else
  pub src/OpenEmu.CoreHost/OpenEmu.CoreHost.csproj "win-$ARCH" "$OUT/host/$ARCH"
  PLATFORMS="windows-$ARCH"
fi
if [ "${SKIP_CORES:-0}" != "1" ]; then for p in $PLATFORMS; do "$HERE/download-cores.sh" "$OUT/cores" "$p"; done; fi
cp "$ROOT/README.md" "$ROOT/docs/EMPACOTAMENTO-WINDOWS.md" "$OUT/"
(cd "$ROOT/dist" && rm -f "OpenEmu-Windows-$ARCH.zip" && zip -qr "OpenEmu-Windows-$ARCH.zip" "OpenEmu-Windows-$ARCH")
echo "Distribution ready: $OUT"
