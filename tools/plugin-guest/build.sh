#!/usr/bin/env bash
# Builds the plugin sandbox guest (QuickJS compiled to WebAssembly) and copies it to
# crates/farm-plugins/guest/farm_plugin_guest.wasm, which is checked in. Run it only when
# crates/farm-plugin-guest changes; building the engine never needs it.
#
# Inputs (all pinned):
#   - Rust from rust-toolchain.toml, target wasm32-wasip1 (added here if missing)
#   - crates/farm-plugin-guest with its Cargo.lock (rquickjs-sys 0.14.0 = quickjs-ng 0.16.2)
#   - wasi-sdk 24.0 (clang + wasi-libc sysroot for the QuickJS C sources), downloaded and
#     checked against its sha256; set WASI_SDK=/path/to/wasi-sdk-24.0 to use your own copy
#     (required on hosts other than x86_64 Linux)
#   - wasm-opt, if it is on PATH (optional; the recorded build did not use it)
#   - tools/plugin-guest/meter with its Cargo.lock, which adds the fuel metering the host's
#     budgets count (see its src/main.rs)
#
# Afterwards, update the sha256 in crates/farm-plugins/guest/README.md (the script prints it);
# the farm-plugins tests fail until it matches.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
guest="$root/crates/farm-plugin-guest"
out="$root/crates/farm-plugins/guest/farm_plugin_guest.wasm"
cache="${FARM_PLUGIN_GUEST_CACHE:-$root/target/plugin-guest}"

sdk_version=24.0
sdk_sha256_x86_64_linux=c6c38aab56e5de88adf6c1ebc9c3ae8da72f88ec2b656fb024eda8d4167a0bc5

if [[ -z "${WASI_SDK:-}" ]]; then
  if [[ "$(uname -s)-$(uname -m)" != "Linux-x86_64" ]]; then
    echo "Set WASI_SDK to a wasi-sdk $sdk_version install on this host." >&2
    exit 1
  fi
  mkdir -p "$cache"
  archive="$cache/wasi-sdk-$sdk_version-x86_64-linux.tar.gz"
  if [[ ! -f "$archive" ]]; then
    curl -sSfL -o "$archive.part" \
      "https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-${sdk_version%%.*}/wasi-sdk-$sdk_version-x86_64-linux.tar.gz"
    mv "$archive.part" "$archive"
  fi
  echo "$sdk_sha256_x86_64_linux  $archive" | sha256sum --check --quiet
  export WASI_SDK="$cache/wasi-sdk-$sdk_version"
  if [[ ! -x "$WASI_SDK/bin/clang" ]]; then
    mkdir -p "$WASI_SDK"
    tar -xzf "$archive" -C "$WASI_SDK" --strip-components 1
  fi
fi

rustup target add wasm32-wasip1 >/dev/null
# rquickjs-sys compiles QuickJS with $WASI_SDK/bin/clang against its sysroot; the Rust side
# links with Rust's own wasm32-wasip1 libc. The guest has its own target dir and lock file.
(cd "$guest" && cargo build --release --locked --target wasm32-wasip1)

built="$guest/target/wasm32-wasip1/release/farm_plugin_guest.wasm"
optimized="$guest/target/wasm32-wasip1/release/farm_plugin_guest.opt.wasm"
if command -v wasm-opt >/dev/null 2>&1; then
  wasm-opt -Oz --strip-debug "$built" -o "$optimized"
else
  cp "$built" "$optimized"
fi
# Metering goes last, so that no optimizer moves code across the charges.
cargo run --quiet --release --locked --manifest-path "$root/tools/plugin-guest/meter/Cargo.toml" -- "$optimized" "$out"

echo "wrote $out ($(wc -c <"$out") bytes)"
echo "sha256: $(sha256sum "$out" | cut -d' ' -f1)"
