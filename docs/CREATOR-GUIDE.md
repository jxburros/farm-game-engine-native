# Creator guide: build a farm game without code

This guide walks through Farming RPG Maker on the desktop: start a project, build
the world in **Edit Mode**, playtest in **Play Mode**, and export a game that
anyone can play. Everything happens in the editor; you never need to write
code. The editor shows this guide under **Help → Creator Guide** (F1).

## 1. Start a project

**File → New Project** (Ctrl+N) asks for a name and a template:

- **Starter Farm**: the full farming loop, with crops, a shop, quests and crafting.
- **Cozy Garden**: the same farm without energy or collapse, and with slow days.
- **Quest RPG**: a story quest chain, gated dialogue and an elder NPC to remix.
- **Blank**: an empty scene and the default item catalog.

**File → Open Project** (Ctrl+O) lists your projects. They are saved
automatically in the app's data folder (`%APPDATA%\FarmingRpgMaker` on
Windows, `~/.config/FarmingRpgMaker` on Linux). **File → Export Project JSON**
writes a backup you can keep anywhere, and **Import Project JSON** brings one
back, including projects made with the web version.

## 2. Build the world in Edit Mode

Press **F6** (or the mode button in the header) for Edit Mode. The editor has
eight tabs: Map, Content, Problems, Settings, Mods, Art, Workshop and Interface.
Undo and redo work everywhere: **Ctrl+Z** and **Ctrl+Y**.

### Map

Pick a scene in the **Scene** list above the map; zoom with **−**, **+** and
**Fit**. Hover a tile to inspect it. The side panel holds the tools:

- **Tile brush**: the terrain to paint (grass, soil, water, path, wall, door,
  floor, and your own tiles from the Art tab).
- **Map tools**: **Brush** paints one tile at a time (click and drag),
  **Rectangle** fills a rectangle, **Fill area** floods connected tiles,
  **Pick** takes the terrain under the cursor as the brush, **Select** marks
  an area for **Copy selection** and **Paste on map**, and **Erase layer**
  clears the chosen layer (Background, Overlay or Object).
- **Block** and **Unblock** set where the player can walk. **Player start**
  moves the player's starting tile.
- **Place**: put an **NPC**, a gathering **Node** (trees, rocks, weeds), an
  **Item**, a **Machine** or an **Animal** on the map, and **Remove** things
  placed there.
- **Scene**: add a scene, rename, resize, duplicate or delete it, **Set as
  start**, **Fill scene** with one tile, **Clear crops/items** and **Reset soil**.
- **Transitions**: choose **Door**, click the departure tile, then pick the
  destination scene and tile. **Return door** adds the way back. The list
  below shows every door of the scene.

### Content

The Content tab edits everything the game is made of. Pick a category, select
an entry (or **Add** one), change its fields and **Save**:

- **NPCs** and **Dialogue**: characters, where they stand, their daily
  schedules and gift tastes, and dialogue trees whose options can open shops,
  offer quests, or depend on friendship, items and flags.
- **Items** and **Crops**: the item catalog (tools and their tiers, seeds,
  materials, gifts) and crops with growth days, seasons, regrowth and yields.
- **Quests**: objectives (harvest, collect, talk, visit, craft, gift),
  rewards, prerequisites and when they are available.
- **Events**: triggers (entering a tile, interacting, time passing),
  conditions and outcomes, for story beats and flags.
- **Shops**: stock, prices, daily limits and tool repair.
- **Recipes**, **Node types** and **Machine types**: hand crafting, the
  resources trees and rocks drop, and machines that process ingredients. A
  recipe's category groups it in the game's crafting menu.
- **Animal species** and **Fish tables**: what animals produce, and which fish
  bite in each scene and season.
- **Actions** and **Minigames**: an action is a named bundle of effects (give
  an item, set a flag, show a message, start a minigame) that an item's
  **Use** button, a dialogue option, an event or a hotkey can run. A minigame
  is a short challenge whose score picks the reward.

