# Native port roadmap

The native app is a phased port of the web version,
[`jxburros/farm-game-engine`](https://github.com/jxburros/farm-game-engine),
which stays the reference implementation. The port is complete: phases 1–6
below are done, and phase 7 (one engine for the web and native versions) is
what's next. Each phase ships as a normal release through the Update Center.

## Done — foundation (v0.1)

- [x] **Engine parity.** Schemas, migrations and the deterministic simulation
  core were ported (first to C#, since replaced by Rust and F#). The golden
  fixtures replay 23 recorded TypeScript sessions and must match every state
  hash and effect.
- [x] **Content and runtime.** Sample games, templates, fixed timestep, input
  bindings, minigames, creator game panels, the audio model, and the Jint
  plugin sandbox (first in C#, since replaced by `farm-runtime`,
  `farm-plugins` and F#).
- [x] **Desktop shell.** Avalonia app, Update Center (Velopack + GitHub
  Releases, Stable/Pre-release channels), and CI and release workflows.
- [x] **Play Mode.** Native Skia renderer, HUD, dialogue, shops, crafting,
  inventory, quests, minigames and playtest snapshots.
- [x] **Project management.** Projects stored as JSON files, JSON
  import/export that works with the web version, and templates.

## Done — editor port

Each part was ported following the checklist in
[docs/LANGUAGES.md](docs/LANGUAGES.md#checklist-porting-a-new-part-of-the-web-editor):
project logic in F#, views in C#, anything that runs in play in Rust.

The web editor lives in `src/components/*` of the web repo (~13k lines of React).
Ported in rough order of how often creators use each part (all done; see
phase 5 below):

1. **Tile painter**: layers, brushes, rectangle/fill, copy/paste, universal
   undo/redo (`EditorPanel.tsx`, `GridTile.tsx`, `SceneManager.tsx`,
   `TransitionEditor.tsx`). The native map UI now covers these tools, collision,
   scene management and transitions; artwork bindings remain in the art phase.
2. **World content editors**:
   - NPCs and dialogue (`NPCEditor.tsx`)
   - items (`ItemEditor.tsx`)
   - crops (`CropEditor.tsx`)
   - quests (`QuestEditor.tsx`)
   - events (`EventsEditor.tsx`, `event-forms.tsx`)
   - shops (`ShopEditor.tsx`)
   - recipes (`RecipeEditor.tsx`)
   - node types (`NodeTypeEditor.tsx`)
   - wildlife (`WildlifeEditor.tsx`)
3. **Project settings and calendar** (`ProjectSettingsEditor.tsx`).
4. **Art pipeline**: asset import, the animation studio and art bindings
   (`AssetManager.tsx`, `ArtBindings.tsx`, `src/lib/import-art.ts`,
   `src/lib/validate-graphics.ts`).
5. **Creator workshop patterns and interface panels**
   (`CreatorWorkshop.tsx`, `InterfaceEditor.tsx`, `src/lib/creator-patterns.ts`).
6. **Mods and actions**: pack install/enable/order, plugin permission review
   (`ModsEditor.tsx`, `ActionsEditor.tsx`, `src/lib/mod-registry.ts`,
   `src/lib/validate-extensibility.ts`).
7. **Problems panel and debug drawer** (`ProblemsPanel.tsx`, `DebugDrawer.tsx`).

## Language migration (Rust, F#, C#)

Described in [docs/LANGUAGES.md](docs/LANGUAGES.md), which has the details
and exit criteria for each phase.

1. [x] Scaffolding: Cargo workspace, FFI, F# projects, Interop, CI (FlatBuffers
   schemas still to write: see [Remaining work](#remaining-work))
2. [x] Rust core in compatibility mode: all 23 replay, content, save, RNG
   and hash goldens pass in Rust, a differential test agrees with the C#
   engine, and `farm-runtime` has input, minigames, panels and the audio
   model.
3. [x] F# authoring: migrations, validation, packs, compiler, cartridges,
   undo. The schema records stay C# for now (moving them to F# is the first
   step of phase 7).
4. [x] Switch the app to F# + Rust; the C# engine projects are deleted
5. [x] Editor port on the new stack (the list above)
6. [x] Rust player and plugin sandbox, embedded in Play Mode; Export Game for
   Windows and Linux. The optional web demo target and a GPU renderer come
   later.
7. [ ] Native numerics (v9): one engine for web and native

## Remaining work

Status at the end of September 2026: phases 1–6 are done. What's left is
phase 7 and the items under [Later](#later).

**Rust core (phase 2)**: done.
- [x] `SaveMigrations.cs` ported to `farm-cart::save`; all eight save
  migration goldens (`fixtures/golden/saves`) match the TypeScript result,
  stable JSON and hash.
- [x] `FarmEngine.Runtime` logic ported to `farm-runtime`: `input`,
  `minigames`, `panels` and the `audio` model, with every C# runtime test
  ported (81 tests). Minigames take their cosmetic randomness from the host
  (`with_random`) instead of the OS.
- [x] The four ignored `farm-sim` tests were test setup problems (the
  starter farm's own Kitchen; the C# 6x6 test field). All four pass now.
- [x] Save files (`farm-cart::save_file`) carry the header from
  [EXPORT.md](docs/EXPORT.md#what-earlier-phases-must-get-right): save
  format, `gameId`, game version and cartridge hash. A save from another
  game is refused, one from a newer game version loads with a warning, and
  when the content changed, inventory items are matched by string id:
  removed items go to quarantine and come back with their id. Exposed as
  `fe_session_save` / `fe_session_load_save` and `RustSession.Save()` /
  `LoadSave()`. Until the export settings exist (phase 3), `gameId` is the
  project id.

**F# authoring (phase 3)**
- [x] F# authoring core merged: `Edit` cases for every editor, `Defaults`,
  the problems pipeline with JSON paths, workshop patterns, the C# `Api`,
  and `ProjectWorkspace` on the F# `Document` (62 F# tests, all app tests
  pass).
- [x] The F# test project hung `dotnet build` on SDK 10.0.112 (minutes per
  file; `DefaultsTests.fs` was excluded). The cause was nullness checking in
  the tests; it is now off for the test project only, the whole project
  compiles in about 15 seconds and `DefaultsTests.fs` is back (68 F# tests).
- [x] Project migrations in F#: `Migrations.fs` (v1→v8 and exported games)
  runs on a Fable-safe immutable JSON type (`Json.fs`) with JavaScript
  semantics; `FarmEngine.Authoring.Net` converts to and from
  `System.Text.Json` and still parses and validates the result with the C#
  schema. Every migration golden passes, and a differential test agrees
  with the C# `Migrations.MigrateProject` on 25 inputs, step by step (150 F#
  tests). `ProjectStore` (open and import) now uses the F# migrations.
- [x] Validation in F#: `SchemaChecks.fs` (the schema checks of
  `SchemaValidation.cs`, project and exported game) and `ContentLints.fs`
  (`Validation.cs`, the web `validateProjectContent`). The Problems panel
  and the F# migrations use them. A parity test compares them with the C#
  on every golden and sample project and on 111 broken projects that
  together hit every check (467 F# tests).
- [x] F# pack namespacing, compatibility checks, load order, conflict-aware
  merge, localization and project import. The built-in authored catalog and
  project-to-content compiler now also run in F#. Cartridge exports, editor
  content previews and Problems use this path. Content goldens and differential
  tests preserve TypeScript behavior, including absent/null fields and ordering.
- [x] Remove `FarmEngine.Authoring`'s dependency on the C# simulation project:
  map construction and layer edits use F# authoring helpers, and the assembly
  dependency is guarded by a test. C# schema records remain a compatibility bridge.
- [x] Port the Farm Essentials starter pack, blank project, Cozy Garden and
  Quest RPG factories to F#. New Project, first launch and imported-game
  defaults use the F# catalog. Time is supplied by the desktop host. The app
  no longer references `FarmEngine.Content`; its C# factories remain only as
  test references while the full schema migration continues.
- [x] Cartridge format 2: compiled content, the new-game start state and
  presentation data as separate sections, and an asset table that stores each
  embedded file once. The player no longer reads project JSON. Binary saves
  (`save.fbs`) carry a slot preview and a zstd-compressed state; JSON saves
  still load. Content keeps string ids until phase 7, when interned indexed
  tables start to pay off.
- [x] `farmc compile` writes a deterministic cartridge; Rust verifies and
  loads it, and cross-language tests compare its content, start state,
  presentation and play state with the project path.
- [x] Add optional export identity/window/target settings to the project,
  with a stable generated game id and F# Problems validation. The cartridge
  compiler and Export Game consume them.
- [x] Editor controls for the export settings (title, executable name,
  version, company, icon), in Project Settings.
- [ ] Move the rest of `FarmEngine.Authoring` off the C# schema records so
  the project compiles under Fable (only `Json.fs` and `Migrations.fs` are
  Fable-safe today). **Moved to phase 7:** it only pays off once the web
  version compiles the F# core, and it changes the data model every editor
  view binds to.

**Switch the app (phase 4)**: done; the C# engine projects are deleted.
- [x] Play Mode runs the Rust engine (`RustPlayEngine` behind `IPlayEngine`)
  whenever the library is available, with the C# engine as the fallback
  (`FARM_ENGINE=csharp` forces it). Rust sends only the state sections
  that changed (`fe_session_state_changes`); `GameStateMirror` keeps a C#
  copy for the overlays and reuses unchanged objects, so views that compare
  by reference don't rebuild. Plugins get the Rust hook events through the
  existing Jint bridge. A scripted play with a plugin pack matches the C#
  engine after every step. A walking frame costs about 0.03 ms.
- [x] Play overlays ask Rust for dialogue, visible options, shop stock limits,
  recipe availability, ingredients and facing tile in one batched query.
  The debug drawer's skip day uses Rust's overnight pass. A scripted play
  compares these results with the C# engine after every step.
- [x] Connect Rust runtime logic to Play Mode: fixed timestep, movement and
  one-shot bindings, all built-in minigames and fallback, creator panels,
  calendar displays and sound-cue mapping. C# collects raw host events and
  draws returned views. Minigame results still enter the command log exactly
  once; expired mounts cannot score a replacement session.
- [x] `farm-render` builds Play Mode's world snapshots in Rust, including crop
  maturity, soil, machine status, live NPC movement, animals and atmosphere.
  Template and scripted-play differential tests compare the entire snapshot
  with the C# reference. Art decoration and Skia drawing remain managed.
- [x] `farm-render` ports all of `FarmEngine.Rendering`: typed snapshots,
  Edit Mode snapshots, art decoration, the built-in art pack, draw lists, a
  CPU rasterizer and embedded fonts. Differential tests match the decorated
  Edit Mode snapshots exactly and the raster within a small pixel tolerance.
  `RustPreview` renders an Edit Mode viewport.
- [x] Edit Mode's map and art previews draw with `farm-render` (only the
  visible region), Play Mode embeds the Rust player (below), and the C#
  simulation, runtime, renderer and content projects are deleted along with
  their tests (the Rust and F# suites carry ports; the shared fixtures moved
  to `fixtures/projects`). `FarmEngine.Interop` keeps a JSON-only headless
  session for tools and tests.

**Editor port (phase 5)**: map tools and a broad content workspace are now
available as native views.
- [x] Tile painter (layers, rectangle, fill, copy/paste), scene manager,
  transitions and collision; art bindings remain in the art pipeline milestone.
- [x] Content workspace for NPCs, dialogue, items, crops, quests, events,
  shops, recipes, node and machine types, animal species, fish tables,
  actions and minigames. Every save/removal uses the F# document and undo
  history.
- [x] Project settings and calendar editor; Problems panel with navigation
  to affected map scenes and content entries.
- [x] Mods panel: validate and review pack manifests, permissions and plugin
  source before install; enable/disable, reorder, remove and import packs.
- [x] Art import (PNG, JPEG, WebP, GIF, BMP and SVG normalized to PNG),
  animation clip slicing and frame edits, visual bindings for the player,
  map brush and content definitions. Art edits use F# undo/redo.
- [x] Creator Workshop exposes the 13 F# patterns with a one-step undo and
  links to their generated content. Interface editor creates and edits
  in-game panels and their entries.
- [x] Nested editors and cross-reference pickers for every content type.
  F# `References` declares what each schema field holds (a test fails when an
  id-like field is undeclared) and lists picker options from the content
  compiler; F# `Vocabulary` ports the condition/outcome forms. Forms show
  nested records, list rows with add/remove/move, reference chips and a
  collapsed Edit as JSON box per nested field. Missing ids stay visible.
- [x] Export settings controls in Project Settings, with the `ChecksExport`
  problems shown inline.
- [x] SVG import (Svg.Skia, self-contained files only, optional size), frame
  duplication and frame durations (one frame or every frame), as F# edits.

**Player and export (phase 6)**: done. `farm-plugins` (QuickJS in wasmi),
`farm-ui`, `farm-player`, game shell, Export Game, and the player embedded in
Play Mode.
- [x] Headless `farm-player` loads `game.cart` beside the executable or by
  `--cart`, replays commands, checks a hash, and loads/writes portable saves.
- [x] `farm-ui`: an immediate-mode UI on `farm-render` draw lists, with
  stable widget ids, layout, scrolling, focus navigation for keyboard,
  gamepad and mouse, and a theme with UI scale and text size. It ports every
  Play Mode overlay (HUD, dialogue, shop, crafting, inventory, quest log,
  creator panels, minigames, toasts, credit). Every action is an engine
  command, and the rules come from Rust queries (`farm_sim::overlay`, shared
  with `farm-ffi`).
- [x] Graphical `farm-player`: title screen, three save slots with previews
  and thumbnails, autosave each morning, pause menu, settings (display,
  audio, controls, accessibility) and credits. winit and softbuffer, gilrs
  gamepads, cpal audio, TOML settings in the user's folders, a crash log.
  `--screenshot` renders PNGs; screenshot goldens at 1280×800 and 1920×1080.
  The `Player` library runs standalone or embedded, from a cartridge or a
  project, is `Send`, and reports engine failures as errors.
- [x] Play Mode embeds `Player` through `farm-ffi` (`fe_player_*`,
  `RustPlayer`): the game and all of its UI are drawn by Rust exactly as in
  an exported game, frames run on a worker thread, sounds play through cpal,
  and the editor keeps Restart, Keep changes and the debug drawer. The C#
  play views are deleted.
- [x] `farm-plugins`: the plugin sandbox in Rust. QuickJS is compiled to
  WebAssembly (checked in; `tools/plugin-guest/build.sh` rebuilds it) and
  each plugin gets its own instance in wasmi, a pure-Rust interpreter with no
  JIT. Budgets are deterministic fuel, not wall-clock time: an infinite loop
  stops after about 40 ms in release builds. Strikes, errors, mutation
  validation and the command queue work like the Jint host, and every C# and
  web plugin test is ported. `PluginRuntime` feeds a step's hook events to
  plugins and hands mutations back as commands.
- [x] Play Mode runs pack plugins in `farm-plugins` (through the embedded
  player); the Jint host is deleted.
- [x] `farm-render` supplies world snapshots, draw lists and a CPU
  rasterizer that also builds for WebAssembly. GPU drawing is under
  [Later](#later).
- [x] Export Game packaging (`FarmEngine.Export`, File → Export Game…,
  `farmc export`): Problems gate, deterministic cartridge, renamed player
  template, Windows icon and version info patched from .NET, Linux `.png` and
  `.desktop`, license notices, reproducible `.zip`/`.tar.gz`. Releases build
  both templates (Linux in Steam Runtime sniper) and ship them in `players/`.
  Exported games run the graphical player.
- [x] Ship only used assets: the compiler leaves unused custom assets out of
  the cartridge, and Export lists them as warnings.

**Audit follow-ups**
- [x] Run plugin hooks off the UI thread: Play Mode frames, plugins included,
  run on a worker thread.
- [x] `respawnDays` keeps absent and `null` apart in both engines, like the
  TypeScript `.nullable().optional()`, so hand-written packs hash like the
  web version (content golden `packs-nodes`).
- [x] Both `settings.json` stores share `JsonSettingsStore.JsonOptions`.

## Export Game

Export makes real desktop games, not browser games. It copies a prebuilt
`farm-player` for the target, renames it, and puts the compiled `game.cart`
next to it. Windows and Linux (including Steam Deck) come first. A web demo
build for itch.io pages is optional and comes after them, and macOS comes
later. The web version's single-file HTML export is not ported.

Packaging works now: **File → Export Game…** and `farmc export` build the
Windows and Linux folders and archives from the prebuilt templates. The games
it makes run the graphical player, with a title screen, save slots, settings
and gamepad support ([docs/PLAYER.md](docs/PLAYER.md)).
[docs/EXPORT.md](docs/EXPORT.md) has the design, what is done and what is left.

## Later

- **Localization** of the editor UI (`src/lib/i18n.ts`).
- **Code signing** for the Windows installer (see `docs/RELEASING.md`).
- **Phase 7: one engine for web and native.** F# schema records so the
  authoring core compiles under Fable, `farm-wasm` for the web version's play
  mode and an optional web demo export, then native numerics (v9) with
  migrations and re-recorded goldens. See
  [docs/LANGUAGES.md](docs/LANGUAGES.md#phases).
- **Player polish.** A GPU renderer (wgpu) for lighting, palette swaps and
  particles; gamepad rebinding in the controls menu (keys are rebindable
  today); a message box when the game crashes (it writes a crash log today).
- **macOS/Linux builds** of the editor. Avalonia and Velopack already support
  them; this needs packaging and CI jobs. (Exported *games* get Linux builds
  in phase 6, whatever the editor runs on.)
- **More export targets**: macOS, and Steamworks integration (achievements,
  overlay, Steam Input). See the open questions in
  [docs/EXPORT.md](docs/EXPORT.md#open-questions).
