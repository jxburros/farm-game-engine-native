# The game player

`farm-player` runs a compiled `game.cart`. It is the program inside every
exported game ([EXPORT.md](EXPORT.md)), and the same player runs the editor's
Play Mode through `farm-ffi` ([below](#in-the-editor)). It has three faces:

- **The game.** `farm-player` opens a window with a title screen, save slots,
  a pause menu, settings and credits around the game. Keyboard, mouse and
  gamepads work everywhere, including menus.
- **Headless.** `--headless` replays commands without a window and prints a
  JSON report (CI and export checks use it).
- **Screenshots.** `--screenshot` renders frames without a window and writes a
  PNG (tests and store pages).

`game.cart` beside the executable is the default cartridge; `--cart` picks
another one.

```sh
cargo run -p farm-player -- --cart game.cart
cargo run -p farm-player -- --headless --cart game.cart --replay replay.json --save slot1.sav
cargo run -p farm-player -- --screenshot title.png --cart game.cart --size 1280x800 --frames 60
```

## Flags

| Flag | What it does |
|---|---|
| `--cart <file>` | The cartridge (default: `game.cart` beside the executable). |
| `--fullscreen`, `--windowed` | Start this way for this run; the setting is saved. |
| `--exit-after-frames <n>` | Close after `n` frames (smoke tests). |
| `--headless` | No window: load, replay, report (see below). |
| `--replay <file>`, `--load <save>`, `--save <save>` | Headless replay input, incoming save and outgoing save. |
| `--screenshot <out.png>` | Render without a window and write the last frame. |
| `--size <W>x<H>` | Screenshot size (default 1280x800). |
| `--frames <n>` | Frames to run before the screenshot, 60 per second (default 60). |
| `--script <file>` | Scripted input for the screenshot run (below). |

A screenshot run uses in-memory saves and settings and a fixed clock, so the
same arguments always give the same image. A script lists input by frame:

```json
{ "steps": [
  { "at": 2, "press": "enter" },
  { "at": 30, "events": [{ "type": "keyDown", "key": "d" }] },
  { "at": 60, "events": [{ "type": "keyUp", "key": "d" }] },
  { "at": 70, "button": "south" },
  { "at": 80, "click": [640, 400] }
] }
```

`press`, `button` and `click` go down on their frame and up on the next.

## Controls

| Keyboard | Gamepad | Action |
|---|---|---|
| WASD / arrows | Left stick / D-pad | Move (diagonals too); move between buttons in menus |
| E / Space / Enter | A | Interact; press the focused button |
| Q · T · R · F · C | X · Y · LB · RB · LT | Watering can · Hoe · Axe · Pickaxe · Scythe |
| X | RT | Crafting |
| Z | L3 | Sleep |
| I | View | Inventory |
| J | R3 | Quests |
| Esc | B / Menu | Close a panel, dialogue or shop; open the pause menu |
| 1–9 | | Pick a dialogue option |
| Tab / Shift+Tab | RB / LB | Next / previous tab in the shop and settings |
| F11, Alt+Enter | | Fullscreen |

The mouse clicks every button, and the wheel (or the right stick) scrolls
lists. The toolbar at the top has Inventory, Quests, Craft, Sleep and Menu.

Planting: **Hold** on a seed or a fertilizer in the inventory picks what
interacting with open soil uses (press it again to put it away). Without a held
seed the first seed that grows this season is planted, and fertilizer is only
used when one is held. Interact presses then go to the engine as `interactWith`
commands.
The hint row at the bottom shows the keys of the device used last: keyboard
keycaps, or gamepad buttons once a pad is used.

Keyboard keys are rebindable in **Settings → Controls**: press a key's button,
then the new key (Esc cancels). A key bound elsewhere moves to the new action.
The engine still sees its default keys: the player translates a pressed key to
the key the action has by default, and swallows default game keys that were
rebound away. Creator hotkeys and digits pass through unchanged.

## Settings

Settings are stored apart from the saves, as TOML:

| | Settings | Saves |
|---|---|---|
| Linux | `$XDG_CONFIG_HOME/<gameId>/settings.toml` | `$XDG_DATA_HOME/<gameId>/saves/` |
| Windows | `%APPDATA%\<company>\<gameId>\settings.toml` | `%APPDATA%\<company>\<gameId>\saves\` |

Without a company the Windows paths skip that folder. `FARM_PLAYER_USER_DIR`
puts both under one folder (tests, portable installs). Every field has a
default, so a partial or older file loads; an unreadable file is ignored.

- **Display:** fullscreen (borderless) or windowed, integer scaling (whole
  pixel steps, letterboxed) or fit, interface size (75–150 %).
- **Audio:** master, music, sound effects, mute. Sound effects are the
  synthesized farm-runtime presets; there is no music content yet.
- **Controls:** keyboard bindings and the gamepad layout.
- **Accessibility:** text size (90–140 %), reduced motion (no floating pops,
  fades or flashes), a readable font (Atkinson Hyperlegible, SIL OFL 1.1, for
  the interface; text drawn in the world stays in Inter) and the language
  (English or Spanish). Until the player picks a language the game follows
  the system's, then the game's `settings.locale`, then English; in the
  editor's Play Mode the editor's language comes first.

The first run takes fullscreen and integer scaling from the cartridge's
`GameInfo` (`window.fullscreen`, `pixelScale`).

## Saves

The game has three save slots. **New Game** starts in the first free slot (or
asks for one), and the game **autosaves each morning** to its slot, as farming
games do. The pause menu saves to any slot (another game's slot asks first) and
loads any slot. **Continue** on the title screen loads the most recent save.

A save is the binary `FGSV` format (`schemas/save.fbs`): a header (game id,
game version, cartridge hash), a preview the slot list reads without loading
the game (farm name, day, season, year, money, play time, when it was saved and
a thumbnail of the scene) and the zstd-compressed state. A save from another
game is refused; one from a newer version loads with a warning; items the game
no longer has go to quarantine. Files are written through a temporary file and
a rename, so a crash never leaves half a save.

## Crash logs

A panic writes `crash-<time>.log` next to the saves and prints its path. The
log has the player version, the game and its version, the cartridge hash, the
slot, tick, day and scene, the state hash, the last 64 commands with their
ticks and recent plugin errors. The simulation is deterministic, so the last
save plus that command log replays the crash.

## Headless replays

`--headless` verifies the cartridge, creates the initial state or loads a
compatible save, applies a replay log, and prints a JSON report with the game
id, version, content and state hashes, effects and save warnings.

```json
{
  "seed": "optional-seed",
  "autoStartQuests": false,
  "inputs": [
    { "kind": "tick", "ticks": 20 },
    { "kind": "command", "command": { "type": "sleep" } }
  ],
  "expectedHash": "optional-16-character-hash"
}
```

The player exits with an error for a malformed cartridge, an incompatible
save or a replay hash mismatch. `--save` writes a binary save; a path ending in
`.json` writes the JSON form instead. `--load` reads either, and bare web
`GameState` JSON too. The checked-in cartridge and replay in
`fixtures/golden/cartridges` are exercised on Windows and Linux.

## Embedding the player

The player is a library first. `Player` owns a `PlaySession`, the in-game UI
(`farm-ui`), the shell's screens, a save store, a settings store and the
renderer. It needs no window, clock or OS access, and it is `Send`, so a host
can step it on a background thread and hand the pixels to its UI thread.

```rust
let mut player = Player::from_cartridge_bytes(&cart, PlayerOptions::standalone())?;
loop {
    let out = player.frame(dt_seconds, &events, width, height)?;
    // out.pixels: premultiplied RGBA; out.sounds; out.requests (Quit, SetFullscreen, SetTitle)
}
```

- `PlayerMode::Standalone` shows the title screen first. `Embedded` (the
  editor's Play Mode) starts in the game and has no title screen, save slots,
  autosave or quitting; its pause menu has Resume and Settings.
- `Player::from_cartridge`, `from_cartridge_bytes` and `from_project` (an
  editor project, through `create_content_from_project`,
  `StartState::from_project` and `Presentation::from_project`).
- For the editor: `state()`, `session()`, `debug(DebugAction)`,
  `replace_state`, `run_command`, `synced_project()` ("keep changes" when
  started from a project), `plugin_errors()`, `settings`/`set_settings`.
- `step` is `frame` without pixels. Engine panics come back as
  `PlayerError::Engine`; the player then refuses further frames.
- `InputEvent`: key down/up (farm-runtime key names), text, pointer
  move/down/up/leave, wheel, gamepad buttons and axes, focus lost.
- Stores: `FsSaveStore`/`FsSettingsStore` (files) and
  `MemorySaveStore`/`MemorySettingsStore` (tests, the editor).

The crate's `desktop` feature (on by default) adds the window: winit and
softbuffer, gilrs gamepads, cpal audio and the user folders. farm-ffi and the
WebAssembly build use the library without it; farm-ffi turns on `audio-out`
alone (cpal through `speaker::SpeakerThread`, which keeps the stream on its own
thread so the player can move between threads).

## In the editor

Play Mode embeds an `Embedded` player through `fe_player_*`
(`crates/farm-ffi/src/player.rs`, `RustPlayer` in `FarmEngine.Interop`):

- `fe_player_new` takes a cartridge (or project JSON) and
  `{seed, reducedMotion, uiScale, audio, locale}`. Play Mode passes the
  cartridge the F# compiler makes for the playtest
  (`CartridgeCompiler.CompileForPlaytest`: the export bytes, even while
  Problems still lists errors), so a playtest runs what Export Game ships;
  `locale` is the editor's language, which the game interface follows until
  the player picks one in Settings.
- `fe_player_frame` takes `{dt, events, width, height, render}` and returns
  the frame size, a small JSON block (sounds, requests, screen, whether a
  panel or modal is open) and the premultiplied RGBA pixels.
- `fe_player_debug` runs the debug drawer's actions (`DebugAction`),
  `fe_player_state_json` feeds "keep changes" (F# `Playtest.applyState`
  writes the state back into the project; `fe_player_synced_project` does the
  same in Rust for a player started from project JSON), and `fe_player_query_json`
  answers the drawer's summary, widget rectangles, toasts and plugin errors
  (the last three for tests).
- An engine failure or panic poisons the handle: every later call fails, and
  the editor ends the playtest without keeping changes.

`PlayModeView` renders at the surface's device pixels (at most 1920×1200;
larger surfaces are scaled up), maps Avalonia keys to the engine's key names
and pointer positions to frame pixels, and runs each frame on a worker thread
under one lock, so plugin hooks never block the editor. Editor shortcuts
(Ctrl/Alt combinations, F5/F6) stay with the editor.

## How a frame is drawn

1. Input is routed: bound keys become the engine's keys, and the UI gets
   navigation, pointer and raw keys.
2. The session steps (fixed timestep) unless a menu pauses the game.
3. `farm-render` builds the world snapshot; `farm-ui` draws the HUD, panels
   and shell screens into a draw list.
4. The world is rasterized at its native pixel-art size (the camera viewport,
   32-pixel tiles, one pixel larger for smooth scrolling) and scaled up with
   nearest neighbour: whole steps with integer scaling, else fitted;
   letterboxed when the scene is smaller than the window.
5. The UI is rasterized at full resolution on top, so text stays crisp.

The UI scales with the window (1× at 1280×800, 1.35× at 1920×1080) times the
interface size setting. 16:10 (Steam Deck) and 16:9 both lay out without
overlap; the HUD drops its labels and then wraps when space runs out.

Frame times in a release build (starter farm, one thread, a 2.1 GHz Xeon
cloud core), for the whole frame: input, UI, world and rasterizing:

| Screen | 1280×800 | 1920×1080 |
|---|---|---|
| Title screen | 3.6 ms | 4.0 ms |
| Gameplay, walking | 3.1 ms | 4.3 ms |
| Shop open | 5.5 ms | 8.8 ms |

The UI keeps these low by drawing text from cached glyph sprites, filling
rectangles with direct pixel loops, dropping commands outside the screen or
clip, and dimming the world at its native size instead of the full frame.
The largest remaining cost is a modal's soft shadow (about 2 ms at 1080p).

## Tests

- `crates/farm-ui`: layout, focus navigation (keyboard and gamepad), clicks
  running the right commands, disabled states, wrapping, scaling.
- `crates/farm-player/tests/player.rs`: title → new game → walk → sleep →
  autosave → quit to title → continue (same state hash), slot previews,
  settings round trip, pause, embedded mode, plugins, a minigame, a gamepad.
- `crates/farm-player/tests/screenshots.rs`: goldens of the title screen,
  settings, gameplay HUD, dialogue and shop at 1280×800 and 1920×1080, stored
  at half size in `fixtures/player/`. Bless with `FARM_PLAYER_BLESS=1`; write
  full-size frames with `FARM_PLAYER_SCREENSHOTS=<dir>` (the ignored
  `review_screens` test writes every other screen too).
- `crates/farm-player/tests/binary.rs`: `--screenshot`, and the real window
  under `xvfb-run` for a few frames (skipped without Xvfb).
- `crates/farm-player/tests/bench.rs` (ignored): frame times, run with
  `cargo test --release -p farm-player --test bench -- --ignored --nocapture`.
