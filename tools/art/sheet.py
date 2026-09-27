"""Sprite records + the sheet packer shared by `tiles.py` and `pixelate.py`.

A `Sprite` is a grid of same-sized frames: `frames[row][column]`. Columns are
either animation frames (when `ticks_per_frame` is set) or look-alike variants
the renderer picks by tile coordinate; rows are facing directions for
`directional` sprites (down, left, right, up). `pack()` lays sprites out on a
sheet with a deterministic shelf packer and returns the manifest entries.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from PIL import Image


@dataclass
class Sprite:
    name: str
    frames: list[list[Image.Image]]
    anchor: str = "bottom"
    footprint: tuple[int, int] = (1, 1)
    ticks_per_frame: int | None = None
    directional: bool = False
    extra: dict = field(default_factory=dict)

    @property
    def width(self) -> int:
        return self.frames[0][0].width

    @property
    def height(self) -> int:
        return self.frames[0][0].height

    @property
    def columns(self) -> int:
        return len(self.frames[0])

    @property
    def rows(self) -> int:
        return len(self.frames)

    def validate(self) -> None:
        size = self.frames[0][0].size
        for row in self.frames:
            assert len(row) == self.columns, f"{self.name}: ragged rows"
            for frame in row:
                assert frame.size == size, f"{self.name}: frame size {frame.size} != {size}"
                assert frame.mode == "RGBA", f"{self.name}: frame mode {frame.mode}"


def pack(sprites: list[Sprite], file_name: str, max_width: int = 512) -> tuple[Image.Image, list[dict]]:
    """Shelf-packs the sprites (tallest block first, then by name) into one RGBA sheet."""
    for sprite in sprites:
        sprite.validate()
    blocks = sorted(sprites, key=lambda s: (-s.height * s.rows, -s.width * s.columns, s.name))
    placements: list[tuple[Sprite, int, int]] = []
    x = y = shelf_height = 0
    for sprite in blocks:
        block_w = sprite.width * sprite.columns
        block_h = sprite.height * sprite.rows
        if x + block_w > max_width and x > 0:
            x = 0
            y += shelf_height
            shelf_height = 0
        placements.append((sprite, x, y))
        x += block_w
        shelf_height = max(shelf_height, block_h)
    sheet_w = max(x for _, x, _ in placements) if placements else 1
    sheet_w = max(sheet_w, max((p[1] + p[0].width * p[0].columns) for p in placements))
    sheet_h = y + shelf_height
    sheet = Image.new("RGBA", (sheet_w, sheet_h), (0, 0, 0, 0))
    entries: list[dict] = []
    for sprite, px, py in placements:
        for r, row in enumerate(sprite.frames):
            for c, frame in enumerate(row):
                sheet.paste(frame, (px + c * sprite.width, py + r * sprite.height))
        entry = {
            "name": sprite.name,
            "file": file_name,
            "x": px,
            "y": py,
            "w": sprite.width,
            "h": sprite.height,
            "frames": sprite.columns,
            "rows": sprite.rows,
            "anchor": sprite.anchor,
            "footprint": list(sprite.footprint),
        }
        if sprite.directional:
            entry["directional"] = True
        if sprite.ticks_per_frame:
            entry["ticksPerFrame"] = sprite.ticks_per_frame
        entry.update(sprite.extra)
        entries.append(entry)
    entries.sort(key=lambda e: e["name"])
    return sheet, entries
