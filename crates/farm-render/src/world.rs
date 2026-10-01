//! Port of `SkiaWorldRenderer.RenderCore`: turns a [`WorldSnapshot`] into a [`DrawList`] with the
//! same passes, ordering and geometry as the Skia reference:
//!
//! 1. ground: tile background, overlay and object layers, soil states, ladders;
//! 2. soft drop shadows under entities and objects (built-in art only);
//! 3. y-sorted objects and entities (nodes, machines, crops, items, NPCs, animals, the player)
//!    so tall things overlap whoever stands behind them; ties keep insertion order;
//! 4. atmosphere: day/night tint (modulate), seasonal foliage, rain or snow;
//! 5. floating pops and the Edit Mode grid.
//!
//! Art per layer or entity: creator-bound sprite or image → built-in art → the original colored
//! shapes. Coordinates are world pixels at 1×; hosts scale when rasterizing. Geometry is computed
//! in `f64` and converted to `f32` exactly where the C# renderer converts to `float`.

use crate::atmosphere::{self, WeatherOverlay};
use crate::builtin_art::{BuiltinArt, BuiltinArtEntry};
use crate::canvas2d::{self, entity_pixel_origin};
use crate::css_color::{self, Color};
use crate::draw::{DrawCmd, DrawList, FontId, Rect, Sampling, TextAlign, TextStroke};
use crate::images::{ImageId, ImageStore};
use crate::num::{cs_clamp, cs_compare, cs_max, cs_min, round_half_even, to_byte, to_int};
use crate::snapshot::{SnapshotEntity, SnapshotSprite, SnapshotTile, WorldSnapshot};
use std::sync::Arc;

const BACKGROUND_TINT: Color = Color::rgba(0x1a, 0x1a, 0x2e, 0x10);
const SHADOW_COLOR: Color = Color::rgba(0, 0, 0, 0x48);
const RAIN_TINT: Color = Color::rgb(0xC8, 0xD2, 0xE6);
const RAIN_DROP: Color = Color::rgba(0xD8, 0xEC, 0xFF, 0x9A);
const SNOW_FLAKE: Color = Color::rgba(0xFF, 0xFF, 0xFF, 0xDC);

/// Builds the draw list of a world frame. `art` is the built-in pack for anything the project
/// binds no art to (`None` draws the original colored shapes only); `images` decodes and caches
/// every image the frame uses (their sizes decide which sprites are drawable).
pub fn build_world(snapshot: &WorldSnapshot, art: Option<&BuiltinArt>, images: &mut ImageStore) -> DrawList {
    let mut list = DrawList::new();
    build_world_into(&mut list, snapshot, art, images);
    list
}

/// [`build_world`] appending to an existing list. The world is wrapped in one save/restore pair,
/// so whatever follows draws in the list's original coordinate space.
pub fn build_world_into(
    list: &mut DrawList,
    snapshot: &WorldSnapshot,
    art: Option<&BuiltinArt>,
    images: &mut ImageStore,
) {
    images.begin_frame();
    let mut builder = Builder::new(list, snapshot, art, images);
    builder.world();
}

/// Trees and weeds take the seasonal foliage tint; rocks and ores don't.
fn is_foliage(entry: Option<&BuiltinArtEntry>) -> bool {
    entry.is_some_and(|entry| entry.name.starts_with("node-tree") || entry.name.starts_with("node-weeds"))
}

fn rect(x: f64, y: f64, width: f64, height: f64) -> Rect {
    Rect::new(x as f32, y as f32, width as f32, height as f32)
}

fn parse(css: &str) -> Color {
    css_color::parse(Some(css))
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DrawKind {
    Node,
    Machine,
    Crop,
    Item,
    Entity,
    Player,
}

/// One thing to draw in the y-sorted pass.
struct Drawable<'a> {
    sort_y: f64,
    seq: usize,
    kind: DrawKind,
    tile: Option<&'a SnapshotTile>,
    entity: Option<&'a SnapshotEntity>,
    tile_x: i32,
    tile_y: i32,
    px: f64,
    py: f64,
}

