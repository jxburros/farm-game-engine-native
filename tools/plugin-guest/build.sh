#!/usr/bin/env bash
# Builds the plugin sandbox guest (QuickJS compiled to WebAssembly) and copies it to
# crates/farm-plugins/guest/farm_plugin_guest.wasm, which is checked in. Run it only when
# crates/farm-plugin-guest or tools/plugin-guest/meter changes; building the engine never needs it.
#
#   tools/plugin-guest/build.sh            build and replace the checked-in guest
#   tools/plugin-guest/build.sh --check    build into the guest's target dir and compare with the
#                                          checked-in guest byte for byte (CI); exits 1 if they
#                                          differ
#   tools/plugin-guest/build.sh --lint     only check the two crates: cargo fmt --check, clippy
#                                          with -D warnings, and the meter's unit tests (CI)
#   --wasm-opt                             also run `wasm-opt -Oz` (needs wasm-opt on PATH). The
#                                          recorded build does not use it.
#
# Inputs (all pinned):
#   - Rust from rust-toolchain.toml, target wasm32-wasip1 (added here if missing)
#   - crates/farm-plugin-guest with its Cargo.lock (rquickjs-sys 0.14.0 = quickjs-ng 0.16.2)
#   - wasi-sdk 24.0 (clang + wasi-libc sysroot for the QuickJS C sources), downloaded and
#     checked against its sha256; set WASI_SDK=/path/to/wasi-sdk-24.0 to use your own copy
#     (required on hosts other than x86_64 Linux)
#   - tools/plugin-guest/meter with its Cargo.lock, which adds the fuel metering the host's
#     budgets count (see its src/main.rs)
#
# cargo runs with a cleared environment (PATH, HOME, the cargo and rustup homes, proxy settings
# and WASI_SDK only): RUSTFLAGS would replace the linker flags in .cargo/config.toml (the
# stack-first layout the sandbox relies on), and CFLAGS/CC* would change how the cc crate
# compiles QuickJS. Source paths are mapped to relative ones, so the bytes do not depend on
# where the repository is checked out.
#
# Afterwards, update the sha256 and the inputs hash in crates/farm-plugins/guest/README.md (the
# script prints both); the farm-plugins tests fail until they match.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
guest="$root/crates/farm-plugin-guest"
meter="$root/tools/plugin-guest/meter"
checked_in="$root/crates/farm-plugins/guest/farm_plugin_guest.wasm"
cache="${FARM_PLUGIN_GUEST_CACHE:-$root/target/plugin-guest}"

mode=build
wasm_opt=0
for arg in "$@"; do
  case "$arg" in
    --check) mode=check ;;
    --lint) mode=lint ;;
    --wasm-opt) wasm_opt=1 ;;
    *)
      echo "usage: $0 [--check | --lint] [--wasm-opt]" >&2
      exit 2
      ;;
  esac
done

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
  WASI_SDK="$cache/wasi-sdk-$sdk_version"
  if [[ ! -x "$WASI_SDK/bin/clang" ]]; then
    mkdir -p "$WASI_SDK"
    tar -xzf "$archive" -C "$WASI_SDK" --strip-components 1
  fi
fi

# The only environment cargo sees. rquickjs-sys compiles QuickJS with $WASI_SDK/bin/clang
# against its sysroot; the Rust side links with Rust's own wasm32-wasip1 libc.
# -ffile-prefix-map turns the absolute paths that QuickJS's assert() messages carry (__FILE__ of
# the sources rquickjs-sys copies into its OUT_DIR) into paths relative to the guest crate.
clean_env=(
  "PATH=$PATH"
  "HOME=$HOME"
  "WASI_SDK=$WASI_SDK"
  "CFLAGS_wasm32_wasip1=-ffile-prefix-map=$guest/="
)
for name in CARGO_HOME RUSTUP_HOME RUSTUP_TOOLCHAIN HTTPS_PROXY https_proxy HTTP_PROXY http_proxy NO_PROXY no_proxy \
  SSL_CERT_FILE SSL_CERT_DIR CARGO_HTTP_CAINFO CARGO_NET_GIT_FETCH_WITH_CLI CARGO_TERM_COLOR; do
  if [[ -n "${!name:-}" ]]; then
    clean_env+=("$name=${!name}")
  fi
done
cargo_clean() {
  env -i "${clean_env[@]}" cargo "$@"
}

if [[ "$mode" == lint ]]; then
  (cd "$guest" && cargo_clean fmt --check)
  (cd "$meter" && cargo_clean fmt --check)
  rustup target add wasm32-wasip1 >/dev/null
  (cd "$guest" && cargo_clean clippy --release --locked --target wasm32-wasip1 -- -D warnings)
  (cd "$meter" && cargo_clean clippy --locked --all-targets -- -D warnings)
  (cd "$meter" && cargo_clean test --locked)
  echo "plugin guest and meter: fmt, clippy and tests pass"
  exit 0
fi

rustup target add wasm32-wasip1 >/dev/null
# The guest has its own target dir and lock file.
(cd "$guest" && cargo_clean build --release --locked --target wasm32-wasip1)

built="$guest/target/wasm32-wasip1/release/farm_plugin_guest.wasm"
optimized="$guest/target/wasm32-wasip1/release/farm_plugin_guest.opt.wasm"
if [[ "$wasm_opt" == 1 ]]; then
  wasm-opt -Oz --strip-debug "$built" -o "$optimized"
else
  cp "$built" "$optimized"
fi

out="$checked_in"
if [[ "$mode" == check ]]; then
  out="$guest/target/wasm32-wasip1/release/farm_plugin_guest.metered.wasm"
fi
# Metering goes last, so that no optimizer moves code across the charges.
(cd "$meter" && cargo_clean run --quiet --release --locked -- "$optimized" "$out")

# The hash of the inputs, as crates/farm-plugins/tests/guest_wasm.rs computes it: for every file
# of the guest crate and the meter (without target/) and this script, sorted by path, the path,
# a tab and the sha256 of the contents with carriage returns removed; then the sha256 of those
# lines.
inputs_sha256() {
  (
    cd "$root"
    {
      find crates/farm-plugin-guest tools/plugin-guest/meter -type d -name target -prune -o -type f -print
      echo tools/plugin-guest/build.sh
    } | LC_ALL=C sort | while IFS= read -r file; do
      printf '%s\t%s\n' "$file" "$(tr -d '\r' <"$file" | sha256sum | cut -d' ' -f1)"
    done | sha256sum | cut -d' ' -f1
  )
}

echo "wrote $out ($(wc -c <"$out") bytes)"
echo "sha256: $(sha256sum "$out" | cut -d' ' -f1)"
echo "inputs sha256: $(inputs_sha256)"

if [[ "$mode" == check ]]; then
  if cmp -s "$out" "$checked_in"; then
    echo "The rebuilt guest equals the checked-in crates/farm-plugins/guest/farm_plugin_guest.wasm."
  else
    echo "The rebuilt guest differs from the checked-in one: rebuild it with tools/plugin-guest/build.sh" >&2
    echo "and record its sha256 in crates/farm-plugins/guest/README.md." >&2
    exit 1
  fi
fi
