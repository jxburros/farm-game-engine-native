//! Rendering over the C ABI, on `farm-render`.
//!
//! - [`fe_render_json`]: stateless requests. `{"type":"editorSnapshot",…}` returns the decorated
//!   Edit Mode snapshot as JSON (what `EditModeView` builds with `BuildEditorSnapshot` +
//!   `ApplyGraphics`); `{"type":"rasterize",…}` returns a PNG of any snapshot (tests).
//! - `fe_preview_*`: a handle for Edit Mode's map. It keeps the project, its compiled content and
//!   the image caches between frames and rasterizes one scene viewport per call into
//!   premultiplied RGBA8.
//!
//! Same conventions as the sessions: Rust allocates results and .NET frees them with
//! `fe_bytes_free`; panics are caught and never unwind into .NET. On failure `out` holds the
//! UTF-8 error message instead of a result. A preview is used by one thread at a time; after a
//! panic it is poisoned and answers `FeResult::Poisoned`.

use crate::{view_json, FeBytes, FeResult};
use farm_render::{apply_graphics, editor_snapshot, GraphicsSource, SnapshotCamera, WorldRenderer, WorldSnapshot};
use farm_sim::schema::{GameContent, GameProject};
use serde::Deserialize;
use std::panic::{catch_unwind, AssertUnwindSafe};

/// Largest image a request may produce, in pixels.
const MAX_PIXELS: u64 = 64 * 1024 * 1024;
/// Largest accepted scale factor.
const MAX_SCALE: f64 = 16.0;

fn default_tile_size() -> f64 {
    28.0
}

fn default_padding() -> f64 {
    12.0
}

fn default_scale() -> f64 {
    1.0
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

/// `{sceneId, tileSize, padding, camera?, scale}` for [`fe_preview_render`].
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

/// `{visual?, tick, direction, moving, size, scale}` for [`fe_preview_render_visual`].
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

fn default_direction() -> String {
    "down".to_owned()
}

fn default_moving() -> bool {
    true
}

/// Largest side of a visual preview.
const MAX_VISUAL_SIZE: f64 = 1024.0;

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

fn handle_render_request(bytes: &[u8]) -> Result<Vec<u8>, String> {
    let request: RenderRequest = serde_json::from_slice(bytes).map_err(|e| format!("render request: {e}"))?;
    match request {
        RenderRequest::EditorSnapshot { project, scene_id, tile_size, padding } => {
            let content = farm_sim::create_content_from_project(&project);
            let graphics = GraphicsSource::from_project(&project);
            let snapshot = decorated_editor_snapshot(&project, &content, &graphics, &scene_id, tile_size, padding)?;
            Ok(view_json::to_json(&snapshot).into_bytes())
        }
        RenderRequest::Rasterize { snapshot, scale } => {
            let pixmap = render(&mut WorldRenderer::new(), &snapshot, scale)?;
            Ok(farm_render::encode_png(&pixmap))
        }
    }
}

fn panic_message(payload: Box<dyn std::any::Any + Send>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_owned()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "panic".to_owned()
    }
}

unsafe fn bytes_arg<'a>(ptr: *const u8, len: usize) -> Option<&'a [u8]> {
    if ptr.is_null() {
        (len == 0).then_some(&[])
    } else {
        Some(std::slice::from_raw_parts(ptr, len))
    }
}

unsafe fn write(out: *mut FeBytes, bytes: Vec<u8>) {
    if !out.is_null() {
        *out = FeBytes::from_vec(bytes);
    }
}

unsafe fn write_empty(out: *mut FeBytes) {
    if !out.is_null() {
        *out = FeBytes::empty();
    }
}

