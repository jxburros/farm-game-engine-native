"""Procedural 32x32 ground tiles and small overlays (Pillow + numpy, no Blender).

Everything is deterministic: noise comes from `numpy.random.default_rng` with
fixed seeds, so two runs produce identical pixels. Colors are palette names
from `palette.py`. Tiles are NOT autotiled; instead most types come in a few
look-alike variants (columns) that the renderer picks from a hash of the tile
coordinates so fields don't look striped.
"""

from __future__ import annotations

import numpy as np
from PIL import Image

from palette import rgb
from sheet import Sprite

SIZE = 32


def blank(size: int = SIZE) -> np.ndarray:
    """Transparent RGBA canvas."""
    return np.zeros((size, size, 4), dtype=np.uint8)


def fill(canvas: np.ndarray, color: str) -> None:
    canvas[:, :, :3] = rgb(color)
    canvas[:, :, 3] = 255


def put(canvas: np.ndarray, x: int, y: int, color: str) -> None:
    if 0 <= x < canvas.shape[1] and 0 <= y < canvas.shape[0]:
        canvas[y, x, :3] = rgb(color)
        canvas[y, x, 3] = 255


def rect(canvas: np.ndarray, x: int, y: int, w: int, h: int, color: str) -> None:
    x0, y0 = max(0, x), max(0, y)
    x1, y1 = min(canvas.shape[1], x + w), min(canvas.shape[0], y + h)
    if x1 > x0 and y1 > y0:
        canvas[y0:y1, x0:x1, :3] = rgb(color)
        canvas[y0:y1, x0:x1, 3] = 255


def speckle(canvas: np.ndarray, rng: np.random.Generator, colors: list[str], count: int, size: int = 1) -> None:
    for _ in range(count):
        x = int(rng.integers(0, SIZE))
        y = int(rng.integers(0, SIZE))
        rect(canvas, x, y, size, 1, colors[int(rng.integers(0, len(colors)))])


def blotches(canvas: np.ndarray, rng: np.random.Generator, base: str, shades: list[tuple[str, float]], cell: int = 2) -> None:
    """Fills with `base`, then low-frequency blobs of the shades (probability each), at `cell` px blocks."""
    fill(canvas, base)
    cells = SIZE // cell
    noise = rng.random((cells, cells))
    threshold = 0.0
    for color, probability in shades:
        mask = (noise >= threshold) & (noise < threshold + probability)
        threshold += probability
        for cy, cx in zip(*np.nonzero(mask)):
            rect(canvas, int(cx) * cell, int(cy) * cell, cell, cell, color)


def to_image(canvas: np.ndarray) -> Image.Image:
    return Image.fromarray(canvas, "RGBA")


# --- grass -----------------------------------------------------------------

def flower(canvas: np.ndarray, x: int, y: int, petal: str, center: str) -> None:
    put(canvas, x, y - 1, petal)
    put(canvas, x - 1, y, petal)
    put(canvas, x + 1, y, petal)
    put(canvas, x, y + 1, petal)
    put(canvas, x, y, center)


def tuft(canvas: np.ndarray, x: int, y: int, color: str) -> None:
    for dx, dy in ((0, 0), (0, -1), (2, 0), (2, -1), (2, -2), (4, 0), (4, -1)):
        put(canvas, x + dx, y + dy, color)


def grass_variants(base: str, dark: str, light: str, seed: int, *, flowers: list[tuple[str, str]], blade: str, leaf: str | None = None, frost: bool = False) -> list[Image.Image]:
    rng = np.random.default_rng(seed)
    frames = []
    for variant in range(4):
        canvas = blank()
        blotches(canvas, rng, base, [(dark, 0.16), (light, 0.10)])
        for _ in range(3):
            tuft(canvas, int(rng.integers(2, 26)), int(rng.integers(4, 30)), blade)
        if frost:
            speckle(canvas, rng, ["WHITE"], 10, 2)
        if variant == 1 and flowers:
            petal, center = flowers[0]
            flower(canvas, int(rng.integers(4, 28)), int(rng.integers(4, 28)), petal, center)
        if variant == 2 and flowers:
            for petal, center in flowers[:2]:
                flower(canvas, int(rng.integers(4, 28)), int(rng.integers(4, 28)), petal, center)
        if variant == 3:
            tuft(canvas, int(rng.integers(4, 24)), int(rng.integers(8, 28)), dark)
            if leaf:
                rect(canvas, int(rng.integers(4, 26)), int(rng.integers(4, 26)), 2, 2, leaf)
        frames.append(to_image(canvas))
    return frames


