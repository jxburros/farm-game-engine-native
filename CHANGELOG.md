# Changelog

## 0.2.0 (unreleased)

- **Nested content forms.** Content entries now edit nested records, lists and
  references with native controls. Lists add, remove and move rows. Reference
  fields are searchable pickers that keep a missing id visible as
  "(missing: id)". Reference lists such as seasons and gift tastes are chips.
  Event, action and minigame conditions and outcomes pick a type and show
  only that type's fields, like the web editor. Quest objectives show the
  targets of their type. Each nested field has a collapsed Edit as JSON box.
  One save is still one undo step.
- **F# field metadata.** `References` declares what every schema field holds
  and lists picker options from the content compiler, built-in items, crops
  and node types included. `Vocabulary` ports `event-vocabulary.ts` and the
  per-type fields of `event-forms.tsx`. A test fails when an id-like schema
  field has no declaration.
- **More reference checks.** Problems now also reports missing ids in action
  and minigame conditions and outcomes, inventory-space conditions, transition
  and tile outcomes, dialogue option items and actions, gift tastes, item
  crops, placed machines, scene NPC and event lists, the player's quests and
  recipe skills. Each has a JSON path.
- **Export settings.** Project Settings edits the export title, executable
  name, version, author, company, icon (large PNG art only), window size,
  fullscreen, pixel scale and credits, and shows export problems inline.
- **SVG import.** The art studio imports SVG files as PNG art at their own
  size or a chosen size, within the raster limits. Files with scripts,
  embedded HTML or external links are refused.
- **Animation tools.** Frames can be duplicated, and durations set for one
  frame or every frame of a clip, as undoable F# edits.
- **Export Game.** File → Export Game… builds standalone Windows x64 and Linux
  x64 games from the open project, with an optional `.zip` or `.tar.gz` for
  each. The Windows executable gets the game's icon and version info, written
  from .NET into placeholder resources, with no external tools. Linux games get
  a `.png` icon and a `.desktop` file. Every game ships the player's license
  notices. Problems errors stop the export, warnings and unused assets are
  listed, and two exports of the same project are byte-identical.
  `farmc export` does the same from the command line. Releases build the
  player templates (Linux in the Steam Runtime sniper SDK) and ship them with
  the app. Exported games run the headless player until the graphical shell
  is ready.
- **Rust plugin sandbox.** The new `farm-plugins` crate runs content-pack
  plugins in QuickJS compiled to WebAssembly, one isolated instance per
  plugin, in the wasmi interpreter (no JIT). Budgets are deterministic fuel
  instead of wall-clock time, so the same plugins give the same results on
  every machine. Hardening, strikes, error kinds, mutation validation and the
  mutation queue match the Jint host. The app still uses Jint; switching it
  over comes next.
- **Rust Play Mode runtime.** Frame timing, gameplay input bindings, minigame
  scoring, creator-panel values, calendar displays and sound-cue mapping now
  execute in Rust when Rust Play Mode is selected. Minigame views use mount
  tokens to reject stale input, and results pass through the command pipeline
  once. C# remains the fallback when the native library is unavailable.
- **Rust world snapshots.** The new `farm-render` crate builds read-only play
  snapshots from Rust state. Differential tests cover all templates, crop
  maturity/withered state, soil, machines, missing definitions/tiles, moving
  NPCs and animals. The app still decorates artwork and draws through Skia;
  a GPU renderer and standalone graphical player are not included yet.
- **Rust renderer.** `farm-render` now holds the whole world renderer: Edit
  Mode snapshots, art decoration, the built-in art pack (moved to
  `assets/builtin-art/`), draw lists and a CPU rasterizer with embedded Inter
  fonts. `RustRender` and `RustPreview` expose it to C#. Differential tests
  compare it with the Skia renderer; the app still draws with Skia for now.
- **F# project templates.** The starter pack and all four project templates now
  originate in F#. New Project, first launch and game import use the new catalog,
  with the clock supplied by the desktop host. The app no longer depends on
  `FarmEngine.Content`. Full project and cartridge comparisons preserve the
  existing samples, and fresh projects have independent maps and defaults.
- **F# content compiler.** Cartridge exports and editor content previews now
  use the F# built-in catalog, pack namespacing, compatibility checks and locale
  resolution. F# authoring no longer references the C# simulation assembly;
  tile construction and layer edits also live in F#. Parity tests compare the
  compiled content with TypeScript goldens and the C# compatibility engine,
  and run compiled sample cartridges through Rust. C# schema records and the
  gameplay fallback remain while migration continues.
