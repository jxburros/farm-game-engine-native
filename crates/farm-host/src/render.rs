//! Rendering for hosts, on `farm-render`.
//!
//! - [`render_json`]: stateless requests. `{"type":"editorSnapshot",…}` returns the decorated
//!   Edit Mode snapshot as JSON (what `EditModeView` builds with `BuildEditorSnapshot` +
//!   `ApplyGraphics`); `{"type":"rasterize",…}` returns a PNG of any snapshot (tests).
//! - [`HostPreview`]: Edit Mode's map. It keeps the project, its compiled content and the image
//!   caches between frames and rasterizes one scene viewport (or one visual binding, for the art
//!   studio) per call into premultiplied RGBA8.

use crate::{view_json, Rgba};
use farm_render::{apply_graphics, editor_snapshot, GraphicsSource, SnapshotCamera, WorldRenderer, WorldSnapshot};
use farm_sim::schema::{GameContent, GameProject};
use serde::Deserialize;

/// Largest image a request may produce, in pixels.
const MAX_PIXELS: u64 = 64 * 1024 * 1024;
/// Largest accepted scale factor.
const MAX_SCALE: f64 = 16.0;
/// Largest side of a visual preview.
const MAX_VISUAL_SIZE: f64 = 1024.0;

fn default_tile_size() -> f64 {
    28.0
}

fn default_padding() -> f64 {
    12.0
}

fn default_scale() -> f64 {
    1.0
}

fn default_direction() -> String {
    "down".to_owned()
}

fn default_moving() -> bool {
    true
}

#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
enum RenderRequest {
    EditorSnapshot {
        project: Box<GameProject>,
        scene_id: String,
        #[serde(default = "default_tile_size")]
        tile_size: f64,
        #[serde(default = "default_padding")]
        padding: f64,
    },
    Rasterize {
        snapshot: Box<WorldSnapshot>,
        #[serde(default = "default_scale")]
        scale: f64,
    },
}

/// `{sceneId, tileSize, padding, camera?, scale}` for [`HostPreview::render`].
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PreviewRequest {
    scene_id: String,
    #[serde(default = "default_tile_size")]
    tile_size: f64,
    #[serde(default = "default_padding")]
    padding: f64,
    #[serde(default)]
    camera: Option<SnapshotCamera>,
    #[serde(default = "default_scale")]
    scale: f64,
}

/// `{visual?, tick, direction, moving, size, scale}` for [`HostPreview::render_visual`].
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct VisualRequest {
    #[serde(default)]
    visual: Option<farm_sim::schema::VisualRef>,
    #[serde(default)]
    tick: f64,
    #[serde(default = "default_direction")]
    direction: String,
    #[serde(default = "default_moving")]
    moving: bool,
    /// The square the frame is fitted into, in logical pixels.
    size: f64,
    #[serde(default = "default_scale")]
    scale: f64,
}

/// The answer to a [`render_json`] request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RenderOutput {
    /// `editorSnapshot`: the snapshot as JSON.
    Json(String),
    /// `rasterize`: PNG bytes.
    Png(Vec<u8>),
}

impl RenderOutput {
    pub fn into_bytes(self) -> Vec<u8> {
        match self {
            RenderOutput::Json(text) => text.into_bytes(),
            RenderOutput::Png(bytes) => bytes,
        }
    }
}

/// The Edit Mode snapshot of a scene decorated with the project's art, as `EditModeView` does:
/// `BuildEditorSnapshot` then `ApplyGraphics(snapshot, FromProject(project), scene, 0, false)`.
fn decorated_editor_snapshot(
    project: &GameProject,
    content: &GameContent,
    graphics: &GraphicsSource,
    scene_id: &str,
    tile_size: f64,
    padding: f64,
) -> Result<WorldSnapshot, String> {
    let scene = project
        .scenes
        .iter()
        .find(|scene| scene.id == scene_id)
        .ok_or_else(|| format!("Scene {scene_id} not found."))?;
    let mut snapshot = editor_snapshot(project, content, scene, tile_size, padding);
    apply_graphics(&mut snapshot, graphics, scene, 0.0, false);
    Ok(snapshot)
}