def grass_sprites() -> list[Sprite]:
    seasons = {
        "": ("GREEN3", "GREEN2", "GREEN4", 11, [("WHITE", "YELLOW"), ("PINK1", "YELLOW")], "GREEN1", None, False),
        "summer": ("GREEN2", "GREEN1", "GREEN3", 12, [("YELLOW", "ORANGE"), ("WHITE", "YELLOW")], "GREEN0", None, False),
        "fall": ("OCHRE1", "OCHRE0", "GREEN5", 13, [("RED2", "AMBER")], "OCHRE0", "RED2", False),
        "winter": ("GREY4", "GREY3", "BLUE4", 14, [], "GREY2", "GREEN1", True),
    }
    sprites = []
    for season, (base, dark, light, seed, flowers, blade, leaf, frost) in seasons.items():
        name = "tile-grass" if not season else f"tile-grass-{season}"
        frames = grass_variants(base, dark, light, seed, flowers=flowers, blade=blade, leaf=leaf, frost=frost)
        sprites.append(Sprite(name, [frames], anchor="top"))
    return sprites


# --- soil ------------------------------------------------------------------

def soil_variant(rng: np.random.Generator, base: str, furrow: str, ridge: str, speck: str) -> Image.Image:
    canvas = blank()
    blotches(canvas, rng, base, [(furrow, 0.08), (ridge, 0.06)])
    for row in (3, 11, 19, 27):
        rect(canvas, 0, row, SIZE, 2, furrow)
        rect(canvas, 0, row - 1, SIZE, 1, ridge)
    speckle(canvas, rng, [speck, furrow], 10)
    return to_image(canvas)


def soil_sprites() -> list[Sprite]:
    rng = np.random.default_rng(21)
    dry = [soil_variant(rng, "BROWN3", "BROWN2", "BROWN4", "BROWN1") for _ in range(2)]
    wet = [soil_variant(rng, "BROWN1", "SOILWET", "BROWN2", "BROWN0") for _ in range(2)]
    fertilized = blank()
    for x, y in ((5, 6), (14, 4), (23, 8), (9, 15), (20, 17), (27, 22), (4, 25), (15, 27)):
        rect(fertilized, x, y, 2, 1, "CREAM")
        put(fertilized, x + 1, y + 1, "BROWN5")
    return [
        Sprite("tile-soil", [dry], anchor="top"),
        Sprite("tile-soil-watered", [wet], anchor="top"),
        Sprite("tile-soil-fertilized", [[to_image(fertilized)]], anchor="top"),
    ]


# --- water (4-frame animation) --------------------------------------------

def water_sprites() -> list[Sprite]:
    rng = np.random.default_rng(31)
    waves = [(int(rng.integers(0, SIZE)), int(rng.integers(0, SIZE)), int(rng.integers(3, 6))) for _ in range(7)]
    frames = []
    for frame in range(4):
        canvas = blank()
        blotches(canvas, rng, "BLUE1", [("BLUE0", 0.05), ("BLUE2", 0.04)], cell=2)
        for index, (x, y, length) in enumerate(waves):
            color = "BLUE3" if (index + frame) % 2 == 0 else "BLUE2"
            for dx in range(length):
                put(canvas, (x + dx + frame) % SIZE, (y + ((index + frame) % 4 == 0)) % SIZE, color)
            put(canvas, (x + length + frame) % SIZE, (y + 1) % SIZE, "BLUE0")
        frames.append(to_image(canvas))
    return [Sprite("tile-water", [frames], anchor="top", ticks_per_frame=8)]


# --- path, wall, door, floor -----------------------------------------------

def path_sprites() -> list[Sprite]:
    rng = np.random.default_rng(41)
    frames = []
    for _ in range(3):
        canvas = blank()
        blotches(canvas, rng, "BROWN5", [("BROWN4", 0.14), ("CREAM", 0.05)])
        for _ in range(4):
            rect(canvas, int(rng.integers(1, 29)), int(rng.integers(1, 30)), 2, 1, "GREY3")
        speckle(canvas, rng, ["BROWN4"], 6)
        frames.append(to_image(canvas))
    return [Sprite("tile-path", [frames], anchor="top")]


