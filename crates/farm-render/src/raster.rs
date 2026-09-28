//! CPU rasterizer (feature `raster`): executes a [`DrawList`] into a [`tiny_skia`] pixmap
//! (premultiplied RGBA8).
//!
//! It follows the Skia semantics of the C# renderer: non-anti-aliased rectangles cover the
//! pixels whose centers are inside, clips are rounded to whole device pixels, blurs are
//! Gaussian (a separable kernel over an offscreen coverage mask, cached by shape), daylight
//! uses `Modulate`, foliage tints are cached copies of the sheets, and glyphs are outlined from
//! the embedded fonts (cached per glyph). Transforms are expected to be axis-aligned.

use crate::builtin_art::BuiltinArt;
use crate::css_color::Color;
use crate::draw::{DrawCmd, DrawList, FontId, Rect, Sampling, TextAlign, TextStroke};
use crate::images::{mul_div_255_round, Image, ImageId, ImageStore};
use crate::snapshot::WorldSnapshot;
use crate::text;
use std::collections::btree_map::Entry;
use std::collections::BTreeMap;
use tiny_skia::{
    BlendMode, FillRule, FilterQuality, LineCap, LineJoin, Mask, Paint, Path, PathBuilder, Pattern, Pixmap, PixmapMut,
    PixmapPaint, PixmapRef, Point, SpreadMode, Stroke, Transform,
};

/// Cache bounds (entries). Caches are cleared when they grow past these.
const MAX_BLURS: usize = 512;
const MAX_TINTED: usize = 64;
const MAX_CROPS: usize = 1024;
const MAX_GLYPHS: usize = 4096;
/// Largest offscreen blur mask, in pixels.
const MAX_BLUR_PIXELS: i64 = 4096 * 4096;

/// A clip in device pixels, `[x0, x1) × [y0, y1)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct ClipBox {
    x0: i32,
    y0: i32,
    x1: i32,
    y1: i32,
}

#[derive(Debug, Clone, Copy)]
struct State {
    transform: Transform,
    clip: Option<ClipBox>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
enum BlurShape {
    Oval,
    Rect,
}

/// A blurred shape, quantized to 1/16 device pixel so a cached mask is exactly what a fresh one
/// would be (rendering never depends on cache state).
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
struct BlurKey {
    shape: BlurShape,
    left: i32,
    top: i32,
    width: i32,
    height: i32,
    sigma: i32,
    color: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
struct CropKey {
    image: ImageId,
    tint: Option<u32>,
    x0: u32,
    y0: u32,
    x1: u32,
    y1: u32,
}

#[derive(Default)]
struct Caches {
    blurs: BTreeMap<BlurKey, Pixmap>,
    tinted: BTreeMap<(ImageId, u32), Pixmap>,
    crops: BTreeMap<CropKey, Pixmap>,
    glyphs: BTreeMap<(FontId, u16), Option<Path>>,
}

#[derive(Default)]
struct ClipMask {
    key: Option<(ClipBox, u32, u32)>,
    mask: Option<Mask>,
}

impl ClipMask {
    /// The mask for a clip; `Ok(None)` when nothing is clipped, `Err(())` when everything is.
    fn get(&mut self, clip: Option<ClipBox>, width: u32, height: u32) -> Result<Option<&Mask>, ()> {
        let Some(clip) = clip else { return Ok(None) };
        let clip = ClipBox {
            x0: clip.x0.clamp(0, width as i32),
            y0: clip.y0.clamp(0, height as i32),
            x1: clip.x1.clamp(0, width as i32),
            y1: clip.y1.clamp(0, height as i32),
        };
        if clip.x0 >= clip.x1 || clip.y0 >= clip.y1 {
            return Err(());
        }
        if clip == (ClipBox { x0: 0, y0: 0, x1: width as i32, y1: height as i32 }) {
            return Ok(None);
        }
        if self.key != Some((clip, width, height)) {
            let mut mask = Mask::new(width, height).ok_or(())?;
            let stride = width as usize;
            let data = mask.data_mut();
            for y in clip.y0 as usize..clip.y1 as usize {
                data[y * stride + clip.x0 as usize..y * stride + clip.x1 as usize].fill(255);
            }
            self.key = Some((clip, width, height));
            self.mask = Some(mask);
        }
        Ok(self.mask.as_ref())
    }
}

/// Executes draw lists. Keeps caches (blurred shapes, tinted sheets, cropped sprites, glyph
/// outlines) between frames; reuse one per surface.
#[derive(Default)]
pub struct Rasterizer {
    caches: Caches,
    clip: ClipMask,
}

impl std::fmt::Debug for Rasterizer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Rasterizer")
            .field("blurs", &self.caches.blurs.len())
            .field("tinted", &self.caches.tinted.len())
            .field("crops", &self.caches.crops.len())
            .field("glyphs", &self.caches.glyphs.len())
            .finish()
    }
}

