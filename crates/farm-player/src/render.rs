//! Frame composition: the world at its native pixel-art resolution, scaled up with nearest
//! neighbour (whole numbers when integer scaling is on, else fitted), letterboxed, then the UI
//! rasterized at full resolution on top so text stays crisp.
//!
//! The camera follows the player smoothly: the world is rendered one pixel larger than the view
//! from the camera's whole-pixel position, and the upscale starts at the fractional remainder.

use farm_cart::AssetTable;
use farm_render::tiny_skia::{self, FilterQuality, Pixmap, PixmapPaint, Transform};
use farm_render::{
    compute_camera, Color, DrawList, ImageStore, Rasterizer, SnapshotCamera, WorldRenderer, WorldSnapshot,
};
use std::sync::Arc;

/// World pixels of the preferred view (20×13 tiles of 32 px, the editor's viewport).
pub const PREFERRED_VIEW: (f64, f64) = (20.0 * 32.0, 13.0 * 32.0);

/// How the world maps onto the frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WorldView {
    /// Device pixels per world pixel.
    pub zoom: f64,
    /// Camera viewport in world pixels (never larger than the world).
    pub camera: SnapshotCamera,
    /// Where the viewport's top-left lands in the frame (device pixels; letterboxing).
    pub origin: (f64, f64),
}

/// Blends a vertical gradient over every row of `pixmap`; `position` maps a row to 0 (top
/// colour) … 1 (bottom colour).
fn tint_rows(pixmap: &mut Pixmap, top: Color, bottom: Color, position: impl Fn(usize) -> f64) {
    let width = pixmap.width() as usize;
    for (row, line) in pixmap.data_mut().chunks_exact_mut(width * 4).enumerate() {
        let t = position(row) as f32;
        let lerp = |a: u8, b: u8| (f32::from(a) + (f32::from(b) - f32::from(a)) * t + 0.5) as u32;
        let alpha = lerp(top.a, bottom.a).min(255);
        if alpha == 0 {
            continue;
        }
        let channel = |a: u8, b: u8| (lerp(a, b).min(255) * alpha + 127) / 255;
        let source = [channel(top.r, bottom.r), channel(top.g, bottom.g), channel(top.b, bottom.b), alpha];
        let inverse = 255 - alpha;
        for pixel in line.chunks_exact_mut(4) {
            for (d, s) in pixel.iter_mut().zip(source) {
                *d = (s + (u32::from(*d) * inverse + 127) / 255).min(255) as u8;
            }
        }
    }
}

/// The zoom for a frame: about the preferred view's area, in whole steps when `integer`.
pub fn zoom_for(width: u32, height: u32, integer: bool) -> f64 {
    let raw = ((f64::from(width) * f64::from(height)) / (PREFERRED_VIEW.0 * PREFERRED_VIEW.1)).sqrt();
    if integer {
        (raw + 0.5).floor().max(1.0)
    } else {
        raw.max(0.5)
    }
}

/// The view of a `world` (pixels) for a frame, following `target` (world pixels).
pub fn world_view(width: u32, height: u32, integer: bool, world: (f64, f64), target: (f64, f64)) -> WorldView {
    let zoom = zoom_for(width, height, integer);
    let (w, h) = (f64::from(width), f64::from(height));
    let view = (world.0.min(w / zoom), world.1.min(h / zoom));
    let camera = compute_camera(target.0, target.1, world.0, world.1, view.0, view.1);
    let origin = (((w - view.0 * zoom) / 2.0).floor(), ((h - view.1 * zoom) / 2.0).floor());
    WorldView { zoom, camera, origin }
}

fn resolver(assets: Arc<AssetTable>) -> impl Fn(&str) -> Option<Vec<u8>> + Send + 'static {
    move |source: &str| assets.resolve(source).map(|asset| asset.data.clone())
}

/// Owns the pixels of the frame and every cache between frames.
pub struct FrameRenderer {
    pub world: WorldRenderer,
    /// Images the UI draws (item art, save thumbnails).
    pub ui_images: ImageStore,
    ui_raster: Rasterizer,
    frame: Pixmap,
    native: Pixmap,
    columns: Vec<usize>,
}

