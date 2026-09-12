#!/usr/bin/env bash
# Downloads every libretro core from cores.json for one platform into <outdir>/<platform>/.
# Usage: scripts/download-cores.sh [outdir] [platform]   platform: windows-x64 | windows-x86 | osx-arm64 | osx-x64
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/../dist/cores}"; PLATFORM="${2:-windows-x64}"
DIR="$OUT/$PLATFORM"; mkdir -p "$DIR"
case "$PLATFORM" in windows*) EXT=dll;; *) EXT=dylib;; esac
python3 - "$HERE/../src/OpenEmu.Core/Resources/cores.json" "$PLATFORM" <<'PY' | while read -r id url; do
import json,sys
m=json.load(open(sys.argv[1]))
for c in m['cores']:
    if c['id']=='2048': continue
    u=c['download'].get(sys.argv[2])
    if u: print(c['id'], u)
PY
  target="$DIR/${id}_libretro.$EXT"
  if [ -f "$target" ]; then echo "  $id (cached)"; continue; fi
  echo "  $id"
  if curl -fsSL -o "$target.zip" "$url"; then
    unzip -qo "$target.zip" -d "$DIR" && rm -f "$target.zip"
    date -u +%Y-%m-%dT%H:%M:%SZ > "$target.version"
  else
    echo "  !! $id failed" >&2; rm -f "$target.zip"
  fi
done
echo "[$PLATFORM] $(ls "$DIR"/*_libretro.$EXT 2>/dev/null | wc -l | tr -d ' ') cores in $DIR"