fn solid(color: Color, anti_alias: bool) -> Paint<'static> {
    let mut paint = Paint::default();
    paint.set_color_rgba8(color.r, color.g, color.b, color.a);
    paint.anti_alias = anti_alias;
    paint
}

fn skia_rect(rect: &Rect) -> Option<tiny_skia::Rect> {
    if !(rect.width > 0.0 && rect.height > 0.0) {
        return None;
    }
    tiny_skia::Rect::from_xywh(rect.x, rect.y, rect.width, rect.height)
}

/// Device-space bounding box of a rectangle.
fn map_rect(transform: Transform, rect: &Rect) -> (f32, f32, f32, f32) {
    let mut points = [
        Point::from_xy(rect.x, rect.y),
        Point::from_xy(rect.right(), rect.y),
        Point::from_xy(rect.x, rect.bottom()),
        Point::from_xy(rect.right(), rect.bottom()),
    ];
    transform.map_points(&mut points);
    let mut bounds = (f32::INFINITY, f32::INFINITY, f32::NEG_INFINITY, f32::NEG_INFINITY);
    for point in points {
        bounds.0 = bounds.0.min(point.x);
        bounds.1 = bounds.1.min(point.y);
        bounds.2 = bounds.2.max(point.x);
        bounds.3 = bounds.3.max(point.y);
    }
    bounds
}

/// `SkScalarRoundToInt`: floor(x + 0.5), saturating.
fn round_to_int(x: f32) -> i32 {
    (x + 0.5).floor() as i32
}

fn round_rect_path(rect: &Rect, radius: f32) -> Option<Path> {
    let r = skia_rect(rect)?;
    let radius = radius.min(rect.width / 2.0).min(rect.height / 2.0);
    if radius.is_nan() || radius <= 0.0 {
        return Some(PathBuilder::from_rect(r));
    }
    // Quarter circles as cubics (Skia uses conics; identical at these sizes).
    const KAPPA: f32 = 0.552_284_8;
    let (x, y, w, h, k) = (rect.x, rect.y, rect.width, rect.height, radius * KAPPA);
    let mut pb = PathBuilder::new();
    pb.move_to(x + radius, y);
    pb.line_to(x + w - radius, y);
    pb.cubic_to(x + w - radius + k, y, x + w, y + radius - k, x + w, y + radius);
    pb.line_to(x + w, y + h - radius);
    pb.cubic_to(x + w, y + h - radius + k, x + w - radius + k, y + h, x + w - radius, y + h);
    pb.line_to(x + radius, y + h);
    pb.cubic_to(x + radius - k, y + h, x, y + h - radius + k, x, y + h - radius);
    pb.line_to(x, y + radius);
    pb.cubic_to(x, y + radius - k, x + radius - k, y, x + radius, y);
    pb.close();
    pb.finish()
}

fn stroke(width: f32) -> Stroke {
    Stroke { width, miter_limit: 4.0, line_cap: LineCap::Butt, line_join: LineJoin::Miter, dash: None }
}

/// A copy of `image` with every premultiplied channel multiplied by `tint` (Skia's
/// `SKColorFilter.CreateBlendMode(tint, Modulate)`).
fn tinted_copy(image: &Image, tint: Color) -> Option<Pixmap> {
    let factors = [tint.r, tint.g, tint.b, tint.a];
    let factors = [
        mul_div_255_round(factors[0], tint.a),
        mul_div_255_round(factors[1], tint.a),
        mul_div_255_round(factors[2], tint.a),
        factors[3],
    ];
    let mut pixels = image.pixels().to_vec();
    for pixel in pixels.chunks_exact_mut(4) {
        for (channel, factor) in pixel.iter_mut().zip(factors) {
            *channel = mul_div_255_round(*channel, factor);
        }
    }
    Pixmap::from_vec(pixels, tiny_skia::IntSize::from_wh(image.width(), image.height())?)
}

fn crop(source: PixmapRef<'_>, x0: u32, y0: u32, x1: u32, y1: u32) -> Option<Pixmap> {
    let (width, height) = (x1 - x0, y1 - y0);
    let mut pixels = Vec::with_capacity(width as usize * height as usize * 4);
    let stride = source.width() as usize * 4;
    for y in y0..y1 {
        let start = y as usize * stride + x0 as usize * 4;
        pixels.extend_from_slice(&source.data()[start..start + width as usize * 4]);
    }
    Pixmap::from_vec(pixels, tiny_skia::IntSize::from_wh(width, height)?)
}