fn check_scale(scale: f64) -> Result<f32, String> {
    if scale.is_finite() && scale > 0.0 && scale <= MAX_SCALE {
        Ok(scale as f32)
    } else {
        Err(format!("Scale must be in (0, {MAX_SCALE}], got {scale}."))
    }
}

/// Device size of a snapshot's viewport at `scale`, refusing oversized requests.
fn pixel_size(snapshot: &WorldSnapshot, scale: f32) -> Result<(u32, u32), String> {
    let (width, height) = snapshot.pixel_size(f64::from(scale));
    if u64::from(width) * u64::from(height) > MAX_PIXELS {
        return Err(format!("The requested image is too large ({width}×{height})."));
    }
    Ok((width, height))
}

fn render(
    renderer: &mut WorldRenderer,
    snapshot: &WorldSnapshot,
    scale: f64,
) -> Result<farm_render::tiny_skia::Pixmap, String> {
    let scale = check_scale(scale)?;
    let (width, height) = pixel_size(snapshot, scale)?;
    let list = renderer.draw_list(snapshot);
    Ok(renderer.rasterizer.render_to_pixmap(&list, &renderer.images, width, height, scale))
}

fn rgba(pixmap: farm_render::tiny_skia::Pixmap) -> Rgba {
    Rgba { width: pixmap.width(), height: pixmap.height(), data: pixmap.take() }
}

/// Runs a stateless request (UTF-8 JSON, tagged by `type`):
///
/// - `{"type":"editorSnapshot","project":{…},"sceneId":"…","tileSize":28,"padding":12}` → the
///   decorated Edit Mode snapshot as JSON;
/// - `{"type":"rasterize","snapshot":{…WorldSnapshot…},"scale":1}` → PNG bytes.
pub fn render_json(request: &[u8]) -> Result<RenderOutput, String> {
    let request: RenderRequest = serde_json::from_slice(request).map_err(|e| format!("render request: {e}"))?;
    match request {
        RenderRequest::EditorSnapshot { project, scene_id, tile_size, padding } => {
            let content = farm_sim::create_content_from_project(&project);
            let graphics = GraphicsSource::from_project(&project);
            let snapshot = decorated_editor_snapshot(&project, &content, &graphics, &scene_id, tile_size, padding)?;
            Ok(RenderOutput::Json(view_json::to_json(&snapshot)))
        }
        RenderRequest::Rasterize { snapshot, scale } => {
            let pixmap = render(&mut WorldRenderer::new(), &snapshot, scale)?;
            Ok(RenderOutput::Png(farm_render::encode_png(&pixmap)))
        }
    }
}

fn parse_project(bytes: &[u8]) -> Result<GameProject, String> {
    serde_json::from_slice(bytes).map_err(|e| format!("project JSON: {e}"))
}

/// An Edit Mode map preview: the project, its compiled content and art, and render caches.
pub struct HostPreview {
    project: GameProject,
    content: GameContent,
    graphics: GraphicsSource,
    renderer: WorldRenderer,
}

impl std::fmt::Debug for HostPreview {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HostPreview").field("project", &self.project.id).finish_non_exhaustive()
    }
}

impl HostPreview {
    /// A preview of a (migrated) project's JSON.
    pub fn new(project_json: &[u8]) -> Result<Self, String> {
        let project = parse_project(project_json)?;
        let mut preview = Self {
            project: GameProject::default(),
            content: GameContent::default(),
            graphics: GraphicsSource::from_project(&GameProject::default()),
            renderer: WorldRenderer::new(),
        };
        preview.replace_project(project);
        Ok(preview)
    }

    fn replace_project(&mut self, project: GameProject) {
        self.content = farm_sim::create_content_from_project(&project);
        self.graphics = GraphicsSource::from_project(&project);
        self.project = project;
    }