- **F# pack authoring progress.** Dependency ordering, conflict-aware content
  merging, and importing a pack into editable project content now run through
  F#. Differential tests compare the results with the C# compatibility engine.
- **Rust play queries.** Dialogue gates, shop stock limits, crafting
  availability and facing tiles now come from the Rust play session in one
  batched read. The debug drawer's Skip day action runs Rust's overnight pass
  during Rust playtests. Both paths are checked against the C# engine.
- **Creator content workspace.** Edit Mode now includes native forms for
  NPCs, dialogue, items, crops, quests, events, shops, recipes, nodes,
  machines, wildlife, actions and minigames. Creators can add, edit and delete
  entries through the F# undoable document. Project settings and the calendar
  have their own controls, and the
  Problems tab links issues to affected entries.
- **Pack manager.** Creators can review a pack's manifest, dependencies,
  requested permissions and plugin source before installing it. The Mods tab
  supports enable/disable, load order, removal and importing content into
  the project, all through undoable F# edits.
- **Art studio.** Import raster artwork into portable project assets, slice
  sprite sheets into animation clips, append/reorder/remove frames, and bind
  visuals to the player, map brush or content definitions. Removing an asset
  clears its bindings.
- **Workshop and interface.** The 13 F# creator patterns can now be used from
  Edit Mode, each as one undo step. Creators can add in-game panels and edit
  their titles, visibility flags and live entries.
- **Export groundwork.** Projects can store desktop export identity, window,
  icon and target settings without changing the web-compatible schema version.
  F# supplies a stable game id and reports invalid settings in Problems. The
  graphical player and export packaging remain future work.
- **Cartridge groundwork.** `farmc compile` now writes a deterministic
  FlatBuffers `game.cart` with game identity and compiled compatibility
  content. Rust verifies and loads it; cartridge sessions use the persistent
  game id in saves. The graphical player and packaging remain future work.
- **Headless player.** A standalone Rust executable loads cartridges, runs
  scripted replays, checks deterministic state hashes, and loads/writes saves.
  The graphical game shell is still being built.
- **Native map editor.** Edit Mode now exposes layered brush, rectangle and
  area fill, erase, collision, selection and copy/paste tools. Creators can add,
  rename, resize, duplicate and delete scenes, set the start scene and player
  start, and create one-way or return doors. Map changes use the F# document's
  undo/redo and autosave.
- **Built-in pixel art.** Sample games (and any project without its own art)
  now render with a bundled pixel-art pack instead of colored rectangles:
  textured grass, tilled/watered/fertilized soil, animated water, stone walls,
  doors and wood floors; trees (three kinds, two tiles tall), stumps, rocks,
  boulders and ore nodes; every built-in crop drawn through five growth stages
  (plus a withered look); furnace, preserves jar, kitchen, workbench and altar
  with a "working" state and an output-ready bubble; chickens and cows; and a
  player, farmer, merchant and villager with four-direction walk cycles.
  Creator-bound art always takes precedence; unknown mod ids fall back to
  generic sprites.
- **Atmosphere.** Play mode tints the world by the game clock (warm dawn and
  dusk, cool blue nights, untouched at midday), colors grass and foliage by
  season (autumn ochre, winter frost) and overlays rain streaks or snow
  flakes for rainy, stormy and snowy weather. All of it is read from the
  simulation state; rendering never writes back.
- **Depth.** Entities and objects cast soft drop shadows and are drawn in
  y-order, so you walk behind trees and in front of them.
- Edit Mode uses the same art at its 28-px grid, so both modes look alike.
- `tools/art`: the reproducible art pipeline (Blender-as-a-module renders of
  procedural low-poly models, pixelated and quantized to one 48-color
  palette; procedural tiles; sprite sheets + `manifest.json`). The generated
  PNGs are committed, so building never needs Python or Blender.
- **The engine now runs in Rust too.** The simulation is ported to the Rust
  `farm-sim` crate. It replays all 23 recorded sessions from the web version
  with byte-identical state hashes, and a random 400-command session gives
  the same hash after every step in the Rust and C# engines. The app ships
  the Rust library (`farm_ffi`) and can run a game in it through
  `RustSession`; Play Mode still uses the C# engine until the switch-over.
- **Language migration, phase 1.** A Cargo workspace (`farm-sim`,
  `farm-cart`, `farm-runtime`, `farm-ffi`) with determinism lints, the
  `FarmEngine.Interop` bindings built by `dotnet build`, and the F#
  `FarmEngine.Authoring` project with the `Document`/`Edit` undo model. CI
  runs rustfmt, clippy, the Rust tests and a WebAssembly build check.
