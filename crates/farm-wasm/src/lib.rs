//! `farm-wasm`: the game for web pages, through `wasm-bindgen` (docs/LANGUAGES.md
//! "WebAssembly (farm-wasm)").
//!
//! It mirrors farm-ffi, the C ABI the desktop editor uses, and runs the same code: both are thin
//! bindings over `farm-host`, so a web page speaks the same JSON as .NET and gets the same
//! pixels, sounds and hashes.
//!
//! - [`Player`](player::WasmPlayer): the graphical player (Play Mode, web demo exports);
//! - [`Session`](session::WasmSession): a headless game;
//! - [`Preview`](preview::WasmPreview) and [`render_json`]: Edit Mode's map and art previews;
//! - [`hash_text`], [`sfx_cues`], [`sfx_samples`], [`version`], [`last_panic`].
//!
//! The JavaScript API is documented in `crates/farm-wasm/README.md`.
//!
//! **Errors** are thrown as JavaScript `Error`s named `FarmError` with a `kind`: `"invalid"`
//! (bad input; the object stays usable), `"poisoned"` (an engine failure or an earlier panic;
//! the object refuses every later call) or `"panic"`.
//!
//! **Panics.** `wasm32-unknown-unknown` aborts on panic: nothing unwinds, so nothing can be
//! caught. The call traps and JavaScript sees a `WebAssembly.RuntimeError` ("unreachable").
//! Before that, the panic hook installed at start-up logs the message with `console.error` and
//! records it; from then on every call on any object throws a `FarmError` of kind
//! `"poisoned"` with that message ([`last_panic`] returns it too), because the module's memory
//! may be half-updated. The page should drop the module instance and load it again.
#![forbid(unsafe_code)]

pub mod player;
pub mod preview;
pub mod session;
pub mod storage;

use js_sys::{Array, Float32Array, Object, Reflect, Uint8Array, Uint8ClampedArray};
use std::sync::Mutex;
use wasm_bindgen::prelude::*;
use wasm_bindgen::JsCast;

/// The message of the first panic, once one happened.
static PANIC: Mutex<Option<String>> = Mutex::new(None);

#[wasm_bindgen]
extern "C" {
    #[wasm_bindgen(js_namespace = console, js_name = error)]
    fn console_error(message: &str);
}

#[wasm_bindgen(typescript_custom_section)]
const TYPES: &'static str = r#"
/** An input event, in frame pixels; keys are `KeyboardEvent.key.toLowerCase()`. */
export type InputEvent =
  | { type: "keyDown"; key: string; repeat?: boolean }
  | { type: "keyUp"; key: string }
  | { type: "text"; text: string }
  | { type: "pointerMove"; x: number; y: number }
  | { type: "pointerDown"; x: number; y: number; button?: "primary" | "secondary" | "middle" }
  | { type: "pointerUp"; x: number; y: number; button?: "primary" | "secondary" | "middle" }
  | { type: "pointerLeft" }
  | { type: "wheel"; dx?: number; dy: number }
  | { type: "gamepadButton"; button: string; pressed: boolean }
  | { type: "gamepadAxis"; axis: "leftX" | "leftY" | "rightX" | "rightY"; value: number }
  | { type: "focusLost" };

/** `Player.frame`'s request (the same JSON as `fe_player_frame`). */
export interface FrameRequest {
  /** Seconds since the last frame (clamped to 0..0.25). */
  dt: number;
  events?: InputEvent[];
  width: number;
  height: number;
  /** False steps the game without drawing. Default true. */
  render?: boolean;
}

/** What a frame tells the page besides the pixels. */
export interface FrameInfo {
  /** Sound cues to play now (`sfxSamples(cue, rate)`), with their gain (volumes applied). */
  sounds: { cue: string; gain: number }[];
  /** `quit`, `fullscreen:on`, `fullscreen:off`, `title:<text>`. */
  requests: string[];
  screen: "title" | "playing" | "pause" | "settings" | "credits" | "loadSlots" | "saveSlots" | "newGameSlots" | "confirm";
  /** An in-game panel or an engine modal is open. */
  modal: boolean;
}