impl std::fmt::Debug for FrameRenderer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FrameRenderer")
            .field("frame", &(self.frame.width(), self.frame.height()))
            .field("native", &(self.native.width(), self.native.height()))
            .finish_non_exhaustive()
    }
}

fn new_pixmap(width: u32, height: u32) -> Pixmap {
    Pixmap::new(width.max(1), height.max(1)).unwrap_or_else(|| Pixmap::new(1, 1).expect("1×1 pixmap"))
}

fn skia_color(color: Color) -> tiny_skia::Color {
    tiny_skia::Color::from_rgba8(color.r, color.g, color.b, color.a)
}

impl FrameRenderer {
    /// A renderer resolving `asset:<id>` images through a cartridge's asset table.
    pub fn new(assets: Arc<AssetTable>) -> Self {
        let mut world = WorldRenderer::new();
        world.images.set_resolver(resolver(Arc::clone(&assets)));
        let mut ui_images = ImageStore::default();
        ui_images.set_resolver(resolver(assets));
        Self {
            world,
            ui_images,
            ui_raster: Rasterizer::new(),
            frame: new_pixmap(1, 1),
            native: new_pixmap(1, 1),
            columns: Vec::new(),
        }
    }

    /// The last composed frame (premultiplied RGBA).
    pub fn frame(&self) -> &Pixmap {
        &self.frame
    }

    /// Starts a frame of `width`×`height` filled with `background`.
    pub fn begin(&mut self, width: u32, height: u32, background: Color) {
        let (width, height) = (width.max(1), height.max(1));
        if self.frame.width() != width || self.frame.height() != height {
            self.frame = new_pixmap(width, height);
        }
        self.frame.fill(skia_color(background));
    }

    /// Renders `snapshot` (its camera set from `view`) at native resolution and scales it into the
    /// frame. `background` fills the world pixmap first (outside the scene). `tints` are vertical
    /// gradients (top, bottom colour) over the frame's height, blended into the world before it is
    /// scaled up (the UI's backdrops).
    pub fn draw_world(
        &mut self,
        snapshot: &mut WorldSnapshot,
        view: &WorldView,
        background: Color,
        tints: &[(Color, Color)],
    ) {
        let camera = view.camera;
        let (base_x, base_y) = (camera.x.floor(), camera.y.floor());
        let (fraction_x, fraction_y) = (camera.x - base_x, camera.y - base_y);
        let native_w = (camera.width.ceil() as u32 + 1).max(1);
        let native_h = (camera.height.ceil() as u32 + 1).max(1);
        snapshot.camera =
            Some(SnapshotCamera { x: base_x, y: base_y, width: f64::from(native_w), height: f64::from(native_h) });
        if self.native.width() != native_w || self.native.height() != native_h {
            self.native = new_pixmap(native_w, native_h);
        }
        self.native.fill(skia_color(background));
        self.world.render_into(snapshot, &mut self.native.as_mut(), Transform::identity());
        let frame_height = f64::from(self.frame.height());
        for (top, bottom) in tints {
            tint_rows(&mut self.native, *top, *bottom, |row| {
                // The row's centre on screen, as a fraction of the frame's height.
                ((view.origin.1 + (row as f64 + 0.5 - fraction_y) * view.zoom) / frame_height).clamp(0.0, 1.0)
            });
        }
        self.upscale(view, (fraction_x, fraction_y));
    }

