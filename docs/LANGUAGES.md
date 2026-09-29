# Language plan: Rust, F# and C#

**Status:** phases 1–7 are done (September 2026). The Rust core passes every golden (replays,
content, saves); the schema records, project logic, the content compiler,
cartridges and Export Game are F#, and the authoring core compiles to
JavaScript with Fable; the editor's Play Mode runs the Rust `farm-player` on
F#-compiled cartridges; Edit Mode draws with `farm-render`; plugins run in
`farm-plugins`; `farm-wasm` runs the same player in the browser, and the web
editor uses it and the Fable-compiled core. The C# engine and the C# schema
records are gone, and the simulation keeps every quantity as an integer
(schema v9, [NUMERICS.md](NUMERICS.md)). See
[ROADMAP.md](../ROADMAP.md#phase-7-one-engine). This is the reference for
where code goes as the native app grows; read it before you port a new part
of the web editor. [PORTING.md](PORTING.md) covers how Rust code mirrors the
TypeScript.

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
                       │                     │                  tool overlays over Rust-drawn frames  │
                       │                     │              FarmEngine.Interop (C#): P/Invoke wrappers │
                       └─────────────────────┼───────────────────────────────▲────────────────────────┘
                                             │  C ABI (farm-ffi, coarse)     │ commands in, effects/views out
                       ┌─────────────────────▼───────────────────────────────┴────────────────────────┐
                       │ Rust workspace                                                                │
                       │  farm-cart (cartridge + save formats) ─▶ farm-sim (deterministic core)       │
                       │  farm-runtime (timestep, input, minigames, panels, audio cues)               │
                       │  farm-plugins (QuickJS in WebAssembly, run by wasmi)                         │
                       │  farm-render (draw lists; CPU raster) · farm-ui (HUD, dialogue, shop…)      │
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
- **Format 2 (current):** `cart.fbs` carries game info, the compiled content,
  the new-game start state (`StartState`) and presentation data
  (`Presentation`) as compatibility JSON, plus an asset table that holds every
  embedded file once (JSON refers to it as `asset:<id>`). The player never
  reads project JSON. F# resolves the built-in catalog, pack namespacing, load
  order, overrides and locale strings. Content keeps its string ids until
  phase 7: interning them into indexed tables only helps once the simulation
  uses interned indices. See [schemas/README.md](../schemas/README.md).

### Saves (Rust only)

- The player's machine persists saves, so **Rust owns the save format and
  save migrations** (from `Save.cs` and `SaveMigrations.cs`). **F# owns
  project migrations** (from `Migrations.cs`). The creator persists projects.
- The format is FlatBuffers too (`schemas/save.fbs`): a version header, a
  slot preview the title screen reads without loading the game, and the state
  compressed with zstd (pure-Rust `ruzstd`, so it also builds for wasm). JSON
  saves with the same header remain readable.
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

- A C ABI `cdylib`. The C# bindings in `FarmEngine.Interop` are hand-written
  `LibraryImport` declarations (`NativeMethods.cs`) wrapped in small owning
  classes (`RustPlayer`, `RustPreview`, `RustSession`). F# uses the same
  wrappers.
- **Coarse, handle-based, batched.** Never one call per tile or entity. Three
  handles:
  ```c
  // The editor's Play Mode: the whole game player (crates/farm-ffi/src/player.rs).
  fe_result fe_player_new(const uint8_t* game, size_t len, const uint8_t* options, size_t options_len,
                          fe_player** out, fe_bytes* error);         // project JSON or cartridge
  fe_result fe_player_frame(fe_player*, const uint8_t* request, size_t len, fe_bytes* out);
                          // {dt, events, width, height} → size, info JSON, RGBA pixels
  fe_result fe_player_debug(fe_player*, const uint8_t* action, size_t len, fe_bytes* out);
  fe_result fe_player_synced_project(fe_player*, fe_bytes* out);    // "keep changes"
  fe_result fe_player_query_json(fe_player*, const uint8_t* query, size_t len, fe_bytes* out);

  // Edit Mode's map and art previews (render.rs).
  fe_result fe_preview_new(const uint8_t* project, size_t len, fe_preview** out, fe_bytes* error);
  fe_result fe_preview_render(fe_preview*, const uint8_t* request, size_t len, fe_bytes* out);

  // A headless game for tools and tests (session.rs).
  fe_result fe_session_new(const uint8_t* game, size_t len, const uint8_t* seed, size_t seed_len,
                           bool auto_start_quests, fe_session** out, fe_bytes* error);
  fe_result fe_session_apply(fe_session*, const uint8_t* commands, size_t len, fe_bytes* out);
  fe_result fe_session_tick(fe_session*, uint32_t ticks, fe_bytes* out);
  fe_result fe_session_save(fe_session*, fe_bytes* out);

  void      fe_bytes_free(fe_bytes);
  ```
  Requests and answers are JSON (stable JSON for state); frames are raw
  premultiplied RGBA. Each handle has its `_free`.
- **Play Mode.** The editor forwards raw input events (keys by the engine's
  names, pointer positions in frame pixels) and draws the returned pixels; the
  player owns the game, its UI and its sounds (`audio` option). Details in
  [PLAYER.md](PLAYER.md#in-the-editor).
- **Render requests.** `fe_render_json` returns Rust-decorated Edit Mode
  snapshots and PNG rasters of any snapshot. The `fe_preview_*` handle keeps a
  project and its image caches, and rasterizes one scene viewport (or one
  visual binding, for the art studio) per call.
- **Memory:** Rust allocates result buffers; .NET copies what it needs and
  frees them through `fe_bytes_free`. No pointer into Rust memory outlives the
  next call on that handle.
- **Threading:** a handle is used by one thread at a time. The editor runs
  Play Mode frames on a worker thread under one lock and shows the pixels on
  the UI thread.
- **Panics** are caught at the boundary (`catch_unwind`) and returned as
  error results, never unwound into .NET. The handle is then poisoned.
- **Synchronous hooks.** Plugin mutations are queued and come back as
  commands (the `PluginMutationQueue` design). The engine lets
  `onWeatherRoll` listeners return an override *during* the nightly step, but
  plugins never do: the web bridge returns nothing from its listeners. So
  `farm-plugins`' `PluginRuntime` dispatches `onWeatherRoll` after the step
  like any other hook, and installs no `WeatherRollListener`.

### WebAssembly (`farm-wasm`)

- `wasm-bindgen` API that mirrors `farm-ffi`: `Player`, `Session`, `Preview`
  and `renderJson`, over the same host protocol (`farm-host`, shared with
  `farm-ffi`). The web editor's Play Mode uses it; the web demo export target
  will too. Plugins run inside the module (QuickJS in wasmi, like the
  desktop), on the calling thread; a page that wants them off the main thread
  runs the whole player in a Worker. See `crates/farm-wasm/README.md`.

## Determinism rules for Rust

These replace `BannedSymbols.txt`. They're enforced in `farm-sim` with
`clippy.toml` and crate attributes, and CI runs clippy with `-D warnings`:

- `#![forbid(unsafe_code)]` in `farm-sim`, `farm-cart`, `farm-runtime`,
  `farm-plugins`.
- Plugins are deterministic too. Their budgets are fuel (counted wasm
  instructions), not wall-clock time; the guest's clock is constant and
  `Math.random` is seeded the same way on every run; payloads are serialized
  in engine order.
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
  `farm-sim`. Every quantity gets an explicit integer type and scale; the full
  table is in [NUMERICS.md](NUMERICS.md). The starting point was:

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
schemas/cart.fbs, save.fbs
crates/
  farm-cart/       # FlatBuffers readers, interning, cartridge + save load, save migrations
  farm-sim/        # deterministic core           ← src/FarmEngine.Core
  farm-runtime/    # timestep, input, minigames, panel model, audio cues ← src/FarmEngine.Runtime
  farm-plugins/    # PluginHost trait; QuickJS-in-wasm host run by wasmi  ← Runtime/Plugins.cs
  farm-render/     # draw-list builder; wgpu backend (feature)           ← src/FarmEngine.Rendering
  farm-ui/         # in-game UI (HUD, dialogue, shop, inventory, crafting, quests, panels)
  farm-player/     # standalone player (winit + wgpu + kira + gilrs) and game shell; embeddable
  farm-host/       # host protocol shared by farm-ffi and farm-wasm
  farm-ffi/        # C ABI for .NET
  farm-wasm/       # wasm-bindgen API for the web
  farm-bench/      # criterion benchmarks and the CI time budgets
fixtures/golden/                 # shared by Rust and F# tests (moved from tests/…/Golden)
src/
  FarmEngine.Authoring/          # F#, Fable-safe core: schema records, edits, checks, compiler (see below)
  FarmEngine.Authoring.Net/      # F#, .NET-only: System.Text.Json edge, RecordJson, RecordWith (C# builders)
  FarmEngine.Export/             # F#, .NET-only: Export Game (templates, PE resources, archives)
  FarmEngine.Cli/                # F#: the farmc CLI (the balancing lab is still to come)
  FarmEngine.Interop/            # C#: generated bindings + SafeHandle wrappers; builds farm-ffi
  FarmingRpgMaker.Updates/       # C#, unchanged
  FarmingRpgMaker.App/           # C#, Avalonia
tests/
  FarmEngine.Authoring.Tests/    # F# (xUnit + FsCheck)
  FarmEngine.Export.Tests/       # F#
  FarmingRpgMaker.App.Tests/     # C#, unchanged role
tools/fable/                     # Fable build of FarmEngine.Authoring + Node smoke test
tools/wasm/                      # farm-wasm build + Node smoke test
tools/codegen/                   # generators for RecordWith.fs and RecordJson.fs tables
```

`FarmEngine.Interop` has an MSBuild target that runs `cargo build` and copies
`farm_ffi.dll` / `.so` / `.dylib` into the output, so `dotnet build`,
`dotnet test` and `dotnet run` keep working as the only commands a contributor
needs (with a Rust toolchain installed).

### F# conventions

- **Fable-safe core.** `FarmEngine.Authoring` compiles with
  [Fable](https://fable.io) to JavaScript, and the web editor uses it for
  migrations, validation, Problems and compiling. That means no reflection, no
  `System.Text.Json`, no file I/O and no .NET-only APIs (such as
  `SortedDictionary` or `System.Security.Cryptography`) in that project. JSON
  goes through the explicit decoders and encoders in `SchemaJson.fs` over the
  immutable `Json` type, and .NET-only code lives in `FarmEngine.Authoring.Net`.
  CI builds it with `tools/fable/build.sh` and checks the JavaScript in Node
  (`tools/fable/smoke.mjs`). `WebApi.fs` is the JavaScript entry point: JSON
  text in and out, so callers never see F# records.
- **Schema records.** `Schema.fs` holds every project, content and save shape
  as an immutable record: `option` for optional fields, `list` for lists,
  `(string * 'T) list` for ordered maps, `Json` for free-form values, an `Extra`
  field for undeclared keys on the records that keep them, and a union for
  conditions. Every record has a `Default`. Change a record and its decoder
  and encoder together, then regenerate the C# helpers
  (`tools/codegen/record-with.fsx`, `tools/codegen/record-json.py`).
- The compiler writes cartridges with a plain-F# FlatBuffers builder
  (`FlatBuffers.fs`) that produces the same bytes as the official C# builder,
  and `CartridgeReader` reads them back; both work under Fable.
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
  the web version (`farming/crops.ts` → `farm_sim::farming::crops`). The
  golden-first workflow from PORTING.md still applies: port literally, and let
  the goldens decide.
- `farm-sim` has no I/O, no threads and no logging side effects. It is a
  reducer: `(state, content, command | ticks) → (state, effects, hook events)`.
- State is **mutable inside a step, immutable across steps.** Unlike the
  TypeScript reducer, Rust updates state in place. Undo and replay use the command log.
  Snapshots for the debug drawer and playtest "keep changes" are explicit
  copies.

## What happened to the C# code

The C# engine projects (`FarmEngine.Core`, `.Runtime`, `.Rendering`,
`.Content`) are deleted, and so is `FarmEngine.Schemas` (phase 7); the tables
below record where each part went.

### `FarmEngine.Schemas` → split between F# and Rust

| File(s) | Goes to | Notes |
|---|---|---|
| `Project.cs`, `World.cs`, `Actors.cs`, `Content.cs`, `Crafting.cs`, `Economy.cs`, `Events.cs`, `Extensibility.cs`, `Fishing.cs`, `Animals.cs`, `Mining.cs`, `Nodes.cs`, `Quests.cs`, `Social.cs`, `Weather.cs`, `Graphics.cs`, `Interface.cs`, `Settings.cs`, `Primitives.cs`, `GameContent.cs` | F# `FarmEngine.Authoring/Schema.fs` + `SchemaJson.fs` | Done in phase 7. The editor and F# use these records; C# builds them with `RecordWith` and reads options through `FSharpInterop`. Rust has its own serde types and reads cartridges. |
| `Migrations.cs` | F# `Authoring.Migrations` | Project v1→v9 (v8→v9 is the numerics grid). The TypeScript migration goldens pin v1→v8 in the F# tests. |
| `SchemaValidation.cs` | F# `Authoring.Validation` | Merged with Core `Validation.cs` into one Problems pipeline with JSON paths. |
| `Packs.cs` (schemas) | F# `Authoring.Packs` | Manifests and permissions. |
| `Save.cs`, `SaveMigrations.cs` | Rust `farm-cart::save` | Rust owns saves. |
| `Json/Js.cs`, `Json/StableJson.cs`, `Json/JsonDefaults.cs` | Rust `farm-sim::js`, `farm-sim::hash` (compatibility phase); F# `Json.fs` (JS number formatting, stable JSON, parsing) | `Js` goes away with v9 ([NUMERICS.md](NUMERICS.md#what-goes-away)). |
| `Generated/*` (FlatBuffers C# readers) | F# `FlatBuffers.fs` (builder) and `CartridgeReader.fs` | The C# readers were only used by tests. |

### `FarmEngine.Core` → Rust `farm-sim` (except three files) — done, deleted

| File | Goes to |
|---|---|
| `Engine.cs`, `EngineTypes.cs`, `Commands.cs`, `Effects.cs`, `Replay.cs`, `Rng.cs`, `Hash.cs`, `State.cs` (runtime half) | `farm-sim` root modules |
| `GameTime.cs`, `Weather.cs`, `Energy.cs`, `Tools.cs`, `Inventory.cs`, `Economy.cs`, `Crafting.cs`, `Gathering.cs`, `Skills.cs`, `Social.cs`, `Animals.cs`, `Fishing.cs`, `Mines.cs`, `DialogueSystem.cs`, `Events.cs`, `Extensibility.cs`, `Hooks.cs` | same-named `farm-sim` modules |
| `Farming/*`, `World/*`, `Npcs/*`, `Quests/*` | `farm_sim::{farming, world, npcs, quests}` |
| `Validation.cs` | **F#** `Authoring.Validation` (it checks the project, not play) |
| `Packs.cs` (merge and namespacing) | **F#** `Authoring.Packs` (merging happens at compile time) |
| `ContentBuiltin.cs` | **F#** `Authoring.Builtin`, compiled into every cartridge |
| `State.cs` `CreateBaseContentFromProject` / `CreateContentFromProject` | **F#** compiler |
| `State.cs` `ApplyStateToProject` | Rust reports the playtest's final `GameState` as JSON; **F#** writes it back (`Playtest.applyState`, one undoable edit). Rust keeps its own `apply_state_to_project` for players started from project JSON, and a test keeps the two identical. |

### `FarmEngine.Content` → F# — done, deleted

The default pack and template factories are F# `StarterContent`,
`SampleProjects` and the C#-friendly `ProjectCatalog` API. Factories take an
explicit timestamp; the desktop host reads its clock. Legacy tile migration is
part of the F# migration pipeline, and movement interpolation lives in
`farm-render`.

### `FarmEngine.Runtime` → Rust `farm-runtime` / `farm-plugins` — done, deleted

| File | Goes to |
|---|---|
| `FixedTimestep.cs` | `farm-runtime::timestep` |
| `Input.cs` | `farm-runtime::input` (bindings → commands). Hosts pass raw key and pad events. |
| `Minigames.cs` | `farm-runtime::minigames` (logic); drawing in `farm-ui` |
| `GamePanels.cs` | `farm-runtime::panels` (model); drawing in `farm-ui` |
| `Audio.cs` | `farm-runtime::audio` (settings, cues, synthesized SFX presets); mixing in `farm-player::audio` and output through cpal (`speaker`), in exported games and in the editor's Play Mode |
| `Plugins.cs` (Jint) | `farm-plugins`: QuickJS compiled to WebAssembly (a checked-in guest), one isolated instance per plugin, run in [wasmi](https://crates.io/crates/wasmi). wasmi is a pure-Rust interpreter: no JIT, so it also runs where JITs are forbidden. Budgets are deterministic fuel (counted wasm instructions) instead of wall-clock timeouts, plus heap, memory and call-depth limits. The wasm engine sits behind a small internal trait, so wasmtime can be added as a feature later for speed. Jint is gone; the editor's Play Mode and exported games both use this sandbox. This also covers the "out-of-process plugin host" roadmap item. |

### `FarmEngine.Rendering` → Rust draw lists — done, deleted

The renderer splits into **what to draw** (one implementation, Rust) and
**how to rasterize it** (per backend):

| File | Goes to |
|---|---|
| `WorldSnapshot.cs`, `ShellSnapshot.cs` | `farm-render::snapshot` (typed structs, same JSON) and `farm-render::shell` (`shell_snapshot`, `editor_snapshot`). Done. |
| `Graphics.cs`, `BuiltinArt.cs` | `farm-render::graphics` (`resolve_visual`, `apply_graphics`, `GraphicsSource` on the cartridge `Presentation`) and `farm-render::builtin_art`. The art pack lives in `assets/builtin-art/`, shared with the C# project. Done. |
| `Atmosphere.cs`, `Canvas2d.cs`, `CssColor.cs` | `farm-render::{atmosphere, canvas2d, css_color}`. Done. |
| `SkiaWorldRenderer.cs` | `farm-render::world` builds the draw list (`farm-render::draw`) with the same passes and geometry. `farm-render::raster` rasterizes it on the CPU with tiny-skia (feature `raster`, also on wasm32). Done; Edit Mode (`MapCanvas`, `RustPreview`) and Play Mode both draw with it. Later: `farm-render::wgpu` for play. |
| `ImageStore.cs` | `farm-render::images`: decodes `data:` URLs and `builtin://` sheets (PNG, JPEG, GIF, WebP, BMP), refuses images over 8192 px a side, caches failures, bounded LRU, and a resolver hook for cartridge `asset:` URLs. Done. |
| (new) | `farm-render::text`: embedded Inter Regular and Bold, measurement, word wrap and ellipsis for `farm-ui`. |

Until the switch, differential tests compared decorated Edit Mode snapshots
exactly and the Rust raster with the Skia one pixel by pixel; golden images in
`fixtures/render/` now pin the Rust output.

A later wgpu backend would add the farming-specific visuals a CPU rasterizer
can't do cheaply: day/night lighting, season palette swaps (the same art in autumn
or snow), rain, snow and wind particles, and water shimmer on watered soil.

### `FarmingRpgMaker.App` (C#), what stays and what changes

| Area | Fate |
|---|---|
| `App.axaml*`, `Views/*`, `ViewModels/*`, `Mvvm/*`, `Controls/*`, `Markdown/*`, `Services/*`, `Hosting/*` | Stay C#. |
| `Projects/*` (`ProjectStore`, `AtomicFile`, `AppDataPaths`, `ProjectDialogs`, `ProjectCommandHandler`) | Stay C#. `ProjectWorkspace` holds an F# `Document` instead of a `GameProject`. |
| `Game/EditModeView.cs` | Stays C#. Edits go through F# `Document.apply`; the map (`MapCanvas`) and art previews (`VisualPreview`) are rasterized by Rust through `RustPreview`, only the visible region. |
| `Game/PlayModeView.cs` | Done: hosts the embedded `farm-player` (`RustPlayer`). Frames are rendered by Rust on a worker thread and shown in an Avalonia control (`PlayerSurface`) rather than a native child window, so the editor's debug drawer and toasts can sit on top of the game. All game UI lives in `farm-ui`. |
| `PlayOverlays.cs`, `PlaySession.cs`, `PlayEngine.cs`, `GameCanvas.cs`, `MinigameOverlay.cs`, `SpriteImage.cs`, `RuntimeBridge.cs`, `GameStateMirror.cs` | Deleted. |
| `Game/DebugDrawer.cs` | Stays C#, over the game: `fe_player_debug` for actions, `fe_player_query_json` for its summary. |

`FarmingRpgMaker.Updates` stays C# and unchanged. Velopack packaging ships
`farm_ffi.dll` and the player templates for Export Game.

### Tests

| Was (`FarmEngine.Core.Tests`) | Now |
|---|---|
| `Golden/GoldenParityTests.cs`, replays, rng, hash, saves | Rust integration tests in `farm-sim` / `farm-cart` reading `fixtures/golden` |
| `Golden/migrations/*`, `Fixtures/project-v*.json`, `Fixtures/migrated/*` | F# `FarmEngine.Authoring.Tests`, fixtures in `fixtures/projects` |
| `Schemas/*`, `JsSemanticsTests.cs` | F# `SchemaRecordTests`, `JsonTests` and `MigrationTests` (the C# `FarmEngine.Schemas.Tests` went with the records in phase 7) |
| `Core/*`, `Runtime/*` characterization tests | Ported to Rust tests next to each module |
| `Content/*` | F# tests against the TypeScript goldens |
| `FarmingRpgMaker.App.Tests` | Stay C# (headless Avalonia), driving the embedded Rust player |

New tests:

- A **differential fuzz test** ran the C# core and the Rust core on the same
  random command streams during phases 1–4 and compared hashes each step,
  until the C# core was retired.
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
| **2. World content editors** (`NPCEditor`, `ItemEditor`, `CropEditor`, `QuestEditor`, `EventsEditor` + `event-forms`, `ShopEditor`, `RecipeEditor`, `NodeTypeEditor`, `WildlifeEditor`) | Edit cases, validation, **condition language** (parse, type-check, compile to bytecode) for events and quests, dialogue graph model and checks (unreachable nodes, dead ends), field metadata (`References`: what each schema field holds and its picker options; `Vocabulary`: the condition/outcome forms) | Forms generated from the schema types plus that metadata (`ContentForm`) | Headless previews: **"where is this NPC at 2 pm on a rainy Tuesday"** (schedule + pathfinding), crop growth timeline, shop price over a season |
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
| Export Game: web demo | Done: the `farm-wasm` player template + `game.cart` + `index.html`, for itch.io pages ([EXPORT.md](EXPORT.md#output-layout)). |
| Seed selection, fertilizer choice, richer animals, fishing and relationships, multi-tile buildings, roaming insects, real-time combat | `farm-sim` (+ `farm-ui` for player UI; F# for the authoring side) |
| Audio-file import | C# import; F# embeds in the cartridge; Rust plays with kira (seasonal music crossfades, weather ambience layers) |
| Zip/folder content packs with binary assets | F# pack loader + compiler |
| JSON Schema for mod autocomplete | F# generates it from the authoring types |
| Property-based determinism, economy and migration tests | Done: proptest (Rust), FsCheck (F#) |
| Balancing lab (new) | F# `FarmEngine.Lab`: run thousands of seeds through `farm-ffi` in parallel; chart gold per day per crop or strategy; flag dominant crops |
| Code signing, macOS/Linux builds | CI. wgpu, winit, kira and Avalonia all support all three. |

## Phases

Each phase ends with CI green and a normal release through the Update Center.
Phases 1–7 are done; the C# engine kept working until its replacement passed
the same tests, and was then deleted.

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
   runs Rust through FFI, Edit Mode draws with the Rust preview. Delete
   `FarmEngine.Core`, `FarmEngine.Content`, `FarmEngine.Rendering` and
   `FarmEngine.Runtime`. *Exit:* app tests green with no C# engine left. (Done.
   `FarmEngine.Schemas` stayed as the editor's data model until the F# schema
   records landed in phase 7.)
5. **Editor port** (the list above), on the new stack. It can start as soon
   as phase 3 lands. See the checklist below.
6. **Rust player and plugins.** `farm-plugins` (QuickJS in wasmi, done),
   `farm-render` wgpu backend, `farm-ui` (done), `farm-player` (the graphical
   player and game shell are done, on the CPU rasterizer with cpal audio).
   Embed it in Play Mode (done: frames rendered by Rust and shown in an
   Avalonia control) and retire the C# play views and Jint (done). Export Game
   for Windows and Linux (done), then the web demo target (done in phase 7,
   with `farm-wasm`; [EXPORT.md](EXPORT.md), [PLAYER.md](PLAYER.md)). *Exit:* the embedded and
   standalone players are the same `Player`, screenshot goldens pin its
   frames, and exported sample games replay their goldens on Windows and
   Linux. The wgpu backend and the wasm player remain open.
7. **One engine (v9).** First move the schema records from C# to F# so
   `FarmEngine.Authoring` compiles under Fable (done), then the web version
   adopts `farm-wasm` for play and Fable-compiled `FarmEngine.Authoring` for
   migrations, validation and compiling, so both apps run one engine (done;
   the web editor vendors both builds). Then switch `farm-sim` to integer and
   fixed-point types, deny float arithmetic, use the binary state hash, add the
   v8→v9 project and save migrations, and re-record goldens from Rust
   ([NUMERICS.md](NUMERICS.md); done). From here Rust is the reference
   implementation and the `js` helpers are deleted. *Exit:* old v8 saves load
   and play on in both apps, and benchmarks meet their budgets (both met).

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


## Open questions

- **Player-installed mods for exported games.** Pack merging is a compile
  step in F#. For players to add mods to a shipped game, either `farm-player`
  bundles the compiler (Fable-compiled or through a .NET sidecar), or packs are
  precompiled into overlay cartridges that Rust layers with the same
  namespacing rules. Decide in phase 6.
- **Fable for the web editor.** Settled: the authoring core compiles under
  Fable 5 without a fallback, and the JavaScript reproduces the .NET
  cartridges byte for byte.
- **Edit Mode rendering.** Settled: Edit Mode rasterizes the visible map
  region with the Rust CPU rasterizer into an Avalonia bitmap and draws its
  tool overlays on top in Avalonia; one rasterizer everywhere.
- **In-game UI toolkit.** Settled: `farm-ui` is a small custom
  immediate-mode layer on the draw list rather than egui, which looks like a
  tool, not a cozy game. Panels are rounded, shadowed shapes in the theme's
  colours, and layout is rectangle cutting, so it needs no layout engine.
  Nine-slice pixel-art panels can come later as a theme option.