export interface FrameResult {
  /** 0 when the request did not render. */
  width: number;
  height: number;
  /** `width × height` RGBA pixels for `new ImageData(pixels, width, height)`; null when not rendered. */
  pixels: Uint8ClampedArray<ArrayBuffer> | null;
  info: FrameInfo;
  /** A save slot or the settings changed: persist `exportStorage()`. */
  storageChanged: boolean;
}

/** `new Player(game, options)`. */
export interface PlayerOptions {
  /** Seed of new games (the project's own seed when absent). */
  seed?: string;
  /** `"embedded"` (default: Play Mode, straight into the game) or `"standalone"` (title screen, save slots). */
  mode?: "embedded" | "standalone";
  /** Overrides the stored accessibility setting. */
  reducedMotion?: boolean;
  /** Overrides the stored interface size (1 = 100 %). */
  uiScale?: number;
  /** The page's language (`"es"`): the game interface follows it until the player picks one in Settings. */
  locale?: string;
  /** Saves and settings to start with (`exportStorage()` of an earlier run). */
  storage?: string | StorageDocument;
}

/** Save slots (base64 `FGSV` saves) and settings (TOML) for the page to persist. */
export interface StorageDocument {
  version: number;
  settings: string | null;
  slots: { [slot: string]: string };
}

/** An RGBA image with straight alpha, for `new ImageData(pixels, width, height)`. */
export interface RgbaImage {
  width: number;
  height: number;
  pixels: Uint8ClampedArray<ArrayBuffer>;
}
"#;

#[wasm_bindgen]
extern "C" {
    #[wasm_bindgen(typescript_type = "FrameResult")]
    pub type FrameResult;
    #[wasm_bindgen(typescript_type = "RgbaImage")]
    pub type RgbaImage;
}

/// Installs the panic hook (runs when the module is initialized).
#[wasm_bindgen(start)]
fn start() {
    std::panic::set_hook(Box::new(|info| {
        let message = info.to_string();
        if cfg!(target_arch = "wasm32") {
            console_error(&format!("farm-wasm: {message}"));
        }
        let mut panic = PANIC.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        panic.get_or_insert(message);
    }));
}

/// The message of the panic that stopped this module instance, if one did.
#[wasm_bindgen(js_name = lastPanic)]
pub fn last_panic() -> Option<String> {
    PANIC.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clone()
}

/// The crate version.
#[wasm_bindgen]
pub fn version() -> String {
    env!("CARGO_PKG_VERSION").to_owned()
}

/// FNV-1a state hash (two 32-bit lanes, 16 hex characters) of `text`: the hash of
/// `stateJson()` is `hash()`.
#[wasm_bindgen(js_name = hashText)]
pub fn hash_text(text: &str) -> Result<String, JsValue> {
    alive()?;
    Ok(farm_sim::hash_text(text))
}

/// Runs a stateless render request (the same JSON as `fe_render_json`):
/// `{"type":"editorSnapshot","project":{…},"sceneId":"…","tileSize":28,"padding":12}` answers
/// the decorated Edit Mode snapshot as a JSON string; `{"type":"rasterize","snapshot":{…},
/// "scale":1}` answers PNG bytes.
#[wasm_bindgen(js_name = renderJson, unchecked_return_type = "string | Uint8Array<ArrayBuffer>")]
pub fn render_json(
    #[wasm_bindgen(unchecked_param_type = "string | object")] request: JsValue,
) -> Result<JsValue, JsValue> {
    alive()?;
    let request = json_arg(&request)?;
    match farm_host::catch(|| farm_host::render::render_json(request.as_bytes())).map_err(host_error)? {
        farm_host::render::RenderOutput::Json(text) => Ok(JsValue::from_str(&text)),
        farm_host::render::RenderOutput::Png(bytes) => Ok(Uint8Array::from(&bytes[..]).into()),
    }
}

