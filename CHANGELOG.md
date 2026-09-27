# Changelog

## 0.2.0 (unreleased)

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
