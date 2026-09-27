# Built-in art pipeline

Generates the pixel-art pack that `FarmEngine.Rendering` embeds and uses for
every project that binds no art of its own (creator art always wins). The
output lives in `src/FarmEngine.Rendering/Assets/builtin/`:

| File | What |
|---|---|
| `tiles.png` | Ground tiles: grass (4 seasons × 4 variants), tilled soil (dry/wet), fertilizer speckles, water (4-frame animation), path, stone wall, door, wood floor, ladder, the "output ready" bubble |
| `objects.png` | Trees (3 variants, two tiles tall), stump, rocks, boulder, mine rock, copper/iron/quartz nodes, weeds, machines (idle + working), dropped items, every built-in crop at 5 growth stages, a withered crop, a generic crop |
| `characters.png` | Player, farmer, merchant and a generic villager (4 directions × 4 walk frames), chicken and cow (4 directions × 2 idle frames) |
| `manifest.json` | Where each sprite is and how to read it (the contract the renderer uses; see below) |

The PNGs are committed. `dotnet build` never needs Python or Blender; this
pipeline only runs when the art changes.

## Running it

Requirements: Python 3.11 with Blender 4.5 as a module (`import bpy`),
Pillow and numpy (`pip install pillow numpy`). No GPU: Cycles renders on
the CPU.

```sh
python3 tools/art/build_art.py             # everything: render, pixelate, pack
python3 tools/art/build_art.py --no-render # reuse tools/art/.cache renders, repack only
python3 tools/art/build_art.py --samples 8 # quick, noisier preview
python3 tools/art/build_art.py --out /tmp/pack   # write elsewhere
```

Runtime: the full run renders 157 images and takes **about 25 seconds on
4 CPU cores** (24 Cycles samples per pixel at 4× resolution; the box
downscale averages 16 rendered pixels per output pixel, so the result is
clean without a denoiser). Raw renders are cached under `tools/art/.cache/`
(git-ignored).

The output is deterministic: fixed Cycles seed, fixed sample count, no
adaptive sampling, no denoiser, Blender's stamp metadata off, and all noise in
the procedural tiles comes from seeded `numpy` generators. Two runs on the
same Blender build produce byte-identical PNGs (verified with `sha1sum` on
the pack).

## Files

| File | Role |
|---|---|
| `build_art.py` | Entry point: render (optional) → pixelate → pack → write the manifest |
| `palette.py` | The ONE shared palette (48 warm colors). Every pixel of every sheet is one of these |
| `tiles.py` | Procedural ground tiles and overlays (Pillow/numpy, no Blender) |
| `render_objects.py` | Builds low-poly models from primitives with `bpy` and renders them with an orthographic, slightly tilted camera (Cycles CPU, transparent film) at 4× the target size. Facing rows come from rotating the camera rig around the model; walk cycles are keyframed leg/arm swings |
| `pixelate.py` | Box-downscale, hard alpha, quantize to the palette (CIELAB nearest), 1-px dark outline, then shelf-pack into sheets |
| `sheet.py` | `Sprite` record + the deterministic packer shared by `tiles.py` and `pixelate.py` |

Target sizes: tiles and small objects 32×32, crops/machines/characters/animals
32×48, trees 32×64 (two tiles tall, so they overlap the tile above). Renders
are 4× that (128×128, 128×192, 128×256).

## The manifest

```json
{
  "version": 1,
  "tileSize": 32,
  "palette": ["#2b2138", "..."],
  "sheets": [{ "file": "tiles.png", "w": 256, "h": 160 }, ...],
  "entries": [
    { "name": "tile-grass", "file": "tiles.png", "x": 0, "y": 0, "w": 32, "h": 32,
      "frames": 4, "rows": 1, "anchor": "top", "footprint": [1, 1] },
    { "name": "tile-water", "file": "tiles.png", "x": 0, "y": 64, "w": 32, "h": 32,
      "frames": 4, "rows": 1, "anchor": "top", "footprint": [1, 1], "ticksPerFrame": 8 },
    { "name": "char-player", "file": "characters.png", "x": 0, "y": 0, "w": 32, "h": 48,
      "frames": 4, "rows": 4, "anchor": "bottom", "footprint": [1, 1],
      "directional": true, "ticksPerFrame": 6, "idleFrame": 0 },
    { "name": "crop-wheat", "file": "objects.png", "x": 0, "y": 96, "w": 32, "h": 48,
      "frames": 5, "rows": 1, "anchor": "bottom", "footprint": [1, 1], "stages": 5 },
    { "name": "machine-furnace", "...": "...", "frames": 2, "states": ["idle", "working"] }
  ]
}
```

Per entry:

- `name` — the lookup key. Naming convention (content ids map straight onto it):
  `tile-<tileType>[-<season>]`, `tile-soil-watered`, `tile-soil-fertilized`,
  `tile-ladder`, `fx-ready`, `node-<nodeTypeId>`, `crop-<cropId>`,
  `crop-withered`, `crop-generic`, `machine-<machineTypeId>`,
  `machine-generic`, `item-generic|seed|material`, `animal-<speciesId>`,
  `npc-<appearance>` (`npc-villager` is the generic NPC), `char-player`.
- `file`, `x`, `y`, `w`, `h` — the cell grid's top-left and cell size on the sheet.
- `frames` — columns. With `ticksPerFrame` they are animation frames
  (`frame = floor(tick / ticksPerFrame) mod frames`); without it they are
  look-alike variants the renderer picks from a hash of the tile coordinates.
  `stages` (crops) and `states` (machines) name what the columns mean instead.
- `rows` — 4 with `directional: true` (down, left, right, up), else 1.
- `anchor` — `bottom`: the cell's bottom-centre stands on the tile's bottom
  edge (tall sprites overhang the tile above); `top`: the cell fills the tile.
- `footprint` — tiles covered, `[width, height]`.
- `idleFrame` — the column shown while a directional sprite is not moving.

Stable guarantees: names listed above keep their meaning; new entries may be
added; `w`/`h` for a name may change only together with `tileSize`. Unknown
ids fall back on the renderer side (generic crop / rock / villager /
chicken / machine), so a project can never reference a missing sprite.