/// The names of the synthesized sound effects frames may ask for.
#[wasm_bindgen(js_name = sfxCues, unchecked_return_type = "string[]")]
pub fn sfx_cues() -> Array {
    farm_runtime::audio::SFX_PRESETS.iter().map(|(name, _)| JsValue::from_str(name)).collect()
}

/// The mono samples (-1..1) of a sound cue at `sampleRate` Hz, before the frame's gain: play
/// them with WebAudio (`AudioBuffer.copyToChannel`, then a `GainNode` at the cue's gain).
/// `undefined` for an unknown cue. Cache them per cue: they never change.
#[wasm_bindgen(js_name = sfxSamples, unchecked_return_type = "Float32Array<ArrayBuffer> | undefined")]
pub fn sfx_samples(cue: &str, #[wasm_bindgen(js_name = sampleRate)] sample_rate: u32) -> Option<Float32Array> {
    let preset = farm_runtime::audio::sfx_preset(cue)?;
    Some(Float32Array::from(&preset.render(sample_rate.clamp(3000, 384_000))[..]))
}

/// Refuses calls once a panic stopped the module.
pub(crate) fn alive() -> Result<(), JsValue> {
    match last_panic() {
        None => Ok(()),
        Some(message) => Err(js_error(
            "poisoned",
            &format!("The game engine panicked earlier and this module instance must be reloaded: {message}"),
        )),
    }
}

/// A JavaScript `Error` named `FarmError` with a `kind`.
pub(crate) fn js_error(kind: &str, message: &str) -> JsValue {
    let error = js_sys::Error::new(message);
    error.set_name("FarmError");
    // Setting a property on a fresh Error cannot fail.
    let _ = Reflect::set(&error, &JsValue::from_str("kind"), &JsValue::from_str(kind));
    error.into()
}

pub(crate) fn host_error(error: farm_host::HostError) -> JsValue {
    let kind = match error.kind {
        farm_host::ErrorKind::Invalid => "invalid",
        farm_host::ErrorKind::Panic => "panic",
        farm_host::ErrorKind::Poisoned => "poisoned",
    };
    js_error(kind, &error.message)
}

/// JSON text from a string, or from any other value through `JSON.stringify`
/// (`undefined`/`null` give an empty string).
pub(crate) fn json_arg(value: &JsValue) -> Result<String, JsValue> {
    if let Some(text) = value.as_string() {
        return Ok(text);
    }
    if value.is_undefined() || value.is_null() {
        return Ok(String::new());
    }
    js_sys::JSON::stringify(value)
        .map(String::from)
        .map_err(|_| js_error("invalid", "The argument cannot be converted to JSON."))
}

/// Bytes from a `Uint8Array`, an `ArrayBuffer`, a string (UTF-8) or a JSON-able object.
pub(crate) fn bytes_arg(value: &JsValue) -> Result<Vec<u8>, JsValue> {
    if let Some(array) = value.dyn_ref::<Uint8Array>() {
        return Ok(array.to_vec());
    }
    if let Some(buffer) = value.dyn_ref::<js_sys::ArrayBuffer>() {
        return Ok(Uint8Array::new(buffer).to_vec());
    }
    if value.is_undefined() || value.is_null() {
        return Err(js_error("invalid", "Expected a Uint8Array, an ArrayBuffer, a string or an object."));
    }
    json_arg(value).map(String::into_bytes)
}

/// `{width, height, pixels}` with straight alpha.
pub(crate) fn rgba_image(mut image: farm_host::Rgba) -> RgbaImage {
    image.unpremultiply();
    let object = Object::new();
    set(&object, "width", image.width.into());
    set(&object, "height", image.height.into());
    set(&object, "pixels", Uint8ClampedArray::from(&image.data[..]).into());
    object.unchecked_into()
}

pub(crate) fn set(object: &Object, key: &str, value: JsValue) {
    // Setting a property on a plain object cannot fail.
    let _ = Reflect::set(object, &JsValue::from_str(key), &value);
}
