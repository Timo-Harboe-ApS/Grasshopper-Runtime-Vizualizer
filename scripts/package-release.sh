#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/src/GrasshopperRuntimeVisualizer/bin/Release/net8.0"
YAK_BIN="${YAK_BIN:-}"

if [[ -z "$YAK_BIN" ]]; then
  for candidate in \
    "/Applications/Rhino 8.app/Contents/Resources/bin/yak" \
    "/Applications/RhinoWIP.app/Contents/Resources/bin/yak" \
    "/Applications/RhinoBETA.app/Contents/Resources/bin/yak"
  do
    if [[ -x "$candidate" ]]; then
      YAK_BIN="$candidate"
      break
    fi
  done
fi

if [[ -z "$YAK_BIN" ]]; then
  echo "Could not find yak. Set YAK_BIN to the Rhino yak executable path." >&2
  exit 1
fi

dotnet build "$ROOT/GrasshopperRuntimeVisualizer.sln" -c Release
cd "$OUT"
"$YAK_BIN" build