    /// Nearest-neighbour blit of the native world into the frame's viewport rectangle.
    fn upscale(&mut self, view: &WorldView, fraction: (f64, f64)) {
        let zoom = view.zoom;
        let (frame_w, frame_h) = (self.frame.width() as usize, self.frame.height() as usize);
        let dest_w = ((view.camera.width * zoom).round_ties_even() as usize).min(frame_w);
        let dest_h = ((view.camera.height * zoom).round_ties_even() as usize).min(frame_h);
        let (x0, y0) = (view.origin.0.max(0.0) as usize, view.origin.1.max(0.0) as usize);
        let dest_w = dest_w.min(frame_w - x0.min(frame_w));
        let dest_h = dest_h.min(frame_h - y0.min(frame_h));
        let (native_w, native_h) = (self.native.width() as usize, self.native.height() as usize);
        let map = |device: usize, fraction: f64, limit: usize| -> usize {
            (((device as f64 + 0.5) / zoom + fraction).floor() as usize).min(limit - 1)
        };
        self.columns.clear();
        self.columns.extend((0..dest_w).map(|x| map(x, fraction.0, native_w) * 4));
        let source = self.native.data();
        let frame_stride = frame_w * 4;
        let data = self.frame.data_mut();
        let mut previous: Option<(usize, usize)> = None; // (source row, frame row start)
        for y in 0..dest_h {
            let row_start = (y0 + y) * frame_stride + x0 * 4;
            let source_row = map(y, fraction.1, native_h);
            if let Some((last_row, last_start)) = previous {
                if last_row == source_row {
                    data.copy_within(last_start..last_start + dest_w * 4, row_start);
                    continue;
                }
            }
            let source_line = &source[source_row * native_w * 4..(source_row + 1) * native_w * 4];
            let target = &mut data[row_start..row_start + dest_w * 4];
            for (out, &column) in target.chunks_exact_mut(4).zip(&self.columns) {
                out.copy_from_slice(&source_line[column..column + 4]);
            }
            previous = Some((source_row, row_start));
        }
    }

    /// Rasterizes the UI draw list onto the frame.
    pub fn draw_ui(&mut self, list: &DrawList) {
        self.ui_raster.render(list, &self.ui_images, &mut self.frame.as_mut(), Transform::identity());
    }

    /// A PNG thumbnail of `snapshot` (camera as set) scaled to fit `max_width`×`max_height`.
    pub fn thumbnail(
        &mut self,
        snapshot: &WorldSnapshot,
        max_width: u32,
        max_height: u32,
        background: Color,
    ) -> Vec<u8> {
        let (width, height) = snapshot.pixel_size(1.0);
        let mut native = new_pixmap(width, height);
        native.fill(skia_color(background));
        self.world.render_into(snapshot, &mut native.as_mut(), Transform::identity());
        let scale = (f64::from(max_width) / f64::from(width)).min(f64::from(max_height) / f64::from(height)).min(1.0);
        let (thumb_w, thumb_h) =
            ((f64::from(width) * scale).floor() as u32, (f64::from(height) * scale).floor() as u32);
        let mut thumb = new_pixmap(thumb_w, thumb_h);
        let paint = PixmapPaint { quality: FilterQuality::Bilinear, ..PixmapPaint::default() };
        thumb.draw_pixmap(0, 0, native.as_ref(), &paint, Transform::from_scale(scale as f32, scale as f32), None);
        farm_render::encode_png(&thumb)
    }
}

