//! Rendering for hosts, on `farm-render`.
//!
//! - [`render_json`]: stateless requests. `{"type":"editorSnapshot",…}` returns the decorated
//!   Edit Mode snapshot as JSON (what `EditModeView` builds with `BuildEditorSnapshot` +
//!   `ApplyGraphics`); `{"type":"rasterize",…}` returns a PNG of any snapshot (tests).
//! - [`HostPreview`]: Edit Mode's map. It keeps the project, its compiled content, the image
//!   caches and the decorated snapshot of the scene on screen between frames, and rasterizes one
//!   scene viewport (or one visual binding, for the art studio) per call into premultiplied
//!   RGBA8. Edits that only change scenes (painting) arrive as those scenes alone
//!   ([`HostPreview::set_scenes`]), without the rest of the project and its art.

use crate::{view_json, Rgba};
use farm_render::{apply_graphics, editor_snapshot, GraphicsSource, SnapshotCamera, WorldRenderer, WorldSnapshot};
use farm_sim::schema::{GameContent, GameProject};
use serde::Deserialize;

/// Largest image a request may produce, in pixels: 256 MB of RGBA natively, 64 MB on the web,
/// where a module has little memory and a failed allocation aborts instead of throwing.
const MAX_PIXELS: u64 = if cfg!(target_arch = "wasm32") { 16 * 1024 * 1024 } else { 64 * 1024 * 1024 };
/// Largest accepted scale factor.
const MAX_SCALE: f64 = 16.0;
/// Largest side of a visual preview.
const MAX_VISUAL_SIZE: f64 = 1024.0;
/// Largest scene side, in tiles, a request may describe.
const MAX_TILES: i32 = 4096;
/// Largest tile size and padding, in world pixels.
const MAX_TILE_SIZE: f64 = 1024.0;
const MAX_PADDING: f64 = 4096.0;

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

/// `value` when it is finite and within `min..=max` (`what` names it in the error).
fn check_range(what: &str, value: f64, min: f64, max: f64) -> Result<f64, String> {
    if value.is_finite() && (min..=max).contains(&value) {
        Ok(value)
    } else {
        Err(format!("{what} must be in [{min}, {max}], got {value}."))
    }
}

/// The tile layout of a request: a tile size of at least one pixel (a zero size would make a huge
/// scene fit a 1×1 image while every tile is still drawn) and a sane padding.
fn check_layout(tile_size: f64, padding: f64) -> Result<(), String> {
    check_range("Tile size", tile_size, 1.0, MAX_TILE_SIZE)?;
    check_range("Padding", padding, 0.0, MAX_PADDING)?;
    Ok(())
}

fn check_camera(camera: Option<&SnapshotCamera>) -> Result<(), String> {
    let Some(camera) = camera else { return Ok(()) };
    let finite = [camera.x, camera.y, camera.width, camera.height].iter().all(|value| value.is_finite());
    if !finite || camera.width <= 0.0 || camera.height <= 0.0 {
        return Err("The camera needs a finite position and a positive size.".to_owned());
    }
    Ok(())
}

/// Refuses snapshots whose drawing would not be bounded by the image they produce.
fn check_snapshot(snapshot: &WorldSnapshot) -> Result<(), String> {
    if !(0..=MAX_TILES).contains(&snapshot.width) || !(0..=MAX_TILES).contains(&snapshot.height) {
        return Err(format!(
            "A snapshot must be at most {MAX_TILES}×{MAX_TILES} tiles, got {}×{}.",
            snapshot.width, snapshot.height
        ));
    }
    check_layout(snapshot.tile_size, snapshot.padding)?;
    if let Some(gap) = snapshot.tile_gap {
        check_range("Tile gap", gap, 0.0, snapshot.tile_size)?;
    }
    check_camera(snapshot.camera.as_ref())
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
    check_snapshot(snapshot)?;
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
            check_layout(tile_size, padding)?;
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
    /// The decorated snapshot of the last scene rendered and its layout (scene id, tile size,
    /// padding): scrolling only moves the camera over it. Dropped by every project change.
    snapshot: Option<(SnapshotKey, WorldSnapshot)>,
}

