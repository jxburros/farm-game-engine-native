//! `Preview`: Edit Mode's map and the art studio's previews (mirrors `fe_preview_*`).

use crate::{alive, bytes_arg, host_error, json_arg, rgba_image, RgbaImage};
use farm_host::{Guarded, HostPreview};
use wasm_bindgen::prelude::*;

/// A project, its compiled content and art, and render caches: rasterizes one scene viewport
/// or one visual binding per call. Images come back with straight alpha, ready for `ImageData`
/// (farm-ffi hands .NET premultiplied pixels).
#[wasm_bindgen(js_name = Preview)]
#[derive(Debug)]
pub struct WasmPreview {
    preview: Guarded<HostPreview>,
}

#[wasm_bindgen(js_class = Preview)]
impl WasmPreview {
    /// A preview of a (migrated) project: JSON text, a parsed object or its UTF-8 bytes.
    #[wasm_bindgen(constructor)]
    pub fn new(
        #[wasm_bindgen(unchecked_param_type = "Uint8Array | string | object")] project: JsValue,
    ) -> Result<WasmPreview, JsValue> {
        alive()?;
        let project = bytes_arg(&project)?;
        let preview = farm_host::catch(|| HostPreview::new(&project)).map_err(host_error)?;
        Ok(WasmPreview { preview: Guarded::new(preview) })
    }

    fn run<R>(&mut self, body: impl FnOnce(&mut HostPreview) -> Result<R, String>) -> Result<R, JsValue> {
        alive()?;
        self.preview.run(body).map_err(host_error)
    }

    /// Replaces the previewed project after an edit (decoded images stay cached). A bad
    /// project throws and keeps the previous one.
    #[wasm_bindgen(js_name = setProject)]
    pub fn set_project(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "Uint8Array | string | object")] project: JsValue,
    ) -> Result<(), JsValue> {
        let project = bytes_arg(&project)?;
        self.run(|p| p.set_project(&project))
    }

    /// Renders a scene: `{sceneId, tileSize?: 28, padding?: 12, camera?: {x, y, width, height},
    /// scale?: 1}` (without `camera`, the whole scene).
    #[wasm_bindgen]
    pub fn render(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "object | string")] request: JsValue,
    ) -> Result<RgbaImage, JsValue> {
        let request = json_arg(&request)?;
        self.run(|p| p.render(request.as_bytes())).map(rgba_image)
    }

    /// Renders one frame of a visual binding: `{visual: {assetId, animation?, frame?} | null,
    /// tick?: 0, direction?: "down", moving?: true, size, scale?: 1}`; 0×0 when nothing
    /// resolves.
    #[wasm_bindgen(js_name = renderVisual)]
    pub fn render_visual(
        &mut self,
        #[wasm_bindgen(unchecked_param_type = "object | string")] request: JsValue,
    ) -> Result<RgbaImage, JsValue> {
        let request = json_arg(&request)?;
        self.run(|p| p.render_visual(request.as_bytes())).map(rgba_image)
    }
}
