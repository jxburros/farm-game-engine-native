"""Builds low-poly models from primitives with `bpy` and renders them (Cycles, CPU)
into `<cache>/renders/` at 4x the target pixel size, plus an `index.json` that
`pixelate.py` turns into pixel-art sprite sheets.

No .blend files: every model is code. The camera is orthographic and tilted a
little (JRPG style); facing directions are made by rotating the camera rig
around the model. Characters get a keyframed 4-frame walk cycle (leg/arm swing
+ a bob), animals a 2-frame idle. Renders are deterministic: fixed Cycles seed,
fixed sample count, no denoiser, no adaptive sampling.

Run through `build_art.py`; or directly: `python3 render_objects.py <cache-dir>`.
"""

from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from palette import linear  # noqa: E402

SCALE = 4  # render at 4x the target pixel size
TILE_PX = 32
SAMPLES = 24
SEED = 7

TAU = math.tau

# Deterministic pseudo-random jitter without `random` (so results never depend on interpreter state).
def hash01(*parts: float) -> float:
    h = 2166136261
    for part in parts:
        for byte in str(round(part, 6)).encode():
            h = ((h ^ byte) * 16777619) & 0xFFFFFFFF
    return (h % 100003) / 100003


# ---------------------------------------------------------------------------
# Scene / rendering
# ---------------------------------------------------------------------------