- **Fixes from an audit:**
  - Projects the web editor saves now import. Checks that the web version
    does not enforce (empty ids, inverted regions, a deleted start scene) are
    reported instead of rejected.
  - Completing a quest that had no progress entry now hashes like the web
    version.
  - An error during a playtest ends the playtest instead of closing the
    editor.
  - Unsaved edits are written before the app exits, before Restart &
    install, and when the app crashes.
  - Project files can't be named after Windows device names (`CON`, `NUL`…).
  - Files with `NaN` or `Infinity` numbers are refused, as on the web.
  - Links in release notes open in the browser.
  - The golden generator writes where the tests read (`fixtures/golden`).
- **Project edits live in F#.** Every change the editor can make is an `Edit`
  (`FarmEngine.Authoring`): tile painting, rectangle and flood fill, paste,
  scene add/resize/duplicate/delete, transitions, NPCs and dialogues, items,
  crops (with their seed and crop items), quests, events, shops, recipes,
  machines, gathering nodes, animals, fish tables, actions, minigames,
  weather, mine, settings, art bindings and assets, content packs. Removing
  something also cleans up what pointed at it. Undo/redo, drag strokes as one
  undo step and autosave come from the F# `Document`; `ProjectWorkspace`
  only holds it.
- **Defaults and ids.** "Add" buttons get the web editor's default values
  from `Defaults`, with deterministic ids instead of the wall clock.
- **Problems pipeline.** `Problems.collect` runs the schema and content
  validators and adds editor checks (unreachable dialogue, unknown flags,
  out-of-bounds doors and starts, duplicate ids, recipe and machine links,
  artwork, packs), each with a JSON path and an editor to jump to.
- **Workshop patterns** (`Patterns`) build their content as one batch edit.
- **Language migration, phase 2 done.** The Rust core now also has:
  - save migrations (`farm-cart::save`), matching the web version on every
    save golden;
  - the runtime logic (`farm-runtime`): keyboard bindings, minigames,
    creator panels and the audio model.
- **Saves survive game updates.** Save files carry a header (game id, game
  version, content hash). A save from another game is refused, a save from a
  newer version of the game loads with a warning, and items the game no
  longer has are set aside instead of breaking the load. They come back if
  a later version brings them back. Web saves still load.
- **Project migrations in F#.** Opening and importing projects runs the new
  F# migrations, which match the web version on every migration golden.
- **Validation in F#.** The Problems panel's schema checks and content
  lints now run in F# too.
- **Play Mode runs on the Rust engine.** Playtests use the Rust simulation
  when its library is present (every build from source with a Rust
  toolchain, and every release). Games play exactly as before; set
  `FARM_ENGINE=csharp` to use the C# engine.
- **Fixes:**
  - Hand-written packs whose node types leave out `respawnDays` now hash
    like the web version.
  - Saving the last-opened project no longer rewrites the Update Center's
    settings.

## 0.1.0

The first native Windows release of Farming RPG Maker: no browser inside,
and updates arrive through the built-in Update Center.

### What's in it

- **Play your games natively.** Walk around and farm (till, plant, water,
  harvest), gather resources, talk to NPCs, shop, craft with machines, give
  gifts, follow quests, fish, explore the mines and play minigames. Weather,
  seasons, festivals and the day/night clock all run too.
- **Same engine as the web version.** Recorded play sessions from the web
  version replay here with byte-identical results, so a game plays exactly
  the same in both.
- **Your projects.** Start from Starter Farm, Cozy Garden, Quest RPG or a
  blank project. Import and export project JSON that works with the web
  version, and projects autosave.
- **Playtesting.** Restart a playtest, keep or discard its changes when you
  leave, and open a debug drawer.
- **Edit Mode (preview).** Browse scenes, inspect tiles, and paint tiles with
  undo/redo.
- **Content packs and plugins.** Mods run in a sandbox.
- **Update Center** (Help → Update Center):
  - Stable and Pre-release channels
  - release notes
  - background download, then Restart & install
  - check-on-startup and auto-download options

### Not yet

- The full visual editor (NPCs, items, quests, events, art, workshop) is
  still being ported. Build games in the web version for now, then use
  File → Import Project JSON.
- No sound output yet, and no Export Game to HTML.

### Installing

Run `FarmingRpgMaker-win-Setup.exe`. It installs for your user account and
needs no admin rights. The build isn't code-signed yet, so Windows SmartScreen
may warn you: choose **More info → Run anyway**.
