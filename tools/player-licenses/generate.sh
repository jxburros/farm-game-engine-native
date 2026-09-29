#!/usr/bin/env bash
# Regenerates THIRD-PARTY.txt: the license notices for the Rust crates in farm-player.
# Every player template ships this file, and Export Game copies it to
# licenses/THIRD-PARTY.txt in each exported game (docs/EXPORT.md). The editor ships it too, as
# licenses/THIRD-PARTY-rust.txt: farm-ffi is built from crates inside farm-player's graph.
#
# Needs cargo-about (cargo install cargo-about --locked --features cli).
# Run it after changing farm-player's dependencies and commit the result.
#
#   tools/player-licenses/generate.sh           # rewrite THIRD-PARTY.txt
#   tools/player-licenses/generate.sh --check   # fail if THIRD-PARTY.txt is out of date (CI)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
output="$here/THIRD-PARTY.txt"

if ! command -v cargo-about >/dev/null 2>&1; then
  echo "cargo-about is not installed: cargo install cargo-about --locked --features cli" >&2
  exit 1
fi

generated="$(mktemp)"
trap 'rm -f "$generated"' EXIT

cargo about generate \
  --locked \
  --fail \
  --manifest-path "$root/crates/farm-player/Cargo.toml" \
  --config "$here/about.toml" \
  --output-file "$generated" \
  "$here/about.hbs"

# Fonts embedded by farm-render (not crates, so cargo-about doesn't see them).
{
  printf '%s\n' "--------------------------------------------------------------------------------"
  printf '%s\n\n' "SIL Open Font License 1.1 (OFL-1.1)"
  printf '%s\n' "Used by:"
  printf '%s\n\n' "- Inter 3.19 (Regular, Bold), embedded by farm-render"
  cat "$root/assets/fonts/OFL.txt"
  printf '\n%s\n' "--------------------------------------------------------------------------------"
  printf '%s\n\n' "SIL Open Font License 1.1 (OFL-1.1)"
  printf '%s\n' "Used by:"
  printf '%s\n\n' "- Atkinson Hyperlegible (Regular, Bold), embedded by farm-render (the Readable font setting)"
  cat "$root/assets/fonts/OFL-AtkinsonHyperlegible.txt"
} >> "$generated"

# Stable line endings and no trailing spaces, whatever the crates packaged.
sed -e 's/\r$//' -e 's/[[:space:]]*$//' "$generated" | cat -s > "$generated.clean"
mv "$generated.clean" "$generated"

if [[ "${1:-}" == "--check" ]]; then
  if ! diff -u "$output" "$generated"; then
    echo "tools/player-licenses/THIRD-PARTY.txt is out of date; run tools/player-licenses/generate.sh" >&2
    exit 1
  fi
  echo "THIRD-PARTY.txt is up to date."
else
  cp "$generated" "$output"
  echo "Wrote $output"
fi
