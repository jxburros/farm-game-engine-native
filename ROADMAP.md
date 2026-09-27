# Native port roadmap

The native app is a phased port of the web version,
[`jxburros/farm-game-engine`](https://github.com/jxburros/farm-game-engine),
which stays the reference implementation and fallback while the port catches up.
Each phase ships as a normal release through the Update Center.

## Done — foundation (v0.1)

- [x] **Engine parity.** Schemas, migrations and the deterministic simulation
  core (`FarmEngine.Schemas`, `FarmEngine.Core`) are ported. The golden
  fixtures replay 23 recorded TypeScript sessions and must match every state
  hash and effect.
- [x] **Content and runtime.** Sample games, templates, fixed timestep, input
  bindings, minigames, creator game panels, the audio model, and the Jint
  plugin sandbox (`FarmEngine.Content`, `FarmEngine.Runtime`).
- [x] **Desktop shell.** Avalonia app, Update Center (Velopack + GitHub
  Releases, Stable/Pre-release channels), and CI and release workflows.
- [x] **Play Mode.** Native Skia renderer, HUD, dialogue, shops, crafting,
  inventory, quests, minigames and playtest snapshots.
- [x] **Project management.** Projects stored as JSON files, JSON
  import/export that works with the web version, and templates.

## Next — editor port

Port each part following the checklist in
[docs/LANGUAGES.md](docs/LANGUAGES.md#checklist-porting-a-new-part-of-the-web-editor):
project logic in F#, views in C#, anything that runs in play in Rust.

The web editor lives in `src/components/*` of the web repo (~13k lines of React).
Ported in rough order of how often creators use each part:

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

Proposed in [docs/LANGUAGES.md](docs/LANGUAGES.md), which has the details and
exit criteria for each phase.

1. [x] Scaffolding: Cargo workspace, FFI, F# projects, Interop, CI (FlatBuffers
   schemas still to write: see [Remaining work](#remaining-work))
2. [x] Rust core in compatibility mode: all 23 replay, content, save, RNG
   and hash goldens pass in Rust, a differential test agrees with the C#
   engine, and `farm-runtime` has input, minigames, panels and the audio
   model.
3. [ ] F# authoring: schema, migrations, validation, packs, compiler, undo
   (**done:** edits and undo, migrations, validation, problems)
4. [ ] Switch the app to F# + Rust; delete the C# engine projects
   (**started:** Play Mode runs on Rust, projects open through F#)
5. [ ] Editor port on the new stack (the list above)
6. [ ] Rust player and plugin sandbox; embedded Play Mode; Export Game for
   Windows and Linux, then an optional web demo target
7. [ ] Native numerics (v9): one engine for web and native

## Remaining work

Status after the second September 2026 session. In order of what unblocks
the most.

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
- [ ] Finish `Packs` and `ContentBuiltin` in F#: load order, conflict-aware
  content merge and project import now run in F#; namespacing and compatibility
  checks still use the C# implementation.
- [ ] Add FlatBuffers `cart.fbs`/`save.fbs` with the `GameInfo` table and the
  deterministic cartridge compiler.
- [ ] Move the rest of `FarmEngine.Authoring` off the C# schema records so
  the project compiles under Fable (only `Json.fs` and `Migrations.fs` are
  Fable-safe today).

**Switch the app (phase 4)**
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
- [ ] Port Edit Mode's preview and remaining play-rule lookups, then delete
  the C# engine projects.

**Editor port (phase 5)**: map tools and a broad content workspace are now
available as native views.
- [x] Tile painter (layers, rectangle, fill, copy/paste), scene manager,
  transitions and collision; art bindings remain in the art pipeline milestone.
- [x] Content workspace for NPCs, dialogue, items, crops, quests, events,
  shops, recipes, node and machine types, animal species, fish tables,
  actions and minigames. Scalar fields have native controls; nested fields
  accept JSON. Every save/removal uses the F# document and undo history.
- [x] Project settings and calendar editor; Problems panel with navigation
  to affected map scenes and content entries.
- [x] Mods panel: validate and review pack manifests, permissions and plugin
  source before install; enable/disable, reorder, remove and import packs.
- [x] Raster art import (PNG, JPEG, WebP, GIF, BMP normalized to PNG),
  animation clip slicing and frame edits, visual bindings for the player,
  map brush and content definitions. Art edits use F# undo/redo.
- [x] Creator Workshop exposes the 13 F# patterns with a one-step undo and
  links to their generated content. Interface editor creates and edits
  in-game panels and their entries.
- [ ] Dedicated nested editors and cross-reference pickers for each content
  type; SVG import and more animation tools.

**Player and export (phase 6)**: `farm-plugins` (QuickJS in wasmtime),
wgpu renderer, `farm-ui`, `farm-player`, game shell, Export Game.

**Audit follow-ups**
- [ ] Run plugin hooks off the UI thread (they can take 50 ms each today).
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

It ships in phase 6, but earlier phases make decisions it depends on (the
cartridge's game info, saves that survive game updates, a deterministic
compiler). [docs/EXPORT.md](docs/EXPORT.md) has the design and the list.

## Later

- **Localization** of the editor UI (`src/lib/i18n.ts`).
- **Code signing** for the Windows installer (see `docs/RELEASING.md`).
- **Out-of-process plugin host.** Jint runs in-process today; a separate
  worker process would fully contain hostile plugins.
- **macOS/Linux builds** of the editor. Avalonia and Velopack already support
  them; this needs packaging and CI jobs. (Exported *games* get Linux builds
  in phase 6, whatever the editor runs on.)
- **More export targets**: macOS, and Steamworks integration (achievements,
  overlay, Steam Input). See the open questions in
  [docs/EXPORT.md](docs/EXPORT.md#open-questions).