/// Separable Gaussian blur of an 8-bit coverage mask in place (zero outside the mask).
fn gaussian_blur(data: &mut [u8], width: usize, height: usize, sigma: f32) {
    let radius = (3.0 * sigma).ceil().max(1.0) as usize;
    let two_sigma_sq = 2.0 * sigma * sigma;
    let mut kernel: Vec<f32> = (0..=2 * radius)
        .map(|i| {
            let d = i as f32 - radius as f32;
            (-(d * d) / two_sigma_sq).exp()
        })
        .collect();
    let total: f32 = kernel.iter().sum();
    for weight in &mut kernel {
        *weight /= total;
    }
    let source: Vec<f32> = data.iter().map(|&v| f32::from(v)).collect();
    let mut horizontal = vec![0.0f32; width * height];
    for y in 0..height {
        let row = &source[y * width..(y + 1) * width];
        for x in 0..width {
            let mut sum = 0.0;
            for (k, weight) in kernel.iter().enumerate() {
                let sx = x as isize + k as isize - radius as isize;
                if sx >= 0 && (sx as usize) < width {
                    sum += row[sx as usize] * weight;
                }
            }
            horizontal[y * width + x] = sum;
        }
    }
    for x in 0..width {
        for y in 0..height {
            let mut sum = 0.0;
            for (k, weight) in kernel.iter().enumerate() {
                let sy = y as isize + k as isize - radius as isize;
                if sy >= 0 && (sy as usize) < height {
                    sum += horizontal[sy as usize * width + x] * weight;
                }
            }
            data[y * width + x] = (sum + 0.5).floor().clamp(0.0, 255.0) as u8;
        }
    }
}

fn render_blur(key: &BlurKey, width: u32, height: u32) -> Option<Pixmap> {
    let q = |v: i32| v as f32 / 16.0;
    let local = Rect::new(q(key.left), q(key.top), q(key.width), q(key.height));
    let rect = skia_rect(&local)?;
    let path = match key.shape {
        BlurShape::Oval => PathBuilder::from_oval(rect)?,
        BlurShape::Rect => PathBuilder::from_rect(rect),
    };
    let mut mask = Mask::new(width, height)?;
    mask.fill_path(&path, FillRule::Winding, true, Transform::identity());
    gaussian_blur(mask.data_mut(), width as usize, height as usize, q(key.sigma));
    let color = key.color.to_be_bytes();
    let mut pixmap = Pixmap::new(width, height)?;
    for (pixel, &coverage) in pixmap.data_mut().chunks_exact_mut(4).zip(mask.data()) {
        let alpha = mul_div_255_round(color[3], coverage);
        pixel[0] = mul_div_255_round(color[0], alpha);
        pixel[1] = mul_div_255_round(color[1], alpha);
        pixel[2] = mul_div_255_round(color[2], alpha);
        pixel[3] = alpha;
    }
    Some(pixmap)
}

fn limit<K: Ord, V>(cache: &mut BTreeMap<K, V>, max: usize) {
    if cache.len() >= max {
        cache.clear();
    }
}

impl Caches {
    fn glyph(&mut self, font: FontId, glyph: ttf_parser::GlyphId) -> Option<&Path> {
        limit(&mut self.glyphs, MAX_GLYPHS);
        self.glyphs
            .entry((font, glyph.0))
            .or_insert_with(|| {
                let mut builder = GlyphPath(PathBuilder::new());
                text::font(font).face().outline_glyph(glyph, &mut builder)?;
                builder.0.finish()
            })
            .as_ref()
    }
}

/// Glyph outlines in font units with y pointing down.
struct GlyphPath(PathBuilder);

impl ttf_parser::OutlineBuilder for GlyphPath {
    fn move_to(&mut self, x: f32, y: f32) {
        self.0.move_to(x, -y);
    }

    fn line_to(&mut self, x: f32, y: f32) {
        self.0.line_to(x, -y);
    }

    fn quad_to(&mut self, x1: f32, y1: f32, x: f32, y: f32) {
        self.0.quad_to(x1, -y1, x, -y);
    }

    fn curve_to(&mut self, x1: f32, y1: f32, x2: f32, y2: f32, x: f32, y: f32) {
        self.0.cubic_to(x1, -y1, x2, -y2, x, -y);
    }

    fn close(&mut self) {
        self.0.close();
    }
}

impl Rasterizer {
    pub fn new() -> Self {
        Self::default()
    }

