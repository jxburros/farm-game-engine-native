# Language plan: Rust, F# and C#

**Status:** proposed, not started (September 2026). This is the reference for
where code goes as the native app grows. Read it before you port a new part of
the web editor. [PORTING.md](PORTING.md) still covers how C# code mirrors the
TypeScript today. This document covers where each piece ends up.

## The rule in one line

**Rust runs the game. F# understands the project. C# is the desktop app.**

| Language | Owns | Never owns |
|---|---|---|
| **Rust** | Everything that runs while a game is being played: simulation, saves, state hashing, RNG, fixed timestep, input bindings, minigame kinds, in-game UI, rendering (draw lists and the GPU backend), audio playback, the plugin sandbox, the standalone player, the WebAssembly build. | Project JSON, editor state, anything a creator edits directly. |
| **F#** | Everything about authored data: the project schema, migrations, validation and the Problems panel, content packs (merge and namespacing), default content and templates, workshop patterns, edit operations and undo/redo, the condition and dialogue languages, and the compiler that turns a project into a cartridge. Plus tools: the balancing lab and the `farmc` CLI. | Gameplay rules, rendering, OS or UI code. |
| **C#** | The Avalonia app: windows, views and view models, dialogs and file pickers, the project store on disk, image import, the Update Center, and hosting the Rust player and preview. | Game rules, project rules. A view model calls F# or Rust; it doesn't decide anything itself. |

The contract between F# and Rust is the **cartridge**: a compiled, validated,
binary form of a project. F# writes it and Rust reads it. JSON stays the
interchange format with the web version and the debug format. It is no longer
what the game runs on.

### If you're unsure where something goes

1. **Does it change what happens in a playthrough?** (a rule, a number, a random
   roll, a timer) → **Rust**, in `farm-sim`.
2. **Does it read or change the project a creator is editing?** (a field, a
   default, a check, an import) → **F#**, in `FarmEngine.Authoring`.
3. **Is it pixels on screen during play, or sound?** → **Rust**
   (`farm-render`, `farm-ui`, `farm-player`).
4. **Is it an editor window, form, file dialog, menu or OS integration?** →
   **C#**.
5. **Is it a check that authored data is valid?** → F#. **Is it a check that a
   command is allowed right now in the game?** → Rust.

## Why this split

The reasoning comes from what a farming game needs (see the discussion that
led here):

- **The overnight pass dominates.** Sleeping advances every tile, crop,
  animal, machine and schedule at once. Today `GameTime.PerformSleep` clones
  every scene's `List<List<Tile>>` of records with string fields each night.
  Rust with flat per-layer arrays (`Vec<u16>` crop kinds, a `BitVec` for
  watered) turns that into tight loops over contiguous memory.
- **Long saves need exact repeatability.** Hundreds of in-game days compound
  any numeric drift. Rust gives integer and fixed-point math with lints that
  enforce it. It compiles to native code and to WebAssembly from the same
  source, which removes the two-engine problem (a TypeScript engine plus a C#
  port kept equal by golden tests).
- **The editor is mostly data transformation.** Migrations, validation,
  pack merging and compiling content are compiler work. F# is built for that:
  discriminated unions, exhaustive pattern matching, immutable collections
  with structural sharing (cheap undo), and units of measure (`<gold>`,
  `<day>`, `<energy>`, `<tile>`) that stop timer and price mix-ups at compile
  time.
- **The desktop app already works in C#.** Avalonia, Velopack, the project
  store and the UI tests stay. F# and C# share one .NET solution at no cost.

## Target architecture

```
                       ┌──────────────────────────── .NET (one solution) ─────────────────────────────┐
 project.json ───────▶ │ FarmEngine.Authoring (F#)            FarmingRpgMaker.App (C#, Avalonia)      │
 (v8, web-compatible)  │  schema · migrations · validation      views · view models · dialogs         │
                       │  packs · edits + undo · languages ◀──── calls F# for every project change    │
                       │  compiler ──▶ cartridge bytes          hosts Rust player / preview surfaces   │
                       │                     │                  Skia executor for edit-mode draw lists│
                       │                     │              FarmEngine.Interop (C#): generated P/Invoke│
                       └─────────────────────┼───────────────────────────────▲────────────────────────┘
                                             │  C ABI (farm-ffi, coarse)     │ commands in, effects/views out
                       ┌─────────────────────▼───────────────────────────────┴────────────────────────┐
                       │ Rust workspace                                                                │
                       │  farm-cart (cartridge + save formats) ─▶ farm-sim (deterministic core)       │
                       │  farm-runtime (timestep, input, minigames, panels, audio cues)               │
                       │  farm-plugins (wasmtime + QuickJS sandbox)                                   │
                       │  farm-render (draw lists; wgpu backend) · farm-ui (HUD, dialogue, shop…)     │
                       │  farm-player (standalone game exe; also embedded in the editor's Play Mode)  │
                       │  farm-wasm (same core + player for the web version and web demo exports)     │
                       └──────────────────────────────────────────────────────────────────────────────┘
```