class Renderer:
    def __init__(self, out_dir: Path, samples: int = SAMPLES):
        self.out_dir = out_dir
        self.out_dir.mkdir(parents=True, exist_ok=True)
        self.materials: dict[str, bpy.types.Material] = {}
        self.index: list[dict] = []
        self.count = 0
        self._setup(samples)

    def _setup(self, samples: int) -> None:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        scene = bpy.context.scene
        self.scene = scene
        scene.render.engine = "CYCLES"
        scene.cycles.device = "CPU"
        scene.cycles.samples = samples
        scene.cycles.use_adaptive_sampling = False
        scene.cycles.use_denoising = False
        scene.cycles.seed = SEED
        scene.cycles.use_animated_seed = False
        scene.cycles.max_bounces = 3
        scene.cycles.caustics_reflective = False
        scene.cycles.caustics_refractive = False
        scene.cycles.film_exposure = 1.0
        scene.render.film_transparent = True
        scene.render.resolution_percentage = 100
        scene.render.image_settings.file_format = "PNG"
        scene.render.image_settings.color_mode = "RGBA"
        scene.render.image_settings.color_depth = "8"
        scene.render.image_settings.compression = 50
        scene.render.dither_intensity = 0
        scene.view_settings.view_transform = "Standard"
        scene.view_settings.look = "None"
        scene.view_settings.exposure = 0
        scene.view_settings.gamma = 1
        for flag in [f for f in dir(scene.render) if f.startswith("use_stamp")]:
            try:
                setattr(scene.render, flag, False)
            except AttributeError:
                pass

        world = bpy.data.worlds.new("world")
        scene.world = world
        world.use_nodes = True
        background = world.node_tree.nodes["Background"]
        background.inputs[0].default_value = (0.75, 0.82, 1.0, 1)
        background.inputs[1].default_value = 0.45

        # Camera rig: an empty at the origin; rotating it around Z gives the facing rows.
        self.rig = bpy.data.objects.new("rig", None)
        scene.collection.objects.link(self.rig)

        cam_data = bpy.data.cameras.new("camera")
        cam_data.type = "ORTHO"
        cam_data.sensor_fit = "HORIZONTAL"
        cam_data.clip_start = 0.1
        cam_data.clip_end = 100
        self.camera = bpy.data.objects.new("camera", cam_data)
        self.camera.parent = self.rig
        scene.collection.objects.link(self.camera)
        scene.camera = self.camera

        sun_data = bpy.data.lights.new("sun", "SUN")
        sun_data.energy = 2.6
        sun_data.angle = math.radians(4)
        self.sun = bpy.data.objects.new("sun", sun_data)
        self.sun.parent = self.rig
        self.sun.rotation_euler = (math.radians(48), 0, math.radians(-28))
        scene.collection.objects.link(self.sun)

        self.model_root: bpy.types.Object | None = None

    # --- materials ---------------------------------------------------------

    def material(self, color: str, emission: float = 0.0, emission_color: str | None = None) -> bpy.types.Material:
        key = f"{color}:{emission}:{emission_color}"
        if key in self.materials:
            return self.materials[key]
        mat = bpy.data.materials.new(key)
        mat.use_nodes = True
        bsdf = mat.node_tree.nodes["Principled BSDF"]
        bsdf.inputs["Base Color"].default_value = linear(color)
        bsdf.inputs["Roughness"].default_value = 1.0
        bsdf.inputs["Specular IOR Level"].default_value = 0.0
        if emission > 0:
            bsdf.inputs["Emission Color"].default_value = linear(emission_color or color)
            bsdf.inputs["Emission Strength"].default_value = emission
        self.materials[key] = mat
        return mat

    # --- primitives --------------------------------------------------------

    def begin_model(self) -> bpy.types.Object:
        """Removes the previous model and creates a fresh root empty at the origin."""
        for ob in list(self.scene.collection.objects):
            if ob not in (self.rig, self.camera, self.sun):
                bpy.data.objects.remove(ob, do_unlink=True)
        for mesh in list(bpy.data.meshes):
            if mesh.users == 0:
                bpy.data.meshes.remove(mesh)
        self.model_root = bpy.data.objects.new("model", None)
        self.scene.collection.objects.link(self.model_root)
        return self.model_root

    def _finish(self, ob: bpy.types.Object, color: str, parent, emission: float, emission_color) -> bpy.types.Object:
        ob.data.materials.append(self.material(color, emission, emission_color))
        ob.parent = parent or self.model_root
        for poly in ob.data.polygons:
            poly.use_smooth = False
        return ob

    def cube(self, loc, size, color, rot=(0, 0, 0), parent=None, pivot_top=False, emission=0.0, emission_color=None):
        bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, 0))
        ob = bpy.context.object
        if pivot_top:
            for v in ob.data.vertices:
                v.co.z -= 0.5
        ob.scale = size
        ob.location = loc
        ob.rotation_euler = rot
        return self._finish(ob, color, parent, emission, emission_color)

    def sphere(self, loc, radius, color, subdiv=2, rot=(0, 0, 0), parent=None, emission=0.0, emission_color=None, smooth=True):
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=subdiv, radius=1, location=(0, 0, 0))
        ob = bpy.context.object
        ob.scale = radius if isinstance(radius, tuple) else (radius, radius, radius)
        ob.location = loc
        ob.rotation_euler = rot
        self._finish(ob, color, parent, emission, emission_color)
        if smooth:
            for poly in ob.data.polygons:
                poly.use_smooth = True
        return ob

    def cylinder(self, loc, radius, depth, color, verts=12, rot=(0, 0, 0), parent=None, pivot_top=False, emission=0.0, emission_color=None):
        bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=radius, depth=depth, location=(0, 0, 0))
        ob = bpy.context.object
        if pivot_top:
            for v in ob.data.vertices:
                v.co.z -= depth / 2
        ob.location = loc
        ob.rotation_euler = rot
        return self._finish(ob, color, parent, emission, emission_color)

    def cone(self, loc, radius1, radius2, depth, color, verts=8, rot=(0, 0, 0), parent=None, emission=0.0, emission_color=None):
        bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=radius1, radius2=radius2, depth=depth, location=(0, 0, 0))
        ob = bpy.context.object
        ob.location = loc
        ob.rotation_euler = rot
        return self._finish(ob, color, parent, emission, emission_color)

    def rock(self, loc, size, color, seed, subdiv=1, parent=None):
        """A jittered icosphere; the same seed always gives the same rock."""
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=subdiv, radius=1, location=(0, 0, 0))
        ob = bpy.context.object
        for i, v in enumerate(ob.data.vertices):
            j = 0.78 + 0.44 * hash01(seed, i)
            v.co = v.co * j
        ob.scale = size
        ob.location = loc
        return self._finish(ob, color, parent, 0.0, None)

    # --- rendering ---------------------------------------------------------

    def frame_camera(self, width_px: int, height_px: int, elevation_deg: float, front_y: float, margin: float = 0.04) -> None:
        """Ortho camera so `width_px/32` world units span the frame and the ground line at
        `front_y` (the model's front edge) sits `margin` units above the bottom of the frame."""
        w_units = width_px / TILE_PX
        h_units = height_px / TILE_PX
        elev = math.radians(elevation_deg)
        forward = Vector((0, math.cos(elev), -math.sin(elev)))
        up = Vector((0, math.sin(elev), math.cos(elev)))
        v_front = Vector((0, front_y, 0)).dot(up)
        v_center = v_front - margin + h_units / 2
        distance = 12.0
        self.camera.location = up * v_center - forward * distance
        self.camera.rotation_euler = (math.pi / 2 - elev, 0, 0)
        self.camera.data.ortho_scale = w_units
        self.scene.render.resolution_x = width_px * SCALE
        self.scene.render.resolution_y = height_px * SCALE

    def render(self, file_name: str, facing_deg: float = 0.0, frame: int = 1) -> str:
        self.rig.rotation_euler = (0, 0, math.radians(facing_deg))
        self.scene.frame_set(frame)
        path = self.out_dir / file_name
        self.scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True)
        self.count += 1
        return file_name

    def sprite(self, name: str, size: tuple[int, int], elevation: float, front_y: float, *, columns: list[str] | None = None, rows=None, anchor="bottom", footprint=(1, 1), directional=False, ticks_per_frame=None, extra=None, facing=None, frames=None):
        """Renders one sprite: `rows` x `columns` frames. `facing` (degrees per row) and
        `frames` (scene frame per column) drive the camera rig / animation; plain
        variants pass neither and get one render per column by re-building the model
        through the `columns` callables."""
        self.frame_camera(size[0], size[1], elevation, front_y)
        facing = facing or [0.0]
        frames = frames or [1]
        grid: list[list[str]] = []
        for r, angle in enumerate(facing):
            row = []
            for c, frame in enumerate(frames):
                file_name = f"{name}-{r}-{c}.png"
                row.append(self.render(file_name, angle, frame))
            grid.append(row)
        self.index.append({
            "name": name,
            "w": size[0],
            "h": size[1],
            "frames": grid,
            "anchor": anchor,
            "footprint": list(footprint),
            "directional": directional,
            "ticksPerFrame": ticks_per_frame,
            "extra": extra or {},
        })

    def variants(self, name: str, size, elevation, front_y, builders, **kwargs):
        """One sprite whose columns are different models (tree variants, machine states)."""
        self.frame_camera(size[0], size[1], elevation, front_y)
        row = []
        for c, build in enumerate(builders):
            self.begin_model()
            build()
            row.append(self.render(f"{name}-0-{c}.png"))
        self.index.append({
            "name": name,
            "w": size[0],
            "h": size[1],
            "frames": [row],
            "anchor": kwargs.get("anchor", "bottom"),
            "footprint": list(kwargs.get("footprint", (1, 1))),
            "directional": False,
            "ticksPerFrame": kwargs.get("ticks_per_frame"),
            "extra": kwargs.get("extra", {}),
        })

    def write_index(self) -> None:
        (self.out_dir / "index.json").write_text(json.dumps(self.index, indent=1, sort_keys=True))