A few advanced fields are still edited as JSON; the form says which.

### Problems

The Problems tab checks the whole project: missing references, unreachable
content and settings that would break the game. **Errors block export**;
warnings and tips help you improve the game. **Go to** jumps to the entry or
scene that needs a fix.

### Settings

Project settings: name, version and the game-text **locale**; gameplay
(energy, skills, player speed, collapse penalties); the clock (day start and
end, game minutes per real second); the calendar's **seasons** (add, rename,
reorder, change their length) and **festivals**. The **Export** section holds
what the exported game shows: its title, executable name, version, author,
company, game id (set it once: it keeps players' save folders stable), icon,
window size, pixel scale, fullscreen and credits.

The locale also picks the language of the game's own interface when the
player hasn't chosen one: English and Spanish are built in. Players can
change it in the game's **Settings → Accessibility → Language**.

### Mods

Install **content packs** from their JSON file: the editor shows what a pack
adds, overrides and needs before **Install reviewed pack**. Installed packs
can be turned on and off, reordered, removed, or imported into the project as
ordinary content.

### Art

**Import image** takes PNG, JPEG, WebP, GIF, BMP and SVG files. Slice a sheet
into **animation clips**, then **Assign artwork** to the player, a map brush
(with its tile behavior), an NPC, an item, a crop, a node, an animal or a
machine. Mark pixel art as **Crisp pixel art** so it stays sharp.

### Workshop

The Workshop creates working interactions in one step (and one undo):
a branching story, a romance milestone, a letter and mailbox, a building with
an interior, a watering spell, a combat encounter, a fishing challenge, a
catchable insect, forageables, trees, rocks, weeds and a crafting discipline.
Fill in the name and text, **Create pattern**, then **Open its editor** to
fine-tune what it made.

### Interface

Game panels appear on the game screen: live counters, story status or action
buttons. Give a panel a title, optionally a story flag that must be set before
it shows, and entries (a label, what it shows, and a value, item id or action
id).

## 3. Playtest in Play Mode

Press **F5** (or the mode button) to play the project with the same player an
exported game uses; it starts straight in the game, without the title screen.
Click the game to give it the keyboard. **Esc** opens the game's own pause menu and settings; **F6**
returns to the editor.

Playtests are safe: returning to Edit Mode restores the world to the moment
you started, so your farm isn't trampled by testing. The Play Mode toolbar has:

- **Restart**: start the playtest over from that moment.
- **Keep changes**: keep what happened in the playtest when you return.
- **Debug**: add money, restore energy, skip a day or an hour, change the
  season, give seeds or materials, teleport to a scene and set flags.

## 4. Share your game

**File → Export Game** (Ctrl+Shift+E) builds real desktop games: a Windows x64
folder with the game's `.exe`, and a Linux x64 folder that also runs on the
Steam Deck, each with an optional zip or tar.gz archive. Players unzip and
play; no editor, browser or install is needed. The exported game has a title
screen, three save slots with autosave each morning, a pause menu, settings
(display, audio, rebindable controls, language, text size, a readable font and
reduced motion) and gamepad support.

Export stops when the Problems tab has errors. Bump the version in Settings
when you ship an update: saves from the older version keep loading.

## Player controls (default)

WASD or the arrow keys move (hold to walk, diagonals too) · E, Space or Enter
interact · Q watering can · T hoe · R axe · F pickaxe · C scythe · X crafting ·
Z sleep · I inventory · J quests · Esc closes a panel or opens the pause menu ·
1–9 pick a dialogue option. Gamepads work everywhere, menus included. Players
can rebind keys in the game's **Settings → Controls**; **Help → Keyboard
Shortcuts** lists the editor's shortcuts too.

## More

- [Exporting games](EXPORT.md): what an exported game contains and how to
  ship it on Steam or itch.io.
- [The game player](PLAYER.md): the player's screens, settings, saves and
  command-line options.