def wall_variant(rng: np.random.Generator) -> Image.Image:
    canvas = blank()
    # Top face (rows 0-7): lighter stone with a highlight edge.
    rect(canvas, 0, 0, SIZE, 8, "GREY3")
    rect(canvas, 0, 0, SIZE, 1, "GREY4")
    speckle(canvas, rng, ["GREY4", "GREY2"], 6)
    rect(canvas, 0, 8, SIZE, 1, "GREY0")
    # Front face: bricks with mortar.
    rect(canvas, 0, 9, SIZE, 23, "GREY2")
    for i, top in enumerate((9, 15, 21, 27)):
        rect(canvas, 0, top + 5, SIZE, 1, "GREY1")
        offset = 8 if i % 2 else 0
        for x in range(offset, SIZE + 1, 16):
            rect(canvas, x - 1, top, 1, 5, "GREY1")
            if rng.random() < 0.35:
                rect(canvas, x, top, min(15, SIZE - x), 5, "GREY3")
                rect(canvas, x, top, min(15, SIZE - x), 1, "GREY4")
    return to_image(canvas)


def wall_sprites() -> list[Sprite]:
    rng = np.random.default_rng(51)
    return [Sprite("tile-wall", [[wall_variant(rng), wall_variant(rng)]], anchor="top")]


def door_sprite() -> Sprite:
    canvas = blank()
    rect(canvas, 0, 0, SIZE, SIZE, "GREY2")
    rect(canvas, 3, 2, 26, 30, "BROWN1")
    rect(canvas, 5, 4, 22, 28, "BROWN3")
    for x in (10, 16, 22):
        rect(canvas, x, 4, 1, 28, "BROWN2")
    rect(canvas, 5, 12, 22, 1, "BROWN2")
    rect(canvas, 5, 22, 22, 1, "BROWN2")
    rect(canvas, 20, 17, 3, 2, "YELLOW")
    put(canvas, 21, 18, "AMBER")
    rect(canvas, 5, 4, 22, 1, "BROWN4")
    return Sprite("tile-door", [[to_image(canvas)]], anchor="top")


def floor_sprites() -> list[Sprite]:
    rng = np.random.default_rng(61)
    frames = []
    for _ in range(2):
        canvas = blank()
        blotches(canvas, rng, "BROWN4", [("BROWN5", 0.08), ("BROWN3", 0.06)], cell=4)
        for i, top in enumerate((0, 8, 16, 24)):
            rect(canvas, 0, top + 7, SIZE, 1, "BROWN2")
            rect(canvas, 0, top, SIZE, 1, "BROWN5")
            end = (i * 11 + 6) % SIZE
            rect(canvas, end, top, 1, 8, "BROWN2")
        frames.append(to_image(canvas))
    return [Sprite("tile-floor", [frames], anchor="top")]


# --- overlays --------------------------------------------------------------

def ladder_sprite() -> Sprite:
    canvas = blank()
    rect(canvas, 6, 6, 20, 20, "OUTLINE")
    rect(canvas, 8, 8, 16, 16, "BROWN0")
    for x in (10, 20):
        rect(canvas, x, 7, 2, 18, "BROWN4")
    for y in range(9, 25, 4):
        rect(canvas, 12, y, 8, 1, "BROWN5")
    return Sprite("tile-ladder", [[to_image(canvas)]], anchor="top")


def ready_bubble_sprite() -> Sprite:
    canvas = blank()
    rect(canvas, 19, 1, 12, 12, "OUTLINE")
    rect(canvas, 20, 2, 10, 10, "CREAM")
    rect(canvas, 24, 4, 2, 5, "RED2")
    rect(canvas, 24, 10, 2, 1, "RED2")
    put(canvas, 21, 13, "OUTLINE")
    put(canvas, 22, 13, "OUTLINE")
    put(canvas, 21, 14, "OUTLINE")
    return Sprite("fx-ready", [[to_image(canvas)]], anchor="top")


def build_tiles() -> list[Sprite]:
    sprites: list[Sprite] = []
    sprites += grass_sprites()
    sprites += soil_sprites()
    sprites += water_sprites()
    sprites += path_sprites()
    sprites += wall_sprites()
    sprites.append(door_sprite())
    sprites += floor_sprites()
    sprites.append(ladder_sprite())
    sprites.append(ready_bubble_sprite())
    return sprites


if __name__ == "__main__":
    import sys
    from pathlib import Path

    out = Path(sys.argv[1] if len(sys.argv) > 1 else "preview")
    out.mkdir(parents=True, exist_ok=True)
    for sprite in build_tiles():
        for r, row in enumerate(sprite.frames):
            for c, frame in enumerate(row):
                frame.save(out / f"{sprite.name}-{r}-{c}.png")
    print(f"wrote previews to {out}")