/// The screen a host shows when the game cannot go on (a failed load, an engine failure): a
/// `title`, then `lines` wrapped to the frame, on a plain background. It needs nothing of the
/// game, so it works when the player itself is gone. Exported games have no console, so this is
/// where a player learns what happened and where the crash log is.
pub fn error_screen(width: u32, height: u32, title: &str, lines: &[String]) -> Pixmap {
    use farm_render::text;
    use farm_render::FontId;
    let (width, height) = (width.max(1), height.max(1));
    let scale = ((width as f32 / 1280.0).min(height as f32 / 800.0)).clamp(0.6, 3.0);
    let margin = 48.0 * scale;
    let max_width = (width as f32 - margin * 2.0).max(40.0);
    let (title_size, body_size) = (28.0 * scale, 16.0 * scale);
    let mut list = DrawList::new();
    let mut y = margin + text::font(FontId::Bold).ascent(title_size);
    let title_color = Color::rgb(0xf6, 0xe7, 0xc8);
    for line in text::wrap(FontId::Bold, title_size, title, max_width) {
        list.text(line, margin, y, FontId::Bold, title_size, title_color);
        y += text::font(FontId::Bold).line_height(title_size);
    }
    y += body_size;
    let body_color = Color::rgb(0xe4, 0xdc, 0xd0);
    let line_height = text::font(FontId::Regular).line_height(body_size);
    for paragraph in lines {
        for line in text::wrap(FontId::Regular, body_size, paragraph, max_width) {
            list.text(line, margin, y, FontId::Regular, body_size, body_color);
            y += line_height;
        }
        y += line_height * 0.5;
    }
    let mut pixmap = new_pixmap(width, height);
    pixmap.fill(skia_color(Color::rgb(0x2a, 0x22, 0x1c)));
    Rasterizer::new().render(&list, &ImageStore::default(), &mut pixmap.as_mut(), Transform::identity());
    pixmap
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_error_screen_draws_its_text() {
        let lines = vec!["The game stopped: boom".to_owned(), "Crash log: /tmp/crash-1-2.log".to_owned()];
        let pixmap = error_screen(640, 400, "Something went wrong", &lines);
        assert_eq!((pixmap.width(), pixmap.height()), (640, 400));
        let background = pixmap.pixel(0, 0).unwrap();
        let drawn = pixmap.pixels().iter().filter(|pixel| **pixel != background).count();
        assert!(drawn > 500, "text was drawn ({drawn} pixels)");
        // Tiny or empty frames do not panic.
        assert_eq!(error_screen(0, 0, "", &[]).width(), 1);
    }

    #[test]
    fn zoom_follows_the_frame_area() {
        assert_eq!(zoom_for(1280, 800, true), 2.0);
        assert_eq!(zoom_for(1920, 1080, true), 3.0);
        assert_eq!(zoom_for(320, 200, true), 1.0);
        assert!((zoom_for(1280, 800, false) - 1.96).abs() < 0.01);
    }

    #[test]
    fn small_worlds_are_letterboxed_and_large_ones_follow_the_target() {
        let small = world_view(1280, 800, true, (344.0, 280.0), (100.0, 100.0));
        assert_eq!((small.camera.width, small.camera.height), (344.0, 280.0));
        assert_eq!((small.camera.x, small.camera.y), (0.0, 0.0));
        assert_eq!(small.origin, (296.0, 120.0));
        let large = world_view(1280, 800, true, (2000.0, 2000.0), (1000.0, 1000.0));
        assert_eq!((large.camera.width, large.camera.height), (640.0, 400.0));
        assert_eq!((large.camera.x, large.camera.y), (680.0, 800.0));
        assert_eq!(large.origin, (0.0, 0.0));
    }

    #[test]
    fn upscale_replicates_pixels() {
        let mut renderer = FrameRenderer::new(Arc::new(AssetTable::default()));
        renderer.begin(8, 4, Color::BLACK);
        renderer.native = new_pixmap(5, 3);
        for (index, pixel) in renderer.native.data_mut().chunks_exact_mut(4).enumerate() {
            pixel.copy_from_slice(&[index as u8, 0, 0, 255]);
        }
        let view = WorldView {
            zoom: 2.0,
            camera: SnapshotCamera { x: 0.0, y: 0.0, width: 4.0, height: 2.0 },
            origin: (0.0, 0.0),
        };
        renderer.upscale(&view, (0.0, 0.0));
        let red: Vec<u8> = renderer.frame.data().chunks_exact(4).map(|p| p[0]).collect();
        assert_eq!(red[..8], [0, 0, 1, 1, 2, 2, 3, 3]);
        assert_eq!(red[8..16], [0, 0, 1, 1, 2, 2, 3, 3]);
        assert_eq!(red[16..24], [5, 5, 6, 6, 7, 7, 8, 8]);
        // Half a world pixel of camera offset shifts the sampling by one device pixel.
        renderer.upscale(&view, (0.5, 0.0));
        let red: Vec<u8> = renderer.frame.data().chunks_exact(4).map(|p| p[0]).collect();
        assert_eq!(red[..8], [0, 1, 1, 2, 2, 3, 3, 4]);
    }
}