# ---------------------------------------------------------------------------
# Models
# ---------------------------------------------------------------------------

def tree_oak(r: Renderer):
    r.cylinder((0, 0, 0.36), 0.12, 0.72, "BROWN2", verts=8)
    r.sphere((0, 0, 1.02), 0.44, "GREEN2")
    r.sphere((-0.24, -0.08, 1.16), 0.3, "GREEN3")
    r.sphere((0.22, 0.04, 1.2), 0.32, "GREEN3")
    r.sphere((0.0, -0.1, 1.36), 0.28, "GREEN3")
    r.sphere((-0.1, -0.3, 0.98), 0.2, "GREEN4")


def tree_cherry(r: Renderer):
    r.cylinder((0, 0, 0.3), 0.11, 0.6, "BROWN1", verts=8)
    r.sphere((0, 0, 0.98), (0.5, 0.45, 0.42), "GREEN3")
    r.sphere((0, -0.05, 1.28), (0.36, 0.34, 0.3), "GREEN4")
    for i in range(7):
        a = i / 7 * TAU
        r.sphere((0.42 * math.cos(a), 0.36 * math.sin(a) - 0.1, 1.0 + 0.28 * math.sin(a * 2)), 0.09, "PINK1", subdiv=1)


def tree_pine(r: Renderer):
    r.cylinder((0, 0, 0.25), 0.1, 0.5, "BROWN1", verts=8)
    r.cone((0, 0, 0.62), 0.48, 0.02, 0.7, "GREEN1", verts=10)
    r.cone((0, 0, 1.0), 0.38, 0.02, 0.62, "GREEN2", verts=10)
    r.cone((0, 0, 1.34), 0.27, 0.0, 0.5, "GREEN3", verts=10)


def stump(r: Renderer):
    r.cylinder((0, 0, 0.14), 0.28, 0.28, "BROWN2", verts=10)
    r.cylinder((0, 0, 0.29), 0.24, 0.02, "BROWN4", verts=10)
    r.cylinder((0, 0, 0.3), 0.12, 0.01, "BROWN3", verts=10)
    r.cube((0.28, 0.1, 0.05), (0.18, 0.12, 0.1), "BROWN2", rot=(0, 0, 0.5))
    r.cube((-0.26, -0.12, 0.05), (0.16, 0.12, 0.1), "BROWN2", rot=(0, 0, -0.4))


def rock_small(r: Renderer):
    r.rock((0, 0, 0.2), (0.38, 0.32, 0.26), "GREY2", seed=3)
    r.rock((0.3, -0.18, 0.08), (0.12, 0.1, 0.09), "GREY3", seed=4)


def boulder(r: Renderer):
    r.rock((0, 0.02, 0.3), (0.48, 0.42, 0.4), "GREY1", seed=5)
    r.rock((-0.3, -0.22, 0.12), (0.18, 0.15, 0.13), "GREY2", seed=6)


def mine_rock(r: Renderer):
    r.rock((0, 0, 0.22), (0.4, 0.36, 0.3), "GREY1", seed=8, subdiv=0)
    r.rock((0.26, -0.2, 0.08), (0.14, 0.12, 0.1), "GREY0", seed=9, subdiv=0)


def ore(r: Renderer, color: str, glow: float = 0.0, glow_color: str | None = None):
    r.rock((0, 0, 0.2), (0.38, 0.32, 0.25), "GREY2", seed=11)
    for i, (x, y, tilt) in enumerate(((-0.12, -0.1, -0.5), (0.1, -0.16, 0.45), (0.18, 0.08, 0.2), (-0.04, 0.12, -0.15))):
        r.cone((x, y, 0.38 + 0.02 * i), 0.07, 0.0, 0.26, color, verts=6, rot=(tilt * 0.6, tilt, 0), emission=glow, emission_color=glow_color)


def weeds(r: Renderer):
    for i in range(7):
        a = i / 7 * TAU + 0.3
        tilt = math.radians(28 + 8 * hash01(i))
        color = "GREEN4" if i % 3 == 0 else "GREEN3"
        r.cone((0.1 * math.cos(a), 0.08 * math.sin(a), 0.16), 0.04, 0.0, 0.36, color, verts=5, rot=(-tilt * math.sin(a), tilt * math.cos(a), 0))


# --- machines ---------------------------------------------------------------

def furnace(r: Renderer, working: bool):
    r.cube((0, 0, 0.28), (0.72, 0.62, 0.56), "GREY1")
    r.cube((0, 0, 0.58), (0.76, 0.66, 0.06), "GREY2")
    r.cylinder((0.2, 0.12, 0.76), 0.09, 0.34, "GREY2", verts=8)
    r.cube((0, -0.3, 0.22), (0.32, 0.06, 0.22), "OUTLINE")
    if working:
        r.cube((0, -0.31, 0.2), (0.24, 0.04, 0.14), "ORANGE", emission=5.0, emission_color="ORANGE")
        r.sphere((0, -0.32, 0.24), 0.07, "AMBER", subdiv=1, emission=6.0, emission_color="YELLOW")
    else:
        r.cube((0, -0.31, 0.16), (0.24, 0.04, 0.06), "RED0")


