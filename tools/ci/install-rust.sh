#!/usr/bin/env bash
# Installs rustup from a pinned, checksummed rustup-init (no `curl | sh`), then the toolchain
# rust-toolchain.toml names. For CI containers without Rust (the Steam Runtime SDK); hosted
# runners already have rustup.
#
#   tools/ci/install-rust.sh     (x86_64 Linux; adds ~/.cargo/bin to $GITHUB_PATH when set)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
rustup_version=1.29.1
rustup_init_sha256=dda7234360b7f578ca8b0ddcb80145646fa61a67c1720a5abc7051b35c9fcb71

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
curl --proto '=https' --tlsv1.2 -sSfL -o "$scratch/rustup-init" \
  "https://static.rust-lang.org/rustup/archive/$rustup_version/x86_64-unknown-linux-gnu/rustup-init"
echo "$rustup_init_sha256  $scratch/rustup-init" | sha256sum --check --quiet
chmod +x "$scratch/rustup-init"
"$scratch/rustup-init" -y --no-modify-path --profile minimal --default-toolchain none

cargo_bin="${CARGO_HOME:-$HOME/.cargo}/bin"
if [[ -n "${GITHUB_PATH:-}" ]]; then
  echo "$cargo_bin" >> "$GITHUB_PATH"
fi
# Without arguments, rustup installs the toolchain of rust-toolchain.toml (version, profile,
# components, targets).
(cd "$root" && "$cargo_bin/rustup" toolchain install && "$cargo_bin/rustc" --version)
