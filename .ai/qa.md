# QA profile: Farming RPG Maker (native)

Machine-readable levels are in [qa.yaml](qa.yaml); this page is for people and
for QA agents deciding what a level means here. Every command is one CI already
runs ([.github/workflows/ci.yml](../.github/workflows/ci.yml));
[CONTRIBUTING.md](../CONTRIBUTING.md) explains them.

## What this repository is

A native desktop app, not a web app: a Rust game engine and player
(`crates/`), an F# authoring core and Export Game (`src/FarmEngine.*`), and a
C# Avalonia editor (`src/FarmingRpgMaker.App`). There is no dev server, no
`package.json` and no npm dependency tree. Node 22 only runs test scripts.

## Prerequisites

- .NET 10 SDK (`global.json`), Rust 1.94.1 (`rust-toolchain.toml`), Node 22,
  Python 3.
- Linux: `libasound2-dev libudev-dev pkg-config` to build;
  `libxkbcommon-x11-0` to run the window; `xvfb` for the player's window test
  (skipped without it).
- Optional tools; their levels are skipped, not failed, when they are absent:
  cargo-about 0.9.2 (player license check), cargo-llvm-cov (coverage),
  cargo-deny and cargo-machete (security), wasm-opt (smaller wasm only).

## Levels

| Level | Commands (summary) | Passes when |
|---|---|---|
| install | `dotnet restore`, `dotnet tool restore` | restore succeeds |
| build | `dotnet build -c Release`, `cargo build --workspace` | no warnings (they are errors) |
| lint | `cargo fmt --check`, clippy `-D warnings`, `dotnet format whitespace/style --verify-no-changes`, `tools/ci/text-style.py`, markdownlint | no output from the checks |
| unit | `cargo test --workspace`, `dotnet test -c Release` | all pass, and `tools/golden/ci-guard.sh fixtures` finds `fixtures/` unchanged |
| generated | `tools/codegen/check.sh`, `tools/release/versions.sh`, the license `--check`s | committed generated files match their generators |
| smoke | headless `farm-player` replay of `project-v8.cart`; `tools/wasm` build + Node smoke tests; `tools/fable` build + smoke | exit 0 |
| export-smoke (Linux) | `farmc export` of `fixtures/projects/project-v8.json` for linux-x64, run the exported game headless | its report equals the template's own run |
| performance (Linux) | `farm-bench --budget --factor 3` | every benchmark within 3x its budget |
| coverage | `cargo llvm-cov`, `tools/coverage/check.py` | above `tools/coverage/thresholds.json` |
| security | cargo-deny advisories and sources, cargo-machete, `tools/ci/dotnet-vulnerable.py` | no findings |

Windows-only checks (PE patching of the real template, the GUI subsystem of
exported games, the VC++ runtime check) run in CI's Windows job; a Linux QA
run reports them as skipped.

## Levels that don't apply

Report these as **skipped**, not blocked:

- **Browser, dev server, Lighthouse:** no hosted web app. The web demo page is
  exercised in Node by `tools/wasm/game-page.mjs` (smoke level).
- **npm audit:** no npm dependency tree.
- **Mobile (Android/iOS):** none; the web demo's touch controls are covered by
  `tools/wasm/smoke.mjs`.
- **AI provider checks:** the app calls no AI service and needs no keys.
- **Desktop UI automation:** the editor's UI tests run headless inside
  `dotnet test` (Avalonia.Headless).

## Rules for QA runs

- Never set `FARM_RECORD_*` or `*_BLESS` switches and never run
  `tools/golden/generate.sh`; a golden diff is a finding, not something to
  re-record.
- `dotnet build` builds the Rust library and player template too; the first
  build takes several minutes and several GB of disk.

## Risky areas

- Golden fixtures and determinism (`fixtures/golden`, `docs/NUMERICS.md`).
- The plugin sandbox: budgets, permissions and hostile packs
  (`crates/farm-plugins`, `docs/PLUGINS.md`).
- Importing projects, packs, images and saves from other people (F# migrations,
  `crates/farm-cart`).
- Export Game: templates, archives, PE patching (`docs/EXPORT.md`).
- The Update Center (Velopack and GitHub Releases, `docs/RELEASING.md`).
- F#/Rust schema parity: the two project pipelines (`docs/LANGUAGES.md`).