def preserves_jar(r: Renderer, working: bool):
    content = "RED1" if working else "PURPLE2"
    r.cylinder((0, 0, 0.26), 0.26, 0.5, content, verts=12)
    r.cylinder((0, 0, 0.52), 0.28, 0.04, "BROWN3", verts=12)
    r.cylinder((0, 0, 0.58), 0.27, 0.08, "CREAM", verts=12)
    r.cube((0, -0.265, 0.26), (0.2, 0.02, 0.16), "WHITE")
    if working:
        r.sphere((0.08, -0.1, 0.42), 0.04, "PINK1", subdiv=1)
        r.sphere((-0.1, -0.05, 0.36), 0.03, "PINK1", subdiv=1)


def kitchen(r: Renderer, working: bool):
    r.cube((0, 0, 0.25), (0.82, 0.6, 0.5), "BROWN4")
    r.cube((0, 0, 0.52), (0.86, 0.64, 0.05), "BROWN5")
    r.cube((-0.2, -0.305, 0.22), (0.3, 0.02, 0.32), "BROWN2")
    r.cube((-0.2, -0.32, 0.26), (0.06, 0.02, 0.04), "YELLOW")
    r.cube((0.2, 0, 0.56), (0.34, 0.34, 0.04), "GREY0")
    r.cylinder((0.2, 0, 0.66), 0.12, 0.14, "GREY2", verts=10)
    if working:
        r.cylinder((0.2, 0, 0.74), 0.13, 0.03, "GREY3", verts=10)
        r.sphere((0.16, -0.04, 0.9), 0.05, "WHITE", subdiv=1)
        r.sphere((0.26, 0.02, 1.02), 0.06, "WHITE", subdiv=1)


def workbench(r: Renderer, working: bool):
    for x, y in ((-0.34, -0.2), (0.34, -0.2), (-0.34, 0.2), (0.34, 0.2)):
        r.cube((x, y, 0.22), (0.07, 0.07, 0.44), "BROWN2")
    r.cube((0, 0, 0.47), (0.82, 0.52, 0.07), "BROWN4")
    r.cube((0, 0, 0.3), (0.7, 0.4, 0.04), "BROWN3")
    r.cylinder((0.18, -0.08, 0.53), 0.025, 0.3, "BROWN3", verts=6, rot=(0, math.radians(90), 0.4))
    r.cube((0.3, -0.13, 0.55), (0.1, 0.06, 0.08), "GREY2")
    if working:
        r.cube((-0.2, 0.08, 0.53), (0.32, 0.12, 0.05), "BROWN5")


def altar(r: Renderer, working: bool):
    r.cylinder((0, 0, 0.16), 0.3, 0.32, "GREY1", verts=8)
    r.cylinder((0, 0, 0.35), 0.35, 0.06, "GREY2", verts=8)
    glow = 3.0 if working else 1.0
    r.cone((0, 0, 0.62), 0.12, 0.0, 0.2, "PURPLE2", verts=6, emission=glow, emission_color="PURPLE3")
    r.cone((0, 0, 0.42), 0.12, 0.0, 0.2, "PURPLE1", verts=6, rot=(math.pi, 0, 0), emission=glow * 0.6, emission_color="PURPLE2")
    for x in (-0.24, 0.24):
        r.cylinder((x, -0.14, 0.44), 0.04, 0.12, "CREAM", verts=6)
        r.sphere((x, -0.14, 0.53), 0.035, "AMBER", subdiv=1, emission=4.0, emission_color="YELLOW")
    if working:
        r.cylinder((0, 0, 0.39), 0.3, 0.01, "TEAL", verts=12, emission=2.0, emission_color="TEAL")


def generic_machine(r: Renderer, working: bool):
    r.cube((0, 0, 0.26), (0.62, 0.56, 0.52), "BROWN3")
    for z in (0.04, 0.5):
        r.cube((0, 0, z), (0.64, 0.58, 0.05), "BROWN1")
    r.cylinder((0.1, -0.29, 0.3), 0.15, 0.04, "GREY2", verts=8, rot=(math.radians(90), 0, 0))
    r.cylinder((0.1, -0.31, 0.3), 0.06, 0.04, "GREY0", verts=8, rot=(math.radians(90), 0, 0))
    if working:
        r.sphere((0.2, -0.2, 0.68), 0.05, "WHITE", subdiv=1)


# --- items ------------------------------------------------------------------

def item_sack(r: Renderer):
    r.sphere((0, 0, 0.2), (0.24, 0.22, 0.2), "BROWN4")
    r.cylinder((0, 0, 0.4), 0.08, 0.12, "BROWN3", verts=8)
    r.cube((0, 0, 0.4), (0.19, 0.19, 0.03), "CREAM")


def item_pouch(r: Renderer):
    r.sphere((0, 0, 0.16), (0.2, 0.18, 0.16), "BROWN5")
    r.cylinder((0, 0, 0.32), 0.06, 0.08, "BROWN3", verts=8)
    r.cone((0.06, -0.02, 0.42), 0.05, 0.0, 0.14, "GREEN3", verts=5, rot=(0, 0.4, 0))
    r.cone((-0.05, 0.02, 0.4), 0.04, 0.0, 0.12, "GREEN4", verts=5, rot=(0, -0.5, 0))


def item_crate(r: Renderer):
    r.cube((0, 0, 0.16), (0.42, 0.42, 0.32), "BROWN3")
    for z in (0.03, 0.29):
        r.cube((0, 0, z), (0.44, 0.44, 0.04), "BROWN1")
    r.cube((0, -0.215, 0.16), (0.04, 0.02, 0.3), "BROWN1")


# --- animals ----------------------------------------------------------------

