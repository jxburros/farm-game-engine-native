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

1. [ ] Scaffolding: Cargo workspace, FlatBuffers schemas, FFI, F# projects, CI
2. [ ] Rust core in compatibility mode (all goldens pass in Rust)
3. [ ] F# authoring: schema, migrations, validation, packs, compiler, undo
4. [ ] Switch the app to F# + Rust; delete the C# engine projects
5. [ ] Editor port on the new stack (the list above)
6. [ ] Rust player and plugin sandbox; embedded Play Mode; Export Game
7. [ ] Native numerics (v9): one engine for web and native

## Later

- **Export Game**: a single-file HTML game and a hosting ZIP (embed the web
  repo's prebuilt `shell.iife.js`, as in `src/lib/export-html.ts`), plus a
  standalone native "player" build.
- **Localization** of the editor UI (`src/lib/i18n.ts`).
- **Code signing** for the Windows installer (see `docs/RELEASING.md`).
- **Out-of-process plugin host.** Jint runs in-process today; a separate
  worker process would fully contain hostile plugins.
- **macOS/Linux builds.** Avalonia and Velopack already support them; this
  needs packaging and CI jobs.