struct Builder<'a, 'l> {
    list: &'l mut DrawList,
    snapshot: &'a WorldSnapshot,
    art: Option<&'a BuiltinArt>,
    images: &'l mut ImageStore,
    sampling: Sampling,
    ts: f64,
    padding: f64,
    pitch: f64,
    tick: f64,
    season: Option<&'a str>,
    foliage_tint: Option<Color>,
    art_scale: f64,
}

impl<'a, 'l> Builder<'a, 'l> {
    fn new(
        list: &'l mut DrawList,
        snapshot: &'a WorldSnapshot,
        art: Option<&'a BuiltinArt>,
        images: &'l mut ImageStore,
    ) -> Self {
        let season = snapshot.atmosphere.as_ref().and_then(|a| a.season.as_deref());
        Self {
            list,
            snapshot,
            art,
            images,
            // Pixel art: nearest-neighbour sampling (canvas imageSmoothingEnabled = false).
            sampling: if snapshot.pixel_art == Some(false) { Sampling::Smooth } else { Sampling::Nearest },
            ts: snapshot.tile_size,
            padding: snapshot.padding,
            pitch: snapshot.pitch(),
            tick: snapshot.tick,
            season,
            foliage_tint: atmosphere::foliage_tint(season),
            art_scale: art.map_or(1.0, |art| snapshot.tile_size / f64::from(art.tile_size())),
        }
    }

    fn push(&mut self, command: DrawCmd) {
        self.list.push(command);
    }

    fn fill_rect(&mut self, rect: Rect, color: Color) {
        self.push(DrawCmd::FillRect { rect, color, anti_alias: false });
    }

    /// The image for a source with its size, decoding it on first use.
    fn image(&mut self, source: &Arc<str>) -> Option<(ImageId, f64, f64)> {
        let id = self.images.get_shared(source)?;
        let (width, height) = self.images.size(id)?;
        Some((id, f64::from(width), f64::from(height)))
    }

    fn push_image(&mut self, id: ImageId, src: Rect, dst: Rect, opacity: f64, tint: Option<Color>) {
        // The paint alpha is a byte: (byte)Math.Round(255 * opacity).
        let alpha = to_byte(cs_clamp(round_half_even(255.0 * opacity), 0.0, 255.0));
        self.push(DrawCmd::Image {
            image: id,
            src,
            dst,
            opacity: f32::from(alpha) / 255.0,
            sampling: self.sampling,
            tint,
        });
    }

    /// Creator sprites: scaled to fit the box, bottom-aligned (TS `drawSprite`).
    fn draw_sprite(
        &mut self,
        sprite: Option<&SnapshotSprite>,
        dx: f64,
        dy: f64,
        dw: f64,
        dh: f64,
        opacity: f64,
    ) -> bool {
        let Some(sprite) = sprite else { return false };
        let Some((id, width, height)) = self.image(&sprite.image_url) else { return false };
        if width == 0.0 || height == 0.0 {
            return false;
        }
        let sw = if sprite.frame_width != 0.0 { sprite.frame_width } else { width };
        let sh = if sprite.frame_height != 0.0 { sprite.frame_height } else { height };
        let sx = sprite.source_x.unwrap_or(sprite.frame * sw);
        let sy = sprite.source_y.unwrap_or(sprite.row * sh);
        if sx < 0.0 || sy < 0.0 || sx + sw > width || sy + sh > height {
            return false;
        }
        let scale = cs_min(dw / sw, dh / sh);
        let dst = rect(dx + ((dw - (sw * scale)) / 2.0), dy + dh - (sh * scale), sw * scale, sh * scale);
        self.push_image(id, rect(sx, sy, sw, sh), dst, opacity, None);
        true
    }

    /// Built-in sprites: drawn at the pack's native scale (one sheet pixel = ts/32 world pixels),
    /// centred on the tile, standing on its bottom edge (or filling it for ground tiles).
    fn draw_builtin(
        &mut self,
        sprite: Option<SnapshotSprite>,
        px: f64,
        py: f64,
        opacity: f64,
        foliage: bool,
        top: bool,
    ) -> bool {
        let Some(sprite) = sprite else { return false };
        let Some((id, width, height)) = self.image(&sprite.image_url) else { return false };
        let (sw, sh) = (sprite.frame_width, sprite.frame_height);
        let sx = sprite.source_x.unwrap_or(0.0);
        let sy = sprite.source_y.unwrap_or(0.0);
        if sw <= 0.0 || sh <= 0.0 || sx + sw > width || sy + sh > height {
            return false;
        }
        let dw = sw * self.art_scale;
        let dh = sh * self.art_scale;
        let ts = self.ts;
        let dst = rect(px + ((ts - dw) / 2.0), if top { py } else { py + ts - dh }, dw, dh);
        let tint = if foliage { self.foliage_tint } else { None };
        self.push_image(id, rect(sx, sy, sw, sh), dst, opacity, tint);
        true
    }