def chicken(r: Renderer) -> dict:
    body = r.sphere((0, 0.02, 0.28), (0.22, 0.27, 0.2), "WHITE")
    head = bpy.data.objects.new("head", None)
    r.scene.collection.objects.link(head)
    head.parent = r.model_root
    head.location = (0, -0.2, 0.46)
    r.sphere((0, 0, 0), 0.13, "WHITE", parent=head)
    r.cone((0, -0.16, -0.01), 0.045, 0.0, 0.12, "ORANGE", verts=6, rot=(-math.pi / 2, 0, 0), parent=head)
    r.cube((0, 0.0, 0.14), (0.04, 0.1, 0.06), "RED1", parent=head)
    r.cube((0, -0.1, -0.09), (0.03, 0.04, 0.06), "RED1", parent=head)
    for x in (-0.08, 0.08):
        r.cube((x, -0.09, 0.03), (0.03, 0.02, 0.035), "OUTLINE", parent=head)
    r.sphere((0, 0.26, 0.4), (0.06, 0.1, 0.12), "GREY4", subdiv=1, rot=(0.5, 0, 0))
    for x in (-0.07, 0.07):
        r.cylinder((x, 0.0, 0.08), 0.02, 0.16, "ORANGE", verts=6)
        r.cube((x, -0.03, 0.005), (0.07, 0.09, 0.01), "ORANGE")
    for x, mirror in ((-0.2, 1), (0.2, -1)):
        r.sphere((x, 0.02, 0.28), (0.06, 0.16, 0.12), "GREY4", subdiv=1)
    return {"head": head, "body": body}


def cow(r: Renderer) -> dict:
    body = r.cube((0, 0.06, 0.5), (0.5, 0.78, 0.4), "BROWN5")
    body.modifiers.new("bevel", "BEVEL").width = 0.08
    body.modifiers["bevel"].segments = 2
    for x, y, z, sy, sz in ((-0.26, 0.2, 0.5, 0.26, 0.2), (0.26, -0.1, 0.56, 0.22, 0.18), (0.0, 0.3, 0.71, 0.24, 0.02)):
        r.cube((x, y, z), (0.02, sy, sz) if x else (0.3, sy, sz), "BROWN1")
    head = bpy.data.objects.new("head", None)
    r.scene.collection.objects.link(head)
    head.parent = r.model_root
    head.location = (0, -0.5, 0.58)
    r.cube((0, 0, 0), (0.3, 0.3, 0.28), "BROWN5", parent=head)
    r.cube((0, -0.17, -0.06), (0.26, 0.1, 0.14), "PINK1", parent=head)
    for x in (-0.06, 0.06):
        r.cube((x, -0.225, -0.05), (0.03, 0.01, 0.03), "OUTLINE", parent=head)
        r.cube((x * 1.6, -0.155, 0.06), (0.04, 0.01, 0.04), "OUTLINE", parent=head)
    for x in (-0.15, 0.15):
        r.cone((x * 1.15, 0.02, 0.15), 0.035, 0.0, 0.12, "CREAM", verts=6, rot=(0, x * 2.2, 0), parent=head)
        r.cube((x * 1.25, 0.04, 0.02), (0.1, 0.03, 0.08), "BROWN5", parent=head)
    for x, y in ((-0.16, -0.24), (0.16, -0.24), (-0.16, 0.32), (0.16, 0.32)):
        r.cube((x, y, 0.15), (0.11, 0.11, 0.3), "BROWN4")
        r.cube((x, y, 0.02), (0.12, 0.12, 0.04), "BROWN1")
    r.sphere((0, 0.22, 0.32), (0.14, 0.12, 0.08), "PINK1", subdiv=1)
    r.cylinder((0, 0.47, 0.5), 0.025, 0.3, "BROWN4", verts=6, rot=(0.3, 0, 0))
    r.sphere((0, 0.5, 0.36), 0.05, "BROWN1", subdiv=1)
    return {"head": head, "body": body}


# --- characters --------------------------------------------------------------

CHARACTERS = {
    "char-player": dict(skin="SKIN2", hair="BROWN1", shirt="BLUE2", sleeve="BLUE1", pants="BROWN2", shoes="BROWN0", hat="band", hat_color="RED1"),
    "npc-farmer": dict(skin="SKIN1", hair="GREY4", shirt="GREEN2", sleeve="GREEN1", pants="BLUE0", shoes="BROWN0", hat="straw", hat_color="OCHRE1", beard="GREY4"),
    "npc-merchant": dict(skin="SKIN2", hair="RED0", shirt="PINK0", sleeve="RED1", pants="PURPLE0", shoes="BROWN1", hat="scarf", hat_color="AMBER", apron="CREAM", bag="BROWN3"),
    "npc-villager": dict(skin="SKIN1", hair="BROWN0", shirt="TEAL", sleeve="DARKTEAL", pants="BROWN2", shoes="BROWN1", hat="none", hat_color="BROWN0"),
}


