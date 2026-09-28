//! Read-only editor preview. The F# compiler supplies content; no simulation is started.
use crate::{view_json, FeBytes, FeResult};
use farm_sim::schema::{GameContent, GameProject};
use serde::Deserialize;
use std::panic::{catch_unwind, AssertUnwindSafe};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PreviewRequest {
    project: GameProject,
    content: GameContent,
    scene_id: String,
    tile_size: f64,
    padding: f64,
}

fn preview(input: &[u8]) -> Result<String, String> {
    let request: PreviewRequest = serde_json::from_slice(input).map_err(|e| e.to_string())?;
    let scene = request.project.scenes.iter().find(|s| s.id == request.scene_id).ok_or("Preview scene not found.")?;
    let mut snapshot =
        farm_render::shell::build_editor(&request.project, &request.content, scene, request.tile_size, request.padding);
    farm_render::graphics::apply_graphics(&mut snapshot, &request.project, &request.content, None, scene, 0.0, false);
    Ok(view_json::to_json(&snapshot))
}

/// Build an editor snapshot from project + compiled content + scene id and geometry.
/// Error details use the same output buffer as a successful snapshot.
///
/// # Safety
/// `input` points to `len` readable bytes and `out` is writable. Free the returned
/// buffer with `fe_bytes_free`, including on errors.
#[no_mangle]
pub unsafe extern "C" fn fe_preview_snapshot_json(input: *const u8, len: usize, out: *mut FeBytes) -> FeResult {
    if out.is_null() {
        return FeResult::InvalidArgument;
    }
    *out = FeBytes::empty();
    if input.is_null() {
        return FeResult::InvalidArgument;
    }
    match catch_unwind(AssertUnwindSafe(|| preview(std::slice::from_raw_parts(input, len)))) {
        Ok(Ok(json)) => {
            *out = FeBytes::from_vec(json.into_bytes());
            FeResult::Ok
        }
        Ok(Err(error)) => {
            *out = FeBytes::from_vec(error.into_bytes());
            FeResult::InvalidArgument
        }
        Err(_) => FeResult::Panic,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn invalid_input_always_returns_an_owned_or_empty_buffer() {
        unsafe {
            let mut out = FeBytes::empty();
            assert_eq!(fe_preview_snapshot_json(std::ptr::null(), 0, &mut out), FeResult::InvalidArgument);
            assert!(out.ptr.is_null());
            assert_eq!(fe_preview_snapshot_json(b"{".as_ptr(), 1, &mut out), FeResult::InvalidArgument);
            assert!(out.len > 0);
            crate::fe_bytes_free(out);
        }
    }
}