    /// Draws a whole image (`image` is its id, width and height) into a box.
    fn draw_whole_image(&mut self, image: (ImageId, f64, f64), x: f64, y: f64, w: f64, h: f64) {
        let (id, width, height) = image;
        self.push_image(id, rect(0.0, 0.0, width, height), rect(x, y, w, h), 1.0, None);
    }

    /// Whole custom image (tile, item, entity `imageUrl`) when it decodes to a non-empty image.
    fn whole_image(&mut self, source: Option<&Arc<str>>) -> Option<(ImageId, f64, f64)> {
        self.image(source?).filter(|(_, width, _)| *width > 0.0)
    }

    fn draw_shadow(&mut self, px: f64, py: f64, width_factor: f64) {
        let ts = self.ts;
        let rx = (ts * width_factor / 2.0) as f32;
        let ry = cs_max(1.5, ts * 0.11) as f32;
        let cx = (px + (ts / 2.0)) as f32;
        let cy = (py + ts - f64::from(ry) - 1.0) as f32;
        let sigma = cs_max(0.8, ts / 14.0) as f32;
        let bounds = Rect::new(cx - rx, cy - ry, (cx + rx) - (cx - rx), (cy + ry) - (cy - ry));
        self.push(DrawCmd::BlurOval { rect: bounds, color: SHADOW_COLOR, sigma });
    }

