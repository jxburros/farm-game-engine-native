# Changelog

## 0.2.0 (unreleased)

- **One engine for the web and native editors.** The web editor
  ([jxburros/farm-game-engine](https://github.com/jxburros/farm-game-engine))
  now runs this repository's engine: its Play Mode plays through `farm-wasm`,
  and opening older projects, importing, the Problems panel and a new
  "Download cartridge (.farmcart)" export run the F# authoring core compiled to
  JavaScript. Both builds are vendored in the web repo's `packages/engine-native`
  with the native commit they came from.
- **Schema records in F#.** The project, content and save records moved from
  C# to F# (`Schema.fs`, with explicit JSON codecs in `SchemaJson.fs`), and the
  C# `FarmEngine.Schemas` project, its migrations, validation and generated
  FlatBuffers readers are gone. The authoring core has no .NET-only code left:
  JSON, UTF-8, base64, SHA-256 and the FlatBuffers builder are plain F#, and
  cartridges come out byte-identical. The whole core compiles with Fable, and
  CI checks the JavaScript against the .NET cartridges and every migration
  golden. The desktop app builds records through generated `WithField`
  extensions (`RecordWith`). Behaviour snapshots of every golden, fixture,
  broken and sample project (`fixtures/authoring/snapshots.json`) pinned the
  move.
- **WebAssembly player (`farm-wasm`).** Web pages can run the same game player
  as the editor's Play Mode. `farm-wasm` is a wasm-bindgen package that mirrors
  `farm-ffi`: `Player` (frames with RGBA pixels for `ImageData`, sound cues,
  debug actions, keep changes, queries), `Session` (headless replays),
  `Preview` and `renderJson`, `hashText`, and `sfxSamples` to play the
  synthesized sound effects with WebAudio. Save slots and settings are kept in
  memory and move to browser storage through `exportStorage`/`importStorage`.
  Pack plugins run inside the module. The player, session and preview protocol
  now lives in the new `farm-host` crate, shared by `farm-ffi` (unchanged C ABI)
  and `farm-wasm`. `tools/wasm/build.sh` builds the package and
  `tools/wasm/smoke.mjs` checks it in Node, including every golden replay; CI
  runs both.
- **Web demo export.** Export Game has a third target, **Web (browser demo)**:
  a folder and a flat zip ready for an itch.io page, with `index.html`, the
  `farm-wasm` player and the game's cartridge and icon. It runs the same title
  screen, save slots, settings and game UI as the desktop game, keeps saves in
  the browser's storage, and plays sounds through WebAudio. `farmc export
  --target web` does the same, and releases ship the web template with the
  app.
- **Play Mode runs the exported cartridge.** A playtest now runs the cartridge
  the F# compiler makes, exactly what Export Game ships (it still starts while
  Problems lists errors). Keep changes writes the final game state back
  through F#, checked against the Rust write-back for every template.
- **Native numerics (schema v9, save version 5).** The Rust simulation no
  longer copies JavaScript number semantics: every quantity is an integer in a
  fixed unit (whole gold, 1/1000 energy point, micro-minutes for the time of
  day, 1/8192 tile for positions, thresholds out of 2³² for probabilities), and
  `farm-sim` forbids float arithmetic outside `farm_sim::units`, whose serde
  adapters convert at the JSON boundary. Project, content, state and save JSON
  stay in authoring units; a value off the grid is rounded once when read. The
  state hash is xxh3-64 over a canonical binary encoding (still 16 hex digits,
  different values). Saves move to version 5 and projects to schema 9; both
  migrate on load, and Problems warns when a price or quantity has a fraction
  the game will round (`numbers.offGrid`). The TypeScript goldens moved to
  `fixtures/golden/v8/` as migration inputs and Rust is the reference. Before
  re-recording, every v8 golden replay was checked to give the same
  player-visible outcome at every step; the only differences are player
  positions within a tile, under 1/1000 tile, because the default speed of 4.5
  tiles per second is now exactly 1843/8192 tile per tick. Keep changes writes
  the state into the project JSON as the editor sent it, so content values
  keep what the creator typed.
- **Game interface in Spanish, and a readable font.** Every string the engine
  draws is in English and Spanish tables: the HUD, toolbar, panels, title
  screen, pause menu, settings, save slots, dialogs and messages. Players pick
  a language in Settings → Accessibility, or it follows the system language,
  then the game's locale. A "Readable font" setting switches the interface to
  Atkinson Hyperlegible (SIL OFL 1.1).
- **Welcome tour and in-app help.** The editor has a first-run welcome tour,
  and a Help menu with the Welcome Tour, a built-in Creator Guide (F1,
  docs/CREATOR-GUIDE.md), Keyboard Shortcuts and Language. Help → Language
  translates the editor's mode names, Play Mode toolbar and help into Spanish,
  and Play Mode passes that language on to the game.
- **Map placement tools.** Edit Mode places items, gathering nodes, machines,
  NPCs and animals on the map and removes them, picks a brush from a tile
  (eyedropper), lists a scene's transitions with Clear all, and lets a new
  scene start from any tile type. Problems entries about art jump to the
  asset.
- **More of the web editor.** Project Settings edits the weather odds per
  season, the mine (entrance scene and tile, floors, ladder chance; switching
  it on fills in sensible defaults) and the order of the seasons, each as one
  undo step. Actions, minigames and crops can be duplicated, and items added to
  the player's starting inventory. The art studio imports several images at
  once and removes all unused art after listing what goes. The project list
  renames and duplicates projects; a duplicate is a separate game with its own
  save folder id. The Mods view lists the curated packs that ship with the
  editor (the Glow Farm demo and the My First Mod template) and exports chosen
  content types or entries as a validated content pack JSON.
- **Benchmarks and property tests.** The new `farm-bench` crate measures the
  runtime against the LANGUAGES.md budgets: the overnight pass on a 64×64 and a
  256×256 farm, saving, loading the sample games, and a gameplay frame on the
  CPU rasterizer at 1280×800 and 1920×1080. It runs under criterion with
  `cargo bench -p farm-bench`, and CI runs its budget check in release. The
  overnight, cartridge and frame budgets are met with room to spare; saving is
  about ten times over its 1 ms target and is held to a regression ceiling
  until the stable-JSON serializer is rewritten. Property tests now cover
  replay determinism and save round trips (proptest), and project migration
  and compiling (FsCheck). The sample games also ship as test cartridges.
- **Play Mode is the real game.** The editor's Play Mode now runs the same Rust
  player as exported games: the world, HUD, dialogue, shops, crafting,
  inventory, quests, minigames, toasts and pause menu look and behave exactly
  as they will for players, including pointer clicks on the HUD. Frames run on
  a worker thread, so a slow plugin no longer stalls the editor. Restart, Keep
  changes and the debug drawer (money, energy, skip day, +1 hour, season,
  items, teleport, flags) work as before. Game sounds now play in Play Mode.
- **One engine.** The C# simulation, runtime, renderer and content projects,
  and the Jint plugin host, are gone: the game runs in Rust (`farm-sim`,
  `farm-runtime`, `farm-render`, `farm-plugins`, `farm-player`) everywhere,
  and templates come from F#. Their tests moved to the Rust and F# suites or
  to `FarmEngine.Schemas.Tests` (since folded into the F# tests with the
  records); shared fixtures live in `fixtures/projects`. The editor ships the Rust license notices.
- **Graphical player.** Exported games now open a window with a title screen,
  three save slots with previews, a pause menu, settings and credits. The game
  autosaves each morning. Keyboard, mouse and gamepads work everywhere,
  including menus, and keys are rebindable. Settings cover display, audio,
  controls and accessibility, and are kept apart from the saves. The world
  renders at its pixel-art size and scales up crisply; the UI draws at full
  resolution, and 16:10 screens such as the Steam Deck lay out cleanly. A
  crash writes a log with the recent commands. `farm-player --screenshot`
  renders frames to a PNG without a window.
- **Rust game UI.** The new `farm-ui` crate draws the HUD, dialogue, shop,
  crafting, inventory, quest log, creator panels, minigames and toasts on
  `farm-render` draw lists. Every button runs an engine command. The player
  is also a library, `farm_player::Player`, that the editor embeds for
  Play Mode.
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
  the app. Exported games run the graphical player.
- **Rust plugin sandbox.** The new `farm-plugins` crate runs content-pack
  plugins in QuickJS compiled to WebAssembly, one isolated instance per
  plugin, in the wasmi interpreter (no JIT). Budgets are deterministic fuel
  instead of wall-clock time, so the same plugins give the same results on
  every machine. Hardening, strikes, error kinds, mutation validation and the
  mutation queue match the Jint host it replaces.
- **Rust Play Mode runtime.** Frame timing, gameplay input bindings, minigame
  scoring, creator-panel values, calendar displays and sound-cue mapping now
  execute in Rust. Minigame views use mount
  tokens to reject stale input, and results pass through the command pipeline
  once.
- **Rust world snapshots.** The new `farm-render` crate builds read-only play
  snapshots from Rust state. Differential tests cover all templates, crop
  maturity/withered state, soil, machines, missing definitions/tiles, moving
  NPCs and animals. A GPU renderer is not included yet.
- **Rust renderer.** `farm-render` now holds the whole world renderer: Edit
  Mode snapshots, art decoration, the built-in art pack (moved to
  `assets/builtin-art/`), draw lists and a CPU rasterizer with embedded Inter
  fonts. `RustRender` and `RustPreview` expose it to C#; Edit Mode and Play
  Mode draw with it.
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
  and run compiled sample cartridges through Rust.
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
  F# supplies a stable game id and reports invalid settings in Problems.
- **Cartridge groundwork.** `farmc compile` now writes a deterministic
  FlatBuffers `game.cart` with game identity and compiled compatibility
  content. Rust verifies and loads it; cartridge sessions use the persistent
  game id in saves.
- **Headless player.** A standalone Rust executable loads cartridges, runs
  scripted replays, checks deterministic state hashes, and loads/writes saves.
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
  the Rust library (`farm_ffi`).
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
  toolchain, and every release). Games play exactly as before.
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