/// Runs a stateless request. `request` is UTF-8 JSON, tagged by `type`:
///
/// - `{"type":"editorSnapshot","project":{…},"sceneId":"…","tileSize":28,"padding":12}` → the
///   decorated Edit Mode snapshot as JSON;
/// - `{"type":"rasterize","snapshot":{…WorldSnapshot…},"scale":1}` → PNG bytes.
///
/// On failure `out` holds the error message.
///
/// # Safety
/// `request` must point to `len` readable bytes; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn fe_render_json(request: *const u8, len: usize, out: *mut FeBytes) -> FeResult {
    write_empty(out);
    let Some(bytes) = bytes_arg(request, len) else { return FeResult::InvalidArgument };
    if out.is_null() {
        return FeResult::InvalidArgument;
    }
    match catch_unwind(AssertUnwindSafe(|| handle_render_request(bytes))) {
        Ok(Ok(result)) => {
            write(out, result);
            FeResult::Ok
        }
        Ok(Err(message)) => {
            write(out, message.into_bytes());
            FeResult::InvalidArgument
        }
        Err(payload) => {
            write(out, panic_message(payload).into_bytes());
            FeResult::Panic
        }
    }
}

/// An Edit Mode map preview: the project, its compiled content and art, and render caches.
pub struct FePreview {
    project: GameProject,
    content: GameContent,
    graphics: GraphicsSource,
    renderer: WorldRenderer,
    poisoned: bool,
}

impl std::fmt::Debug for FePreview {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FePreview").field("poisoned", &self.poisoned).finish_non_exhaustive()
    }
}

impl FePreview {
    fn set_project(&mut self, project: GameProject) {
        self.content = farm_sim::create_content_from_project(&project);
        self.graphics = GraphicsSource::from_project(&project);
        self.project = project;
    }

    fn render(&mut self, request: &PreviewRequest) -> Result<Vec<u8>, String> {
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
        let pixmap = render(&mut self.renderer, &snapshot, request.scale)?;
        let mut out = Vec::with_capacity(8 + pixmap.data().len());
        out.extend_from_slice(&pixmap.width().to_le_bytes());
        out.extend_from_slice(&pixmap.height().to_le_bytes());
        out.extend_from_slice(pixmap.data());
        Ok(out)
    }
}

impl FePreview {
    /// One frame of a visual binding, resolved exactly as the game resolves it (clip, frame at
    /// `tick`, direction), scaled to fit `size` and centered. An empty frame (0×0) when the
    /// binding resolves to nothing.
    fn render_visual(&mut self, request: &VisualRequest) -> Result<Vec<u8>, String> {
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
            return Ok(vec![0; 8]);
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
        let mut out = Vec::with_capacity(8 + pixmap.data().len());
        out.extend_from_slice(&pixmap.width().to_le_bytes());
        out.extend_from_slice(&pixmap.height().to_le_bytes());
        out.extend_from_slice(pixmap.data());
        Ok(out)
    }
}

fn parse_project(bytes: &[u8]) -> Result<GameProject, String> {
    serde_json::from_slice(bytes).map_err(|e| format!("project JSON: {e}"))
}

/// Creates a preview for a (migrated) project. On failure `error` holds the message.
///
/// # Safety
/// `project_json` must point to `len` readable bytes; `out` and `error` must be valid pointers.
/// Free the preview with [`fe_preview_free`].
#[no_mangle]
pub unsafe extern "C" fn fe_preview_new(
    project_json: *const u8,
    len: usize,
    out: *mut *mut FePreview,
    error: *mut FeBytes,
) -> FeResult {
    write_empty(error);
    if out.is_null() {
        return FeResult::InvalidArgument;
    }
    *out = std::ptr::null_mut();
    let Some(bytes) = bytes_arg(project_json, len) else { return FeResult::InvalidArgument };
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<FePreview, String> {
        let project = parse_project(bytes)?;
        let mut preview = FePreview {
            project: GameProject::default(),
            content: GameContent::default(),
            graphics: GraphicsSource::from_project(&GameProject::default()),
            renderer: WorldRenderer::new(),
            poisoned: false,
        };
        preview.set_project(project);
        Ok(preview)
    }));
    match result {
        Ok(Ok(preview)) => {
            *out = Box::into_raw(Box::new(preview));
            FeResult::Ok
        }
        Ok(Err(message)) => {
            write(error, message.into_bytes());
            FeResult::InvalidArgument
        }
        Err(payload) => {
            write(error, panic_message(payload).into_bytes());
            FeResult::Panic
        }
    }
}