    fn world(&mut self) {
        let snapshot = self.snapshot;
        let art = self.art;
        let ts = self.ts;
        let padding = self.padding;
        let pitch = self.pitch;
        let tick = self.tick;
        let camera = snapshot.camera;
        let (view_width, view_height) = snapshot.viewport_size();
        let (world_width, world_height) = snapshot.world_pixel_size();

        // clearRect + faint tint over the whole canvas.
        self.push(DrawCmd::Save);
        let view = Rect::new(0.0, 0.0, view_width as f32, view_height as f32);
        self.push(DrawCmd::ClipRect { rect: view });
        self.fill_rect(view, BACKGROUND_TINT);
        if let Some(camera) = camera {
            self.push(DrawCmd::Translate { dx: -camera.x as f32, dy: -camera.y as f32 });
        }

        // Visible tile range (everything when there is no camera).
        let (x_start, x_end, y_start, y_end) = match camera {
            Some(camera) => (
                to_int(((camera.x - padding) / pitch).floor()).max(0),
                (snapshot.width - 1).min(to_int(((camera.x + camera.width - padding) / pitch).ceil())),
                to_int(((camera.y - padding) / pitch).floor()).max(0),
                (snapshot.height - 1).min(to_int(((camera.y + camera.height - padding) / pitch).ceil())),
            ),
            None => (0, snapshot.width - 1, 0, snapshot.height - 1),
        };

        self.ground(x_start, x_end, y_start, y_end);

        // ---- Collect everything that stands on the ground ------------------------------------
        // One extra row below the viewport: tall objects there poke into view.
        let mut drawables: Vec<Drawable<'a>> = Vec::new();
        let objects_end = (snapshot.height - 1).min(y_end.saturating_add(1));
        let mut y = y_start;
        while y <= objects_end && (y as usize) < snapshot.tiles.len() {
            let row = &snapshot.tiles[y as usize];
            let mut x = x_start;
            while x <= x_end && (x as usize) < row.len() {
                let tile = &row[x as usize];
                let px = padding + (f64::from(x) * pitch);
                let py = padding + (f64::from(y) * pitch);
                for (present, kind) in [
                    (tile.node.is_some(), DrawKind::Node),
                    (tile.machine.is_some(), DrawKind::Machine),
                    (tile.crop.is_some(), DrawKind::Crop),
                    (tile.item.is_some(), DrawKind::Item),
                ] {
                    if present {
                        let seq = drawables.len();
                        drawables.push(Drawable {
                            sort_y: py + ts,
                            seq,
                            kind,
                            tile: Some(tile),
                            entity: None,
                            tile_x: x,
                            tile_y: y,
                            px,
                            py,
                        });
                    }
                }
                x += 1;
            }
            y += 1;
        }
        for npc in &snapshot.npcs {
            let px = entity_pixel_origin(npc.x, padding, pitch);
            let py = entity_pixel_origin(npc.y, padding, pitch);
            let seq = drawables.len();
            drawables.push(Drawable {
                sort_y: py + ts,
                seq,
                kind: DrawKind::Entity,
                tile: None,
                entity: Some(npc),
                tile_x: 0,
                tile_y: 0,
                px,
                py,
            });
        }
        let player = &snapshot.player;
        let player_x = player.pixel_x.unwrap_or_else(|| entity_pixel_origin(player.x, padding, pitch));
        let player_y = player.pixel_y.unwrap_or_else(|| entity_pixel_origin(player.y, padding, pitch));
        let seq = drawables.len();
        drawables.push(Drawable {
            sort_y: player_y + ts,
            seq,
            kind: DrawKind::Player,
            tile: None,
            entity: Some(player),
            tile_x: 0,
            tile_y: 0,
            px: player_x,
            py: player_y,
        });

        // Y-sort: lower on screen draws later (in front); ties keep insertion order.
        drawables.sort_by(|a, b| cs_compare(a.sort_y, b.sort_y).then(a.seq.cmp(&b.seq)));

        // ---- Soft drop shadows (built-in art only; the color-block look stays as it was) ----
        if let Some(art) = art {
            for d in &drawables {
                let factor = match (d.kind, d.tile, d.entity) {
                    (DrawKind::Node, Some(tile), _) => tile
                        .node
                        .as_ref()
                        .filter(|node| node.sprite.is_none() && !node.depleted)
                        .map(|node| if is_foliage(art.node_entry(node.type_id.as_deref())) { 0.7 } else { 0.6 }),
                    (DrawKind::Machine, Some(tile), _) => {
                        tile.machine.as_ref().filter(|machine| machine.sprite.is_none()).map(|_| 0.7)
                    }
                    (DrawKind::Item, Some(tile), _) => {
                        tile.item.as_ref().filter(|item| item.sprite.is_none() && item.image_url.is_none()).map(|_| 0.4)
                    }
                    (DrawKind::Entity | DrawKind::Player, _, Some(entity))
                        if entity.sprite.is_none() && entity.image_url.is_none() =>
                    {
                        Some(0.55)
                    }
                    _ => None,
                };
                if let Some(factor) = factor {
                    self.draw_shadow(d.px, d.py, factor);
                }
            }
        }

        // ---- Y-sorted objects and entities ----------------------------------------------------
        for d in &drawables {
            match d.kind {
                DrawKind::Node => self.node(d),
                DrawKind::Machine => self.machine(d),
                DrawKind::Crop => self.crop(d),
                DrawKind::Item => self.item(d),
                DrawKind::Entity => self.entity(d),
                DrawKind::Player => self.player(d),
            }
        }

        // ---- Atmosphere -------------------------------------------------------------------------
        if let Some(atmosphere) = &snapshot.atmosphere {
            let world_rect = Rect::new(0.0, 0.0, world_width as f32, world_height as f32);
            let overlay =
                atmosphere::weather_overlay(atmosphere.weather_id.as_deref(), atmosphere.weather_overlay.as_deref());
            let mut tint = atmosphere::daylight_tint(atmosphere.time_minutes);
            if overlay == Some(WeatherOverlay::Rain) {
                // Integer math, like the C# byte arithmetic.
                let mix = |a: u8, b: u8| (u32::from(a) * u32::from(b) / 255) as u8;
                tint = Color::rgb(mix(tint.r, RAIN_TINT.r), mix(tint.g, RAIN_TINT.g), mix(tint.b, RAIN_TINT.b));
            }
            if !atmosphere::is_neutral(tint) {
                // Modulate multiplies color (and alpha) so the padding and letterbox stay untouched.
                self.push(DrawCmd::ModulateRect { rect: world_rect, color: tint });
            }
            if let Some(overlay) = overlay {
                let (origin_x, origin_y) = camera.map_or((0.0, 0.0), |camera| (camera.x, camera.y));
                self.weather(overlay, tick, origin_x, origin_y, view_width, view_height, world_rect);
            }
        }

        // Juice: floating feedback text rises and fades with age.
        if let Some(pops) = snapshot.pops.as_ref().filter(|pops| !pops.is_empty()) {
            let size = cs_max(11.0, (ts * 0.42).floor()) as f32;
            for pop in pops {
                let age = cs_clamp(pop.age, 0.0, 1.0);
                let px = entity_pixel_origin(pop.x, padding, pitch) + (ts / 2.0);
                let py = entity_pixel_origin(pop.y, padding, pitch) - (age * ts * 0.8);
                let opacity = 1.0 - (age * age);
                self.push(DrawCmd::Text {
                    text: pop.text.clone(),
                    x: px as f32,
                    y: py as f32,
                    font: FontId::Bold,
                    size,
                    color: parse(pop.color.as_deref().unwrap_or("#ffd94a")).with_opacity(opacity),
                    stroke: Some(TextStroke { color: parse("#00000090").with_opacity(opacity), width: 3.0 }),
                    align: TextAlign::Center,
                });
            }
        }

        // Edit Mode grid overlay.
        if snapshot.grid_overlay {
            let color = parse("#ffffff10");
            // Only over tiles the snapshot has: `width`/`height` alone must not set the work.
            let rows = i32::try_from(snapshot.tiles.len()).unwrap_or(i32::MAX);
            let columns = i32::try_from(snapshot.tiles.iter().map(Vec::len).max().unwrap_or(0)).unwrap_or(i32::MAX);
            for y in y_start..=y_end.min(rows - 1) {
                for x in x_start..=x_end.min(columns - 1) {
                    let rect = rect(padding + (f64::from(x) * pitch), padding + (f64::from(y) * pitch), ts, ts);
                    self.push(DrawCmd::StrokeRect { rect, color, width: 0.5 });
                }
            }
        }

        self.push(DrawCmd::Restore);
    }