    /// Draws `list` onto `target` with `base` as the initial transform (for example
    /// `Transform::from_scale(zoom, zoom)`). Images come from the store that built the list;
    /// evicted or missing ids draw nothing.
    pub fn render(&mut self, list: &DrawList, images: &ImageStore, target: &mut PixmapMut<'_>, base: Transform) {
        let (width, height) = (target.width(), target.height());
        let mut state = State { transform: base, clip: None };
        let mut stack: Vec<State> = Vec::new();
        for command in &list.commands {
            match command {
                DrawCmd::Save => stack.push(state),
                DrawCmd::Restore => {
                    if let Some(saved) = stack.pop() {
                        state = saved;
                    }
                }
                DrawCmd::Translate { dx, dy } => state.transform = state.transform.pre_translate(*dx, *dy),
                DrawCmd::Scale { sx, sy } => state.transform = state.transform.pre_scale(*sx, *sy),
                DrawCmd::ClipRect { rect } => {
                    let (left, top, right, bottom) = map_rect(state.transform, rect);
                    let mut clip = ClipBox {
                        x0: round_to_int(left),
                        y0: round_to_int(top),
                        x1: round_to_int(right),
                        y1: round_to_int(bottom),
                    };
                    if let Some(outer) = state.clip {
                        clip = ClipBox {
                            x0: clip.x0.max(outer.x0),
                            y0: clip.y0.max(outer.y0),
                            x1: clip.x1.min(outer.x1),
                            y1: clip.y1.min(outer.y1),
                        };
                    }
                    state.clip = Some(clip);
                }
                other => {
                    let Ok(mask) = self.clip.get(state.clip, width, height) else { continue };
                    draw(&mut self.caches, target, images, other, state.transform, mask, state.clip);
                }
            }
        }
    }

    /// Renders `list` into a new transparent pixmap of `width`×`height` device pixels at `scale`.
    pub fn render_to_pixmap(
        &mut self,
        list: &DrawList,
        images: &ImageStore,
        width: u32,
        height: u32,
        scale: f32,
    ) -> Pixmap {
        let mut pixmap = Pixmap::new(width.max(1), height.max(1)).unwrap_or_else(|| Pixmap::new(1, 1).expect("1×1"));
        self.render(list, images, &mut pixmap.as_mut(), Transform::from_scale(scale, scale));
        pixmap
    }
}

fn draw(
    caches: &mut Caches,
    target: &mut PixmapMut<'_>,
    images: &ImageStore,
    command: &DrawCmd,
    transform: Transform,
    mask: Option<&Mask>,
    clip: Option<ClipBox>,
) {
    match command {
        DrawCmd::FillRect { rect, color, anti_alias } => {
            if let Some(rect) = skia_rect(rect) {
                target.fill_rect(rect, &solid(*color, *anti_alias), transform, mask);
            }
        }
        DrawCmd::StrokeRect { rect, color, width } => {
            if let Some(rect) = skia_rect(rect) {
                target.stroke_path(
                    &PathBuilder::from_rect(rect),
                    &solid(*color, true),
                    &stroke(*width),
                    transform,
                    mask,
                );
            }
        }
        DrawCmd::FillRoundRect { rect, radius, color } => {
            if let Some(path) = round_rect_path(rect, *radius) {
                target.fill_path(&path, &solid(*color, true), FillRule::Winding, transform, mask);
            }
        }
        DrawCmd::StrokeRoundRect { rect, radius, color, width } => {
            if let Some(path) = round_rect_path(rect, *radius) {
                target.stroke_path(&path, &solid(*color, true), &stroke(*width), transform, mask);
            }
        }
        DrawCmd::FillCircle { cx, cy, radius, color } => {
            if let Some(path) = PathBuilder::from_circle(*cx, *cy, *radius) {
                target.fill_path(&path, &solid(*color, true), FillRule::Winding, transform, mask);
            }
        }
        DrawCmd::StrokeCircle { cx, cy, radius, color, width } => {
            if let Some(path) = PathBuilder::from_circle(*cx, *cy, *radius) {
                target.stroke_path(&path, &solid(*color, true), &stroke(*width), transform, mask);
            }
        }
        DrawCmd::FillOval { rect, color } => {
            if let Some(path) = skia_rect(rect).and_then(PathBuilder::from_oval) {
                target.fill_path(&path, &solid(*color, true), FillRule::Winding, transform, mask);
            }
        }
        DrawCmd::BlurOval { rect, color, sigma } => {
            draw_blur(caches, target, BlurShape::Oval, rect, *color, *sigma, transform, mask);
        }
        DrawCmd::BlurRect { rect, color, sigma } => {
            draw_blur(caches, target, BlurShape::Rect, rect, *color, *sigma, transform, mask);
        }
        DrawCmd::Line { x0, y0, x1, y1, color, width } => {
            let mut pb = PathBuilder::new();
            pb.move_to(*x0, *y0);
            pb.line_to(*x1, *y1);
            if let Some(path) = pb.finish() {
                target.stroke_path(&path, &solid(*color, true), &stroke(*width), transform, mask);
            }
        }
        DrawCmd::Image { image, src, dst, opacity, sampling, tint } => {
            let paint = ImagePaint { opacity: *opacity, sampling: *sampling, tint: *tint };
            draw_image(caches, target, images, *image, src, dst, paint, transform, (mask, clip));
        }
        DrawCmd::ModulateRect { rect, color } => {
            if let Some(rect) = skia_rect(rect) {
                let mut paint = solid(*color, false);
                paint.blend_mode = BlendMode::Modulate;
                target.fill_rect(rect, &paint, transform, mask);
            }
        }
        DrawCmd::Text { text, x, y, font, size, color, stroke, align } => {
            draw_text(caches, target, text, *x, *y, *font, *size, *color, *stroke, *align, transform, mask);
        }
        DrawCmd::Save
        | DrawCmd::Restore
        | DrawCmd::Translate { .. }
        | DrawCmd::Scale { .. }
        | DrawCmd::ClipRect { .. } => {}
    }
}