One player, three places: the editor's **Play Mode**, **exported Windows and
Linux games**, and the optional **web demo export** all run `farm-player`.
What a creator tests is exactly what ships. [EXPORT.md](EXPORT.md) covers
what an exported game contains.

## Contracts between languages

### Cartridge (F# → Rust)

- **Format:** [FlatBuffers](https://flatbuffers.dev), schemas in `schemas/cart.fbs`.
  It reads without copying (fast loads), generates code for Rust, C# (usable from
  F#) and TypeScript, and has clear rules for evolving a schema: fields are
  only added, never renumbered.
- **Game info:** a `GameInfo` table (title, version, `gameId`, author,
  window defaults, pixel scale) from the export settings, so the standalone
  player never reads project JSON. See [EXPORT.md](EXPORT.md).
- **Contents:** content tables with string ids interned to dense integer
  indices (`u16`/`u32`), a string table (ids, names, dialogue text, per
  locale), compiled condition bytecode, compiled dialogue graphs, scene tile
  layers as flat arrays, embedded images and audio (PNG/OGG as authored; Rust
  packs texture atlases at load), and pack and plugin manifests with plugin
  sources.
- **Version header:** `cart_format` (bumped on breaking change) plus the
  project schema version it was compiled from.
- **Compatibility phase:** until the v9 cutover (phase 7), the cartridge keeps
  today's `GameContent` shape and `double` values, so golden hashes still
  match the TypeScript engine.

### Saves (Rust only)

- The player's machine persists saves, so **Rust owns the save format and
  save migrations** (from `Save.cs` and `SaveMigrations.cs`). **F# owns
  project migrations** (from `Migrations.cs`). The creator persists projects.
- The format is FlatBuffers too (`schemas/save.fbs`) with a version header,
  compressed with zstd.
- **Saves outlive the cartridge that wrote them.** A player's save from
  version 1.0 of a game has to load in 1.1. So saves refer to content by its
  stable string id, never by the cartridge's interned indices, and the header
  carries `gameId`, the game version and the cartridge hash. Ids that no
  longer exist go to quarantine instead of failing the load. See
  [EXPORT.md](EXPORT.md#what-earlier-phases-must-get-right). Rust can also export a save as stable JSON for
  debugging and for the web version.
- Autosave serializes on the simulation thread in microseconds, then
  compresses and writes on a worker thread.

### FFI: .NET → Rust (`farm-ffi`)

- A C ABI `cdylib`. C# bindings are generated with
  [csbindgen](https://github.com/Cysharp/csbindgen) into `FarmEngine.Interop`
  and wrapped in `SafeHandle` types. F# uses the same wrappers.
- **Coarse, handle-based, batched.** Never one call per tile or entity:
  ```c
  fe_session*  fe_session_new(const uint8_t* cart, size_t len, const char* seed);
  fe_result    fe_session_apply(fe_session*, const uint8_t* commands, size_t len); // batch
  fe_result    fe_session_tick(fe_session*, uint32_t ticks);
  fe_view      fe_session_view(fe_session*, fe_view_kind);  // snapshot / draw list / panel state
  fe_bytes     fe_session_save(fe_session*);
  fe_bytes     fe_session_query_json(fe_session*, const char* path); // debug drawer
  void         fe_bytes_free(fe_bytes);
  ```
  Commands go in and effects plus hook events come out, each as a FlatBuffers
  buffer. This is the command/effect pipeline the engine already has.
- **Memory:** Rust allocates result buffers; .NET copies what it needs and
  frees them through `fe_bytes_free`. No pointer into Rust memory outlives the
  next call on that session.
- **Threading:** a session is used by one thread at a time. The editor runs
  the simulation on a dedicated thread and marshals views to the UI thread.
- **Panics** are caught at the boundary (`catch_unwind`) and returned as
  error results, never unwound into .NET.
- **Synchronous hooks.** Plugin mutations are queued and come back as
  commands (the `PluginMutationQueue` design), so they cross the boundary
  cleanly. The exception is `onWeatherRoll`, whose listeners return an
  override *during* the nightly step (`GameTime.cs`). In Rust, synchronous
  hooks are answered by the Rust plugin host. Until that exists, `farm-ffi`
  exposes a callback so the interim C# Jint host can answer.

### WebAssembly (`farm-wasm`)

- `wasm-bindgen` API that mirrors `farm-ffi` (same buffers). It's used by the
  web version's playtest and by the optional web demo export. On the web, plugins keep
  running in Web Workers: the Rust core defines a `PluginHost` trait, and each
  host implements it.

## Determinism rules for Rust

These replace `BannedSymbols.txt`. They're enforced in `farm-sim` with
`clippy.toml` and crate attributes, and CI runs clippy with `-D warnings`:

- `#![forbid(unsafe_code)]` in `farm-sim`, `farm-cart`, `farm-runtime`.
- `disallowed-types`: `std::collections::HashMap` and `HashSet` (use `Vec`,
  `IndexMap`, `BTreeMap`), `std::time::Instant` and `SystemTime` (use the game
  clock), any `rand::rngs::ThreadRng` (use the seeded `Rng`).
- `disallowed-methods`: `slice::sort_unstable*` where order is observable,
  `f64::round` in the compatibility phase (use `js::round`).
- **Compatibility phase (phases 1–6):** port with JavaScript number
  semantics so the TypeScript goldens pass. That means `f64` everywhere,
  a `js` module (`round`, `trunc`, stable sort, `is_integer`) and
  [`ryu-js`](https://crates.io/crates/ryu-js) for JavaScript-identical number
  formatting in messages and stable JSON. The hash stays FNV-1a over stable
  JSON (from `Hash.cs`).
- **Native phase (phase 7 on):** `#![deny(clippy::float_arithmetic)]` in
  `farm-sim`. Every quantity gets an explicit integer type and scale, and the
  full table goes in `docs/NUMERICS.md` when phase 7 starts. Starting point:

  | Quantity | Type | Unit |
  |---|---|---|
  | Money | `i64` | whole gold |
  | Energy, stamina | `i32` | 1/1000 point |
  | Game time | `u32` | ticks (20/s); day minute as `u16` |
  | Crop growth | `u16` | 1/1000 of a stage |
  | Friendship | `i32` | points |
  | Player/NPC position | `i32` | 1/256 pixel |
  | Probabilities | `u32` | out of 2³² (compare against `Rng::next_u32`) |

  The state hash becomes xxh3-64 over the canonical binary state encoding.
  Rendering may use floats. It reads state and never writes it.

## Repository layout (target)

```
Cargo.toml                       # Rust workspace; rust-toolchain.toml pins the version
schemas/cart.fbs, save.fbs, messages.fbs
crates/
  farm-cart/       # FlatBuffers readers, interning, cartridge + save load, save migrations
  farm-sim/        # deterministic core           ← src/FarmEngine.Core
  farm-runtime/    # timestep, input, minigames, panel model, audio cues ← src/FarmEngine.Runtime
  farm-plugins/    # PluginHost trait; wasmtime + QuickJS host (native)  ← Runtime/Plugins.cs
  farm-render/     # draw-list builder; wgpu backend (feature)           ← src/FarmEngine.Rendering
  farm-ui/         # in-game UI (HUD, dialogue, shop, inventory, crafting, quests, panels)
  farm-player/     # standalone player (winit + wgpu + kira + gilrs) and game shell; embeddable
  farm-ffi/        # C ABI for .NET
  farm-wasm/       # wasm-bindgen API for the web
  farm-bench/      # criterion benchmarks
fixtures/golden/                 # shared by Rust and F# tests (moved from tests/…/Golden)
src/
  FarmEngine.Authoring/          # F#, Fable-safe core (see below)
  FarmEngine.Authoring.Net/      # F#, .NET-only: JSON I/O, files, cartridge writer
  FarmEngine.Lab/                # F#: balancing lab, farmc CLI
  FarmEngine.Interop/            # C#: generated bindings + SafeHandle wrappers; builds farm-ffi
  FarmingRpgMaker.Updates/       # C#, unchanged
  FarmingRpgMaker.App/           # C#, Avalonia
tests/
  FarmEngine.Authoring.Tests/    # F# (xUnit + FsCheck)
  FarmingRpgMaker.App.Tests/     # C#, unchanged role
```

`FarmEngine.Interop` has an MSBuild target that runs `cargo build` and copies
`farm_ffi.dll` / `.so` / `.dylib` into the output, so `dotnet build`,
`dotnet test` and `dotnet run` keep working as the only commands a contributor
needs (with a Rust toolchain installed).

### F# conventions

- **Fable-safe core.** `FarmEngine.Authoring` must compile with
  [Fable](https://fable.io) to JavaScript so the web editor can use the same
  migrations, validation and compiler (phase 7). That means no reflection, no
  `System.Text.Json` and no file I/O in that project. JSON goes through
  `Thoth.Json`-style explicit encoders, and .NET-only code lives in
  `FarmEngine.Authoring.Net`. The rule is cheap to keep from day one and
  expensive to add later.
- The compiler produces a pure `CartModel`. A small per-platform writer
  serializes it (FlatBuffers C# code on .NET, FlatBuffers TS code under
  Fable).
- **Units of measure** on authored quantities: `price: int<gold>`,
  `growthDays: int<day>`, `energyCost: int<energy>`, `x: int<tile>`. They are
  erased at compile time, so C# sees plain numbers.
- **Every project change is an `Edit`** (a discriminated union) applied by
  `Document.apply`. Undo/redo is a stack of immutable documents (structural
  sharing makes that cheap). Workshop patterns are functions that return a
  list of edits, applied as one undo step.
  ```fsharp
  type Edit =
    | PaintTiles of scene: SceneId * layer: Layer * cells: (int<tile> * int<tile>) list * brush: TileBrush
    | FloodFill  of scene: SceneId * layer: Layer * at: int<tile> * int<tile> * brush: TileBrush
    | SetCrop    of CropId * Crop
    | AddNpc     of Npc
    | Batch      of label: string * Edit list      // one undo step
  ```
- **C# friendliness at the boundary.** Types C# view models consume are
  records with `[<CLIMutable>]` only where binding needs it. Options are
  exposed as nullable through small helper modules; don't make C# match on
  F# unions.

### Rust conventions

- One TypeScript module → one Rust module, snake_case, same folder shape as
  today's C# mapping (`farming/crops.ts` → `farm_sim::farming::crops`). The
  golden-first workflow from PORTING.md still applies: port literally, and let
  the goldens decide.
- `farm-sim` has no I/O, no threads and no logging side effects. It is a
  reducer: `(state, content, command | ticks) → (state, effects, hook events)`.
- State is **mutable inside a step, immutable across steps.** Unlike the C#
  port, Rust updates state in place. Undo and replay use the command log.
  Snapshots for the debug drawer and playtest "keep changes" are explicit
  copies.

## What happens to the existing code

### `FarmEngine.Schemas` → split between F# and Rust

| File(s) | Goes to | Notes |
|---|---|---|
| `Project.cs`, `World.cs`, `Actors.cs`, `Content.cs`, `Crafting.cs`, `Economy.cs`, `Events.cs`, `Extensibility.cs`, `Fishing.cs`, `Animals.cs`, `Mining.cs`, `Nodes.cs`, `Quests.cs`, `Social.cs`, `Weather.cs`, `Graphics.cs`, `Interface.cs`, `Settings.cs`, `Primitives.cs`, `GameContent.cs` | F# `FarmEngine.Authoring.Schema` | The authoring model. Rust gets generated cartridge types instead. |
| `Migrations.cs` | F# `Authoring.Migrations` | Project v1→v8 (then v9). Migration goldens move to F# tests. |
| `SchemaValidation.cs` | F# `Authoring.Validation` | Merged with Core `Validation.cs` into one Problems pipeline with JSON paths. |
| `Packs.cs` (schemas) | F# `Authoring.Packs` | Manifests and permissions. |
| `Save.cs`, `SaveMigrations.cs` | Rust `farm-cart::save` | Rust owns saves. |
| `Json/Js.cs`, `Json/StableJson.cs`, `Json/JsonDefaults.cs` | Rust `farm-sim::js`, `farm-sim::hash` (compatibility phase); F# keeps a stable-JSON writer for export | `Js` goes away after phase 7. |

### `FarmEngine.Core` → Rust `farm-sim` (except three files)

| File | Goes to |
|---|---|
| `Engine.cs`, `EngineTypes.cs`, `Commands.cs`, `Effects.cs`, `Replay.cs`, `Rng.cs`, `Hash.cs`, `State.cs` (runtime half) | `farm-sim` root modules |
| `GameTime.cs`, `Weather.cs`, `Energy.cs`, `Tools.cs`, `Inventory.cs`, `Economy.cs`, `Crafting.cs`, `Gathering.cs`, `Skills.cs`, `Social.cs`, `Animals.cs`, `Fishing.cs`, `Mines.cs`, `DialogueSystem.cs`, `Events.cs`, `Extensibility.cs`, `Hooks.cs` | same-named `farm-sim` modules |
| `Farming/*`, `World/*`, `Npcs/*`, `Quests/*` | `farm_sim::{farming, world, npcs, quests}` |
| `Validation.cs` | **F#** `Authoring.Validation` (it checks the project, not play) |
| `Packs.cs` (merge and namespacing) | **F#** `Authoring.Packs` (merging happens at compile time) |
| `ContentBuiltin.cs` | **F#** `Authoring.Builtin`, compiled into every cartridge |
| `State.cs` `CreateBaseContentFromProject` / `CreateContentFromProject` | **F#** compiler |
| `State.cs` `ApplyStateToProject` | Rust exports the playtest diff as JSON; **F#** applies it as an `Edit.Batch` |

### `FarmEngine.Content` → F#

`DefaultContent.cs`, `Templates.cs` and `GameHelpers.cs` (legacy tile
migration) move to `FarmEngine.Authoring.Content`. Render interpolation
(`GameHelpers.MovementSpeed`) moves to `farm-render`.

### `FarmEngine.Runtime` → Rust `farm-runtime` / `farm-plugins`

| File | Goes to |
|---|---|
| `FixedTimestep.cs` | `farm-runtime::timestep` |
| `Input.cs` | `farm-runtime::input` (bindings → commands). Hosts pass raw key and pad events. |
| `Minigames.cs` | `farm-runtime::minigames` (logic); drawing in `farm-ui` |
| `GamePanels.cs` | `farm-runtime::panels` (model); drawing in `farm-ui` |
| `Audio.cs` | `farm-runtime::audio` (settings, cues, synthesized SFX presets); playback in `farm-player` with [kira](https://crates.io/crates/kira) |
| `Plugins.cs` (Jint) | `farm-plugins`: QuickJS compiled to WebAssembly, run in wasmtime with fuel (step) and memory limits. Jint stays as the interim host until then. This also covers the "out-of-process plugin host" roadmap item, since each plugin gets its own isolated wasm instance. |

### `FarmEngine.Rendering` → Rust draw lists, with a C# Skia executor

The renderer splits into **what to draw** (one implementation, Rust) and
**how to rasterize it** (per backend):

| File | Goes to |
|---|---|
| `WorldSnapshot.cs`, `ShellSnapshot.cs`, `Graphics.cs` (art binding resolution, animation frames), `Canvas2d.cs` (tile colors, constants), `CssColor.cs` | `farm-render::drawlist`: builds a draw list (layered, y-sorted sprite and tile quads with atlas regions and tints) from state or a preview |
| `SkiaWorldRenderer.cs`, `ImageStore.cs` | Play: `farm-render::wgpu`. Edit Mode: a thin C# `SkiaDrawListExecutor` that replays the same draw list with SkiaSharp, so Avalonia can draw selection and brush overlays on top. |

The wgpu backend adds the farming-specific visuals the Skia renderer can't
do cheaply: day/night lighting, season palette swaps (the same art in autumn
or snow), rain, snow and wind particles, and water shimmer on watered soil.

### `FarmingRpgMaker.App` (C#), what stays and what changes

| Area | Fate |
|---|---|
| `App.axaml*`, `Views/*`, `ViewModels/*`, `Mvvm/*`, `Controls/*`, `Markdown/*`, `Services/*`, `Hosting/*` | Stay C#. |
| `Projects/*` (`ProjectStore`, `AtomicFile`, `AppDataPaths`, `ProjectDialogs`, `ProjectCommandHandler`) | Stay C#. `ProjectWorkspace` holds an F# `Document` instead of a `GameProject`. |
| `Game/EditModeView.cs` | Stays C#. Edits go through F# `Document.apply`, and the map draws with the Skia draw-list executor fed by a Rust **preview session** (see below). |
| `Game/PlayModeView.cs`, `PlayOverlays.cs`, `PlaySession.cs`, `GameCanvas.cs`, `MinigameOverlay.cs`, `ToastHost.cs`, `SpriteImage.cs`, `Ui.cs`, `GameStyles.axaml` | **Interim:** keep, reading snapshots from Rust over FFI (phase 4). **Final:** replaced by `farm-player` embedded through Avalonia's `NativeControlHost` (phase 6). All Play Mode UI then lives in `farm-ui`. |
| `Game/DebugDrawer.cs` | Stays C#. It reads state through `fe_session_query_json` and sits *beside* the player, not over it: native child windows can't have Avalonia content drawn on top of them (the "airspace" limit). |
| `Game/RuntimeBridge.cs` | Replaced by `FarmEngine.Interop`. |

`FarmingRpgMaker.Updates` stays C# and unchanged. Velopack packaging adds
`farm_ffi.dll` (and later `farm-player.exe`).

### Tests

| Today | Target |
|---|---|
| `Golden/GoldenParityTests.cs`, replays, rng, hash, saves | Rust integration tests in `farm-sim` / `farm-cart` reading `fixtures/golden` |
| `Golden/migrations/*`, `Schemas/*`, `Fixtures/project-v*.json`, `Fixtures/migrated/*` | F# `FarmEngine.Authoring.Tests` |
| `Core/*`, `Runtime/*` characterization tests | Ported to Rust unit tests next to each module |
| `Content/*` | F# tests |
| `FarmingRpgMaker.App.Tests` | Stay C# (headless Avalonia) |
| `JsSemanticsTests.cs` | Rust `js` module tests; deleted at phase 7 |

New tests:

- A **differential fuzz test** during phases 1–4 runs the C# core and the Rust
  core on the same random command streams and compares hashes each step.
- **Property tests:** proptest (Rust) for "replay ⇒ same hash" and "save →
  load ⇒ same state". FsCheck (F#) for "migrate(vN) is valid vN+1" and
  "compile never throws on a project with no validation errors".
- **Benchmarks** (`farm-bench`, criterion), with budgets checked in CI once
  measured. Starting targets for a mid-range laptop:

  | Scenario | Target |
  |---|---|
  | Overnight pass, 64×64 farm, full crops | < 2 ms |
  | Overnight pass, 256×256 farm, full crops + 50 machines | < 30 ms |
  | Save (serialize on sim thread) | < 1 ms |
  | Cartridge load, sample games | < 50 ms |
  | Frame (draw list + wgpu), 1080p, integrated GPU | < 4 ms |

## What still needs to be built, by language

This maps the editor list in [ROADMAP.md](../ROADMAP.md) (web sources in
`src/components` and `src/lib` of the web repo) onto the three languages.
**F#** is logic and data, **C#** is the view, and **Rust** is anything that
runs in play or previews play.

| Roadmap item (web source) | F# | C# | Rust |
|---|---|---|---|
| **1. Tile painter** (`EditorPanel.tsx`, `GridTile.tsx`, `SceneManager.tsx`, `TransitionEditor.tsx`) | `Edit` cases for paint, rect, flood fill, copy/paste, scene add/resize/delete, transitions; universal undo/redo; transition validation | Tool palette, brush picker, scene list, transition forms, canvas input | Preview session renders the edited scene's draw list; tile-behavior queries (walkable, farmable) for hover info |
| **2. World content editors** (`NPCEditor`, `ItemEditor`, `CropEditor`, `QuestEditor`, `EventsEditor` + `event-forms`, `ShopEditor`, `RecipeEditor`, `NodeTypeEditor`, `WildlifeEditor`) | Edit cases, validation, **condition language** (parse, type-check, compile to bytecode) for events and quests, dialogue graph model and checks (unreachable nodes, dead ends) | One form per editor | Headless previews: **"where is this NPC at 2 pm on a rainy Tuesday"** (schedule + pathfinding), crop growth timeline, shop price over a season |
| **3. Project settings and calendar** (`ProjectSettingsEditor.tsx`) | Calendar model with `<day>` units; festival and season validation | Settings view | Calendar preview (weather odds per season) |
| **4. Art pipeline** (`AssetManager`, `ArtBindings`, `import-art.ts`, `validate-graphics.ts`) | Art binding model, clip/frame model, graphics limits validation | Image import with SkiaSharp (PNG/JPEG/WebP/GIF/BMP/SVG → PNG, size limits, sheet slicing), animation studio view | Atlas packing at cartridge load; clip playback in draw lists; live preview |
| **5. Creator workshop + interface panels** (`CreatorWorkshop`, `InterfaceEditor`, `creator-patterns.ts`) | Patterns as `Project → Edit list` (one undo step each); panel layout model | Workshop and panel editor views | `farm-ui` renders creator panels in play |
| **6. Mods and actions** (`ModsEditor`, `ActionsEditor`, `mod-registry.ts`, `validate-extensibility.ts`) | Pack install/enable/order, merge with conflict problems, permission model, action validation | Mod list, **permission review dialog**, action forms | `farm-plugins` sandbox; action execution in `farm-sim` |
| **7. Problems panel + debug drawer** (`ProblemsPanel`, `DebugDrawer`) | One problems pipeline: schema, content and compiler diagnostics with JSON paths and "go to" targets | Problems list with navigation; debug drawer view | `fe_session_query_json`, state inspector, RNG and clock controls for playtests |

Other web parts:

| Web source | Goes to |
|---|---|
| `ProjectManager.tsx`, `WelcomeDialog.tsx`, `projects.ts`, `asset-storage.ts`, `useLocalKV.ts` | C# (mostly done: `Projects/*`) |
| `GameView.tsx`, `DialogueBox`, `ShopDialog`, `CraftingDialog`, `PlayerInventory`, `QuestTracker`, `MinigameOverlay`, `GamePanels`, `TouchControls`, `useGameLoop.ts`, `packages/game-shell` | Rust `farm-ui` + `farm-player` (C# `PlayOverlays` in the meantime) |
| `export-html.ts`, `export-game.ts`, `export-assets.ts`, `zip.ts` | F# export orchestration (compile the cartridge, assemble the export folder, write the archive) + Rust player templates. The single-file HTML export is not ported. See [EXPORT.md](EXPORT.md). |
| `i18n.ts` | Editor UI strings: C# `.resx`. Game text: F# compiler string tables per locale, looked up in Rust. |
| `templates.ts`, `game-helpers.ts`, `crops.ts`, `quests.ts`, `tools.ts`, `creator-patterns.ts`, `event-vocabulary.ts`, `reserved-keys.ts` | F# |

Later items (native and web roadmaps):

| Item | Where |
|---|---|
| Export Game: Windows and Linux | Copy the prebuilt `farm-player` template for the target, rename it, set its icon and version info, and put `game.cart` next to it. The cartridge is never appended to the exe: that gets in the way of code signing and makes every Steam patch re-ship the runtime. See [EXPORT.md](EXPORT.md). |
| Export Game: web demo (optional) | The `farm-wasm` player template + `game.cart` + `index.html`, for itch.io pages. Comes after Windows and Linux. |
| Seed selection, fertilizer choice, richer animals, fishing and relationships, multi-tile buildings, roaming insects, real-time combat | `farm-sim` (+ `farm-ui` for player UI; F# for the authoring side) |
| Audio-file import | C# import; F# embeds in the cartridge; Rust plays with kira (seasonal music crossfades, weather ambience layers) |
| Zip/folder content packs with binary assets | F# pack loader + compiler |
| JSON Schema for mod autocomplete | F# generates it from the authoring types |
| Property-based determinism, economy and migration tests | proptest (Rust), FsCheck (F#) |
| Balancing lab (new) | F# `FarmEngine.Lab`: run thousands of seeds through `farm-ffi` in parallel; chart gold per day per crop or strategy; flag dominant crops |
| Code signing, macOS/Linux builds | CI. wgpu, winit, kira and Avalonia all support all three. |

## Phases

Each phase ends with CI green and a normal release through the Update Center.
The C# engine keeps working until its replacement passes the same tests.

1. **Scaffolding.** Cargo workspace, `rust-toolchain.toml`, FlatBuffers
   schemas (compatibility shape, plus the `GameInfo` table from
   [EXPORT.md](EXPORT.md)), `farm-ffi` stub, `FarmEngine.Interop` with the
   MSBuild cargo target, empty F# projects in the solution. CI adds
   `cargo fmt --check`, `cargo clippy -D warnings`, `cargo test` and a
   `wasm32-unknown-unknown` build check on Linux and Windows. Move goldens to
   `fixtures/golden`. *Exit:* `dotnet build` builds and loads the Rust library
   on both OSes.
2. **Rust core, compatibility mode.** Port `FarmEngine.Core` module by module
   into `farm-sim` with JavaScript semantics. Port the Runtime logic files
   (`FixedTimestep`, `Input`, `Minigames`, `GamePanels`, `Audio` model) into
   `farm-runtime`. Saves refer to content by string id (see
   [Saves](#saves-rust-only)). Add the differential fuzz test against the C#
   core. *Exit:*
   all 23 replays and the RNG, hash and save goldens pass in Rust.
3. **F# authoring.** Port the schema records, `Migrations`,
   `SchemaValidation`, Core `Validation`, `Packs`, `ContentBuiltin`,
   `FarmEngine.Content`, and the compiler to a compatibility cartridge. Add
   `Document`, `Edit` and undo/redo, the `export` settings, and a clear
   new-game start state. The compiler is deterministic (byte-identical
   output). *Exit:* migration goldens pass in F#, and
   F# compile + Rust run gives the same hashes as the C# path for every
   golden scenario.
4. **Switch the app.** `ProjectWorkspace` uses the F# document, Play Mode
   runs the Rust session through FFI (C# overlays still draw), Edit Mode uses
   the preview session and the Skia draw-list executor. Delete
   `FarmEngine.Core`, `FarmEngine.Schemas`, `FarmEngine.Content`,
   `FarmEngine.Rendering` and the Runtime logic files. *Exit:* app tests green
   with no C# engine left.
5. **Editor port** (the list above), on the new stack. It can start as soon
   as phase 3 lands. See the checklist below.
6. **Rust player and plugins.** `farm-plugins` (QuickJS in wasmtime),
   `farm-render` wgpu backend, `farm-ui`, `farm-player` with kira. Embed it in
   Play Mode through `NativeControlHost` and retire the C# play views and Jint.
   Add the game shell (title screen, save slots, settings, gamepad) and Export
   Game for Windows and Linux, then the optional web demo target
   ([EXPORT.md](EXPORT.md)). *Exit:* screenshot tests and replays match
   between embedded, standalone and wasm players, and exported sample games
   replay their goldens on Windows and Linux.
7. **Native numerics (v9).** First the web version adopts `farm-wasm` for play
   and Fable-compiled `FarmEngine.Authoring` for migrations, validation and
   compiling, so both apps run one engine. Then switch `farm-sim` to integer
   and fixed-point types, deny float arithmetic, use the binary state hash,
   add the v8→v9 project and save migrations, and re-record goldens from Rust.
   From here Rust is the reference implementation and `Js` helpers are
   deleted. *Exit:* old v8 saves load and play on in both apps, and benchmarks
   meet their budgets.

## Checklist: porting a new part of the web editor

Use this for every item in the editor list:

1. **Split the web component.** List what it *decides* (defaults, checks,
   derived values, what a button changes) and what it *shows*. Decisions go
   to F#, display goes to C#.
2. **Anything that changes play goes to Rust.** If the web component reaches
   into engine rules, the rule belongs in `farm-sim`, not in the editor.
3. **Add `Edit` cases** in F#. Every change to the project goes through
   `Document.apply`, so undo/redo and autosave come for free. Multi-step
   actions use `Edit.Batch`.
4. **Add validation** to the problems pipeline with a JSON path, so the
   Problems panel can jump to it.
5. **Keep view models thin.** A C# view model binds fields, calls F# and
   shows results. If it contains an `if` about game or project rules, move
   that `if` to F#.
6. **Previews come from Rust.** If the editor needs to show what will
   happen in play (sprite, schedule, growth), ask a Rust preview session
   instead of re-implementing the rule.
7. **Tests:** F# tests for edits and validation, a headless Avalonia test for
   the view, Rust tests plus a golden or replay if simulation changed.
8. **Tick the item** in [ROADMAP.md](../ROADMAP.md) and add it to the
   [CHANGELOG](../CHANGELOG.md).

**Before phase 3 lands:** if you port an editor part now, write it in C#
against the current schema records, but keep the same split. Put the logic
in a plain class in `FarmingRpgMaker.App/Editors/<Name>Model.cs` with no
Avalonia references, shaped as "edit functions + checks". That class then
moves to F# almost line for line.

## Open questions

- **Player-installed mods for exported games.** Pack merging is a compile
  step in F#. For players to add mods to a shipped game, either `farm-player`
  bundles the compiler (Fable-compiled or through a .NET sidecar), or packs are
  precompiled into overlay cartridges that Rust layers with the same
  namespacing rules. Decide in phase 6.
- **Fable for the web editor.** If the Fable-safe rule proves too costly,
  the fallback is to keep the compiler .NET-only and have the web version
  compile through a small Rust port of the compiler. Revisit at the end of
  phase 3.
- **Edit Mode rendering.** The plan uses a Skia executor for Edit Mode so
  Avalonia can draw overlays. If wgpu offscreen rendering into an Avalonia
  bitmap turns out to be fast enough, Edit Mode can drop Skia too and use one
  rasterizer everywhere.
- **In-game UI toolkit.** `farm-ui` is planned as a small custom layer
  (nine-slice pixel-art panels on the draw list, layout with
  [taffy](https://crates.io/crates/taffy)) rather than egui, which looks like
  a tool, not a cozy game. Confirm with a prototype of the inventory grid and
  dialogue box.
