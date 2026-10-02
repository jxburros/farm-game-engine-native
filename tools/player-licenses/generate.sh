#!/usr/bin/env bash
# Regenerates THIRD-PARTY.txt: the license notices for the Rust crates in farm-player, the fonts
# and the plugin sandbox guest it embeds, and farm-player's own MIT license.
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

# The plugin sandbox guest (crates/farm-plugins/guest/farm_plugin_guest.wasm), which farm-plugins
# embeds with include_bytes!. Its crate is outside the workspace and its C code is not a crate, so
# cargo-about doesn't see it. Keep these files in step with crates/farm-plugin-guest/Cargo.lock
# (rquickjs-sys = quickjs-ng) and the wasi-sdk version in tools/plugin-guest/build.sh.
guest_notice() {
  printf '\n%s\n' "--------------------------------------------------------------------------------"
  printf '%s\n\n' "$1"
  printf '%s\n' "Used by:"
  printf '%s\n\n' "- $2"
  cat "$here/plugin-guest/$3"
}
{
  guest_notice "MIT License (MIT)" "quickjs-ng 0.16.2 (QuickJS), compiled into the plugin sandbox" quickjs-ng.txt
  guest_notice "MIT License (MIT)" "rquickjs-sys 0.14.0, compiled into the plugin sandbox" rquickjs.txt
  guest_notice "MIT License (MIT)" "wasi-libc (wasi-sdk 24.0), linked into the plugin sandbox" wasi-libc.txt
  guest_notice "MIT License (MIT)" "musl, through wasi-libc" musl.txt
  guest_notice "BSD 2-Clause \"Simplified\" License (BSD-2-Clause)" "cloudlibc, through wasi-libc" cloudlibc.txt
} >> "$generated"

# The player itself: Farming RPG Maker's own crates (the workspace license, ../../LICENSE).
{
  printf '\n%s\n' "--------------------------------------------------------------------------------"
  printf '%s\n\n' "MIT License (MIT)"
  printf '%s\n' "Used by:"
  printf '%s\n\n' "- farm-player and the Farming RPG Maker engine crates"
  cat "$root/LICENSE"
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