    fn ground(&mut self, x_start: i32, x_end: i32, y_start: i32, y_end: i32) {
        let snapshot = self.snapshot;
        let (ts, padding, pitch, tick, season) = (self.ts, self.padding, self.pitch, self.tick, self.season);
        let art = self.art;
        for y in y_start..=y_end {
            let Some(row) = snapshot.tiles.get(y as usize) else { break };
            let mut x = x_start;
            while x <= x_end && (x as usize) < row.len() {
                let tile = &row[x as usize];
                let px = padding + (f64::from(x) * pitch);
                let py = padding + (f64::from(y) * pitch);
                let tile_rect = rect(px, py, ts, ts);

                self.fill_rect(tile_rect, canvas2d::tile_color(Some(&tile.background), "grass"));
                if let Some(url) = &tile.image_url {
                    if let Some((id, width, height)) = self.whole_image(Some(url)) {
                        self.draw_whole_image((id, width, height), px, py, ts, ts);
                    }
                } else {
                    if !self.draw_sprite(tile.art_layer(0), px, py, ts, ts, 1.0) {
                        if let Some(art) = art {
                            if tile.background == "soil" {
                                // Farmable soil is drawn tilled; wet after watering, speckled when fertilized.
                                self.draw_builtin(art.soil(tile.watered, x, y), px, py, 1.0, false, true);
                                if tile.fertilized {
                                    self.draw_builtin(art.fertilized_marker(), px, py, 1.0, false, true);
                                }
                            } else {
                                self.draw_builtin(
                                    art.tile(Some(&tile.background), season, x, y, tick),
                                    px,
                                    py,
                                    1.0,
                                    false,
                                    true,
                                );
                            }
                        }
                    }

                    if let Some(overlay) = &tile.overlay {
                        if !self.draw_sprite(tile.art_layer(1), px, py, ts, ts, 1.0)
                            && !art.is_some_and(|art| {
                                self.draw_builtin(art.tile(Some(overlay), None, x, y, tick), px, py, 1.0, false, true)
                            })
                        {
                            self.fill_rect(tile_rect, canvas2d::tile_color(Some(overlay), "path").with_opacity(0.7));
                        }
                    }

                    if let Some(object) = &tile.object {
                        if !self.draw_sprite(tile.art_layer(2), px, py, ts, ts, 1.0)
                            && !art.is_some_and(|art| {
                                self.draw_builtin(art.tile(Some(object), None, x, y, tick), px, py, 1.0, false, true)
                            })
                        {
                            self.fill_rect(tile_rect, canvas2d::tile_color(Some(object), "wall"));
                        }
                    }
                }

                if tile.ladder_down && !art.is_some_and(|art| self.draw_builtin(art.ladder(), px, py, 1.0, false, true))
                {
                    let hole = (ts * 0.6).floor();
                    self.fill_rect(
                        rect(px + ((ts - hole) / 2.0), py + ((ts - hole) / 2.0), hole, hole),
                        parse("#241a10"),
                    );
                    let color = parse("#c9a95e");
                    let lx = px + (ts / 2.0);
                    let top = (py + ((ts - hole) / 2.0) + 2.0) as f32;
                    let bottom = (py + ((ts + hole) / 2.0) - 2.0) as f32;
                    for side in [-0.2, 0.2] {
                        let x = (lx + (hole * side)) as f32;
                        self.push(DrawCmd::Line { x0: x, y0: top, x1: x, y1: bottom, color, width: 2.0 });
                    }
                }
                x += 1;
            }
        }
    }

