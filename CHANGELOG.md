# Changelog

## 0.3.0 (unreleased)

- **Calendar, clock and save fixes.**
  - **Games can start in any season.** The clock keeps its own day of season,
    so every season lasts its configured length and festivals, birthdays and
    the HUD date follow it, also after season lengths change under a save.
    Problems says which day a start that doesn't line up with the calendar
    becomes.
  - **Day window checks.** A day that ends before it starts (or within an
    hour of it), ends after minute 4294, or a clock faster than 1440 minutes
    per second is an error; the engine no longer collapses the player on
    every tick when one slips through. One invalid setting now replaces only
    itself instead of every setting.
  - **Indoor scenes.** Mark a scene **Indoor (no weather)** and rain and storms
    leave it alone; mine floors are indoor.
  - **The clock pauses in dialogue, shops, minigames and menus** (Project
    Settings, on by default). Sleeping closes an open minigame, time-of-day
    conditions work after midnight and may wrap past it, and NPCs keep up at
    fast clock rates.
  - **Keep changes keeps everything.** Number and text flags, the tick, an
    open dialogue, shop or minigame, today's purchases, walking NPCs and the
    mine floor carry into the next playtest, and project keys keep their
    order.
  - **Game updates reach old saves.** A save from another version of the game
    takes its fixed maps, new doors and scenes while keeping the player's
    crops, soil, nodes and machines, and NPCs added in the update join the
    world. A save without a random state gets a new one, and a save/load round
    trip no longer changes which NPC you talk to or the state hash.

- **Authoring core hardening.**
  - **Hostile project files are refused, not crashes.** Deeply nested JSON is
    an error instead of a stack overflow that killed `farmc` and the editor,
    and a number like `1e400` is refused when the project loads instead of
    being saved as `null` (after which the project no longer opened). Loaders,
    the cartridge reader and the web API report every failure in their
    results.
  - **Deleting things no longer unlocks what they gated.** Removing an item,
    NPC, quest or season keeps the conditions, required items, prerequisites,
    unlocks and season lists that named it: the gated content stays locked and
    Problems lists each reference to fix. Before, winter-only stock became
    available all year when "winter" was deleted.
  - **Dialogue copies are reconciled.** Projects whose NPC dialogue and project
    dialogue list differ (the web Romance pattern) load with the NPC's copy,
    the one the game plays, and saving in the Dialogue editor no longer drops
    options that only the NPC's copy had.
  - **Problems** also lists empty ids, inverted regions and day or year
    ranges as warnings, reports each dangling dialogue link once, and gives
    numeric weather-table seasons readable paths. Reordering content packs
    with a duplicated id no longer throws or loses a pack.
  - **`farmc`** takes options in any order, has `--help` and `--version`,
    exits 2 with a specific message for a wrong command line and 1 (never a
    stack trace) for any failure.
  - **F# and Rust stay in step.** New parity tests check that the Rust engine
    reads every field the F# records write and that the editor's previews and
    exported cartridges compile the same content; they found and fixed pack
    scenes whose grids only Rust repaired.

- **A world that can't be broken from outside.** Doors and warps land on the
  nearest walkable tile, tile grids that don't match their scene are repaired
  when a game or save loads, action chains stop after 256 actions, mine floors
  are at most 256 tiles a side, fast players no longer pass through walls, and
  a full inventory no longer swallows a door. Players can pick machines back
  up, and machines stay off doors and the mine entrance. Commands apply only
  where a player could give them (hosts and scripts can opt out), and scenes
  of enabled content packs join the world. Problems reports the grids, loops,
  blocked landings and sizes the game would have to fix.

- **Fuzz targets and invariant tests, from the 2026-09-30 audit.** The new
  `farm-fuzz` crate feeds arbitrary and edited bytes to every place where
  outside input reaches the engine (cartridges, saves, save migrations,
  images, project JSON, render, preview and session requests, plugin
  mutations) and checks after every command and tick that money stays
  non-negative, energy in range, stacks and slots within their caps and the
  player on the grid, under generated content. `cargo test` runs it on
  stable; `fuzz/` runs the same targets under cargo-fuzz (an optional weekly
  workflow). What it found is fixed: a new game whose project starts the
  player beside the scene (or with a collision box over its edge) could walk
  off the map, so the player now starts on the nearest tile; a starting
  inventory over its stack sizes or slot count goes to quarantine and comes
  back when there is room; negative starting money and energy above the
  maximum are clamped; and an item stack held from before its stack size
  shrank no longer grows past the new size.
