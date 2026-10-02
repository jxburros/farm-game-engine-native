#!/usr/bin/env bash
# Export smoke test against a folder of player templates (docs/EXPORT.md):
#
#   tools/release/smoke-export.sh TARGET TEMPLATES OUT.json
#
# Exports fixtures/projects/project-v8.json for TARGET (windows-x64, linux-x64) with farmc,
# taking the template from TEMPLATES/TARGET (farmc --templates), checks that the exported
# cartridge is the golden one, runs the exported game headless through the twenty-tick
# replay, and compares its report with the template's own run on the golden cartridge. The
# report goes to OUT.json, so reports from different platforms can be compared too.
#
# farmc must be built (Release) with the templates' version: the editor refuses templates of
# another version. The release workflow runs this on the release templates before publishing.
set -euo pipefail

target="${1:?usage: smoke-export.sh TARGET TEMPLATES OUT.json}"
templates="$(cd "${2:?usage: smoke-export.sh TARGET TEMPLATES OUT.json}" && pwd)"
out="${3:?usage: smoke-export.sh TARGET TEMPLATES OUT.json}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
exe=""
case "$target" in
  windows-x64) exe=".exe" ;;
  linux-x64) ;;
  *)
    echo "smoke-export.sh: TARGET is windows-x64 or linux-x64" >&2
    exit 2
    ;;
esac

work="$root/target/smoke-export-$target"
rm -rf "$work"
mkdir -p "$work"
trap 'rm -rf "$work"' EXIT
cart="$root/fixtures/golden/cartridges/project-v8.cart"
replay="$root/fixtures/golden/cartridges/twenty-ticks.json"

dotnet run --project "$root/src/FarmEngine.Cli" -c Release --no-build -- \
  export "$root/fixtures/projects/project-v8.json" --target "$target" --templates "$templates" --out "$work/export"
game="$(find "$work/export/$target" -mindepth 1 -maxdepth 1 -type d -print -quit)"
name="$(basename "$game")"
ls -l "$game" "$game/licenses"
test -x "$game/$name$exe"
cmp "$game/game.cart" "$cart"

"$game/$name$exe" --headless --replay "$replay" | tr -d '\r' >"$out"
"$templates/$target/farm-player$exe" --headless --cart "$cart" --replay "$replay" | tr -d '\r' >"$work/reference.json"
cat "$out"
if ! diff "$out" "$work/reference.json"; then
  echo "The exported $target game and its template report different runs." >&2
  exit 1
fi