    fn node(&mut self, d: &Drawable<'_>) {
        let Some(node) = d.tile.and_then(|tile| tile.node.as_ref()) else { return };
        let (px, py, ts) = (d.px, d.py, self.ts);
        let opacity = if node.depleted { 0.35 } else { 1.0 };
        if self.draw_sprite(node.sprite.as_ref(), px, py, ts, ts, opacity) {
            return;
        }
        if let Some(art) = self.art {
            let foliage = is_foliage(art.node_entry(node.type_id.as_deref()));
            if self.draw_builtin(art.node(node.type_id.as_deref(), d.tile_x, d.tile_y), px, py, opacity, foliage, false)
            {
                return;
            }
        }
        // Gathering node: filled circle in the node color; faded while depleted.
        let radius = (ts * 0.32).floor() as f32;
        let cx = (px + (ts / 2.0)) as f32;
        let cy = (py + (ts / 2.0)) as f32;
        self.push(DrawCmd::FillCircle { cx, cy, radius, color: parse(&node.color).with_opacity(opacity) });
        self.push(DrawCmd::StrokeCircle {
            cx,
            cy,
            radius,
            color: parse("#00000040").with_opacity(opacity),
            width: 1.0,
        });
    }

    fn machine(&mut self, d: &Drawable<'_>) {
        let Some(machine) = d.tile.and_then(|tile| tile.machine.as_ref()) else { return };
        let (px, py, ts) = (d.px, d.py, self.ts);
        if self.draw_sprite(machine.sprite.as_ref(), px, py, ts, ts, 1.0) {
            return;
        }
        if let Some(art) = self.art {
            if self.draw_builtin(art.machine(machine.type_id.as_deref(), machine.working), px, py, 1.0, false, false) {
                if machine.output_ready {
                    self.draw_builtin(art.ready_marker(), px, py - (ts * 0.45), 1.0, false, true);
                }
                return;
            }
        }
        let size = (ts * 0.7).floor();
        let mx = px + ((ts - size) / 2.0);
        let my = py + ((ts - size) / 2.0);
        let body = rect(mx, my, size, size);
        self.push(DrawCmd::FillRoundRect { rect: body, radius: 3.0, color: parse(&machine.color) });
        self.push(DrawCmd::StrokeRoundRect { rect: body, radius: 3.0, color: parse("#00000060"), width: 1.5 });
        if machine.output_ready {
            self.push(DrawCmd::FillCircle {
                cx: (px + (ts * 0.72)) as f32,
                cy: (py + (ts * 0.3)) as f32,
                radius: (ts * 0.12).floor() as f32,
                color: parse("#ffd94a"),
            });
        } else if machine.working {
            let light = rect(mx + (size * 0.35), my + (size * 0.35), size * 0.3, size * 0.3);
            self.fill_rect(light, parse("#ffffff70"));
        }
    }