- **Exported games, from the 2026-09-30 audit:**
  - Windows games open no console window: Export marks the executable as a
    GUI program (the template stays a console program for `--headless`).
    Windows builds link the C runtime statically, so games and the editor's
    engine library start without the VC++ redistributable; CI checks both.
  - Saves are flushed to disk before they replace the old file, use a
    temporary file of their own per running copy, and keep the previous save
    as `slot<N>.bak`, which loads (with a message) when a save is damaged.
  - Closing the window during a game asks first, like Quit. When the game
    stops with an error or its cartridge doesn't load, the window shows what
    happened and where the crash log is instead of vanishing.
  - Crash logs are never written through an existing file or link, get their
    own name per run, say how old their game report is, and only the newest
    ten are kept.
  - Sound comes back after headphones are unplugged or the default output
    device changes, the audio callback no longer allocates, and every sample
    format a device offers works.
  - A gamepad can cancel the key-rebind prompt (B or Start), and the game
    ignores the gamepad while its window is in the background.
  - Toasts stay long enough to read (longer for long text and errors; point
    at one to hold it, click to dismiss), a long dialogue scrolls inside its
    card, and Play Mode warns when the game's text has characters the fonts
    can't draw.
  - A frame builds only the tiles the camera shows (a 256×256 farm costs
    about what the starter farm does), text that fades no longer re-renders
    its glyphs every frame, and clips no longer rebuild a full-screen mask.
  - Smaller fixes: a NaN frame time can't stall the simulation, save folders
    avoid Windows device names (`Con`) and overlong names, and scripted input
    at the last frame index no longer overflows.

- **Safer pack plugins** ([docs/PLUGINS.md](docs/PLUGINS.md)):
  - **Mutation capabilities.** A pack manifest declares what its plugins may
    do (`permissions.mutations`: `message`, `giveItem`, `giveItem:any`, `*`,
    …), and the install review shows it. Plugins name only their own pack's
    items, quests, flags and so on unless a capability says `:any`; plain ids
    resolve to the pack (`setFlag` `met` sets `pack:met`). No plugin may
    write `event:` flags. Packs that declare nothing can show messages, play
    sounds and use their own flags and items, and their answers to
    `onEffect`/`onCommand` are ignored.
  - **No feedback loops.** Steps caused by plugin mutations no longer fire
    `onCommand` or `onEffect`, chains of plugin reactions stop after three
    links, and an answer keeps at most 64 mutations. A plugin that echoed its
    own effects could freeze the game and the editor.
  - **Frame-rate independent.** Play advances one tick at a time and plugin
    answers apply right before the next tick, so the same input gives the
    same game at any frame rate.
  - **Budgets.** Each plugin has a fuel budget per second of game time;
    strikes no longer reset on a success (three within 100 calls disable a
    plugin), restarts are capped and cheaper, and a game runs at most 32
    plugins within a startup fuel and memory budget. Ids, flag values, new
    flags and skills, answers and error messages have size limits.
  - **Pack permissions that work.** `contentInject: false` now keeps a pack's
    content out (with a Problems warning); `uiPanels` is marked as reserved.
    The Problems panel warns about unknown capabilities and about plugins
    listening to `onWeatherRoll`, which they cannot answer in time.
- **NPC birthdays follow the calendar.** A birthday in a custom season no
  longer stops the project from loading; the Problems panel warns when the
  season is not in the calendar or the day is past its end.
- **Export Game and imports, from the 2026-09-30 audit:**
  - Export never throws: every failure, expected or not, is a sentence in the
    report for the target it hit, the dialog shows it, and `farmc export`
    prints it. Executable names are at most 64 characters and can't be
    `licenses`; a name made from the title avoids reserved names (`con-game`).
  - Each target is written under temporary names and renamed into place, so
    a failed or cancelled export leaves the previous one intact. Export never
    writes through a link in the output folder, and `.DS_Store`, `Thumbs.db`,
    `desktop.ini` and `.directory` no longer block a re-export.
  - Closing the Export Game window during an export cancels it and closes
    once it has stopped; the result is never lost.
  - A signed Windows template has its signature removed rather than shipping a
    broken one. Patching only ever rewrites the resource section, and every
    template file (the web page files too) is checked against `template.json`
    and exported from the checked bytes.
  - Content packs exported from the Mods view carry the art their entries use,
    and installing a pack adds it to the project's art.
  - Images inside imported projects and packs, and the export icon, are
    checked against the art import limits from their header before they are
    decoded; content form thumbnails decode small, once per image, and are
    freed. Picked project files over 256 MB and packs over 64 MB are refused,
    and Export Project JSON replaces the file atomically.
  - The Art tab's preview redraws only while an animation plays, into the same
    bitmap.
  - Shared save files can't exhaust memory: a save's state may be at most
    64 MiB (it was 256 MiB), slot files are read only that far, and loading
    no longer copies the state several times. Save-slot thumbnails larger
    than 512 pixels aren't decoded, and refreshing the slots no longer adds a
    thumbnail to the image cache every time.