def character(r: Renderer, spec: dict) -> dict:
    parts: dict = {}
    # legs (pivot at the hip so they swing)
    hip = 0.38
    for side, x in (("left", -0.1), ("right", 0.1)):
        leg = r.cube((x, 0, hip), (0.15, 0.15, 0.38), spec["pants"], pivot_top=True)
        r.cube((0, -0.02, -0.34), (1.05, 1.25, 0.16), spec["shoes"], parent=leg)
        parts[f"leg_{side}"] = leg
    # torso
    r.cube((0, 0, 0.6), (0.42, 0.26, 0.44), spec["shirt"])
    r.cube((0, 0, 0.4), (0.36, 0.22, 0.06), spec["pants"])
    if spec.get("apron"):
        r.cube((0, -0.135, 0.52), (0.28, 0.02, 0.36), spec["apron"])
    # arms (pivot at the shoulder)
    for side, x in (("left", -0.28), ("right", 0.28)):
        arm = r.cube((x, 0, 0.8), (0.12, 0.12, 0.38), spec["sleeve"], pivot_top=True)
        r.cube((0, 0, -0.38), (0.9, 0.9, 0.12), spec["skin"], parent=arm)
        parts[f"arm_{side}"] = arm
    if spec.get("bag"):
        r.cube((0.36, 0.02, 0.5), (0.14, 0.2, 0.2), spec["bag"])
    # head + face
    r.cube((0, 0, 1.0), (0.42, 0.36, 0.4), spec["skin"])
    for x in (-0.09, 0.09):
        r.cube((x, -0.18, 0.99), (0.05, 0.02, 0.07), "OUTLINE")
    r.cube((0, -0.18, 0.9), (0.08, 0.01, 0.02), spec.get("mouth", "SKIN0"))
    if spec.get("beard"):
        r.cube((0, -0.16, 0.84), (0.3, 0.08, 0.12), spec["beard"])
    # hair: cap + back
    r.cube((0, 0.02, 1.18), (0.46, 0.4, 0.14), spec["hair"])
    r.cube((0, 0.16, 1.02), (0.46, 0.1, 0.34), spec["hair"])
    hat = spec.get("hat", "none")
    if hat == "straw":
        r.cylinder((0, 0, 1.24), 0.36, 0.04, spec["hat_color"], verts=12)
        r.cylinder((0, 0, 1.33), 0.22, 0.16, spec["hat_color"], verts=12)
        r.cylinder((0, 0, 1.28), 0.23, 0.04, "BROWN1", verts=12)
    elif hat == "band":
        r.cube((0, 0, 1.16), (0.47, 0.41, 0.06), spec["hat_color"])
    elif hat == "scarf":
        r.cube((0, 0.02, 1.2), (0.47, 0.41, 0.12), spec["hat_color"])
        r.cube((0, 0.14, 1.1), (0.47, 0.14, 0.24), spec["hat_color"])
        r.cube((0.2, -0.17, 1.24), (0.1, 0.06, 0.08), spec["hat_color"])
    parts["root"] = r.model_root
    return parts


def keyframe_walk(parts: dict, swing_deg: float = 32.0) -> None:
    """4 frames: stand, left leg forward, stand, right leg forward (arms opposite, a bob)."""
    swing = math.radians(swing_deg)
    poses = [0.0, 1.0, 0.0, -1.0]
    for f, s in enumerate(poses, start=1):
        parts["leg_left"].rotation_euler = (-s * swing, 0, 0)
        parts["leg_right"].rotation_euler = (s * swing, 0, 0)
        parts["arm_left"].rotation_euler = (s * swing * 0.8, 0, 0)
        parts["arm_right"].rotation_euler = (-s * swing * 0.8, 0, 0)
        parts["root"].location = (0, 0, 0.035 if s == 0.0 else 0.0)
        for key in ("leg_left", "leg_right", "arm_left", "arm_right"):
            parts[key].keyframe_insert("rotation_euler", frame=f)
        parts["root"].keyframe_insert("location", frame=f)
    for ob in (parts["leg_left"], parts["leg_right"], parts["arm_left"], parts["arm_right"], parts["root"]):
        for fcurve in ob.animation_data.action.fcurves:
            for kp in fcurve.keyframe_points:
                kp.interpolation = "CONSTANT"


def keyframe_idle(parts: dict, dz: float, dy: float = 0.0) -> None:
    head = parts["head"]
    base = tuple(head.location)
    head.location = base
    head.keyframe_insert("location", frame=1)
    head.location = (base[0], base[1] + dy, base[2] + dz)
    head.keyframe_insert("location", frame=2)
    for fcurve in head.animation_data.action.fcurves:
        for kp in fcurve.keyframe_points:
            kp.interpolation = "CONSTANT"


# --- crops -------------------------------------------------------------------

CROPS = {
    "wheat": dict(kind="stalks", leaf="GREEN3", fruit="YELLOW", height=1.0, count=7),
    "corn": dict(kind="corn", leaf="GREEN2", fruit="YELLOW", height=1.15),
    "tomato": dict(kind="bush", leaf="GREEN2", fruit="RED1", fruit_r=0.085, height=0.75, count=5, stake=True),
    "carrot": dict(kind="root", leaf="GREEN3", fruit="ORANGE", height=0.45),
    "potato": dict(kind="bush", leaf="GREEN1", fruit="WHITE", fruit_r=0.045, height=0.5, count=4, tubers=True),
    "strawberry": dict(kind="low", leaf="GREEN2", fruit="RED1", fruit_r=0.07, height=0.32, count=5),
    "pumpkin": dict(kind="vine", leaf="GREEN2", fruit="ORANGE", fruit_r=0.3, height=0.32),
    "cauliflower": dict(kind="head", leaf="GREEN3", fruit="WHITE", fruit_r=0.22, height=0.42),
    "blueberry": dict(kind="bush", leaf="GREEN1", fruit="PURPLE1", fruit_r=0.05, height=0.7, count=9),
    "generic": dict(kind="bush", leaf="GREEN3", fruit="AMBER", fruit_r=0.08, height=0.7, count=4),
}


