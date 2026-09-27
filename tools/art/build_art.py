#!/usr/bin/env python3
"""Regenerates the built-in art pack in one run.

    python3 tools/art/build_art.py            # Blender renders + tiles + sheets
    python3 tools/art/build_art.py --no-render  # reuse cached renders, repack only
    python3 tools/art/build_art.py --samples 8  # faster, noisier preview renders

Writes `src/FarmEngine.Rendering/Assets/builtin/{tiles,objects,characters}.png`
and `manifest.json`. Raw renders are cached in `tools/art/.cache/` (ignored by
git). The generated PNGs are committed, so `dotnet build` never needs Python
or Blender; this script is only for changing the art.
"""

from __future__ import annotations

import argparse
import shutil
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
DEFAULT_OUT = ROOT / "src" / "FarmEngine.Rendering" / "Assets" / "builtin"
CACHE = HERE / ".cache"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="output directory (default: the Rendering project's Assets/builtin)")
    parser.add_argument("--no-render", action="store_true", help="skip Blender and reuse the cached renders")
    parser.add_argument("--samples", type=int, default=None, help="Cycles samples per pixel (default 24)")
    parser.add_argument("--clean", action="store_true", help="delete the render cache first")
    args = parser.parse_args()

    sys.path.insert(0, str(HERE))
    start = time.time()
    renders = CACHE / "renders"
    if args.clean and CACHE.exists():
        shutil.rmtree(CACHE)
    if not args.no_render or not (renders / "index.json").exists():
        import render_objects

        count = render_objects.render_all(renders, args.samples or render_objects.SAMPLES)
        print(f"[build_art] rendered {count} images in {time.time() - start:.1f}s")

    import pixelate

    manifest = pixelate.build_pack(renders, args.out)
    total = sum((args.out / s["file"]).stat().st_size for s in manifest["sheets"]) + (args.out / "manifest.json").stat().st_size
    print(f"[build_art] {len(manifest['entries'])} entries, {total / 1024:.1f} KB in {args.out} ({time.time() - start:.1f}s total)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