- **The editor's engine library, from the 2026-09-30 audit:**
  - The editor loads `farm_ffi` from its own folder only, refuses a library
    built from other sources (it checks the library's ABI version), and says
    why the engine isn't available (a missing system library, for example)
    instead of "not available in this build". On Linux, `dotnet build
    -p:FarmFfiAudio=false` builds an engine library that runs without ALSA
    (Play Mode is then silent).
  - Play Mode copies each frame once, straight into the screen bitmap (it was
    copied three times). Painting the map sends only the changed scene to the
    renderer, and only when the map is on screen; scrolling reuses the drawn
    scene.
  - Every engine handle reports errors the same way, freeing one can't crash
    the editor, and using one after it was closed throws a clear error.
  - Tests that need the Rust library fail when it's missing (also with
    `-p:CargoProfile=dev`); `FARM_ALLOW_MISSING_NATIVE=1` skips them instead.
- **License, notices and contributor docs.** The repository has an MIT
  `LICENSE`. The app ships it in its `licenses` folder with notices for its
  .NET packages, the runtime, icons and fonts (`THIRD-PARTY-dotnet.txt`), and
  About links to it. Exported games' `THIRD-PARTY.txt` now also covers the
  plugin sandbox (QuickJS, wasi-libc) and ends with the engine's own license.
  The in-app guide opens the docs of the installed version. New:
  CONTRIBUTING.md (fixture switches, generated files), SECURITY.md, a README
  Troubleshooting section, and CI checks for C# style, text files, Markdown
  and the editor's notices.
- **The last web editor features, ported.** Everything the web editor did that
  the native one didn't:
  - **Keyboard map editing and screen readers.** The map takes the keyboard:
    arrow keys move a gold editing cursor, Enter or Space uses the current tool
    there (Rectangle and Select take a press per corner, Esc cancels), and a
    live status line announces the tile. Editor controls have screen-reader
    names, and a test walks every tab and content form to keep it that way.
  - **Touch controls in the web demo.** On phones and tablets the page shows a
    D-pad with Interact, Sleep, Inventory and Menu. They send the new `action`
    input event (a game action held or let go), so they keep working when a
    player rebinds keys.
  - **Profit readouts.** Recipes show their profit per craft and per hour of
    machine time (in the list too); crops show their profit per harvest and a
    summary card. They update as you type. The numbers come from F#
    (`Readouts`).
  - **Errors while editing.** An unexpected error shows an error screen with
    **Try Again** and **Undo last change and try again** instead of closing the
    app, and a playtest running at the time is discarded. A save that fails
    (full disk, no permission) no longer crashes the autosave: a banner says
    why and offers **Retry save**, and the changes stay open.
  - **Art studio.** Click cells of the sprite sheet to add frames, see
    thumbnails in the asset list, pause and play the preview, and build one
    animation from frames of several images.
  - **NPC portraits.** The game's dialogue box shows the NPC's art (its visual,
    custom image or built-in look) instead of a generic icon.
  - **Friendlier content forms.** NPC schedules, patrol waypoints and shop
    stock have their own row layouts; crops have Basic, Growth and Asset tabs;
    the skill level curve is a list of levels instead of a JSON box. The crop
    and node type lists include the built-in ones (read-only, with
    **Customize**), and every list and art field shows thumbnails. Deleting a
    crop or node type that replaced a built-in one now brings the built-in back
    with its seeds, planted crops and placed nodes, instead of clearing them.
  - **Interface panels** pick items and actions from dropdowns.
  - **Smaller things:** the Workshop's "Build your game" links, duplicating a
    single door, dialogue and asset counts in the project stats, and the scene
    size calculator.
