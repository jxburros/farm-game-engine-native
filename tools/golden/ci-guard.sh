#!/usr/bin/env bash
# CI guardrails for the golden fixtures (docs/NUMERICS.md "Goldens").
#
#   tools/golden/ci-guard.sh switches   fails when a record or bless switch is set
#                                       (FARM_RECORD_*, *_BLESS): CI checks goldens, it never
#                                       re-records them
#   tools/golden/ci-guard.sh fixtures   fails when the tests left fixtures/ different from the
#                                       commit (a test that wrote a golden, or a new file)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

case "${1:-}" in
  switches)
    set_switches="$(env | cut -d= -f1 | grep -E '^FARM_RECORD_|_BLESS$' || true)"
    if [[ -n "$set_switches" ]]; then
      echo "Record/bless switches are set in CI: $(echo "$set_switches" | tr '\n' ' ')" >&2
      echo "Re-record goldens locally, review the diff and commit it (docs/NUMERICS.md \"Goldens\")." >&2
      exit 1
    fi
    echo "No record or bless switch is set."
    ;;
  fixtures)
    changes="$(git -C "$root" status --porcelain --untracked-files=all -- fixtures)"
    if [[ -n "$changes" ]]; then
      echo "The tests changed fixtures/:" >&2
      echo "$changes" >&2
      git -C "$root" --no-pager diff --stat -- fixtures >&2 || true
      exit 1
    fi
    echo "fixtures/ is unchanged."
    ;;
  *)
    echo "usage: $0 switches|fixtures" >&2
    exit 2
    ;;
esac
