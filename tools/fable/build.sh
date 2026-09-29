#!/usr/bin/env bash
# Compiles the F# authoring core to JavaScript with Fable (docs/LANGUAGES.md "Phase 7"), into
# tools/fable/dist. The JS entry point is dist/WebApi.js. Needs the .NET SDK; Fable comes from the
# local tool manifest (dotnet-tools.json).
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
dotnet tool restore >/dev/null
rm -rf tools/fable/dist
dotnet fable src/FarmEngine.Authoring/FarmEngine.Authoring.fsproj -o tools/fable/dist --noCache
