#!/usr/bin/env bash
# Checks that the version numbers in the repository agree (docs/RELEASING.md "Versions").
#
#   tools/release/versions.sh                  development: the Cargo workspace version and the
#                                              Directory.Build.props default are the same
#                                              X.Y.Z-dev, and CHANGELOG.md starts with
#                                              "## X.Y.Z (unreleased)"; in both forms,
#                                              VPK_VERSION in release.yml is the Velopack
#                                              package version
#   tools/release/versions.sh --release X.Y.Z  a release commit: both are X.Y.Z, and for a stable
#                                              X.Y.Z, CHANGELOG.md has a "## X.Y.Z" section that
#                                              is not marked unreleased
#
# CI runs the first form; the release workflow runs the second before it builds anything, so
# crash logs, fe_version and the wasm version() report the version that shipped.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# version = "..." in the root Cargo.toml's [workspace.package] table.
cargo_version="$(awk '
  /^\[/ { section = $0 }
  section == "[workspace.package]" && /^version *=/ { gsub(/.*= *"|".*/, ""); print; exit }
' "$root/Cargo.toml")"
# <Version Condition="...">X</Version> in Directory.Build.props.
props_version="$(sed -n 's:.*<Version Condition="[^"]*">\([^<]*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n1)"
changelog_heading="$(grep -m1 '^## ' "$root/CHANGELOG.md" || true)"

fail() {
  echo "versions.sh: $*" >&2
  exit 1
}

[[ -n "$cargo_version" ]] || fail "no version in Cargo.toml [workspace.package]"
[[ -n "$props_version" ]] || fail "no default <Version> in Directory.Build.props"
[[ "$cargo_version" == "$props_version" ]] ||
  fail "Cargo.toml says $cargo_version, Directory.Build.props says $props_version"

# The vpk tool that packs a release must be the Velopack version the app ships.
velopack="$(sed -n 's:.*<PackageVersion Include="Velopack" Version="\([^"]*\)".*:\1:p' "$root/Directory.Packages.props")"
vpk="$(sed -n 's/^ *VPK_VERSION: *\([^ #]*\).*/\1/p' "$root/.github/workflows/release.yml")"
[[ -n "$velopack" && "$velopack" == "$vpk" ]] ||
  fail "Velopack is '$velopack' in Directory.Packages.props but VPK_VERSION is '$vpk' in release.yml"

case "${1:-}" in
  "")
    [[ "$cargo_version" == *-dev ]] ||
      fail "development builds use X.Y.Z-dev, Cargo.toml says $cargo_version (bump it after a release)"
    next="${cargo_version%-dev}"
    [[ "$changelog_heading" == "## $next (unreleased)" ]] ||
      fail "CHANGELOG.md should start with \"## $next (unreleased)\", not \"$changelog_heading\""
    echo "versions: $cargo_version (CHANGELOG: $next unreleased)"
    ;;
  --release)
    release="${2:?usage: versions.sh --release X.Y.Z}"
    [[ "$cargo_version" == "$release" ]] ||
      fail "releasing $release, but Cargo.toml and Directory.Build.props say $cargo_version; commit the version bump first"
    if [[ "$release" != *-* ]]; then
      escaped="${release//./\\.}"
      heading="$(grep -m1 -E "^## \\[?v?$escaped\\]?([[:space:]]|$)" "$root/CHANGELOG.md" || true)"
      [[ -n "$heading" ]] || fail "CHANGELOG.md has no \"## $release\" section"
      [[ "$heading" != *unreleased* ]] || fail "CHANGELOG.md still marks $release unreleased: \"$heading\""
    fi
    echo "versions: releasing $release"
    ;;
  *)
    fail "usage: versions.sh [--release X.Y.Z]"
    ;;
esac
