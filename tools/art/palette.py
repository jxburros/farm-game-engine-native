"""The one shared palette of the built-in art pack (48 warm, cozy colors).

Every sprite the pipeline produces (Blender renders after `pixelate.py`, and
the procedural tiles from `tiles.py`) uses only these colors, so the whole
pack reads as one piece. Names are used by `tiles.py` / `render_objects.py`;
`pixelate.py` only needs the RGB list.
"""

from __future__ import annotations

PALETTE: dict[str, str] = {
    # outlines / deep shadow
    "OUTLINE": "#2b2138",
    "PLUM": "#4a3b4f",
    # foliage & grass ramp
    "GREEN0": "#1f4d2e",
    "GREEN1": "#2e6b3a",
    "GREEN2": "#3f8b44",
    "GREEN3": "#5fae4f",
    "GREEN4": "#8ccb5a",
    "GREEN5": "#c4e07a",
    # wood & soil ramp
    "BROWN0": "#3d2a1e",
    "BROWN1": "#5c3d28",
    "BROWN2": "#7c563a",
    "BROWN3": "#a07350",
    "BROWN4": "#c49a6a",
    "BROWN5": "#e0c091",
    "SOILWET": "#4e3526",
    # stone ramp
    "GREY0": "#3a3a48",
    "GREY1": "#5a5a6b",
    "GREY2": "#7d7d8e",
    "GREY3": "#a3a3b1",
    "GREY4": "#cfcfd8",
    "WHITE": "#eef0f5",
    # skin
    "SKIN0": "#a86a45",
    "SKIN1": "#d9a074",
    "SKIN2": "#f2c9a0",
    # water & cloth blues
    "BLUE0": "#1e3a6e",
    "BLUE1": "#2f6db5",
    "BLUE2": "#4f9be0",
    "BLUE3": "#8dd0f0",
    "BLUE4": "#d6f0fa",
    # reds, oranges, yellows
    "RED0": "#7a1f2b",
    "RED1": "#c23a3a",
    "RED2": "#e8613f",
    "ORANGE": "#f28c3b",
    "AMBER": "#f7b24a",
    "YELLOW": "#ffd96b",
    "CREAM": "#fff0b0",
    # purples & pinks
    "PURPLE0": "#3c2a5c",
    "PURPLE1": "#6a4a9c",
    "PURPLE2": "#9b7bd0",
    "PURPLE3": "#c9b3ee",
    "PINK0": "#d95f8e",
    "PINK1": "#f4a2c2",
    # autumn ochres
    "OCHRE0": "#b89a3c",
    "OCHRE1": "#d8c06a",
    # metals & crystal
    "COPPER": "#b8642f",
    "SLATE": "#6b7a90",
    "TEAL": "#7fd6cc",
    "DARKTEAL": "#2f7f7a",
}

assert len(PALETTE) == 48, len(PALETTE)


def hex_to_rgb(value: str) -> tuple[int, int, int]:
    value = value.lstrip("#")
    return int(value[0:2], 16), int(value[2:4], 16), int(value[4:6], 16)


def rgb(name: str) -> tuple[int, int, int]:
    """8-bit sRGB triple for a palette name."""
    return hex_to_rgb(PALETTE[name])


def srgb_to_linear(channel: float) -> float:
    return channel / 12.92 if channel <= 0.04045 else ((channel + 0.055) / 1.055) ** 2.4


def linear(name: str) -> tuple[float, float, float, float]:
    """Linear RGBA for a Blender base color (Blender's color inputs are scene-linear)."""
    r, g, b = (c / 255 for c in rgb(name))
    return srgb_to_linear(r), srgb_to_linear(g), srgb_to_linear(b), 1.0


PALETTE_RGB: list[tuple[int, int, int]] = [hex_to_rgb(v) for v in PALETTE.values()]
PALETTE_NAMES: list[str] = list(PALETTE.keys())
