#!/usr/bin/env bash
# Regenerate the golden parity fixtures for the C# port from the TypeScript
# reference engine (jxburros/farm-game-engine).
#
# Usage:
#   tools/golden/generate.sh [path-to-farm-game-engine-checkout]
#
# Without an argument the reference repo is cloned (or updated) into
# tools/golden/.work/ts-ref and checked out at $REF (default: main).
# With an argument that checkout is used as-is (its current HEAD).
#
# Output: tests/FarmEngine.Core.Tests/Golden/ (wiped and rewritten), plus
# Golden/SOURCE.txt recording the exact source commit.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NATIVE_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUT="$NATIVE_ROOT/tests/FarmEngine.Core.Tests/Golden"
GENERATOR="$SCRIPT_DIR/golden.gen.test.ts"
REPO_URL="${REPO_URL:-https://github.com/jxburros/farm-game-engine}"
REF="${REF:-main}"

if [[ $# -gt 1 ]]; then
  echo "usage: $0 [path-to-farm-game-engine-checkout]" >&2
  exit 2
fi

if [[ $# -eq 1 ]]; then
  CHECKOUT="$(cd "$1" && pwd)"
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
  echo "==> npm ci (node_modules missing)"
  (cd "$CHECKOUT" && npm ci)
fi

# Record provenance before we drop our test file into the tree.
COMMIT="unknown"
DESCRIBE="unknown"
DIRTY="unknown"
ORIGIN="unknown"
if git -C "$CHECKOUT" rev-parse --git-dir >/dev/null 2>&1; then
  COMMIT="$(git -C "$CHECKOUT" rev-parse HEAD)"
  DESCRIBE="$(git -C "$CHECKOUT" describe --always --tags 2>/dev/null || echo "$COMMIT")"
  ORIGIN="$(git -C "$CHECKOUT" remote get-url origin 2>/dev/null || echo none)"
  if [[ -n "$(git -C "$CHECKOUT" status --porcelain --untracked-files=no)" ]]; then
    DIRTY="yes (tracked files modified — fixtures may not match $COMMIT)"
  else
    DIRTY="no"
  fi
fi

TARGET="$CHECKOUT/tests/unit/golden.gen.test.ts"
cleanup() { rm -f "$TARGET"; }
trap cleanup EXIT
cp "$GENERATOR" "$TARGET"

echo "==> wiping previous fixtures in $OUT"
mkdir -p "$OUT"
rm -rf "$OUT/migrations" "$OUT/saves" "$OUT/replays" "$OUT/content" \
       "$OUT/rng.json" "$OUT/hash.json" "$OUT/SOURCE.txt"

echo "==> running generator (vitest)"
(cd "$CHECKOUT" && GOLDEN_OUT="$OUT" npx vitest run tests/unit/golden.gen.test.ts)

{
  echo "Golden fixtures generated from the TypeScript reference engine."
  echo "Do not hand-edit; rerun tools/golden/generate.sh instead."
  echo
  echo "source:    $SOURCE_KIND"
  echo "origin:    $ORIGIN"
  echo "commit:    $COMMIT"
  echo "describe:  $DESCRIBE"
  echo "dirty:     $DIRTY"
  echo "node:      $(node --version)"
  echo "vitest:    $(cd "$CHECKOUT" && npx vitest --version 2>/dev/null | head -n1)"
  echo "generator: tools/golden/golden.gen.test.ts sha256 $(sha256sum "$GENERATOR" | cut -d' ' -f1)"
} > "$OUT/SOURCE.txt"

FILES="$(find "$OUT" -type f | wc -l | tr -d ' ')"
SIZE="$(du -sh "$OUT" | cut -f1)"
echo "==> done: $FILES files, $SIZE in $OUT (source commit $COMMIT)"