unsafe fn with_preview<F>(preview: *mut FePreview, out: *mut FeBytes, body: F) -> FeResult
where
    F: FnOnce(&mut FePreview) -> Result<Vec<u8>, String>,
{
    write_empty(out);
    if preview.is_null() {
        return FeResult::InvalidArgument;
    }
    let preview = &mut *preview;
    if preview.poisoned {
        return FeResult::Poisoned;
    }
    match catch_unwind(AssertUnwindSafe(|| body(preview))) {
        Ok(Ok(bytes)) => {
            write(out, bytes);
            FeResult::Ok
        }
        Ok(Err(message)) => {
            write(out, message.into_bytes());
            FeResult::InvalidArgument
        }
        Err(payload) => {
            preview.poisoned = true;
            write(out, panic_message(payload).into_bytes());
            FeResult::Panic
        }
    }
}

/// Replaces the previewed project (after an edit). Decoded images stay cached. On failure the
/// previous project stays and `out` holds the message; on success `out` is empty.
///
/// # Safety
/// `preview` from [`fe_preview_new`]; `project_json` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_preview_set_project(
    preview: *mut FePreview,
    project_json: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(project_json, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_preview(preview, out, |preview| {
        preview.set_project(parse_project(bytes)?);
        Ok(Vec::new())
    })
}

/// Renders a scene: `request` is `{"sceneId":"…","tileSize":28,"padding":12,"camera":{"x":…,
/// "y":…,"width":…,"height":…},"scale":1}` (`camera` optional: the whole scene). `out` receives
/// the width and height as little-endian `u32`s followed by `width × height` premultiplied RGBA8
/// pixels.
///
/// # Safety
/// `preview` from [`fe_preview_new`]; `request` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_preview_render(
    preview: *mut FePreview,
    request: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(request, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_preview(preview, out, |preview| {
        let request: PreviewRequest = serde_json::from_slice(bytes).map_err(|e| format!("preview request: {e}"))?;
        preview.render(&request)
    })
}

/// Renders one frame of a visual binding of the previewed project (the art studio's preview):
/// `request` is `{"visual":{"assetId":…,"animation"?:…,"frame"?:…}|null,"tick":0,
/// "direction":"down","moving":true,"size":96,"scale":1}`. `out` receives the width and height
/// as little-endian `u32`s and the premultiplied RGBA8 pixels; 0×0 when nothing resolves.
///
/// # Safety
/// `preview` from [`fe_preview_new`]; `request` points to `len` bytes; `out` is valid.
#[no_mangle]
pub unsafe extern "C" fn fe_preview_render_visual(
    preview: *mut FePreview,
    request: *const u8,
    len: usize,
    out: *mut FeBytes,
) -> FeResult {
    let Some(bytes) = bytes_arg(request, len) else {
        write_empty(out);
        return FeResult::InvalidArgument;
    };
    with_preview(preview, out, |preview| {
        let request: VisualRequest = serde_json::from_slice(bytes).map_err(|e| format!("visual request: {e}"))?;
        preview.render_visual(&request)
    })
}