    /// Replaces the previewed project (after an edit). Decoded images stay cached. On failure
    /// the previous project stays.
    pub fn set_project(&mut self, project_json: &[u8]) -> Result<(), String> {
        self.replace_project(parse_project(project_json)?);
        Ok(())
    }

    /// Renders a scene: `{"sceneId":"…","tileSize":28,"padding":12,"camera":{"x":…,"y":…,
    /// "width":…,"height":…},"scale":1}` (`camera` optional: the whole scene), as premultiplied
    /// RGBA8.
    pub fn render(&mut self, request: &[u8]) -> Result<Rgba, String> {
        let request: PreviewRequest = serde_json::from_slice(request).map_err(|e| format!("preview request: {e}"))?;
        let mut snapshot = decorated_editor_snapshot(
            &self.project,
            &self.content,
            &self.graphics,
            &request.scene_id,
            request.tile_size,
            request.padding,
        )?;
        // Only the camera viewport is culled, translated and rasterized.
        snapshot.camera = request.camera;
        Ok(rgba(render(&mut self.renderer, &snapshot, request.scale)?))
    }

    /// One frame of a visual binding (the art studio's preview): `{"visual":{"assetId":…,
    /// "animation"?:…,"frame"?:…}|null,"tick":0,"direction":"down","moving":true,"size":96,
    /// "scale":1}`, resolved exactly as the game resolves it (clip, frame at `tick`, direction),
    /// scaled to fit `size` and centered, as premultiplied RGBA8. An empty image (0×0) when the
    /// binding resolves to nothing.
    pub fn render_visual(&mut self, request: &[u8]) -> Result<Rgba, String> {
        let request: VisualRequest = serde_json::from_slice(request).map_err(|e| format!("visual request: {e}"))?;
        let scale = check_scale(request.scale)?;
        if !request.size.is_finite() || request.size < 1.0 || request.size > MAX_VISUAL_SIZE {
            return Err(format!("Visual size must be in [1, {MAX_VISUAL_SIZE}], got {}.", request.size));
        }
        let tick = if request.tick.is_finite() { request.tick } else { 0.0 };
        let sprite = farm_render::resolve_visual(
            &self.graphics.assets,
            request.visual.as_ref(),
            tick,
            &request.direction,
            request.moving,
        );
        let frame = sprite.and_then(|sprite| {
            let image = self.renderer.images.get_shared(&sprite.image_url)?;
            let (image_width, image_height) = self.renderer.images.size(image)?;
            let (image_width, image_height) = (f64::from(image_width), f64::from(image_height));
            let width = if sprite.frame_width > 0.0 { sprite.frame_width } else { image_width };
            let height = if sprite.frame_height > 0.0 { sprite.frame_height } else { image_height };
            let x = sprite.source_x.unwrap_or(sprite.frame * width);
            let y = sprite.source_y.unwrap_or(sprite.row * height);
            let inside = x >= 0.0 && y >= 0.0 && x + width <= image_width && y + height <= image_height;
            (inside && width > 0.0 && height > 0.0).then_some((image, x, y, width, height))
        });
        let Some((image, x, y, width, height)) = frame else {
            return Ok(Rgba::default());
        };
        let side = request.size;
        let fit = (side / width).min(side / height);
        let (w, h) = (width * fit, height * fit);
        let mut list = farm_render::DrawList::new();
        list.push(farm_render::DrawCmd::Image {
            image,
            src: farm_render::Rect::new(x as f32, y as f32, width as f32, height as f32),
            dst: farm_render::Rect::new(((side - w) / 2.0) as f32, ((side - h) / 2.0) as f32, w as f32, h as f32),
            opacity: 1.0,
            sampling: if self.graphics.pixel_art {
                farm_render::Sampling::Nearest
            } else {
                farm_render::Sampling::Smooth
            },
            tint: None,
        });
        let pixels = (side * f64::from(scale)).ceil() as u32;
        let pixmap = self.renderer.rasterizer.render_to_pixmap(&list, &self.renderer.images, pixels, pixels, scale);
        Ok(rgba(pixmap))
    }
}