def sprout(r: Renderer, color: str):
    r.cylinder((0, 0, 0.09), 0.025, 0.18, color, verts=5)
    r.sphere((-0.1, 0.0, 0.17), (0.1, 0.05, 0.04), "GREEN4", subdiv=1, rot=(0, -0.4, 0))
    r.sphere((0.1, 0.0, 0.19), (0.1, 0.05, 0.04), "GREEN4", subdiv=1, rot=(0, 0.4, 0))


def crop_model(r: Renderer, spec: dict, stage: int):
    kind = spec["kind"]
    leaf = spec["leaf"]
    if stage == 0:
        sprout(r, leaf)
        return
    size = (0.4, 0.68, 0.92, 1.0)[stage - 1]
    ripe = stage == 4
    fruit_color = spec["fruit"] if ripe else "GREEN4"
    height = spec["height"] * size

    if kind == "stalks":
        count = spec["count"]
        for i in range(count):
            a = i / count * TAU
            rad = 0.2 + 0.1 * hash01(i, 1)
            x, y = rad * math.cos(a), rad * math.sin(a) * 0.7
            tilt = 0.12 + 0.1 * hash01(i, 2)
            h = height * (0.85 + 0.15 * hash01(i, 3))
            r.cylinder((x, y, h / 2), 0.022, h, leaf, verts=5, rot=(-tilt * math.sin(a), tilt * math.cos(a), 0))
            if stage >= 3:
                r.sphere((x - tilt * h * 0.5 * math.sin(a) * 0.0, y, h + 0.06), (0.05, 0.05, 0.12), fruit_color, subdiv=1)
    elif kind == "corn":
        r.cylinder((0, 0, height / 2), 0.045, height, leaf, verts=6)
        for i in range(4):
            a = i / 4 * TAU + 0.6
            z = height * (0.3 + 0.15 * i)
            r.cone((0.22 * math.cos(a) * size, 0.22 * math.sin(a) * size, z), 0.06, 0.0, 0.5 * size, "GREEN3", verts=4, rot=(0.9 * math.sin(a), -0.9 * math.cos(a), 0))
        if stage >= 3:
            for i, a in enumerate((0.4, 3.6)):
                r.cylinder((0.1 * math.cos(a), 0.1 * math.sin(a), height * 0.55 + 0.1 * i), 0.06, 0.26, fruit_color, verts=6, rot=(0.25 * math.sin(a), -0.25 * math.cos(a), 0))
        r.cone((0, 0, height + 0.1), 0.03, 0.0, 0.2, "GREEN5" if ripe else leaf, verts=5)
    elif kind == "bush":
        radius = 0.24 * size
        if spec.get("stake"):
            r.cylinder((0.02, 0.04, height * 0.5), 0.018, height, "BROWN3", verts=5)
        for i, (x, y, z) in enumerate(((0, 0, 0.55), (-0.16, -0.06, 0.4), (0.15, 0.04, 0.48), (0.0, -0.12, 0.8))):
            r.sphere((x * size, y * size, z * height + radius * 0.4), radius * (1.0 - 0.1 * (i == 3)), leaf, subdiv=1)
        if stage >= 3:
            count = spec["count"]
            fr = spec["fruit_r"] * (1.0 if ripe else 0.6)
            for i in range(count):
                a = i / count * TAU + 0.5
                r.sphere((math.cos(a) * radius * 0.9, -abs(math.sin(a)) * radius * 0.5 - 0.12 * size, height * (0.35 + 0.5 * hash01(i, 5)) + 0.1), fr, fruit_color, subdiv=1)
        if spec.get("tubers") and ripe:
            for x in (-0.16, 0.0, 0.16):
                r.sphere((x, -0.22, 0.05), (0.08, 0.06, 0.05), "BROWN4", subdiv=1)
    elif kind == "root":
        for i in range(8):
            a = i / 8 * TAU
            tilt = 0.35 + 0.2 * hash01(i, 6)
            r.cone((0.05 * math.cos(a), 0.05 * math.sin(a), height * 0.5), 0.045, 0.0, height, leaf, verts=4, rot=(-tilt * math.sin(a), tilt * math.cos(a), 0))
            r.cone((0.05 * math.cos(a), 0.05 * math.sin(a), height * 0.5), 0.02, 0.0, height * 1.05, "GREEN4", verts=4, rot=(-tilt * 0.5 * math.sin(a), tilt * 0.5 * math.cos(a), 0))
        if stage >= 3:
            r.cone((0, -0.06, 0.05), 0.09 if ripe else 0.05, 0.02, 0.12, fruit_color if ripe else "ORANGE", verts=8, rot=(math.pi, 0, 0))
    elif kind == "low":
        for i in range(5):
            a = i / 5 * TAU
            r.sphere((0.16 * size * math.cos(a), 0.12 * size * math.sin(a), 0.08), (0.14 * size, 0.1 * size, 0.06), leaf, subdiv=1)
        r.sphere((0, 0, 0.14 * size + 0.04), (0.16 * size, 0.14 * size, 0.1 * size), "GREEN3", subdiv=1)
        if stage >= 3:
            for i in range(spec["count"]):
                a = i / spec["count"] * TAU + 0.2
                r.sphere((0.22 * math.cos(a), 0.16 * math.sin(a) - 0.06, 0.07), spec["fruit_r"] * (1.0 if ripe else 0.55), spec["fruit"] if ripe else "WHITE", subdiv=1)
    elif kind == "vine":
        for i in range(4):
            a = i / 4 * TAU + 0.4
            r.sphere((0.26 * size * math.cos(a), 0.2 * size * math.sin(a), 0.06), (0.16 * size, 0.12 * size, 0.05), leaf, subdiv=1)
            r.cylinder((0.13 * size * math.cos(a), 0.1 * size * math.sin(a), 0.03), 0.015, 0.3 * size, "GREEN1", verts=4, rot=(0, math.radians(88), a))
        if stage >= 3:
            fr = spec["fruit_r"] * (1.0 if ripe else 0.5)
            r.sphere((0, -0.05, fr * 0.8), (fr, fr * 0.9, fr * 0.75), fruit_color, subdiv=2)
            r.cylinder((0, -0.05, fr * 1.5), 0.03, 0.08, "BROWN2", verts=5)
    elif kind == "head":
        for i in range(6):
            a = i / 6 * TAU
            r.sphere((0.24 * size * math.cos(a), 0.2 * size * math.sin(a), 0.12 * size), (0.15 * size, 0.1 * size, 0.09 * size), leaf, subdiv=1, rot=(0, 0.5, a))
        if stage >= 3:
            fr = spec["fruit_r"] * (1.0 if ripe else 0.55)
            r.sphere((0, -0.02, 0.1 + fr * 0.7), fr, spec["fruit"] if ripe else "GREEN5", subdiv=2)
        else:
            r.sphere((0, 0, 0.16 * size), 0.1 * size, "GREEN4", subdiv=1)


