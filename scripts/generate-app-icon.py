#!/usr/bin/env python3
"""Draws the Farming RPG Maker app icon (a wheat stalk over a sunny field).

Pure standard library (no Pillow): shapes are rasterised on a 1024x1024
supersampled grid and box-filtered down. Writes

  src/FarmingRpgMaker.App/Assets/app-icon-256.png
  src/FarmingRpgMaker.App/Assets/app.ico   (16-256 px; 256 as PNG, others as 32-bit BMP)

Run from the repo root:  python3 scripts/generate-app-icon.py
"""
import math
import os
import struct
import zlib

SS = 1024  # supersampled canvas
OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "src", "FarmingRpgMaker.App", "Assets")


def hex_rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


SKY_TOP = hex_rgb("#FFF4D6")
SKY_BOTTOM = hex_rgb("#F9D98A")
SUN = hex_rgb("#F9C718")
SUN_GLOW = hex_rgb("#FBE08A")
HILL = hex_rgb("#7DB356")
FIELD = hex_rgb("#2F7D4A")
FURROW = hex_rgb("#1F6139")
STEM = hex_rgb("#B8740F")
GRAIN = hex_rgb("#E7A41F")
GRAIN_HI = hex_rgb("#F6C85A")
BORDER = hex_rgb("#095C34")


def lerp(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def in_rounded_rect(x, y, x0, y0, x1, y1, r):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def in_ellipse(x, y, cx, cy, rx, ry, angle):
    c, s = math.cos(angle), math.sin(angle)
    dx, dy = x - cx, y - cy
    u = dx * c + dy * s
    v = -dx * s + dy * c
    return (u / rx) ** 2 + (v / ry) ** 2 <= 1.0


def dist_to_segment(x, y, ax, ay, bx, by):
    px, py = bx - ax, by - ay
    t = max(0.0, min(1.0, ((x - ax) * px + (y - ay) * py) / (px * px + py * py)))
    qx, qy = ax + t * px, ay + t * py
    return math.hypot(x - qx, y - qy)


# Wheat stalk: a gently curved stem with grains alternating left/right near the top.
STEM_PTS = [(0.36, 0.95), (0.40, 0.70), (0.44, 0.46), (0.47, 0.22)]
GRAINS = []
for i in range(5):
    t = i / 4.0
    gx = 0.462 - 0.03 * t
    gy = 0.25 + 0.058 * i
    GRAINS.append((gx - 0.045, gy + 0.012, 0.055, 0.026, math.radians(-55)))
    GRAINS.append((gx + 0.045, gy + 0.012, 0.055, 0.026, math.radians(55 + 180)))
GRAINS.append((0.472, 0.205, 0.024, 0.05, math.radians(8)))


def pixel(x, y):
    """Colour (r, g, b) and alpha for canvas point (x, y) in [0, 1]."""
    m = 0.03
    if not in_rounded_rect(x, y, m, m, 1 - m, 1 - m, 0.2):
        return None
    border = not in_rounded_rect(x, y, m + 0.022, m + 0.022, 1 - m - 0.022, 1 - m - 0.022, 0.18)

    col = lerp(SKY_TOP, SKY_BOTTOM, min(1.0, max(0.0, (y - 0.05) / 0.55)))

    d_sun = math.hypot(x - 0.72, y - 0.30)
    if d_sun < 0.17:
        col = lerp(col, SUN_GLOW, 0.6)
    if d_sun < 0.115:
        col = SUN

    hill_y = 0.60 + 0.045 * math.sin(x * 5.5 + 0.8)
    if y > hill_y:
        col = HILL

    field_y = 0.70 - 0.05 * math.cos((x - 0.2) * 3.2)
    if y > field_y:
        col = FIELD
        # Furrows converge towards a vanishing point above the field.
        vy = 0.52
        u = (x - 0.62) / max(0.02, (y - vy))
        if (u * 3.2) % 1.0 < 0.38:
            col = FURROW

    # Stem
    for (ax, ay), (bx, by) in zip(STEM_PTS, STEM_PTS[1:]):
        if dist_to_segment(x, y, ax, ay, bx, by) < 0.016:
            col = STEM
            break

    for (gx, gy, rx, ry, a) in GRAINS:
        if in_ellipse(x, y, gx, gy, rx, ry, a):
            col = GRAIN
            if in_ellipse(x, y, gx - 0.008, gy - 0.008, rx * 0.5, ry * 0.45, a):
                col = GRAIN_HI
            break

    if border:
        col = BORDER
    return col


def render():
    buf = [[None] * SS for _ in range(SS)]
    for j in range(SS):
        y = (j + 0.5) / SS
        row = buf[j]
        for i in range(SS):
            row[i] = pixel((i + 0.5) / SS, y)
    return buf


def downsample(buf, size):
    out = []
    for j in range(size):
        y0, y1 = j * SS // size, (j + 1) * SS // size
        row = []
        for i in range(size):
            x0, x1 = i * SS // size, (i + 1) * SS // size
            r = g = b = a = 0.0
            n = 0
            for yy in range(y0, y1):
                src = buf[yy]
                for xx in range(x0, x1):
                    n += 1
                    c = src[xx]
                    if c is not None:
                        r += c[0]
                        g += c[1]
                        b += c[2]
                        a += 1
            if a == 0:
                row.append((0, 0, 0, 0))
            else:
                row.append((round(r / a), round(g / a), round(b / a), round(255 * a / n)))
        out.append(row)
    return out


def png_bytes(img):
    size = len(img)
    raw = b"".join(b"\x00" + bytes(v for px in row for v in px) for row in img)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def bmp_bytes(img):
    size = len(img)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0)
    pixels = b"".join(bytes((px[2], px[1], px[0], px[3])) for row in reversed(img) for px in row)
    mask_row = ((size + 31) // 32) * 4
    mask = b"\x00" * (mask_row * size)
    return header + pixels + mask


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    buf = render()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = {s: downsample(buf, s) for s in sizes}

    with open(os.path.join(OUT_DIR, "app-icon-256.png"), "wb") as f:
        f.write(png_bytes(images[256]))

    blobs = [(s, png_bytes(images[s]) if s == 256 else bmp_bytes(images[s])) for s in sizes]
    offset = 6 + 16 * len(blobs)
    entries = b""
    data = b""
    for s, blob in blobs:
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(blob), offset + len(data))
        data += blob
    with open(os.path.join(OUT_DIR, "app.ico"), "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(blobs)) + entries + data)
    print("wrote", os.path.abspath(OUT_DIR))


if __name__ == "__main__":
    main()
