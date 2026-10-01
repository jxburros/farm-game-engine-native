#!/usr/bin/env bash
# Regenerates the v8 golden fixtures (fixtures/golden/v8) from the TypeScript reference engine
# (jxburros/farm-game-engine) and compares them with the checked-in ones.
#
# Since schema v9 the Rust engine records its own goldens (fixtures/golden/SOURCE.txt,
# docs/NUMERICS.md "Goldens"). The TypeScript goldens are frozen migration inputs under
# fixtures/golden/v8; this script exists to reproduce them, not to change them.
#
# Usage:
#   tools/golden/generate.sh [--write] [path-to-farm-game-engine-checkout]
#
# Without a path, the reference repo is cloned (or updated) into tools/golden/.work/ts-ref and
# checked out at $REF, which defaults to the commit fixtures/golden/SOURCE.txt records. With a
# path, that checkout is used as-is (its current HEAD).
#
# The generator always writes into tools/golden/.work/out (wiped first), never into fixtures/.
# The script then compares the v8 subtrees (replays, content, saves, rng.json, hash.json) with
# fixtures/golden/v8 and exits 1 when they differ. Only with --write does it copy them over the
# checked-in ones, and only outside CI, from a clean fixtures/ tree and at the pinned commit.
# v8/outcomes, the v9 goldens and everything else in fixtures/ are never touched.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NATIVE_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
V8="$NATIVE_ROOT/fixtures/golden/v8"
OUT="$SCRIPT_DIR/.work/out"
GENERATOR="$SCRIPT_DIR/golden.gen.test.ts"
REPO_URL="${REPO_URL:-https://github.com/jxburros/farm-game-engine}"
# The commit the checked-in v8 goldens were generated from (fixtures/golden/SOURCE.txt).
PINNED_REF="01b2d8f0a789b9cde5355823a1ae36feaf1babe3"
REF="${REF:-$PINNED_REF}"
# What the generator writes that fixtures/golden/v8 keeps. (It also writes migrations/, which
# the F# migration goldens replaced; that stays in .work/out.)
SUBTREES=(replays content saves rng.json hash.json)

usage() {
  echo "usage: $0 [--write] [path-to-farm-game-engine-checkout]" >&2
  exit 2
}

WRITE=0
CHECKOUT_ARG=""
for arg in "$@"; do
  case "$arg" in
    --write) WRITE=1 ;;
    -*) usage ;;
    *)
      [[ -z "$CHECKOUT_ARG" ]] || usage
      CHECKOUT_ARG="$arg"
      ;;
  esac
done

if [[ "$WRITE" == 1 ]]; then
  if [[ -n "${CI:-}" || -n "${GITHUB_ACTIONS:-}" ]]; then
    echo "error: --write is refused in CI; the v8 goldens change only in a reviewed commit" >&2
    exit 1
  fi
  if [[ -n "$(git -C "$NATIVE_ROOT" status --porcelain -- fixtures)" ]]; then
    echo "error: fixtures/ has uncommitted changes; commit or discard them before --write" >&2
    exit 1
  fi
  if [[ -n "$CHECKOUT_ARG" || "$REF" != "$PINNED_REF" ]]; then
    echo "error: --write only from the pinned commit $PINNED_REF (no checkout path, no REF);" >&2
    echo "       moving the v8 goldens to another TypeScript commit means editing PINNED_REF and" >&2
    echo "       fixtures/golden/SOURCE.txt in the same reviewed change" >&2
    exit 1
  fi
fi

if [[ -n "$CHECKOUT_ARG" ]]; then
  CHECKOUT="$(cd "$CHECKOUT_ARG" && pwd)"
  SOURCE_KIND="local checkout (as-is)"
else
  CHECKOUT="$SCRIPT_DIR/.work/ts-ref"
  if [[ ! -d "$CHECKOUT/.git" ]]; then
    echo "==> cloning $REPO_URL into $CHECKOUT"
    mkdir -p "$(dirname "$CHECKOUT")"
    git clone "$REPO_URL" "$CHECKOUT"
  fi
  echo "==> checking out $REF"
  git -C "$CHECKOUT" fetch origin "$REF"
  git -C "$CHECKOUT" checkout --detach FETCH_HEAD
  SOURCE_KIND="clone of $REPO_URL at REF=$REF"
fi

if [[ ! -f "$CHECKOUT/tests/fixtures/project-v1.json" || ! -f "$CHECKOUT/vitest.config.ts" ]]; then
  echo "error: $CHECKOUT does not look like a farm-game-engine checkout" >&2
  exit 1
fi

if [[ ! -d "$CHECKOUT/node_modules" ]]; then
  # --ignore-scripts: no dependency's install script runs on this machine; vitest needs none.
  echo "==> npm ci --ignore-scripts (node_modules missing)"
  (cd "$CHECKOUT" && npm ci --ignore-scripts)
fi

COMMIT="unknown"
DIRTY="unknown"
if git -C "$CHECKOUT" rev-parse --git-dir >/dev/null 2>&1; then
  COMMIT="$(git -C "$CHECKOUT" rev-parse HEAD)"
  if [[ -n "$(git -C "$CHECKOUT" status --porcelain --untracked-files=no)" ]]; then
    DIRTY="yes (tracked files modified)"
  else
    DIRTY="no"
  fi
fi

TARGET="$CHECKOUT/tests/unit/golden.gen.test.ts"
cleanup() { rm -f "$TARGET"; }
trap cleanup EXIT
cp "$GENERATOR" "$TARGET"

echo "==> generating into $OUT"
rm -rf "$OUT"
mkdir -p "$OUT"
(cd "$CHECKOUT" && GOLDEN_OUT="$OUT" npx vitest run tests/unit/golden.gen.test.ts)

echo "==> source: $SOURCE_KIND, commit $COMMIT, dirty: $DIRTY"
echo "==> node $(node --version), generator sha256 $(sha256sum "$GENERATOR" | cut -d' ' -f1)"

differences=0
for item in "${SUBTREES[@]}"; do
  if ! diff -r -q "$V8/$item" "$OUT/$item" >/dev/null 2>&1; then
    differences=1
    echo "differs: v8/$item"
    diff -r -q "$V8/$item" "$OUT/$item" 2>&1 | sed 's/^/  /' | head -n 20 || true
  fi
done

if [[ "$differences" == 0 ]]; then
  echo "==> the regenerated goldens equal fixtures/golden/v8"
  exit 0
fi

if [[ "$WRITE" == 0 ]]; then
  echo "==> the regenerated goldens differ from fixtures/golden/v8 (left in $OUT; nothing written)" >&2
  exit 1
fi

for item in "${SUBTREES[@]}"; do
  rm -rf "${V8:?}/$item"
  cp -R "$OUT/$item" "$V8/$item"
done
echo "==> wrote ${SUBTREES[*]} into fixtures/golden/v8; review the diff:"
git -C "$NATIVE_ROOT" diff --stat -- fixtures/golden/v8