def withered(r: Renderer):
    for i in range(5):
        a = i / 5 * TAU
        tilt = 0.45 + 0.2 * hash01(i, 9)
        r.cylinder((0.12 * math.cos(a), 0.1 * math.sin(a), 0.22), 0.022, 0.5, "BROWN2", verts=5, rot=(-tilt * math.sin(a), tilt * math.cos(a), 0))
        r.sphere((0.25 * math.cos(a), 0.2 * math.sin(a), 0.4), (0.06, 0.04, 0.02), "BROWN3", subdiv=1)


# ---------------------------------------------------------------------------
# Catalogue
# ---------------------------------------------------------------------------

FACING = [0.0, 90.0, -90.0, 180.0]  # down, left, right, up (camera rig rotation about Z)


def render_all(out_dir: Path, samples: int = SAMPLES) -> int:
    r = Renderer(out_dir, samples)
    obj = (32, 32)
    tall = (32, 48)
    tree = (32, 64)
    elev_obj = 30.0

    # Gathering nodes
    r.variants("node-tree", tree, elev_obj, -0.45, [lambda: tree_oak(r), lambda: tree_cherry(r), lambda: tree_pine(r)], footprint=(1, 1))
    r.variants("node-stump", obj, elev_obj, -0.4, [lambda: stump(r)])
    r.variants("node-rock", obj, elev_obj, -0.4, [lambda: rock_small(r)])
    r.variants("node-boulder", obj, elev_obj, -0.45, [lambda: boulder(r)])
    r.variants("node-mine-rock", obj, elev_obj, -0.4, [lambda: mine_rock(r)])
    r.variants("node-copper-ore", obj, elev_obj, -0.4, [lambda: ore(r, "COPPER", 0.6, "ORANGE")])
    r.variants("node-iron-ore", obj, elev_obj, -0.4, [lambda: ore(r, "SLATE", 0.2, "GREY4")])
    r.variants("node-quartz", obj, elev_obj, -0.4, [lambda: ore(r, "TEAL", 1.6, "BLUE4")])
    r.variants("node-weeds", obj, elev_obj, -0.35, [lambda: weeds(r)])

    # Machines: column 0 idle, column 1 working
    for name, build in (("machine-furnace", furnace), ("machine-preserves", preserves_jar), ("machine-kitchen", kitchen), ("machine-workbench", workbench), ("machine-altar", altar), ("machine-generic", generic_machine)):
        r.variants(name, tall, elev_obj, -0.4, [lambda b=build: b(r, False), lambda b=build: b(r, True)], extra={"states": ["idle", "working"]})

    # Dropped items
    r.variants("item-generic", obj, elev_obj, -0.3, [lambda: item_sack(r)])
    r.variants("item-seed", obj, elev_obj, -0.3, [lambda: item_pouch(r)])
    r.variants("item-material", obj, elev_obj, -0.3, [lambda: item_crate(r)])

    # Animals: 4 facings x 2 idle frames
    for name, build, bob in (("animal-chicken", chicken, (-0.09, -0.04)), ("animal-cow", cow, (-0.14, -0.02))):
        r.begin_model()
        parts = build(r)
        keyframe_idle(parts, *bob)
        r.sprite(name, tall, elev_obj, -0.5, facing=FACING, frames=[1, 2], directional=True, ticks_per_frame=24)

    # Characters: 4 facings x 4 walk frames (frame 0 is the idle pose)
    for name, spec in CHARACTERS.items():
        r.begin_model()
        parts = character(r, spec)
        keyframe_walk(parts)
        r.sprite(name, tall, 22.0, -0.2, facing=FACING, frames=[1, 2, 3, 4], directional=True, ticks_per_frame=6, extra={"idleFrame": 0})

    # Crops: 5 growth stages per crop
    for crop, spec in CROPS.items():
        r.variants(f"crop-{crop}", tall, elev_obj, -0.35, [lambda s=stage, sp=spec: crop_model(r, sp, s) for stage in range(5)], extra={"stages": 5})
    r.variants("crop-withered", tall, elev_obj, -0.35, [lambda: withered(r)])

    r.write_index()
    return r.count


if __name__ == "__main__":
    target = Path(sys.argv[1] if len(sys.argv) > 1 else ".cache") / "renders"
    samples = int(sys.argv[2]) if len(sys.argv) > 2 else SAMPLES
    import time

    start = time.time()
    count = render_all(target, samples)
    print(f"rendered {count} images into {target} in {time.time() - start:.1f}s")