    fn crop(&mut self, d: &Drawable<'_>) {
        let Some(crop) = d.tile.and_then(|tile| tile.crop.as_ref()) else { return };
        let (px, py, ts) = (d.px, d.py, self.ts);
        let crop_size = (ts * 0.35).floor();
        let crop_rect = rect(px + ((ts - crop_size) / 2.0), py + ((ts - crop_size) / 2.0), crop_size, crop_size);
        if !crop.withered && crop.mature {
            // Canvas shadowBlur 8 ≈ Gaussian sigma 4: a golden glow behind ripe crops.
            self.push(DrawCmd::BlurRect { rect: crop_rect, color: parse("#d6bd2499"), sigma: 4.0 });
        }
        if self.draw_sprite(crop.sprite.as_ref(), px, py, ts, ts, 1.0) {
            return;
        }
        if let Some(art) = self.art {
            let sprite = art.crop(crop.crop_id.as_deref(), crop.color_index, crop.stages, crop.withered, crop.mature);
            if self.draw_builtin(sprite, px, py, 1.0, false, false) {
                return;
            }
        }
        let index = crop.color_index.clamp(0, canvas2d::CROP_STAGE_COLORS.len() as i32 - 1) as usize;
        let css = if crop.withered { canvas2d::WITHERED_CROP_COLOR } else { canvas2d::CROP_STAGE_COLORS[index] };
        self.fill_rect(crop_rect, parse(css));
    }

    fn item(&mut self, d: &Drawable<'_>) {
        let Some(item) = d.tile.and_then(|tile| tile.item.as_ref()) else { return };
        let (px, py, ts) = (d.px, d.py, self.ts);
        if self.draw_sprite(item.sprite.as_ref(), px, py, ts, ts, 1.0) {
            return;
        }
        if item.image_url.is_some() {
            if let Some((id, width, height)) = self.whole_image(item.image_url.as_ref()) {
                let size = (ts * 0.5).floor();
                self.draw_whole_image(
                    (id, width, height),
                    px + ((ts - size) / 2.0),
                    py + ((ts - size) / 2.0),
                    size,
                    size,
                );
            }
            return;
        }
        if let Some(art) = self.art {
            if self.draw_builtin(art.item(item.item_type.as_deref()), px, py, 1.0, false, false) {
                return;
            }
        }
        self.push(DrawCmd::FillCircle {
            cx: (px + (ts / 2.0)) as f32,
            cy: (py + (ts / 2.0)) as f32,
            radius: (ts * 0.15).floor() as f32,
            color: parse(canvas2d::ITEM_COLOR),
        });
    }

    /// Creator sprite, then the entity's whole image, then built-in art (`builtin` picks it).
    fn entity_art(
        &mut self,
        entity: &SnapshotEntity,
        px: f64,
        py: f64,
        body_width: f64,
        body_height: f64,
        builtin: impl FnOnce(&BuiltinArt) -> Option<SnapshotSprite>,
    ) -> bool {
        let ts = self.ts;
        if entity.sprite.is_some() && self.draw_sprite(entity.sprite.as_ref(), px, py, ts, ts, 1.0) {
            return true;
        }
        if let Some((id, width, height)) = self.whole_image(entity.image_url.as_ref()) {
            let (x, y) = (px + ((ts - body_width) / 2.0), py + ((ts - body_height) / 2.0));
            self.draw_whole_image((id, width, height), x, y, body_width, body_height);
            return true;
        }
        match self.art {
            Some(art) => self.draw_builtin(builtin(art), px, py, 1.0, false, false),
            None => false,
        }
    }

    fn entity(&mut self, d: &Drawable<'_>) {
        let Some(npc) = d.entity else { return };
        let (px, py, ts, tick) = (d.px, d.py, self.ts, self.tick);
        let npc_w = (ts * 0.65).floor();
        let npc_h = (ts * 0.75).floor();
        let drawn = self.entity_art(npc, px, py, npc_w, npc_h, |art| {
            if npc.kind == "animal" {
                art.animal(npc.species_id.as_deref(), Some(&npc.direction), tick, npc.moving)
            } else {
                art.npc(npc.appearance.as_deref(), Some(&npc.direction), tick, npc.moving)
            }
        });
        if drawn {
            return;
        }
        let body = rect(px + ((ts - npc_w) / 2.0), py + ((ts - npc_h) / 2.0), npc_w, npc_h);
        let color = parse(npc.color.as_deref().unwrap_or(canvas2d::NPC_COLOR));
        self.push(DrawCmd::FillRoundRect { rect: body, radius: 2.0, color });
        self.push(DrawCmd::StrokeRoundRect {
            rect: body,
            radius: 2.0,
            color: parse(canvas2d::NPC_BORDER_COLOR),
            width: 1.0,
        });
    }