#[allow(clippy::too_many_arguments)]
fn draw_blur(
    caches: &mut Caches,
    target: &mut PixmapMut<'_>,
    shape: BlurShape,
    rect: &Rect,
    color: Color,
    sigma: f32,
    transform: Transform,
    mask: Option<&Mask>,
) {
    let (sx, sy) = transform.get_scale();
    let device_sigma = sigma * (sx * sy).abs().sqrt();
    let (left, top, right, bottom) = map_rect(transform, rect);
    if !(left.is_finite() && top.is_finite() && right.is_finite() && bottom.is_finite() && right > left && bottom > top)
    {
        return;
    }
    let quantize = |v: f32| (v * 16.0 + 0.5).floor();
    let sigma16 = quantize(device_sigma).max(1.0);
    if sigma16 <= 1.0 {
        // No visible blur: draw the plain shape.
        let path = match shape {
            BlurShape::Oval => skia_rect(rect).and_then(PathBuilder::from_oval),
            BlurShape::Rect => skia_rect(rect).map(PathBuilder::from_rect),
        };
        if let Some(path) = path {
            target.fill_path(&path, &solid(color, true), FillRule::Winding, transform, mask);
        }
        return;
    }
    let (l16, t16, r16, b16) = (quantize(left), quantize(top), quantize(right), quantize(bottom));
    let pad = (3.0 * sigma16 / 16.0).ceil() as i64 + 1;
    let origin_x = (l16 / 16.0).floor() as i64 - pad;
    let origin_y = (t16 / 16.0).floor() as i64 - pad;
    let width = (r16 / 16.0).ceil() as i64 + pad - origin_x;
    let height = (b16 / 16.0).ceil() as i64 + pad - origin_y;
    if width <= 0
        || height <= 0
        || width * height > MAX_BLUR_PIXELS
        || origin_x.abs() > 1 << 24
        || origin_y.abs() > 1 << 24
    {
        return;
    }
    let key = BlurKey {
        shape,
        left: (l16 - (origin_x * 16) as f32) as i32,
        top: (t16 - (origin_y * 16) as f32) as i32,
        width: (r16 - l16) as i32,
        height: (b16 - t16) as i32,
        sigma: sigma16 as i32,
        color: color.to_u32(),
    };
    limit(&mut caches.blurs, MAX_BLURS);
    let pixmap = match caches.blurs.entry(key) {
        Entry::Occupied(entry) => entry.into_mut(),
        Entry::Vacant(entry) => match render_blur(&key, width as u32, height as u32) {
            Some(pixmap) => entry.insert(pixmap),
            None => return,
        },
    };
    target.draw_pixmap(
        origin_x as i32,
        origin_y as i32,
        pixmap.as_ref(),
        &PixmapPaint::default(),
        Transform::identity(),
        mask,
    );
}

/// How an image command paints.
#[derive(Debug, Clone, Copy)]
struct ImagePaint {
    opacity: f32,
    sampling: Sampling,
    tint: Option<Color>,
}

