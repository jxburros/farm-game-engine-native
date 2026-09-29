//! Rendering over the C ABI, on `farm-render` (requests are `farm_host::render`'s, shared with
//! farm-wasm).
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

use crate::{bytes_arg, write, write_empty, FeBytes, FeResult};
use farm_host::{Guarded, HostPreview, Rgba};

/// Width and height as little-endian `u32`s, then the pixels.
fn sized(image: Rgba) -> Vec<u8> {
    let mut out = Vec::with_capacity(8 + image.data.len());
    out.extend_from_slice(&image.width.to_le_bytes());
    out.extend_from_slice(&image.height.to_le_bytes());
    out.extend_from_slice(&image.data);
    out
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
    match farm_host::catch(|| farm_host::render::render_json(bytes)) {
        Ok(result) => {
            write(out, result.into_bytes());
            FeResult::Ok
        }
        Err(e) => {
            write(out, e.message.into_bytes());
            e.kind.into()
        }
    }
}

/// An Edit Mode map preview: the project, its compiled content and art, and render caches.
pub struct FePreview {
    preview: Guarded<HostPreview>,
}

impl std::fmt::Debug for FePreview {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FePreview").field("poisoned", &self.preview.is_poisoned()).finish_non_exhaustive()
    }
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
    match farm_host::catch(|| HostPreview::new(bytes)) {
        Ok(preview) => {
            *out = Box::into_raw(Box::new(FePreview { preview: Guarded::new(preview) }));
            FeResult::Ok
        }
        Err(e) => {
            write(error, e.message.into_bytes());
            e.kind.into()
        }
    }
}

/// Runs `body` on the preview. A poisoned preview answers `Poisoned` with an empty `out`.
unsafe fn with_preview<F>(preview: *mut FePreview, out: *mut FeBytes, body: F) -> FeResult
where
    F: FnOnce(&mut HostPreview) -> Result<Vec<u8>, String>,
{
    write_empty(out);
    if preview.is_null() {
        return FeResult::InvalidArgument;
    }
    match (*preview).preview.run(body) {
        Ok(bytes) => {
            write(out, bytes);
            FeResult::Ok
        }
        Err(e) if e.kind == farm_host::ErrorKind::Poisoned => FeResult::Poisoned,
        Err(e) => {
            write(out, e.message.into_bytes());
            e.kind.into()
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
    with_preview(preview, out, |preview| preview.set_project(bytes).map(|()| Vec::new()))
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
    with_preview(preview, out, |preview| preview.render(bytes).map(sized))
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
    with_preview(preview, out, |preview| preview.render_visual(bytes).map(sized))
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
    use farm_render::WorldSnapshot;

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