/// Scene id, tile size and padding (as bits) of a cached snapshot.
type SnapshotKey = (String, u64, u64);

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
            snapshot: None,
        };
        preview.replace_project(project);
        Ok(preview)
    }

    fn replace_project(&mut self, project: GameProject) {
        self.content = farm_sim::create_content_from_project(&project);
        self.graphics = GraphicsSource::from_project(&project);
        self.project = project;
        self.snapshot = None;
    }

    /// Replaces the previewed project (after an edit). Decoded images stay cached. On failure
    /// the previous project stays.
    pub fn set_project(&mut self, project_json: &[u8]) -> Result<(), String> {
        self.replace_project(parse_project(project_json)?);
        Ok(())
    }

    /// Replaces scenes of the previewed project by id (an edit that changed only scenes):
    /// `scenes_json` is a JSON array of scenes. The art and the rest of the project stay; the
    /// compiled content is rebuilt from them. A scene the project does not have refuses the
    /// whole call and the previous project stays (the host then sends the project).
    pub fn set_scenes(&mut self, scenes_json: &[u8]) -> Result<(), String> {
        let scenes: Vec<farm_sim::schema::Scene> =
            serde_json::from_slice(scenes_json).map_err(|e| format!("scenes JSON: {e}"))?;
        let mut slots = Vec::with_capacity(scenes.len());
        for scene in &scenes {
            let slot = self
                .project
                .scenes
                .iter()
                .position(|known| known.id == scene.id)
                .ok_or_else(|| format!("Scene {} is not in the previewed project.", scene.id))?;
            slots.push(slot);
        }
        for (slot, scene) in slots.into_iter().zip(scenes) {
            self.project.scenes[slot] = scene;
        }
        self.content = farm_sim::create_content_from_project(&self.project);
        self.snapshot = None;
        Ok(())
    }

    /// Renders a scene: `{"sceneId":"…","tileSize":28,"padding":12,"camera":{"x":…,"y":…,
    /// "width":…,"height":…},"scale":1}` (`camera` optional: the whole scene), as premultiplied
    /// RGBA8.
    pub fn render(&mut self, request: &[u8]) -> Result<Rgba, String> {
        let request: PreviewRequest = serde_json::from_slice(request).map_err(|e| format!("preview request: {e}"))?;
        check_layout(request.tile_size, request.padding)?;
        check_camera(request.camera.as_ref())?;
        let key = (request.scene_id, request.tile_size.to_bits(), request.padding.to_bits());
        let snapshot = match &mut self.snapshot {
            Some((cached, snapshot)) if *cached == key => snapshot,
            slot => {
                let snapshot = decorated_editor_snapshot(
                    &self.project,
                    &self.content,
                    &self.graphics,
                    &key.0,
                    request.tile_size,
                    request.padding,
                )?;
                &mut slot.insert((key, snapshot)).1
            }
        };
        // Only the camera viewport is culled, translated and rasterized.
        snapshot.camera = request.camera;
        Ok(rgba(render(&mut self.renderer, snapshot, request.scale)?))
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

#[cfg(test)]
mod tests {
    use super::*;

    fn rasterize(snapshot: &str) -> Result<RenderOutput, String> {
        render_json(format!(r#"{{"type":"rasterize","snapshot":{snapshot}}}"#).as_bytes())
    }

    /// A 2×2 snapshot without tiles, with `fields` overriding its defaults.
    fn snapshot(fields: &str) -> String {
        let mut snapshot: serde_json::Map<String, serde_json::Value> =
            serde_json::from_str(r#"{"width":2,"height":2,"tileSize":28,"padding":12,"gridOverlay":true}"#).unwrap();
        let fields: serde_json::Map<String, serde_json::Value> =
            serde_json::from_str(&format!("{{{fields}}}")).unwrap();
        snapshot.extend(fields);
        serde_json::Value::Object(snapshot).to_string()
    }

    #[test]
    fn rasterize_refuses_layouts_that_do_not_bound_the_work() {
        assert!(matches!(rasterize(&snapshot("")), Ok(RenderOutput::Png(_))));
        // tileSize 0 made a 100000×100000 scene fit a 1×1 image while every grid cell was still
        // drawn (10^10 draw commands).
        let huge = snapshot(r#""width":100000,"height":100000,"tileSize":0,"padding":0,"tileGap":0"#);
        assert!(rasterize(&huge).unwrap_err().contains("at most"));
        for (fields, error) in [
            (r#""tileSize":0"#, "Tile size"),
            (r#""tileSize":-28"#, "Tile size"),
            (r#""tileSize":5000"#, "Tile size"),
            (r#""padding":-1"#, "Padding"),
            (r#""tileGap":-1"#, "Tile gap"),
            (r#""tileGap":100"#, "Tile gap"),
            (r#""width":-1"#, "at most"),
            (r#""height":5000"#, "at most"),
            (r#""camera":{"x":0,"y":0,"width":0,"height":10}"#, "camera"),
        ] {
            let result = rasterize(&snapshot(fields));
            assert!(result.as_ref().is_err_and(|e| e.contains(error)), "{fields}: {result:?}");
        }
    }

    #[test]
    fn the_grid_overlay_follows_the_tiles_not_the_declared_size() {
        // 4096×4096 tiles declared, none sent.
        let snapshot: WorldSnapshot =
            serde_json::from_str(&snapshot(r#""width":4096,"height":4096,"tileSize":1,"padding":0"#)).unwrap();
        let list = WorldRenderer::new().draw_list(&snapshot);
        assert!(list.len() < 64, "{} draw commands", list.len());
    }

    #[test]
    fn previews_refuse_bad_layouts() {
        let path: std::path::PathBuf =
            [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", "starter-farm.json"]
                .iter()
                .collect();
        let fixture: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
        let project = &fixture["project"];
        let scene = project["player"]["sceneId"].as_str().unwrap();
        let mut preview = HostPreview::new(project.to_string().as_bytes()).unwrap();
        assert!(preview.render(format!(r#"{{"sceneId":"{scene}"}}"#).as_bytes()).unwrap().width > 0);
        for fields in [
            r#""tileSize":0"#,
            r#""tileSize":-28"#,
            r#""padding":-12"#,
            r#""camera":{"x":0,"y":0,"width":-5,"height":10}"#,
        ] {
            let request = format!(r#"{{"sceneId":"{scene}",{fields}}}"#);
            assert!(preview.render(request.as_bytes()).is_err(), "{fields}");
        }
        let request = format!(r#"{{"type":"editorSnapshot","project":{project},"sceneId":"{scene}","tileSize":0}}"#);
        assert!(render_json(request.as_bytes()).unwrap_err().contains("Tile size"));
    }

    #[test]
    fn scene_edits_replace_scenes_without_the_rest_of_the_project() {
        let path: std::path::PathBuf =
            [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", "starter-farm.json"]
                .iter()
                .collect();
        let fixture: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
        let mut project = fixture["project"].clone();
        let scene_id = project["player"]["sceneId"].as_str().unwrap().to_owned();
        let request = format!(r#"{{"sceneId":"{scene_id}","tileSize":28,"padding":12,"scale":1}}"#);
        let mut preview = HostPreview::new(project.to_string().as_bytes()).unwrap();
        let before = preview.render(request.as_bytes()).unwrap();
        // Scrolling reuses the cached snapshot and draws the same pixels.
        assert_eq!(preview.render(request.as_bytes()).unwrap(), before);

        // Paint a tile as water, sending only that scene.
        let scenes = project["scenes"].as_array_mut().unwrap();
        let scene = scenes.iter_mut().find(|scene| scene["id"] == scene_id.as_str()).unwrap();
        let tile = &mut scene["tiles"][4][4];
        tile["type"] = serde_json::json!("water");
        tile["background"] = serde_json::json!("water");
        tile["overlay"] = serde_json::Value::Null;
        tile["object"] = serde_json::Value::Null;
        let edited = serde_json::Value::Array(vec![scene.clone()]).to_string();
        let mut stranger = scene.clone();
        stranger["id"] = serde_json::json!("nowhere");
        let stranger = serde_json::Value::Array(vec![stranger]).to_string();
        preview.set_scenes(edited.as_bytes()).unwrap();
        let painted = preview.render(request.as_bytes()).unwrap();
        assert_ne!(painted, before, "the cached snapshot was dropped");
        // The same as sending the whole project.
        let mut whole = HostPreview::new(project.to_string().as_bytes()).unwrap();
        assert_eq!(whole.render(request.as_bytes()).unwrap(), painted);

        // An unknown scene changes nothing.
        let error = preview.set_scenes(stranger.as_bytes());
        assert!(error.unwrap_err().contains("nowhere"));
        assert_eq!(preview.render(request.as_bytes()).unwrap(), painted);
        assert!(preview.set_scenes(b"{oops").is_err());
    }
}
