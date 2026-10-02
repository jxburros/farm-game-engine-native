# Contributing

Thanks for helping with Farming RPG Maker. This page collects what you need
to build, test and change the repository: the prerequisites, the checks CI
runs, and how to regenerate every checked-in file that a tool produces.

The architecture is in [docs/LANGUAGES.md](docs/LANGUAGES.md) (Rust / F# / C#
split), [docs/NUMERICS.md](docs/NUMERICS.md) (integer numerics and
goldens) and [docs/EDITOR-ARCHITECTURE.md](docs/EDITOR-ARCHITECTURE.md) (the
editor's views and shared helpers); [ROADMAP.md](ROADMAP.md) has what is
planned.

## Prerequisites

- The [.NET 10 SDK](https://dotnet.microsoft.com/download) (`global.json`
  pins the version).
- A [Rust toolchain](https://rustup.rs); `rust-toolchain.toml` pins the
  version, components and targets, and rustup installs them on first use.
- Node 22, only for the WebAssembly and Fable smoke tests and markdownlint.
- On Linux, the system packages the player links and its tests use:

  ```sh
  sudo apt-get install libasound2-dev libudev-dev pkg-config   # build: ALSA (cpal), libudev (gilrs)
  sudo apt-get install libxkbcommon-x11-0 xvfb                 # run the window; xvfb for the window test
  ```

  Without `xvfb`, `crates/farm-player/tests/binary.rs` skips its window test.

Windows needs the MSVC build tools for Rust (the Visual Studio "Desktop
development with C++" workload). macOS is untested: CI has no macOS job, and
Export Game has no macOS host template yet.

## Build and test

```sh
dotnet build -c Release            # also builds crates/farm-ffi and the player templates
dotnet test -c Release --no-build
cargo test --workspace             # Rust engine tests, including the golden replays
dotnet run --project src/FarmingRpgMaker.App
```

Before you push, run what CI runs on your change:

```sh
cargo fmt --all --check
cargo clippy --workspace --all-targets -- -D warnings
dotnet format whitespace FarmingRpgMaker.sln --verify-no-changes
dotnet format style FarmingRpgMaker.sln --verify-no-changes
python3 tools/ci/text-style.py
npx --yes markdownlint-cli2@0.23.3
```

The full list of CI jobs is in [.github/workflows/ci.yml](.github/workflows/ci.yml);
[.ai/qa.md](.ai/qa.md) describes the same checks for automated QA runs.

## Formatting

- **Rust:** `cargo fmt` (`rustfmt.toml`, 120 columns).
- **C#:** `.editorconfig` (file-scoped namespaces, `System` usings first,
  sorted usings). The build enforces the rules set to `warning`
  (`EnforceCodeStyleInBuild`), and CI runs `dotnet format whitespace` and
  `dotnet format style` in check mode; run them without `--verify-no-changes`
  to fix a file. Lines should stay within 160 columns (not enforced yet).
- **F#:** no formatter is enforced (Fantomas would rewrite almost every
  file); follow the style of the file you edit. CI checks the basics for F#
  and every other text file: UTF-8, LF line endings, a final newline, no
  trailing whitespace and no tab indentation (`tools/ci/text-style.py`, with
  `--fix`).
- **Markdown:** markdownlint with `.markdownlint-cli2.jsonc`.
- `.gitattributes` keeps text files LF on every platform, so the bash scripts
  the Windows release jobs run keep working.

## Updating test fixtures

Tests compare against checked-in goldens. After an **intended** change, set
the switch to `1`, run the test once, run it again without the switch, and
review the diff before you commit it. The Rust recorders rewrite only the
files that change and then fail on purpose, so a recording run never passes
as a test run. Put re-recordings in their own commit. CI refuses to run
with any `FARM_RECORD_*` or `*_BLESS` switch set and fails when the tests
leave `fixtures/` changed (`tools/golden/ci-guard.sh`).

| Switch | Rewrites | Run |
|---|---|---|
| `FARM_RECORD_GOLDENS=1` | `fixtures/golden/{replays,content,saves}`, `rng.json`, `hash.json` (and stamps `SOURCE.txt`) | `cargo test -p farm-sim --test golden_replays` (`golden_content`, `golden_primitives`), `cargo test -p farm-cart --test golden_saves` |
| `FARM_RENDER_BLESS=1` | `fixtures/render/*.png` (renderer goldens) | `cargo test -p farm-render --test golden` |
| `FARM_PLAYER_BLESS=1` | `fixtures/player/*.png` (game screens) | `cargo test -p farm-player --test screenshots` |
| `FARM_RECORD_CARTRIDGES=1` | `fixtures/golden/cartridges/*.cart` | `dotnet test tests/FarmEngine.Authoring.Tests --filter "FullyQualifiedName~CartridgeTests\|FullyQualifiedName~ValidationTests"` |
| `FARM_RECORD_SNAPSHOTS=1` | `fixtures/authoring/snapshots.json` | `dotnet test tests/FarmEngine.Authoring.Tests --filter FullyQualifiedName~SnapshotTests` |
| `FARM_RECORD_CONTENT_LINTS=1` | `fixtures/projects/content-lints.json` | `dotnet test tests/FarmEngine.Authoring.Tests --filter FullyQualifiedName~ValidationCaseTests` |

Never re-record `fixtures/golden/v8/`: those are the frozen TypeScript
goldens and the v8 outcomes, kept as migration inputs
([fixtures/golden/SOURCE.txt](fixtures/golden/SOURCE.txt),
[docs/NUMERICS.md](docs/NUMERICS.md#goldens)). Do not run
`tools/golden/generate.sh` either; it regenerates them from the TypeScript
engine.

Switches that only write images for a look, never compared:

| Variable | Writes |
|---|---|
| `FARM_PLAYER_SCREENSHOTS=<dir>` | full-size game screens (`cargo test -p farm-player --test screenshots`; `-- --ignored review_screens` for every screen) |
| `FARM_WASM_SCREENSHOTS=<dir>` | the web demo's frames (`node tools/wasm/smoke.mjs`) |
| `FRM_SCREENSHOT_DIR=<dir>` | the editor screenshots in `docs/media` (`ScreenshotTests`, `GameScreenshotTests` in the app tests) |

## Regenerating checked-in files

Some files are generated by a tool and checked in, so that building never
needs that tool. CI checks that each matches its generator.

| What | Regenerate | When | CI check |
|---|---|---|---|
| FlatBuffers accessors (`crates/farm-cart-schema/src/*_generated.rs`) | `flatc --rust` 25.2.10 ([schemas/README.md](schemas/README.md)) | a `.fbs` changes | `tools/codegen/check.sh` |
| `src/FarmEngine.Authoring.Net/RecordJson.fs` tables | `python3 tools/codegen/record-json.py` | `SchemaJson.fs` changes | `tools/codegen/check.sh` |
| `src/FarmEngine.Authoring.Net/RecordWith.fs` | `dotnet build -c Release src/FarmEngine.Authoring && dotnet fsi tools/codegen/record-with.fsx` | a schema record changes | `tools/codegen/check.sh` |
| Player license notices (`tools/player-licenses/THIRD-PARTY.txt`) | `tools/player-licenses/generate.sh` (needs cargo-about 0.9.2) | `farm-player`'s dependencies, the fonts or the plugin guest change | `generate.sh --check` |
| Editor license notices (`tools/editor-licenses/THIRD-PARTY-dotnet.txt`) | `dotnet restore && node tools/editor-licenses/generate.mjs` | a NuGet package changes | `generate.mjs --check` |
| Plugin sandbox guest (`crates/farm-plugins/guest/farm_plugin_guest.wasm`) | `tools/plugin-guest/build.sh`, then update the hashes in [its README](crates/farm-plugins/guest/README.md) | `crates/farm-plugin-guest` or the meter changes | `build.sh --check` |
| Built-in art (`assets/builtin-art/`) | `python3 tools/art/build_art.py` (Blender 4.5 as a module, Pillow, numpy; [tools/art](tools/art/README.md)) | the art changes | — |
| PE test fixture (`tests/FarmEngine.Export.Tests/Fixtures/pe/player-fixture.exe`) | `tests/FarmEngine.Export.Tests/Fixtures/pe/build.sh` (mingw-w64) | the template's placeholder resources change | — |
| Editor screenshots (`docs/media/*.png`) | `FRM_SCREENSHOT_DIR="$PWD/docs/media" dotnet test tests/FarmingRpgMaker.App.Tests --filter "FullyQualifiedName~Screenshot"`, then keep only the images the docs use | the editor's look changes | — |

Not checked in, built on demand: the Fable JavaScript of the authoring core
(`tools/fable/build.sh`, checked by `node tools/fable/smoke.mjs`), the
`farm-wasm` package (`tools/wasm/build.sh`, checked by
`node tools/wasm/smoke.mjs` and `node tools/wasm/game-page.mjs`), and the
player templates (`dotnet build`, or `tools/player-templates/package.sh` in
the release).

## Changes and commits

- Keep commits focused, with a short imperative subject and a body that says
  why.
- Add a line to the `## X.Y.Z (unreleased)` section of
  [CHANGELOG.md](CHANGELOG.md) for anything a creator or player would
  notice.
- Versions, tags and the release workflow are in
  [docs/RELEASING.md](docs/RELEASING.md).
- Report security problems privately ([SECURITY.md](SECURITY.md)).

By contributing you agree that your contribution is licensed under the
[MIT License](LICENSE).
