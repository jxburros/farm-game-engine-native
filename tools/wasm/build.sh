#!/usr/bin/env bash
# Builds crates/farm-wasm for web pages: `wasm-bindgen --target web` output (farm_wasm.js,
# farm_wasm_bg.wasm and TypeScript declarations) in tools/wasm/dist/ (gitignored), or in the
# folder given as the first argument. See crates/farm-wasm/README.md.
#
#   tools/wasm/build.sh            # build, bind, optimize when wasm-opt is on PATH
#   tools/wasm/build.sh out/dir    # somewhere else
#   node tools/wasm/smoke.mjs      # then smoke-test the output
#
# Needs the wasm32-unknown-unknown target (rust-toolchain.toml adds it). Installs the
# wasm-bindgen CLI whose version matches the wasm-bindgen crate in Cargo.lock when it is missing
# or different (the two must match exactly). wasm-opt (binaryen) is optional: set
# FARM_WASM_OPT=0 to skip it even when installed.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="${1:-$here/dist}"
target_dir="${CARGO_TARGET_DIR:-$root/target}"

# The version of the wasm-bindgen crate in Cargo.lock.
version="$(awk '/^name = "wasm-bindgen"$/ { getline; gsub(/"/, "", $3); print $3; exit }' "$root/Cargo.lock")"
if [[ -z "$version" ]]; then
  echo "wasm-bindgen is not in Cargo.lock" >&2
  exit 1
fi
if [[ "$(wasm-bindgen --version 2>/dev/null | awk '{print $2}')" != "$version" ]]; then
  echo "Installing wasm-bindgen-cli $version" >&2
  cargo install wasm-bindgen-cli --version "$version" --locked
fi

cargo build --locked --manifest-path "$root/Cargo.toml" -p farm-wasm --target wasm32-unknown-unknown --release
wasm="$target_dir/wasm32-unknown-unknown/release/farm_wasm.wasm"

rm -rf "$out"
mkdir -p "$out"
# wasm-bindgen drops the DWARF debug info the release profile keeps (debug = 1).
wasm-bindgen --target web --out-dir "$out" "$wasm"
bound="$out/farm_wasm_bg.wasm"

size() { wc -c < "$1" | tr -d ' '; }
echo "farm_wasm_bg.wasm: $(size "$bound") bytes after wasm-bindgen"

# binaryen before 116 writes an externref table the browsers and Node refuse to load.
wasm_opt_usable() {
  command -v wasm-opt >/dev/null 2>&1 || return 1
  local version
  version=$(wasm-opt --version | sed -n 's/.*version \([0-9][0-9]*\).*/\1/p')
  [[ -n "$version" && "$version" -ge 116 ]]
}

if [[ "${FARM_WASM_OPT:-1}" != "0" ]] && wasm_opt_usable; then
  # The features rustc enables by default for wasm32-unknown-unknown.
  wasm-opt -O3 \
    --enable-bulk-memory --enable-mutable-globals --enable-nontrapping-float-to-int \
    --enable-sign-ext --enable-reference-types --enable-multivalue \
    "$bound" -o "$bound.opt"
  mv "$bound.opt" "$bound"
  echo "farm_wasm_bg.wasm: $(size "$bound") bytes after wasm-opt -O3"
else
  echo "wasm-opt 116 or newer not found (or FARM_WASM_OPT=0): skipped"
fi
echo "gzip -9: $(gzip -9 -c "$bound" | wc -c | tr -d ' ') bytes"
echo "Wrote $out"
