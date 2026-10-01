#!/usr/bin/env bash
# Checks that the committed generated sources match their generators (CI runs it):
#
#   - crates/farm-cart-schema/src/{cart,save}_generated.rs: flatc 25.2.10 (schemas/README.md)
#     over schemas/*.fbs
#   - src/FarmEngine.Authoring.Net/RecordJson.fs: tools/codegen/record-json.py
#   - src/FarmEngine.Authoring.Net/RecordWith.fs: tools/codegen/record-with.fsx, which reads
#     the Release build of src/FarmEngine.Authoring (built here when it is missing)
#
# flatc: $FLATC, else a flatc 25.2.10 on PATH, else (x86_64 Linux) the release binary,
# downloaded once into target/codegen and checked against its sha256.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cache="$root/target/codegen"
flatc_version=25.2.10
flatc_zip_sha256=6f01258d7475806f375d6da66a61df47add8016edd73f1774673f37b80b9a711
failed=0

find_flatc() {
  if [[ -n "${FLATC:-}" ]]; then
    echo "$FLATC"
  elif command -v flatc >/dev/null 2>&1 && [[ "$(flatc --version)" == "flatc version $flatc_version" ]]; then
    command -v flatc
  elif [[ "$(uname -s)-$(uname -m)" == "Linux-x86_64" ]]; then
    if [[ ! -x "$cache/flatc-$flatc_version/flatc" ]]; then
      mkdir -p "$cache/flatc-$flatc_version"
      zip="$cache/flatc-$flatc_version.zip"
      curl -sSfL -o "$zip" \
        "https://github.com/google/flatbuffers/releases/download/v$flatc_version/Linux.flatc.binary.g++-13.zip"
      echo "$flatc_zip_sha256  $zip" | sha256sum --check --quiet
      python3 -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' \
        "$zip" "$cache/flatc-$flatc_version"
      chmod +x "$cache/flatc-$flatc_version/flatc"
    fi
    echo "$cache/flatc-$flatc_version/flatc"
  else
    echo "No flatc $flatc_version: set FLATC to one." >&2
    return 1
  fi
}

flatc="$(find_flatc)"
version="$("$flatc" --version)"
if [[ "$version" != "flatc version $flatc_version" ]]; then
  echo "$flatc is '$version', the accessors are generated with flatc $flatc_version" >&2
  exit 1
fi
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
for schema in cart save; do
  "$flatc" --rust -o "$scratch" "$root/schemas/$schema.fbs"
  if diff -u "$root/crates/farm-cart-schema/src/${schema}_generated.rs" "$scratch/${schema}_generated.rs" >"$scratch/$schema.diff"; then
    echo "crates/farm-cart-schema/src/${schema}_generated.rs is current"
  else
    echo "crates/farm-cart-schema/src/${schema}_generated.rs is out of date: regenerate it (schemas/README.md)" >&2
    head -n 40 "$scratch/$schema.diff" >&2
    failed=1
  fi
done

python3 "$root/tools/codegen/record-json.py" --check || failed=1

if [[ ! -f "$root/src/FarmEngine.Authoring/bin/Release/net10.0/FarmEngine.Authoring.dll" ]]; then
  dotnet build -c Release "$root/src/FarmEngine.Authoring" >/dev/null
fi
dotnet fsi "$root/tools/codegen/record-with.fsx" --check || failed=1

exit "$failed"
