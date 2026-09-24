# Export Game

**Status:** decided, not started (September 2026). Export is built in phase 6
of [LANGUAGES.md](LANGUAGES.md#phases), on the Rust player. This document
covers what an exported game is. It also lists what the earlier phases must
get right so that export works when it arrives.

## The goal

Export makes real desktop games that a creator can sell or give away. A player
unzips a folder and runs it. The game has a title screen, save slots, settings
and controller support, and it's ready to upload to Steam or itch.io. Export
does not make browser games.

| Target | Priority | Output |
|---|---|---|
| **Windows x64** | First | A folder (and zip) with `<Game>.exe` |
| **Linux x64**, including Steam Deck | First | A folder (and `.tar.gz`) with a `<Game>` binary |
| Web demo | Optional, after the first two | `index.html` + wasm + `game.cart`, for itch.io pages |
| macOS | Later | An `.app`; needs signing and notarization |
| Consoles, mobile | Not planned | Keep the player portable (see [open questions](#open-questions)) |

The web version's HTML export (a single file that embeds `shell.iife.js`, from
`src/lib/export-html.ts`) is **not** ported. The web demo is a different thing:
the same Rust player compiled to WebAssembly, reading the same `game.cart`.

## How export works

```
project ──▶ F# compiler ──▶ game.cart ──┐
                                        ├──▶ export folder ──▶ zip / tar.gz
player template for the target ─────────┘    (renamed exe, icon, version info,
                                              license notices)
```

- **Player templates.** On every release, CI builds `farm-player` for each
  target and publishes the builds as release assets (`player-windows-x64`,
  `player-linux-x64`, `player-web`). The installer ships the Windows and Linux
  templates, so export works offline. The web template downloads on first use,
  through the same GitHub Releases client as the Update Center. A template's
  version must match the editor's version exactly. The editor refuses a
  template that doesn't match.
- **No toolchain on the creator's machine.** Export never compiles Rust or
  .NET code. It copies files, patches metadata and writes files. That means an
  editor on Windows can export the Linux build, and the other way around.
- **The cartridge sits next to the player. It is never appended to it.**
  Appending data to an executable gets in the way of code signing (macOS
  rejects it outright). It also means every content patch re-ships the whole
  runtime, because Steam computes patches per file. The player loads
  `game.cart` from the folder its executable is in. `--cart <path>` overrides
  that for development.
- **Headless export.** `farmc export <project> --target windows-x64 --out dist/`
  does exactly what the menu command does, so creators can build their game in
  CI.
- **Reproducible.** The same project exported by the same editor version gives
  a byte-identical `game.cart`. The cartridge holds no timestamps, and archive
  entries use a fixed timestamp.
- **Errors block export.** Export runs the Problems pipeline first. Any error
  stops it. Warnings are listed in the export report.
- **Only used assets ship.** The compiler embeds the assets that content
  references. Unused assets are reported as a warning, so a game doesn't ship
  the creator's scratch art.

### Output layout

Windows:

```
WillowCreek/
  WillowCreek.exe          farm-player, renamed; export sets its icon and version info
  game.cart
  licenses/THIRD-PARTY.txt
```

Linux:

```
WillowCreek/
  WillowCreek              farm-player, renamed; executable bit set in the archive
  game.cart
  WillowCreek.png
  WillowCreek.desktop
  licenses/THIRD-PARTY.txt
```

Web demo:

```
WillowCreek-web/
  index.html
  farm_player.js
  farm_player_bg.wasm
  game.cart
```

On Windows, export writes the icon and the `VERSIONINFO` resource (product
name, version, company) into the renamed executable's PE resources, from .NET,
with no external tools. Linux executables carry no icon. The `.png` and
`.desktop` files are there for desktop launchers, and Steam uses its own store
art.

`licenses/THIRD-PARTY.txt` lists the Rust crates in the player. It is
generated with [cargo-about](https://github.com/EmbarkStudios/cargo-about)
when the template is built.

## Export settings

Export settings live in the project, in a new optional `export` object. The
field is additive and optional, so it doesn't bump the schema version. Before
relying on it, confirm that the web version keeps unknown fields when it loads
and saves a project (the native app does, through `[JsonExtensionData]`).

| Field | Example | Notes |
|---|---|---|
| `title` | `Willow Creek Farm` | Window title and title screen. Defaults to the project name. |
| `executableName` | `WillowCreek` | Must be a valid file name on every target. |
| `version` | `1.2.0` | Shown on the title screen, written to the exe's version info, and stored in saves. |
| `gameId` | `com.example.willowcreek` | Names the save folder. It is generated once and **never** changes, even when the game is renamed. |
| `author`, `company` | | Version info and credits. |
| `icon` | an asset id | PNG, at least 256×256. Export builds the `.ico`. |
| `window` | `{ width, height, fullscreen }` | Default window size and whether the game starts fullscreen. |
| `pixelScale` | `integer` or `fit` | Integer scaling keeps pixel art crisp and letterboxes the rest. |
| `credits` | text | Shown on the credits screen. |
| `targets` | `["windows-x64", "linux-x64"]` | The targets the creator exported last time. |

Validation lives in F# `Authoring.Validation`, like every other check.

## What the player must include (phase 6)

The web version relied on the browser for a lot of this. A desktop game has
to do it itself. `farm-player` gets a **game shell** (in `farm-ui`) that
surrounds the game:

- **Title screen:** New Game, Continue, Load, Settings, Credits and Quit, with
  a background and logo from the art pipeline.
- **Save slots**, each with a preview: farm name, day, season, year, money,
  play time and a thumbnail. The game autosaves when the player sleeps, as
  farming games do.
- **Pause menu:** Resume, Settings, Save and quit to title, Quit.
- **Settings**, stored in the config folder separately from saves:
  - Display: windowed or borderless fullscreen, resolution, vsync, frame cap,
    integer scaling, UI scale.
  - Audio: master, music, sound effects and ambience volume.
  - Controls: rebinding for keyboard and gamepad, through
    `farm-runtime::input` bindings.
  - Accessibility: text size, text speed, reduced screen flashing.
- **Gamepad support** with [gilrs](https://crates.io/crates/gilrs). The whole
  game is playable with a controller, including menus and inventory. Button
  prompts switch between keyboard and gamepad glyphs. Steam Deck needs this.
- **16:10 layouts.** The Steam Deck's 1280×800 screen must look right, not
  just the common 16:9 resolutions.
- **Proper user folders**, through the
  [directories](https://crates.io/crates/directories) crate:

  | | Saves | Settings |
  |---|---|---|
  | Windows | `%APPDATA%\<company>\<gameId>\saves\` | `%APPDATA%\<company>\<gameId>\settings.toml` |
  | Linux | `$XDG_DATA_HOME/<gameId>/saves/` | `$XDG_CONFIG_HOME/<gameId>/settings.toml` |

- **Crash reports.** A panic hook writes a crash log with the game version,
  the cartridge hash and the recent command log, then shows a message box.
  The simulation is deterministic, so the save plus the command log replays
  the crash exactly.

## What earlier phases must get right

Most of export is phase 6 work. These decisions come earlier, and they are
expensive to change after games have shipped:

| Phase | Decision |
|---|---|
| **1. Scaffolding** | `cart.fbs` has a `GameInfo` table (title, version, `gameId`, author, window defaults, pixel scale) and an embedded-asset table keyed by id from the start. The player never reads project JSON. The `wasm32-unknown-unknown` CI check keeps the web demo possible. |
| **2. Rust core** | **Saves store content by stable string id, never by the cartridge's interned indices.** Every compile re-interns ids, and a save from version 1.0 of a game must still load in 1.1 after the creator adds an item. Loading a save maps ids to indices. Ids that no longer exist go to the existing quarantine path (`QuarantinedItems`) instead of failing. |
| **2. Rust core** | The save header carries `gameId`, the game version, the cartridge content hash and the save format version. The player refuses a save from a different `gameId`, loads saves from older game versions, and warns about saves from newer ones. |
| **2. Rust core** | `farm-sim` does no file, environment or clock access (already a rule). The web demo depends on it. |
| **3. F# authoring** | The `export` settings schema and its validation. |
| **3. F# authoring** | The compiler is deterministic and has no dependency on the app, so `farmc export` runs anywhere. |
| **3. F# authoring** | A clear new-game start state (see [open questions](#open-questions)). |
| **6. Player** | Linux templates are built in an old-glibc container ([Steam Runtime 3 "sniper"](https://gitlab.steamos.cloud/steamrt/sniper/sdk)) so they run on older distributions and on Steam Deck. |

## Tests

- CI exports every sample game for each target. It then runs the exported
  player with `--headless --replay <file>` (Windows builds on a Windows
  runner, Linux builds on a Linux runner) and compares the final state hash
  with the golden.
- Screenshot tests of the title screen, settings and a gameplay frame at
  1280×800 and 1920×1080. They must match between the embedded, standalone
  and web players (the phase 6 exit criterion).
- **Save compatibility:** a save fixture from version 1 of a test game still
  loads after content is added to and removed from that game.
- **Reproducible export:** exporting a fixture project twice gives identical
  bytes.

## Notes for creators

This material goes in the in-app help later.

- **Steam:** upload the Windows and Linux folders as separate depots. Steam
  Deck runs either one: the Linux build natively, or the Windows build through
  Proton. Steam Cloud can sync the save folder with Auto-Cloud (configured on
  Steam's side), with no SDK in the game.
- **itch.io:** upload the zips with [butler](https://itch.io/docs/butler/),
  plus the optional web demo.
- **Code signing:** exported Windows games are unsigned, so SmartScreen warns
  players until the creator signs `<Game>.exe` with their own certificate.
  Export never signs. Signing after export works because the cartridge is a
  separate file.

## Open questions

- **Steamworks.** Achievements, the Steam overlay and Steam Input need an
  optional `steam` feature in `farm-player`
  ([steamworks-rs](https://crates.io/crates/steamworks)). That needs a
  template variant that ships Valve's redistributable library, plus authored
  achievements in the project. Decide after the first export ships.
- **Start state vs. playtest state.** Today the project mixes the authored
  start state with state kept from playtests (`ApplyStateToProject` writes the
  day, season, animals, friendships and more back into the project). An
  exported game needs an explicit "a new game starts here" state, so that a
  kept playtest doesn't ship as the opening of the game. Decide in phase 3,
  when the F# schema is designed (possibly in v9).
- **Player-installed mods** for exported games: see
  [LANGUAGES.md](LANGUAGES.md#open-questions).
- **Plugin sandbox without a JIT.** wasmtime compiles with a JIT by default,
  which is fine on Windows, Linux and macOS. Consoles and iOS forbid JITs.
  wasmtime's Pulley interpreter avoids them. Keep `farm-plugins` independent
  of which wasmtime backend is used, so that choice stays a build flag.
- **Web demo limits.** Should the web demo target offer a "the demo ends after
  day N" option?
- **macOS.** It needs a Mac (or [rcodesign](https://github.com/indygreg/apple-platform-rs))
  for signing and notarization, and each creator needs their own Apple
  Developer account. Revisit after Windows and Linux ship.
