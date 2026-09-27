"""Turns the 4x Blender renders into crisp pixel art and packs the sprite sheets.

Per frame: box-downscale to the target size, hard alpha (>= 50 %), quantize
every opaque pixel to the nearest color of the shared palette (CIELAB
distance), then add a 1-px dark outline around the silhouette. The procedural
tiles from `tiles.py` already use palette colors and are packed as they are.

Outputs `<out>/tiles.png`, `<out>/objects.png`, `<out>/characters.png` and
`<out>/manifest.json` (the contract `BuiltinArt` reads; see README.md).
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from palette import PALETTE, PALETTE_RGB, rgb  # noqa: E402
from sheet import Sprite, pack  # noqa: E402
from tiles import build_tiles  # noqa: E402

OUTLINE = rgb("OUTLINE")
MANIFEST_VERSION = 1
TILE_SIZE = 32


# --- color math --------------------------------------------------------------

def srgb_to_lab(rgb_values: np.ndarray) -> np.ndarray:
    """sRGB 0..255 (..., 3) -> CIELAB (..., 3)."""
    c = rgb_values.astype(np.float64) / 255.0
    lin = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    m = np.array([[0.4124564, 0.3575761, 0.1804375], [0.2126729, 0.7151522, 0.0721750], [0.0193339, 0.1191920, 0.9503041]])
    xyz = lin @ m.T
    xyz /= np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > 0.008856, np.cbrt(xyz), 7.787 * xyz + 16 / 116)
    l = 116 * f[..., 1] - 16
    a = 500 * (f[..., 0] - f[..., 1])
    b = 200 * (f[..., 1] - f[..., 2])
    return np.stack([l, a, b], axis=-1)


PALETTE_LAB = srgb_to_lab(np.array(PALETTE_RGB))


def quantize(rgb_values: np.ndarray) -> np.ndarray:
    """Nearest palette color for each pixel (N, 3) -> (N, 3)."""
    if len(rgb_values) == 0:
        return rgb_values
    lab = srgb_to_lab(rgb_values)
    # Hue matters more than lightness for keeping materials recognisable.
    weights = np.array([0.8, 1.0, 1.0])
    d = (((lab[:, None, :] - PALETTE_LAB[None, :, :]) * weights) ** 2).sum(axis=-1)
    return np.array(PALETTE_RGB)[d.argmin(axis=1)]


# --- per-frame processing ------------------------------------------------------

def pixelate_frame(image: Image.Image, target: tuple[int, int], outline: bool = True) -> Image.Image:
    rgba = np.array(image.convert("RGBA")).astype(np.float64)
    h, w = rgba.shape[:2]
    tw, th = target
    fx, fy = w // tw, h // th
    assert fx * tw == w and fy * th == h, f"render {w}x{h} is not a multiple of {target}"
    blocks = rgba.reshape(th, fy, tw, fx, 4)
    alpha = blocks[..., 3]
    weight = alpha.sum(axis=(1, 3))
    # Alpha-weighted color average so edge pixels don't darken toward the transparent black.
    color = (blocks[..., :3] * alpha[..., None]).sum(axis=(1, 3)) / np.maximum(weight[..., None], 1e-6)
    coverage = weight / (255.0 * fx * fy)
    opaque = coverage >= 0.5
    out = np.zeros((th, tw, 4), dtype=np.uint8)
    out[opaque, :3] = quantize(np.clip(color[opaque], 0, 255))
    out[opaque, 3] = 255
    if outline:
        shifted = np.zeros_like(opaque)
        shifted[1:, :] |= opaque[:-1, :]
        shifted[:-1, :] |= opaque[1:, :]
        shifted[:, 1:] |= opaque[:, :-1]
        shifted[:, :-1] |= opaque[:, 1:]
        edge = shifted & ~opaque
        out[edge, :3] = OUTLINE
        out[edge, 3] = 255
    return Image.fromarray(out, "RGBA")


def load_rendered(render_dir: Path) -> list[Sprite]:
    index = json.loads((render_dir / "index.json").read_text())
    sprites = []
    for entry in index:
        target = (entry["w"], entry["h"])
        frames = [[pixelate_frame(Image.open(render_dir / f), target) for f in row] for row in entry["frames"]]
        sprites.append(Sprite(
            entry["name"],
            frames,
            anchor=entry.get("anchor", "bottom"),
            footprint=tuple(entry.get("footprint", (1, 1))),
            ticks_per_frame=entry.get("ticksPerFrame"),
            directional=entry.get("directional", False),
            extra=entry.get("extra", {}),
        ))
    return sprites


def build_pack(render_dir: Path, out_dir: Path) -> dict:
    out_dir.mkdir(parents=True, exist_ok=True)
    rendered = load_rendered(render_dir)
    tiles = build_tiles()
    characters = [s for s in rendered if s.name.startswith(("char-", "npc-", "animal-"))]
    objects = [s for s in rendered if s not in characters]

    manifest = {
        "version": MANIFEST_VERSION,
        "tileSize": TILE_SIZE,
        "palette": list(PALETTE.values()),
        "sheets": [],
        "entries": [],
    }
    for file_name, sprites, width in (("tiles.png", tiles, 256), ("objects.png", objects, 512), ("characters.png", characters, 512)):
        sheet, entries = pack(sprites, file_name, max_width=width)
        sheet.save(out_dir / file_name, optimize=True)
        manifest["sheets"].append({"file": file_name, "w": sheet.width, "h": sheet.height})
        manifest["entries"].extend(entries)
    manifest["entries"].sort(key=lambda e: e["name"])
    (out_dir / "manifest.json").write_text(json.dumps(manifest, indent=1) + "\n")
    return manifest


if __name__ == "__main__":
    cache = Path(sys.argv[1] if len(sys.argv) > 1 else ".cache")
    out = Path(sys.argv[2] if len(sys.argv) > 2 else "out")
    result = build_pack(cache / "renders", out)
    print(f"{len(result['entries'])} entries in {[s['file'] for s in result['sheets']]} -> {out}")