- **Editor data safety.** Edits that exist only in memory are no longer lost:
  - Closing the window, **Restart & install** or opening another project while
    saving fails asks first: **Export Project JSON…**, **Retry save**, or go on
    without the edits.
  - Project and settings files are flushed to disk before they replace the old
    file, the previous version is kept (`projects/backups/`,
    `settings.json.bak`), opening an older project keeps the original file
    (`backups/<id>.v<from>.json`), and leftover temp files are cleaned up.
  - A project file changed outside the editor is never overwritten silently:
    the banner offers **Keep my version** or **Load the file's version**. A
    project open in one editor window can't be opened (or deleted) in another.
  - Hand-copied project files keep saving to their own file, whatever id the
    JSON inside says (web exports all say `project-1`); ids map one-to-one to
    file names on every platform.
  - A settings.json that can't be read is never rewritten (so the Update
    Center's and the editor's settings can't wipe each other), and both live in
    the data folder `FARMING_RPG_MAKER_DATA_DIR` points to.
  - Renaming the open project during a playtest with **Keep changes** keeps the
    new name; "Created"/"Imported" and form confirmations no longer claim a
    save that failed.
  - **Try Again** on the error screen can no longer close the app, and replaced
    editors stop following the project. Errors go to a rolling log
    (`logs/editor-*.log`, Help → About → Log files), unhandled errors on other
    threads are logged, and projects that fail to load at startup are listed
    once the window has opened.
- **One engine for the web and native editors.** The web editor
  (`jxburros/farm-game-engine`, private)
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
  `Preview` and `renderJson`, `hashState` (the state hash of a state as JSON),
  and `sfxSamples` to play the synthesized sound effects with WebAudio. Save slots and settings are kept in
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
  the browser's storage, and plays sounds through WebAudio. The page sets a
  Content-Security-Policy (nothing inline, only its own files), lays the game
  interface out in CSS pixels on high-density phones, keeps the HUD, panels and
  dialogue above its touch controls (which show no keyboard hints), and says
  when the browser's storage is full instead of losing saves quietly.
  `farmc export --target web` does the same, and releases ship the web
  template with the app.
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
- **Safer releases and builds.** A release must come from a commit on `main`
  that passed CI and carries the release version, and a published version is
  never overwritten. Only the final publish step can write to the repository,
  after a reviewer approves it; it attaches `SHA256SUMS` and a build
  provenance attestation. Before publishing, a sample game is exported and
  replayed with the very Windows and Linux templates the release ships.
  Actions, the Steam Runtime image, rustup, the Rust toolchain and the .NET
  SDK are pinned; shipped binaries are built from `Cargo.lock` and contain no
  build machine paths; the checked-in plugin sandbox builds byte-identically
  anywhere and CI rebuilds it. CI also runs the .NET tests on Windows,
  measures coverage, checks generated code and dependency advisories, and
  Dependabot keeps dependencies current. Golden re-records can no longer pass
  as test runs (docs/NUMERICS.md "Goldens").

## 0.2.0

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
- **Gameplay fixes (inventory, crafting, farming, quests, dialogue):**
  - Stacks respect `maxStack` everywhere (0 means no cap): adds top up every
    slot of the item, then open capped slots while there is room, and say how
    much fit. Selling, pack starting items, restored save items and the
    debug drawer's "give" buttons follow the same rules.
  - Crafting is all or nothing: a craft whose outputs don't fit uses no
    ingredients and counts for nothing. Craft objectives count the items made,
    by hand or collected from a machine.
  - Planting: the inventory's **Hold** button picks the seed and fertilizer
    to plant with (`interactWith`); fertilizer is no longer used unasked.
  - Multi-tile crops (pumpkin, cauliflower) are one crop: one harvest, one
    watering, fertilizer under every tile, cleared and storm-damaged whole,
    and they no longer fit over machines, items or rocks.
  - Harvest quality rises with farming skill and stays on the items: a
    silver, gold or iridium stack sells at its quality's price, which the
    harvest message now shows. Crops can name their harvest item
    (`harvestItemId`); Problems reports a crop without one.
  - Dialogue options can be once-only (`once`) or hidden by a flag
    (`hiddenIfFlag`); `eventFlag` and `takeMoney` now work, the editor makes
    new reward options once-only, and Problems warns about repeatable
    rewards. An option's action can open another conversation.
  - Quests: `rewards.experience` grants skill XP (to `rewards.skill`,
    farming by default), repeatable quests can be started again (auto-start
    ones restart the next morning), and a partly lost reward says how much.
  - Saves keep tool wear across game updates and refresh items lying on the
    ground; `waterArea` waters like the watering can; `modifyEnergy`
    saturates.

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
