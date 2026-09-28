# Export Game

**Status:** Export Game works (September 2026). **File → Export Game…** in the
editor and `farmc export` build the Windows and Linux folders and archives
described below. See [What is implemented](#what-is-implemented). The games
still run the headless player: the graphical shell, title screen and
controller support are phase 6 work
([LANGUAGES.md](LANGUAGES.md#phases)). When that shell lands in `farm-player`,
exported games get it with no change to export. This document covers what an
exported game is, how export builds it, and what the earlier phases must get
right.

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
  target and publishes the builds as release assets (`player-windows-x64.zip`,
  `player-linux-x64.tar.gz`, and later `player-web`). The installer ships the
  Windows and Linux templates, so export works offline. The web template will
  download on first use, through the same GitHub Releases client as the Update
  Center. A template's version must match the editor's version exactly. The
  editor refuses a template that doesn't match. See
  [Player templates](#player-templates).
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
  CI:

  ```sh
  farmc export my-game.json --target windows-x64 --target linux-x64 --out dist
  farmc export my-game.json --out dist --no-archive --templates path/to/players
  ```

  Without `--target` it exports the project's saved targets. It prints the
  report and exits with 1 when anything failed.
- **Reproducible.** The same project exported by the same editor version gives
  a byte-identical `game.cart`. The cartridge holds no timestamps. Archives
  sort their entries, use one fixed timestamp (1980-01-01), no owner names and
  a fixed gzip header, so two exports on the same machine are byte-identical.
  Icons are the exception across machines: Skia and zlib may pick CPU-specific
  code paths, so icon bytes (and the files holding them) can differ between
  machines. The cartridge never does.
- **Errors block export.** Export runs the Problems pipeline first. Any error
  stops it. Warnings are listed in the export report.
- **Only used assets ship.** The compiler should embed only the assets that
  content references. Export already reports unused assets as warnings (an
  asset is used when anything in the project outside the asset list names its
  id or data URL, when it is the art of a tile type in use, or when a used
  asset's frames draw from it). The cartridge still embeds every asset for
  now; the report says so, and [ROADMAP.md](../ROADMAP.md) tracks the fix.

### Output layout

Each target gets its own folder under the output folder, and an archive next
to it:

```
dist/
  windows-x64/WillowCreek/        the Windows game folder (below)
  linux-x64/WillowCreek/          the Linux game folder (below)
  WillowCreek-windows-x64.zip     entries under WillowCreek/
  WillowCreek-linux-x64.tar.gz    entries under WillowCreek/
```

Export refuses to write into a game folder that holds files it wouldn't
write (a creator's notes, say), so a mistyped output folder loses nothing.
Exporting again over its own earlier output replaces it.

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

`licenses/THIRD-PARTY.txt` lists the Rust crates in the player with their
license texts. [cargo-about](https://github.com/EmbarkStudios/cargo-about)
generates it (`tools/player-licenses/generate.sh`, checked in; CI fails when
it is stale). Every template ships it and export copies it.

The `.desktop` file starts the game from the folder it sits in
(`Exec=sh -c "exec \\"\\$(dirname \\"\\$0\\")/WillowCreek\\"" %k`), which works in
launchers that pass the file's location. Its `Icon=WillowCreek` works once the
game is installed with its `.png`. The file passes `desktop-file-validate`.

### Player templates

A template is a folder per target with the player, its manifest and the
license notices:

```
players/windows-x64/farm-player.exe   players/linux-x64/farm-player
players/windows-x64/template.json     players/linux-x64/template.json
players/windows-x64/THIRD-PARTY.txt   players/linux-x64/THIRD-PARTY.txt
```

`template.json` is `{ "target": "linux-x64", "version": "0.2.0", "sha256": "…" }`.
Export refuses a template for another target, another editor version, or with
an executable that doesn't match the checksum. A missing template fails that
target only; the other targets still export.

Export looks in `--templates` (farmc) or `ProjectCommandHandler.PlayerTemplatesFolder`
(the app), then `FARM_PLAYER_TEMPLATES`, then `players/` next to the running
executable. The release workflow builds both templates (the Linux one in the
Steam Runtime "sniper" SDK container), ships them in the app's `players/`
folder through `-p:FarmPlayerTemplatesDir`, and attaches them to the release
as `player-windows-x64.zip` and `player-linux-x64.tar.gz`
(`tools/player-templates/package.sh` stages them). Development builds get the
host template from the `FarmPlayerTemplates` target in
`src/FarmEngine.Export/FarmEngine.Export.fsproj`, which runs
`cargo build -p farm-player` and writes `template.json` with the build's
version. The app, farmc and the tests all get it in their output.

### Windows icon and version info

Export rewrites the icon and `VERSIONINFO` without a resource compiler, and
without moving anything in the file:

1. **Placeholders in the template.** On Windows targets, `crates/farm-player/build.rs`
   compiles a resource script with `embed-resource` (`rc.exe` on MSVC,
   `windres` on GNU). It holds icon group 1 with four PNG icon slots (16, 32,
   48 and 256 pixels) and a `VERSIONINFO` with nine 200-character strings.
   Each icon slot is a 1×1 PNG padded with zeros to `n×n×4 + n + 1024` bytes,
   room for any 8-bit RGBA PNG of that size. That reserves about 281 KB.
2. **Patch in place.** `PeResources` (F#) parses the PE headers and the
   `.rsrc` tree (type → name → language → data entry). The room for each
   resource is the gap to the next resource structure or the section end. It
   writes Skia's PNGs into the icon slots, updates the icon group entries
   (size, 32 bits, byte count), writes a fresh `VS_VERSIONINFO` (`VersionInfo`)
   into the version slot, shrinks each data entry's size, zeroes the rest of
   the slot, and recomputes the PE checksum.
3. **Clear failures.** A template without the slots, data larger than its slot
   (a very long title, say) or a file that isn't a PE gives one sentence in
   the report, never a broken executable.

The version info has `ProductName` and `FileDescription` (the title; Task
Manager shows the description), `FileVersion`/`ProductVersion` (the version
string, and its leading numbers in the fixed info), `CompanyName` and
`LegalCopyright` (the company, else the author), `InternalName`,
`OriginalFilename` and a "Made with Farming RPG Maker" comment. The icon is the
export `icon` asset scaled into a square, or the editor's icon when none is set.

The tests patch `tests/FarmEngine.Export.Tests/Fixtures/pe/player-fixture.exe`,
a 56 KB program built by mingw's gcc and windres with the same layout and
smaller slots (`build.sh` next to it rebuilds it), and read everything back.
`wrestool`/`icotool`, `objdump -p` and Python's `pefile` read the patched files
correctly, and pefile agrees with the checksum. CI exports a game on the Windows
runner and checks its version info with PowerShell.

## Export settings

Export settings live in the project, in a new optional `export` object. The
field is additive and optional, so it doesn't bump the schema version. The
web `GameProjectSchema` is `.passthrough()`, and native import and JSON
round-trip tests preserve the block. `Defaults.newExportSettings` creates
`gameId` once from the project id; later title changes leave it alone. The
F# Problems pipeline validates the block and blocks export on errors.

| Field | Example | Notes |
|---|---|---|
| `title` | `Willow Creek Farm` | Window title and title screen. Defaults to the project name. |
| `executableName` | `WillowCreek` | Must be a valid file name on every target. |
| `version` | `1.2.0` | Shown on the title screen, written to the exe's version info, and stored in saves. |
| `gameId` | `com.example.willowcreek` | Names the save folder. It is generated once and **never** changes, even when the game is renamed. |
| `author`, `company` | | Version info and credits. |
| `icon` | an asset id | PNG, at least 256×256. Export renders the 16–256 pixel icons from it. |
| `window` | `{ width, height, fullscreen }` | Default window size and whether the game starts fullscreen. |
| `pixelScale` | `integer` or `fit` | Integer scaling keeps pixel art crisp and letterboxes the rest. |
| `credits` | text | Shown on the credits screen. |
| `targets` | `["windows-x64", "linux-x64"]` | The targets the creator exported last time. |

Validation lives in F# `ChecksExport`, called by `Problems.collect`. Project
Settings → Export edits every field except `gameId` (set once) and `targets`
(Export Game records them), and shows the `ChecksExport` problems inline. A
project that never saved export settings exports with the defaults.

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

## What is implemented

| Piece | Where |
|---|---|
| Orchestration, report, C# entry points | `src/FarmEngine.Export` (F#): `Exporter.run`, `GameExporter.Export` |
| Menu command and dialog | `ProjectCommandHandler.ExportGameAsync`, `ExportGameViewModel`, `ExportGameWindow` |
| CLI | `farmc export` in `src/FarmEngine.Cli` |
| Placeholder resources | `crates/farm-player/build.rs` |
| Templates | `FarmPlayerTemplates` MSBuild target, `tools/player-templates/package.sh`, release workflow |
| License notices | `tools/player-licenses` |

The F# API:

- `Exporter.run : ExportOptions -> GameProject -> ExportReport` runs everything.
  `ExportOptions` has `Targets`, `OutputFolder`, `TemplatesFolder`,
  `CreateArchives` and `EditorVersion`. It never throws for project, template or
  file problems; they are in the report.
- `Exporter.identity`, `Exporter.check` (Problems errors and warnings plus
  unused assets), `Exporter.package` (one target's files in memory) and
  `Exporter.format` (the report as text).
- `PeResources.read/patch/readIcons/readVersion/checksum`, `VersionInfo.build/parse`,
  `Icons.render`, `Templates.find`, `AssetUsage.unused`, `DesktopEntry.create`,
  `Archives.zip/tar/gzip/tarGz`.

From C#: `GameExporter.Export(project, targets, outputFolder, createArchives, templatesFolder)`,
`GameExporter.Summarize(project)` (title, executable, version, game id, last
targets, Problems counts), `GameExporter.TryRememberTargets(project, targets, out edit)`
(the undoable edit that records `export.targets`), `GameExporter.Targets`,
`GameExporter.EditorVersion`, `GameExporter.DefaultTemplatesFolder` and
`GameExporter.Format(report)`.

The report has, per target: the folder, the archive, each file with its size,
warnings and errors. At the top: the title, version, game id, the cartridge
size and SHA-256, errors that stopped the export and project warnings.

The dialog remembers the output folder and the archive choice in the app's
`settings.json`, and records the chosen targets in the project. Its **Open
folder** links go through `IUrlLauncher.OpenFolder`.

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

Implemented now: `tests/FarmEngine.Export.Tests` covers the folder layout per
target, Problems errors blocking export, warnings and unused assets, identical
archives and folders from two exports, the tar execute bit, the `.desktop`
text, icon sizes, the PE patch round trip on the fixture (and its failures),
template version and checksum refusal, a missing template failing one target,
and an end-to-end export for the host target whose player runs headless and
reports the right game id, version and hash. `FarmingRpgMaker.App.Tests` drives
the dialog. CI exports `project-v8.json` on Linux and Windows and runs the
exported game against the checked-in replay.

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
- **Plugin sandbox without a JIT.** Settled: `farm-plugins` runs plugins in
  wasmi, a pure-Rust interpreter, so no platform needs a JIT (consoles and iOS
  forbid them). The wasm engine sits behind a small internal trait, so
  wasmtime (JIT or its Pulley interpreter) can be added as a build flag if
  plugins ever need more speed.
- **Web demo limits.** Should the web demo target offer a "the demo ends after
  day N" option?
- **macOS.** It needs a Mac (or [rcodesign](https://github.com/indygreg/apple-platform-rs))
  for signing and notarization, and each creator needs their own Apple
  Developer account. Revisit after Windows and Linux ship.
