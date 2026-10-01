#!/usr/bin/env bash
# Stages one player template folder for Export Game (docs/EXPORT.md "Player templates"):
#
#   <out>/<target>/farm-player[.exe]   the player built for <target>
#                                      (web: farm_wasm_bg.wasm, farm_wasm.js from tools/wasm/build.sh,
#                                      and index.html, style.css, game.js from tools/wasm/web-template)
#   <out>/<target>/template.json       { "target", "version", "sha256" }
#   <out>/<target>/THIRD-PARTY.txt     tools/player-licenses/THIRD-PARTY.txt
#
# The editor refuses a template whose version differs from its own, so <version> must be the
# version the app is published with. Development builds get the same layout from the
# FarmPlayerTemplates target in src/FarmEngine.Export/FarmEngine.Export.fsproj.
#
# Usage: tools/player-templates/package.sh <windows-x64|linux-x64> <version> <player executable> <out dir>
#        tools/player-templates/package.sh web <version> <tools/wasm/dist folder> <out dir>
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "Usage: $0 <windows-x64|linux-x64|web> <version> <player executable, or the wasm dist folder for web> <out dir>" >&2
  exit 2
fi
target="$1"
version="$2"
player="$3"
out="$4"

case "$target" in
  windows-x64) name="farm-player.exe" ;;
  linux-x64) name="farm-player" ;;
  web) name="farm_wasm_bg.wasm" ;;
  *) echo "Unknown target $target (windows-x64, linux-x64 or web)." >&2; exit 2 ;;
esac

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
folder="$out/$target"

rm -rf "$folder"
mkdir -p "$folder"
if [[ "$target" == "web" ]]; then
  cp "$player/farm_wasm_bg.wasm" "$player/farm_wasm.js" "$folder/"
  cp "$root/tools/wasm/web-template/index.html" "$root/tools/wasm/web-template/style.css" "$root/tools/wasm/web-template/game.js" "$folder/"
else
  cp "$player" "$folder/$name"
  chmod 755 "$folder/$name"
fi
cp "$root/tools/player-licenses/THIRD-PARTY.txt" "$folder/THIRD-PARTY.txt"
sha="$(sha256sum "$folder/$name" | cut -d' ' -f1)"
printf '{ "target": "%s", "version": "%s", "sha256": "%s" }\n' "$target" "$version" "$sha" > "$folder/template.json"
echo "Staged $folder:"
ls -l "$folder"
cat "$folder/template.json"