    fn player(&mut self, d: &Drawable<'_>) {
        let Some(player) = d.entity else { return };
        let (px, py, ts, tick) = (d.px, d.py, self.ts, self.tick);
        let player_w = (ts * 0.65).floor();
        let player_h = (ts * 0.75).floor();
        if self.entity_art(player, px, py, player_w, player_h, |art| {
            art.player(Some(&player.direction), tick, player.moving)
        }) {
            return;
        }
        let body = rect(px + ((ts - player_w) / 2.0), py + ((ts - player_h) / 2.0), player_w, player_h);
        self.push(DrawCmd::FillRoundRect { rect: body, radius: 2.0, color: parse(canvas2d::PLAYER_COLOR) });
        self.push(DrawCmd::StrokeRoundRect {
            rect: body,
            radius: 2.0,
            color: parse(canvas2d::PLAYER_BORDER_COLOR),
            width: 2.0,
        });

        let (dx, dy) = canvas2d::direction_offset(Some(&player.direction));
        let dot = (ts * 0.1).floor();
        let center_x = px + (ts / 2.0);
        let center_y = py + (ts / 2.0);
        let dot_x = center_x + (f64::from(dx) * ((player_w / 2.0) - (dot / 2.0))) - (dot / 2.0);
        let dot_y = center_y + (f64::from(dy) * ((player_h / 2.0) - (dot / 2.0))) - (dot / 2.0);
        self.push(DrawCmd::FillCircle {
            cx: (dot_x + (dot / 2.0)) as f32,
            cy: (dot_y + (dot / 2.0)) as f32,
            radius: (dot / 2.0) as f32,
            color: parse("#ffffffcc"),
        });
    }

    /// Rain streaks or snow flakes: every particle's position is a pure function of the tick and
    /// its index (hash), so frames are reproducible and nothing is stored.
    #[allow(clippy::too_many_arguments)]
    fn weather(
        &mut self,
        overlay: WeatherOverlay,
        tick: f64,
        origin_x: f64,
        origin_y: f64,
        view_width: f64,
        view_height: f64,
        world_rect: Rect,
    ) {
        let count = atmosphere::particle_count(view_width, view_height);
        let t = cs_max(0.0, tick);
        self.push(DrawCmd::Save);
        self.push(DrawCmd::ClipRect { rect: world_rect });
        for i in 0..count.max(0) as u32 {
            let unit = |salt: u32| atmosphere::unit(i, salt);
            match overlay {
                WeatherOverlay::Rain => {
                    let speed = 9.0 + (unit(3) * 4.0);
                    let x = origin_x + ((((unit(1) * view_width) - (t * 2.5)) % view_width + view_width) % view_width);
                    let y = origin_y + (((unit(2) * view_height) + (t * speed)) % view_height);
                    self.push(DrawCmd::Line {
                        x0: x as f32,
                        y0: y as f32,
                        x1: (x - 2.0) as f32,
                        y1: (y - 9.0) as f32,
                        color: RAIN_DROP,
                        width: 1.0,
                    });
                }
                WeatherOverlay::Snow => {
                    let speed = 0.9 + (unit(3) * 0.8);
                    // The reference renderer uses the literal 6.283, not TAU.
                    #[allow(clippy::approx_constant)]
                    let sway = ((t / 12.0) + (unit(4) * 6.283)).sin() * 5.0;
                    let x = origin_x + ((((unit(1) * view_width) + sway) % view_width + view_width) % view_width);
                    let y = origin_y + (((unit(2) * view_height) + (t * speed)) % view_height);
                    self.push(DrawCmd::FillCircle {
                        cx: x as f32,
                        cy: y as f32,
                        radius: (1.0 + (unit(5) * 1.2)) as f32,
                        color: SNOW_FLAKE,
                    });
                }
            }
        }
        self.push(DrawCmd::Restore);
    }
}
