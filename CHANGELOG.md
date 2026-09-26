# Changelog

## 0.2.0 (unreleased)

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
