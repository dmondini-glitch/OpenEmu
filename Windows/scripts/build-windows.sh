#!/usr/bin/env bash
# Cross-builds the Windows x64 distribution from macOS/Linux (cores are downloaded for Windows).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/.." && pwd)"
OUT="$ROOT/dist/OpenEmu-Windows-x64"
dotnet publish "$ROOT/src/OpenEmu.App/OpenEmu.App.csproj" -c Release -r win-x64 --self-contained true -o "$OUT"
dotnet publish "$ROOT/src/OpenEmu.Cli/OpenEmu.Cli.csproj" -c Release -r win-x64 --self-contained false -o "$OUT/cli"
[ "${SKIP_CORES:-0}" = "1" ] || "$HERE/download-cores.sh" "$OUT/cores" windows-x64
cp "$ROOT/README.md" "$OUT/"
(cd "$ROOT/dist" && rm -f OpenEmu-Windows-x64.zip && zip -qr OpenEmu-Windows-x64.zip OpenEmu-Windows-x64)
echo "Distribution ready: $OUT"