/// Frees a preview. Null is a no-op.
///
/// # Safety
/// `preview` must have come from [`fe_preview_new`] and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn fe_preview_free(preview: *mut FePreview) {
    if !preview.is_null() {
        drop(Box::from_raw(preview));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn starter_project() -> String {
        let path: std::path::PathBuf =
            [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", "starter-farm.json"]
                .iter()
                .collect();
        let fixture: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
        fixture["project"].to_string()
    }

    unsafe fn take(bytes: FeBytes) -> Vec<u8> {
        if bytes.ptr.is_null() {
            return Vec::new();
        }
        let data = std::slice::from_raw_parts(bytes.ptr, bytes.len).to_vec();
        crate::fe_bytes_free(bytes);
        data
    }

    fn render_json(request: &str) -> (FeResult, Vec<u8>) {
        let mut out = FeBytes::empty();
        let result = unsafe { fe_render_json(request.as_ptr(), request.len(), &mut out) };
        (result, unsafe { take(out) })
    }

    #[test]
    fn editor_snapshots_and_png_rasters_through_the_c_abi() {
        let project = starter_project();
        let scene_id = serde_json::from_str::<serde_json::Value>(&project).unwrap()["player"]["sceneId"].clone();
        let request = format!(
            r#"{{"type":"editorSnapshot","project":{project},"sceneId":{scene_id},"tileSize":28,"padding":12}}"#
        );
        let (result, json) = render_json(&request);
        assert_eq!(result, FeResult::Ok, "{}", String::from_utf8_lossy(&json));
        let snapshot: WorldSnapshot = serde_json::from_slice(&json).unwrap();
        assert!(snapshot.grid_overlay && snapshot.tile_gap == Some(1.0) && snapshot.pixel_art == Some(true));
        assert!(snapshot.tiles.iter().flatten().all(|tile| tile.art_layers.is_some()));

        let request = format!(r#"{{"type":"rasterize","snapshot":{},"scale":2}}"#, String::from_utf8(json).unwrap());
        let (result, png) = render_json(&request);
        assert_eq!(result, FeResult::Ok, "{}", String::from_utf8_lossy(&png));
        assert_eq!(&png[..8], b"\x89PNG\r\n\x1a\n");
        let image = farm_render::images::decode_image(&png).unwrap();
        let (width, height) = snapshot.pixel_size(2.0);
        assert_eq!((image.width(), image.height()), (width, height));
    }

    #[test]
    fn bad_render_requests_report_errors() {
        for (request, expected) in [
            ("{not json", "render request"),
            (r#"{"type":"nope"}"#, "render request"),
            (r#"{"type":"rasterize","snapshot":{},"scale":0}"#, "Scale must be"),
            (r#"{"type":"rasterize","snapshot":{"width":100000,"height":100000,"tileSize":32}}"#, "too large"),
        ] {
            let (result, message) = render_json(request);
            assert_eq!(result, FeResult::InvalidArgument, "{request}");
            let message = String::from_utf8(message).unwrap();
            assert!(message.contains(expected), "{request}: {message}");
        }
        let project = starter_project();
        let (result, message) =
            render_json(&format!(r#"{{"type":"editorSnapshot","project":{project},"sceneId":"missing"}}"#));
        assert_eq!(result, FeResult::InvalidArgument);
        assert_eq!(String::from_utf8(message).unwrap(), "Scene missing not found.");
        let mut out = FeBytes::empty();
        assert_eq!(unsafe { fe_render_json(std::ptr::null(), 3, &mut out) }, FeResult::InvalidArgument);
    }

    #[test]
    fn previews_render_camera_viewports_as_premultiplied_rgba() {
        let project = starter_project();
        let mut preview: *mut FePreview = std::ptr::null_mut();
        let mut error = FeBytes::empty();
        let result = unsafe { fe_preview_new(project.as_ptr(), project.len(), &mut preview, &mut error) };
        assert_eq!(result, FeResult::Ok, "{}", String::from_utf8_lossy(&unsafe { take(error) }));
        let scene_id = serde_json::from_str::<serde_json::Value>(&project).unwrap()["player"]["sceneId"].clone();
        let render = |request: String| {
            let mut out = FeBytes::empty();
            let result = unsafe { fe_preview_render(preview, request.as_ptr(), request.len(), &mut out) };
            (result, unsafe { take(out) })
        };
        let size = |bytes: &[u8]| {
            (u32::from_le_bytes(bytes[0..4].try_into().unwrap()), u32::from_le_bytes(bytes[4..8].try_into().unwrap()))
        };

        let (result, frame) = render(format!(
            r#"{{"sceneId":{scene_id},"tileSize":28,"padding":12,"camera":{{"x":40,"y":30,"width":100.5,"height":60}},"scale":2}}"#
        ));
        assert_eq!(result, FeResult::Ok, "{}", String::from_utf8_lossy(&frame));
        assert_eq!(size(&frame), (201, 120));
        assert_eq!(frame.len(), 8 + 201 * 120 * 4);
        // Premultiplied: no channel exceeds alpha.
        assert!(frame[8..].chunks_exact(4).all(|p| p[0] <= p[3] && p[1] <= p[3] && p[2] <= p[3]));

        // Without a camera: the whole scene.
        let (result, whole) = render(format!(r#"{{"sceneId":{scene_id},"tileSize":28,"padding":12,"scale":1}}"#));
        assert_eq!(result, FeResult::Ok);
        let snapshot_width = 12.0 * 2.0 + 16.0 * 29.0 - 1.0;
        assert_eq!(size(&whole).0, snapshot_width as u32);

        // A new project replaces the old one; bad input keeps it.
        let bad = b"{oops";
        let mut out = FeBytes::empty();
        assert_eq!(
            unsafe { fe_preview_set_project(preview, bad.as_ptr(), bad.len(), &mut out) },
            FeResult::InvalidArgument
        );
        assert!(String::from_utf8(unsafe { take(out) }).unwrap().starts_with("project JSON"));
        let mut out = FeBytes::empty();
        assert_eq!(unsafe { fe_preview_set_project(preview, project.as_ptr(), project.len(), &mut out) }, FeResult::Ok);
        let (result, message) = render(r#"{"sceneId":"missing"}"#.to_owned());
        assert_eq!(result, FeResult::InvalidArgument);
        assert_eq!(String::from_utf8(message).unwrap(), "Scene missing not found.");
        unsafe { fe_preview_free(preview) };
        unsafe { fe_preview_free(std::ptr::null_mut()) };
    }

    #[test]
    fn previews_render_one_frame_of_a_visual_binding() {
        // A 4×2 sheet: two 2×2 frames, red then blue.
        let mut sheet = farm_render::tiny_skia::Pixmap::new(4, 2).unwrap();
        for (i, pixel) in sheet.pixels_mut().iter_mut().enumerate() {
            let blue = i % 4 >= 2;
            *pixel = farm_render::tiny_skia::PremultipliedColorU8::from_rgba(
                if blue { 0 } else { 255 },
                0,
                if blue { 255 } else { 0 },
                255,
            )
            .unwrap();
        }
        let png = farm_render::encode_png(&sheet);
        let mut project: serde_json::Value = serde_json::from_str(&starter_project()).unwrap();
        project["customAssets"] = serde_json::json!([{
            "id": "art-sheet", "name": "sheet.png", "type": "art", "width": 4, "height": 2,
            "dataUrl": format!("data:image/png;base64,{}", farm_cart::cartridge::base64_encode(&png)),
            "animations": [{"name": "idle", "loop": true, "frames": [
                {"x": 0, "y": 0, "width": 2, "height": 2, "ticks": 10},
                {"x": 2, "y": 0, "width": 2, "height": 2, "ticks": 10}
            ]}]
        }]);
        let project = project.to_string();
        let mut preview: *mut FePreview = std::ptr::null_mut();
        let mut error = FeBytes::empty();
        let result = unsafe { fe_preview_new(project.as_ptr(), project.len(), &mut preview, &mut error) };
        assert_eq!(result, FeResult::Ok, "{}", String::from_utf8_lossy(&unsafe { take(error) }));
        let render = |request: &str| {
            let mut out = FeBytes::empty();
            let result = unsafe { fe_preview_render_visual(preview, request.as_ptr(), request.len(), &mut out) };
            (result, unsafe { take(out) })
        };
        let center = |frame: &[u8]| {
            let width = u32::from_le_bytes(frame[0..4].try_into().unwrap()) as usize;
            let at = 8 + ((width / 2) * width + width / 2) * 4;
            (frame[at], frame[at + 2])
        };

        let (result, first) = render(r#"{"visual":{"assetId":"art-sheet"},"tick":0,"size":16}"#);
        assert_eq!(result, FeResult::Ok, "{}", String::from_utf8_lossy(&first));
        assert_eq!(&first[0..8], &[16, 0, 0, 0, 16, 0, 0, 0]);
        assert_eq!(center(&first), (255, 0), "tick 0 shows the red frame");
        let (_, second) = render(r#"{"visual":{"assetId":"art-sheet"},"tick":12,"size":16}"#);
        assert_eq!(center(&second), (0, 255), "tick 12 shows the blue frame");

        let (result, none) = render(r#"{"visual":{"assetId":"missing"},"size":16}"#);
        assert_eq!(result, FeResult::Ok);
        assert_eq!(none, vec![0; 8]);
        let (result, _) = render(r#"{"visual":null,"size":0}"#);
        assert_eq!(result, FeResult::InvalidArgument);
        unsafe { fe_preview_free(preview) };
    }
}
