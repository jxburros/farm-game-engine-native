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
   `TransitionEditor.tsx`).
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
2. [ ] Rust core in compatibility mode. **Mostly done:** all 23 replay,
   content, RNG and hash goldens pass in Rust, and a differential test
   agrees with the C# engine. Still to do: save migrations and
   `farm-runtime` (input, minigames, panels, audio model).
3. [ ] F# authoring: schema, migrations, validation, packs, compiler, undo
4. [ ] Switch the app to F# + Rust; delete the C# engine projects
5. [ ] Editor port on the new stack (the list above)
6. [ ] Rust player and plugin sandbox; embedded Play Mode; Export Game for
   Windows and Linux, then an optional web demo target
7. [ ] Native numerics (v9): one engine for web and native

## Remaining work

Status after the September 2026 session. In order of what unblocks the most.

**Rust core (phase 2)**
- [ ] Port `SaveMigrations.cs` to `farm-cart::save` and pass the save
  migration goldens (`fixtures/golden/saves`).
- [ ] Port `FarmEngine.Runtime` logic to `farm-runtime`: `Input.cs`,
  `Minigames.cs`, `GamePanels.cs`, the `Audio.cs` model.
- [ ] Check four ignored `farm-sim` tests against their C# originals: two
  crafting-station tests and two weather/animal tests (see their
  `#[ignore]` reasons). The golden replays pass, so these are likely test
  setup problems.
- [ ] Move saves to string-id references and the save header from
  [EXPORT.md](docs/EXPORT.md#what-earlier-phases-must-get-right).

**F# authoring (phase 3)**
- [x] F# authoring core merged: `Edit` cases for every editor, `Defaults`,
  the problems pipeline with JSON paths, workshop patterns, the C# `Api`,
  and `ProjectWorkspace` on the F# `Document` (62 F# tests, all app tests
  pass).
- [x] The F# test project hung `dotnet build` on SDK 10.0.112 (minutes per
  file; `DefaultsTests.fs` was excluded). The cause was nullness checking in
  the tests; it is now off for the test project only, the whole project
  compiles in about 15 seconds and `DefaultsTests.fs` is back (68 F# tests).
- [ ] Port `Migrations.cs`, `SchemaValidation.cs`, `Validation.cs`, `Packs`
  merging and `ContentBuiltin` to F#; FlatBuffers `cart.fbs`/`save.fbs` with
  the `GameInfo` table; the deterministic cartridge compiler.

**Switch the app (phase 4)**
- [ ] Play Mode runs `RustSession` instead of the C# engine; Edit Mode uses a
  preview session; then delete the C# engine projects.

**Editor port (phase 5)**: the numbered list above, none started as views yet.
- [ ] Tile painter (layers, rectangle, fill, copy/paste), scene manager,
  transitions.
- [ ] Content editors: NPCs and dialogue, items, crops, quests, events,
  shops, recipes, node types, wildlife.
- [ ] Project settings and calendar, Problems panel, mods and actions, art
  import.

**Player and export (phase 6)**: `farm-plugins` (QuickJS in wasmtime),
wgpu renderer, `farm-ui`, `farm-player`, game shell, Export Game.

**Audit follow-ups not yet done**
- [ ] Run plugin hooks off the UI thread (they can take 50 ms each today).
- [ ] Decide absent-versus-null for `respawnDays` so hand-written packs hash
  like the web version.
- [ ] Share one `JsonSerializerOptions` between the two `settings.json`
  stores.

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