#[allow(clippy::too_many_arguments)]
fn draw_image(
    caches: &mut Caches,
    target: &mut PixmapMut<'_>,
    images: &ImageStore,
    id: ImageId,
    src: &Rect,
    dst: &Rect,
    paint: ImagePaint,
    transform: Transform,
    (mask, clip): (Option<&Mask>, Option<ClipBox>),
) {
    let ImagePaint { opacity, sampling, tint } = paint;
    let Some(image) = images.image(id) else { return };
    let Some(dst_rect) = skia_rect(dst) else { return };
    if !(src.width > 0.0 && src.height > 0.0 && opacity > 0.0) {
        return;
    }
    let tint = tint.filter(|tint| *tint != Color::WHITE);
    let full = match tint {
        Some(tint) => {
            limit(&mut caches.tinted, MAX_TINTED);
            match caches.tinted.entry((id, tint.to_u32())) {
                Entry::Occupied(entry) => entry.into_mut().as_ref(),
                Entry::Vacant(entry) => match tinted_copy(image, tint) {
                    Some(copy) => entry.insert(copy).as_ref(),
                    None => return,
                },
            }
        }
        None => match PixmapRef::from_bytes(image.pixels(), image.width(), image.height()) {
            Some(full) => full,
            None => return,
        },
    };

    let quality = match sampling {
        Sampling::Nearest => FilterQuality::Nearest,
        Sampling::Smooth => FilterQuality::Bilinear,
    };
    let (sx, sy) = (dst.width / src.width, dst.height / src.height);
    fn pattern_paint(pixmap: PixmapRef<'_>, quality: FilterQuality, opacity: f32, transform: Transform) -> Paint<'_> {
        Paint {
            shader: Pattern::new(pixmap, SpreadMode::Pad, quality, opacity, transform),
            anti_alias: false,
            ..Paint::default()
        }
    }
    // Maps image pixels (of a copy starting at `offset`) onto the destination rectangle.
    let pattern = |offset_x: f32, offset_y: f32| {
        Transform::from_row(sx, 0.0, 0.0, sy, dst.x - (src.x - offset_x) * sx, dst.y - (src.y - offset_y) * sy)
    };

    if sampling == Sampling::Nearest {
        // Axis-aligned pixel art (the common case) picks its source pixels exactly like Skia.
        if blit_nearest(target, full, src, dst, opacity, transform, clip) {
            return;
        }
        // Rotated or skewed transforms: let tiny-skia sample.
        let paint = pattern_paint(full, quality, opacity, pattern(0.0, 0.0));
        target.fill_rect(dst_rect, &paint, transform, mask);
        return;
    }

    // Filtering would bleed neighbouring sprites into the edges: sample a cropped copy (clamped
    // at its border, like Skia's strict source-rect constraint).
    let clamp = |v: f32, max: u32| v.max(0.0).min(max as f32) as u32;
    let (x0, y0) = (clamp(src.x.floor(), full.width()), clamp(src.y.floor(), full.height()));
    let (x1, y1) = (clamp(src.right().ceil(), full.width()), clamp(src.bottom().ceil(), full.height()));
    if x0 >= x1 || y0 >= y1 {
        return;
    }
    let key = CropKey { image: id, tint: tint.map(Color::to_u32), x0, y0, x1, y1 };
    limit(&mut caches.crops, MAX_CROPS);
    let cropped = match caches.crops.entry(key) {
        Entry::Occupied(entry) => entry.into_mut(),
        Entry::Vacant(entry) => match crop(full, x0, y0, x1, y1) {
            Some(copy) => entry.insert(copy),
            None => return,
        },
    };
    let paint = pattern_paint(cropped.as_ref(), quality, opacity, pattern(x0 as f32, y0 as f32));
    target.fill_rect(dst_rect, &paint, transform, mask);
}

/// Skia's nearest-neighbour mapping along one axis of an axis-aligned image draw.
#[derive(Debug, Clone, Copy)]
struct NearestAxis {
    /// Device pixels covered by the destination, `[start, end)`.
    start: i32,
    end: i32,
    inv_scale: f32,
    inv_translate: f32,
    /// The integer source subset Skia samples (strict source-rect constraint).
    subset_origin: i32,
    subset_len: i32,
}

impl NearestAxis {
    /// `SkBitmapDevice::drawImageRect` + `SkImageShader` for one axis: the strict source rect is
    /// cut to its integer bounds, the local matrix is `RectToRect(src, dst)` moved to that
    /// subset, concatenated with the canvas scale/translate, then inverted; the destination
    /// rectangle is rounded to device pixels like `SkScan::FillRect`.
    fn new(
        src_pos: f32,
        src_len: f32,
        dst_pos: f32,
        dst_len: f32,
        image_len: u32,
        scale: f32,
        translate: f32,
    ) -> Option<Self> {
        let (src_end, dst_end) = (src_pos + src_len, dst_pos + dst_len);
        let subset_origin = (src_pos.floor().max(0.0) as i64).min(i64::from(image_len)) as i32;
        let subset_end = (src_end.ceil().max(0.0) as i64).min(i64::from(image_len)) as i32;
        if subset_end <= subset_origin {
            return None;
        }
        let local_scale = (dst_end - dst_pos) / (src_end - src_pos);
        let mut local_translate = dst_pos - src_pos * local_scale;
        if subset_origin > 0 {
            local_translate += local_scale * subset_origin as f32;
        }
        let total_scale = scale * local_scale;
        let total_translate = scale * local_translate + translate;
        let inv_scale = 1.0 / total_scale;
        // skia:4649: for nearest sampling Skia nudges the inverse translation one ulp toward its
        // floor, so pixel centers that land exactly on a source pixel edge pick the pixel before.
        let mut inv_translate = -total_translate * inv_scale;
        if inv_translate.floor() != inv_translate {
            inv_translate = next_down(inv_translate);
        }
        if !(inv_scale.is_finite() && inv_translate.is_finite() && total_scale > 0.0) {
            return None;
        }
        let round = |v: f32| (v + 0.5).floor();
        let start = round(scale * dst_pos + translate);
        let end = round(scale * dst_end + translate);
        if !(start.is_finite() && end.is_finite()) {
            return None;
        }
        Some(Self {
            start: start.clamp(-(1 << 30) as f32, (1 << 30) as f32) as i32,
            end: end.clamp(-(1 << 30) as f32, (1 << 30) as f32) as i32,
            inv_scale,
            inv_translate,
            subset_origin,
            subset_len: subset_end - subset_origin,
        })
    }

    /// The source pixel sampled for device pixel `device`: its center mapped back (a separate
    /// multiply and add, like Skia's low-precision pipeline), clamped to the subset, truncated.
    fn sample(&self, device: i32) -> usize {
        let center = device as f32 + 0.5;
        let u = center * self.inv_scale + self.inv_translate;
        let last = f32::from_bits((self.subset_len as f32).to_bits() - 1);
        (self.subset_origin + u.max(0.0).min(last) as i32) as usize
    }
}

/// The next `f32` toward negative infinity (`f32::next_down`, newer than the crate's MSRV).
fn next_down(x: f32) -> f32 {
    if x.is_nan() || x == f32::NEG_INFINITY {
        x
    } else if x == 0.0 {
        -f32::from_bits(1)
    } else if x > 0.0 {
        f32::from_bits(x.to_bits() - 1)
    } else {
        f32::from_bits(x.to_bits() + 1)
    }
}

/// Skia's low-precision `div255` approximation (tiny-skia's too): `(v + 255) / 256`.
fn div255(v: u32) -> u32 {
    (v + 255) >> 8
}

/// Draws `src` of `source` into `dst` with nearest sampling when `transform` only scales
/// (positively) and translates; returns false for other transforms. Pixels are chosen with
/// Skia's arithmetic and blended source-over like its low-precision pipeline.
fn blit_nearest(
    target: &mut PixmapMut<'_>,
    source: PixmapRef<'_>,
    src: &Rect,
    dst: &Rect,
    opacity: f32,
    transform: Transform,
    clip: Option<ClipBox>,
) -> bool {
    if transform.kx != 0.0 || transform.ky != 0.0 || !(transform.sx > 0.0 && transform.sy > 0.0) {
        return false;
    }
    let (Some(x), Some(y)) = (
        NearestAxis::new(src.x, src.width, dst.x, dst.width, source.width(), transform.sx, transform.tx),
        NearestAxis::new(src.y, src.height, dst.y, dst.height, source.height(), transform.sy, transform.ty),
    ) else {
        return true;
    };
    let (width, height) = (target.width() as i32, target.height() as i32);
    let clip = clip.unwrap_or(ClipBox { x0: 0, y0: 0, x1: width, y1: height });
    let (x0, x1) = (x.start.max(clip.x0).max(0), x.end.min(clip.x1).min(width));
    let (y0, y1) = (y.start.max(clip.y0).max(0), y.end.min(clip.y1).min(height));
    if x0 >= x1 || y0 >= y1 {
        return true;
    }
    // Paint alpha as a byte (it came from one), applied like Skia's `scale_1_float`.
    let alpha = (opacity.clamp(0.0, 1.0) * 255.0 + 0.5).floor() as u32;
    let columns: Vec<usize> = (x0..x1).map(|device| x.sample(device) * 4).collect();
    let source_stride = source.width() as usize * 4;
    let target_stride = width as usize * 4;
    let pixels = source.data();
    let data = target.data_mut();
    for device_y in y0..y1 {
        let source_row = &pixels[y.sample(device_y) * source_stride..];
        let row_start = device_y as usize * target_stride + x0 as usize * 4;
        let target_row = &mut data[row_start..row_start + columns.len() * 4];
        for (out, &column) in target_row.chunks_exact_mut(4).zip(&columns) {
            let mut s = [
                u32::from(source_row[column]),
                u32::from(source_row[column + 1]),
                u32::from(source_row[column + 2]),
                u32::from(source_row[column + 3]),
            ];
            if alpha < 255 {
                s = s.map(|c| div255(c * alpha));
            }
            match s[3] {
                0 => {}
                255 => {
                    for (d, c) in out.iter_mut().zip(s) {
                        *d = c as u8;
                    }
                }
                a => {
                    for (d, c) in out.iter_mut().zip(s) {
                        *d = (c + div255(u32::from(*d) * (255 - a))).min(255) as u8;
                    }
                }
            }
        }
    }
    true
}

#[allow(clippy::too_many_arguments)]
fn draw_text(
    caches: &mut Caches,
    target: &mut PixmapMut<'_>,
    content: &str,
    x: f32,
    y: f32,
    font_id: FontId,
    size: f32,
    color: Color,
    outline: Option<TextStroke>,
    align: TextAlign,
    transform: Transform,
    mask: Option<&Mask>,
) {
    if size.is_nan() || size <= 0.0 || content.is_empty() {
        return;
    }
    let font = text::font(font_id);
    let scale = font.scale(size);
    let start = match align {
        TextAlign::Left => x,
        TextAlign::Center => x - font.measure(content, size) / 2.0,
        TextAlign::Right => x - font.measure(content, size),
    };
    // The outline first, then the fill (canvas strokeText then fillText).
    let passes = outline.into_iter().map(Some).chain(std::iter::once(None));
    for pass in passes {
        let paint = solid(pass.map_or(color, |outline| outline.color), true);
        let pass_stroke =
            pass.map(|outline| Stroke { width: outline.width / scale, line_join: LineJoin::Round, ..stroke(1.0) });
        for (glyph, offset) in font.layout(content, size) {
            let Some(path) = caches.glyph(font_id, glyph) else { continue };
            let glyph_transform = transform.pre_translate(start + offset, y).pre_scale(scale, scale);
            match &pass_stroke {
                Some(stroke) => target.stroke_path(path, &paint, stroke, glyph_transform, mask),
                None => target.fill_path(path, &paint, FillRule::Winding, glyph_transform, mask),
            }
        }
    }
}

/// Everything needed to turn world snapshots into pixels, kept across frames: the image cache,
/// the rasterizer caches and the built-in art pack.
#[derive(Debug)]
pub struct WorldRenderer {
    pub images: ImageStore,
    pub rasterizer: Rasterizer,
    /// Built-in art for anything the game binds no art to (`None`: colored shapes only).
    pub art: Option<&'static BuiltinArt>,
}

impl Default for WorldRenderer {
    fn default() -> Self {
        Self::new()
    }
}

impl WorldRenderer {
    /// A renderer with the embedded art pack and a default-size image cache.
    pub fn new() -> Self {
        Self { images: ImageStore::default(), rasterizer: Rasterizer::new(), art: Some(BuiltinArt::embedded()) }
    }

    /// The draw list of a frame (images are decoded into [`WorldRenderer::images`]).
    pub fn draw_list(&mut self, snapshot: &WorldSnapshot) -> DrawList {
        crate::world::build_world(snapshot, self.art, &mut self.images)
    }

    /// Renders a frame into a new pixmap of the viewport size × `scale` (like the C#
    /// `RenderToBitmap`).
    pub fn render(&mut self, snapshot: &WorldSnapshot, scale: f32) -> Pixmap {
        let list = self.draw_list(snapshot);
        let (width, height) = snapshot.pixel_size(f64::from(scale));
        self.rasterizer.render_to_pixmap(&list, &self.images, width, height, scale)
    }

    /// Draws a frame onto an existing surface with `transform` as the base transform.
    pub fn render_into(&mut self, snapshot: &WorldSnapshot, target: &mut PixmapMut<'_>, transform: Transform) {
        let list = self.draw_list(snapshot);
        self.rasterizer.render(&list, &self.images, target, transform);
    }
}

/// Renders a snapshot with the embedded art and fresh caches (tests, thumbnails, screenshots).
pub fn render_to_pixmap(snapshot: &WorldSnapshot, scale: f32) -> Pixmap {
    WorldRenderer::new().render(snapshot, scale)
}

/// PNG bytes of a pixmap (straight alpha). Empty only if encoding fails.
pub fn encode_png(pixmap: &Pixmap) -> Vec<u8> {
    pixmap.encode_png().unwrap_or_default()
}
