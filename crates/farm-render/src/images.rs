//! Port of `ImageStore.cs`: the decoded-image cache behind the renderer.
//!
//! Custom assets arrive as `data:` URLs (base64) inside the project; built-in sheets as
//! `builtin://<file>`. Each source is decoded once (PNG, JPEG, GIF first frame, WebP, BMP) into
//! premultiplied RGBA and kept in a bounded LRU cache under a stable [`ImageId`]. Sources that
//! cannot be decoded (web-only `idb://` references, corrupt data, oversized images) are cached
//! as missing so they are not retried every frame.
//!
//! Lookups of a shared `Arc<str>` first try the pointer (no hashing or comparing of large data
//! URLs every frame; the C# store used reference equality the same way), then the content.
//!
//! Hosts can plug in a [`SourceResolver`] for other URL schemes, such as the `asset:<id>`
//! references of a cartridge's embedded files.

use crate::builtin_art::{BuiltinArt, URL_SCHEME};
use base64::Engine as _;
use serde::{Deserialize, Serialize};
use std::borrow::Cow;
use std::collections::BTreeMap;
use std::io::Cursor;
use std::sync::Arc;

/// Largest accepted image side, in pixels (decompression-bomb guard).
pub const MAX_IMAGE_SIDE: u32 = 8192;
/// Largest accepted image area, in pixels (64 MP).
pub const MAX_IMAGE_PIXELS: u64 = 64 * 1024 * 1024;
/// Default number of cached sources (`ImageStore(maxSize: 300)`).
pub const DEFAULT_CAPACITY: usize = 300;

/// A decoded image: premultiplied RGBA8, row-major, no padding.
#[derive(Clone, PartialEq, Eq)]
pub struct Image {
    width: u32,
    height: u32,
    pixels: Vec<u8>,
}

impl std::fmt::Debug for Image {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Image").field("width", &self.width).field("height", &self.height).finish_non_exhaustive()
    }
}

impl Image {
    /// Wraps premultiplied RGBA8 pixels; `None` when the size does not match or is empty.
    pub fn from_premultiplied(width: u32, height: u32, pixels: Vec<u8>) -> Option<Self> {
        let expected = u64::from(width) * u64::from(height) * 4;
        (width > 0 && height > 0 && pixels.len() as u64 == expected).then_some(Self { width, height, pixels })
    }

    /// Premultiplies straight-alpha RGBA8 pixels (the way Skia does, rounding to nearest).
    pub fn from_rgba(width: u32, height: u32, mut pixels: Vec<u8>) -> Option<Self> {
        for pixel in pixels.chunks_exact_mut(4) {
            let a = pixel[3];
            if a != 255 {
                for channel in &mut pixel[..3] {
                    *channel = mul_div_255_round(*channel, a);
                }
            }
        }
        Self::from_premultiplied(width, height, pixels)
    }

    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }

    /// Premultiplied RGBA8 pixels.
    pub fn pixels(&self) -> &[u8] {
        &self.pixels
    }
}

/// `SkMulDiv255Round`: `a * b / 255` rounded to nearest.
pub(crate) fn mul_div_255_round(a: u8, b: u8) -> u8 {
    let product = u32::from(a) * u32::from(b) + 128;
    ((product + (product >> 8)) >> 8) as u8
}

/// A cached image of an [`ImageStore`]. Ids are never reused within a store, so a draw list
/// can refer to images without copying them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(transparent)]
pub struct ImageId(pub u32);

/// Why an image could not be decoded.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ImageError {
    /// Not a supported format, or corrupt data.
    Decode(String),
    /// Wider, taller or larger than [`MAX_IMAGE_SIDE`] / [`MAX_IMAGE_PIXELS`].
    TooLarge { width: u32, height: u32 },
}

impl std::fmt::Display for ImageError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ImageError::Decode(message) => write!(f, "cannot decode image: {message}"),
            ImageError::TooLarge { width, height } => write!(f, "image is too large ({width}×{height})"),
        }
    }
}

impl std::error::Error for ImageError {}

/// Decodes PNG, JPEG, GIF (first frame), WebP or BMP bytes into premultiplied RGBA, refusing
/// images over [`MAX_IMAGE_SIDE`] or [`MAX_IMAGE_PIXELS`] before allocating their pixels.
pub fn decode_image(bytes: &[u8]) -> Result<Image, ImageError> {
    let decode_error = |e: image::ImageError| match e {
        image::ImageError::Limits(_) => ImageError::TooLarge { width: 0, height: 0 },
        other => ImageError::Decode(other.to_string()),
    };
    let reader = image::ImageReader::new(Cursor::new(bytes))
        .with_guessed_format()
        .map_err(|e| ImageError::Decode(e.to_string()))?;
    let (width, height) = reader.into_dimensions().map_err(decode_error)?;
    if width == 0 || height == 0 {
        return Err(ImageError::Decode("empty image".to_owned()));
    }
    if width > MAX_IMAGE_SIDE || height > MAX_IMAGE_SIDE || u64::from(width) * u64::from(height) > MAX_IMAGE_PIXELS {
        return Err(ImageError::TooLarge { width, height });
    }
    let mut reader = image::ImageReader::new(Cursor::new(bytes))
        .with_guessed_format()
        .map_err(|e| ImageError::Decode(e.to_string()))?;
    let mut limits = image::Limits::default();
    limits.max_image_width = Some(MAX_IMAGE_SIDE);
    limits.max_image_height = Some(MAX_IMAGE_SIDE);
    limits.max_alloc = Some(MAX_IMAGE_PIXELS * 4 + 16 * 1024 * 1024);
    reader.limits(limits);
    let decoded = reader.decode().map_err(decode_error)?;
    let rgba = decoded.into_rgba8();
    let (width, height) = rgba.dimensions();
    Image::from_rgba(width, height, rgba.into_raw()).ok_or_else(|| ImageError::Decode("empty image".to_owned()))
}

/// The raw bytes of a base64 `data:` URL (`data:<type>;base64,<payload>`), or `None`.
/// Whitespace inside the payload is ignored, like .NET `Convert.FromBase64String`.
pub fn data_url_bytes(source: &str) -> Option<Vec<u8>> {
    if source.len() < 5 || !source.as_bytes()[..5].eq_ignore_ascii_case(b"data:") {
        return None;
    }
    let comma = source.find(',')?;
    let header = &source.as_bytes()[5..comma];
    if header.len() < 7 || !header[header.len() - 7..].eq_ignore_ascii_case(b";base64") {
        return None;
    }
    let payload: Vec<u8> =
        source.as_bytes()[comma + 1..].iter().copied().filter(|b| !b.is_ascii_whitespace()).collect();
    let engine = base64::engine::GeneralPurpose::new(
        &base64::alphabet::STANDARD,
        base64::engine::GeneralPurposeConfig::new()
            .with_decode_allow_trailing_bits(true)
            .with_decode_padding_mode(base64::engine::DecodePaddingMode::Indifferent),
    );
    engine.decode(payload).ok()
}

/// The raw image bytes behind a source: a base64 `data:` URL or a `builtin://` sheet of the
/// embedded art pack; `None` otherwise.
pub fn source_bytes(source: &str) -> Option<Cow<'static, [u8]>> {
    if source.starts_with(URL_SCHEME) {
        return BuiltinArt::resolve_sheet(source).map(Cow::Borrowed);
    }
    data_url_bytes(source).map(Cow::Owned)
}

/// Decodes an image source (see [`source_bytes`]); `None` when unsupported or corrupt.
pub fn decode_source(source: &str) -> Option<Image> {
    let bytes = source_bytes(source)?;
    if bytes.is_empty() {
        return None;
    }
    decode_image(&bytes).ok()
}

/// Supplies the encoded bytes of image sources the store does not know itself (for example a
/// player resolving `asset:<id>` through the cartridge's asset table). Returning `None` falls
/// back to the built-in handling of `data:` and `builtin://` sources.
pub type SourceResolver = Box<dyn Fn(&str) -> Option<Vec<u8>> + Send>;

#[derive(Debug)]
struct Entry {
    source: Arc<str>,
    image: Option<Image>,
    last_used: u64,
}

/// Bounded LRU cache of decoded images, keyed by source string.
///
/// Entries used since the last [`ImageStore::begin_frame`] are never evicted, so every image a
/// draw list refers to stays available until it is rasterized, even when a frame uses more
/// sources than the capacity (the store then grows until the next frame).
pub struct ImageStore {
    capacity: usize,
    resolver: Option<SourceResolver>,
    entries: BTreeMap<u32, Entry>,
    by_content: BTreeMap<Arc<str>, u32>,
    /// Pointer fast path: address of a shared source → (the source, kept alive so the address
    /// cannot be reused, and its entry).
    by_pointer: BTreeMap<usize, (Arc<str>, u32)>,
    next_id: u32,
    clock: u64,
    frame_start: u64,
}

impl std::fmt::Debug for ImageStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ImageStore")
            .field("capacity", &self.capacity)
            .field("entries", &self.entries.len())
            .field("resolver", &self.resolver.is_some())
            .finish_non_exhaustive()
    }
}

impl Default for ImageStore {
    fn default() -> Self {
        Self::new(DEFAULT_CAPACITY)
    }
}

impl ImageStore {
    /// A store keeping at most `capacity` sources (at least 1) between frames.
    pub fn new(capacity: usize) -> Self {
        Self {
            capacity: capacity.max(1),
            resolver: None,
            entries: BTreeMap::new(),
            by_content: BTreeMap::new(),
            by_pointer: BTreeMap::new(),
            next_id: 1,
            clock: 0,
            frame_start: 0,
        }
    }

    /// Consults `resolver` for every source before the built-in `data:`/`builtin://` handling.
    /// Sources decoded before the call stay cached.
    pub fn set_resolver(&mut self, resolver: impl Fn(&str) -> Option<Vec<u8>> + Send + 'static) {
        self.resolver = Some(Box::new(resolver));
    }

    /// Decodes a source the way this store does (resolver first, then [`decode_source`]).
    pub fn decode(&self, source: &str) -> Option<Image> {
        if let Some(bytes) = self.resolver.as_ref().and_then(|resolve| resolve(source)) {
            return decode_image(&bytes).ok();
        }
        decode_source(source)
    }

    /// Number of cached sources (including ones that failed to decode).
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Marks the start of a frame: images looked up from now on stay cached until the next
    /// call. [`crate::build_world`] calls it.
    pub fn begin_frame(&mut self) {
        self.frame_start = self.clock + 1;
    }

    /// The image for `source`, decoding it on first use; `None` when it cannot be decoded
    /// (cached as missing).
    pub fn get(&mut self, source: &str) -> Option<ImageId> {
        if source.is_empty() {
            return None;
        }
        let id = match self.by_content.get(source) {
            Some(&id) => id,
            None => self.insert_entry(Arc::from(source), None),
        };
        self.touch(id)
    }

    /// Like [`ImageStore::get`] for a shared source; repeated lookups of the same allocation skip
    /// comparing the (possibly very long) string.
    pub fn get_shared(&mut self, source: &Arc<str>) -> Option<ImageId> {
        if source.is_empty() {
            return None;
        }
        let address = Arc::as_ptr(source).cast::<u8>() as usize;
        if let Some((known, id)) = self.by_pointer.get(&address) {
            if Arc::ptr_eq(known, source) {
                let id = *id;
                return self.touch(id);
            }
        }
        let id = match self.by_content.get(source.as_ref()) {
            Some(&id) => id,
            None => self.insert_entry(Arc::clone(source), None),
        };
        if self.by_pointer.len() >= self.capacity * 4 {
            // Snapshots deserialized every frame bring new allocations each time; start over.
            self.by_pointer.clear();
        }
        self.by_pointer.insert(address, (Arc::clone(source), id));
        self.touch(id)
    }

    /// Adds (or replaces) an image the host decoded or generated itself, under `source`.
    pub fn insert(&mut self, source: &str, image: Image) -> ImageId {
        if let Some(old) = self.by_content.get(source).copied() {
            self.remove(old);
        }
        let id = self.insert_entry(Arc::from(source), Some(image));
        self.touch(id);
        ImageId(id)
    }

    /// The decoded image of an id (`None` once evicted).
    pub fn image(&self, id: ImageId) -> Option<&Image> {
        self.entries.get(&id.0).and_then(|entry| entry.image.as_ref())
    }

    /// Width and height of an image.
    pub fn size(&self, id: ImageId) -> Option<(u32, u32)> {
        self.image(id).map(|image| (image.width, image.height))
    }

    /// The source an id was loaded from.
    pub fn source(&self, id: ImageId) -> Option<&str> {
        self.entries.get(&id.0).map(|entry| entry.source.as_ref())
    }

    /// Drops every cached image.
    pub fn clear(&mut self) {
        self.entries.clear();
        self.by_content.clear();
        self.by_pointer.clear();
    }

    fn insert_entry(&mut self, source: Arc<str>, image: Option<Image>) -> u32 {
        let image = image.or_else(|| self.decode(&source));
        let id = self.next_id;
        self.next_id = self.next_id.wrapping_add(1).max(1);
        self.by_content.insert(Arc::clone(&source), id);
        // A new entry counts as used this frame, so eviction never picks it.
        self.entries.insert(id, Entry { source, image, last_used: self.clock + 1 });
        self.evict();
        id
    }

    fn touch(&mut self, id: u32) -> Option<ImageId> {
        self.clock += 1;
        let entry = self.entries.get_mut(&id)?;
        entry.last_used = self.clock;
        entry.image.as_ref().map(|_| ImageId(id))
    }

    fn evict(&mut self) {
        while self.entries.len() > self.capacity {
            let oldest = self
                .entries
                .iter()
                .filter(|(_, entry)| entry.last_used < self.frame_start)
                .min_by_key(|(_, entry)| entry.last_used)
                .map(|(&id, _)| id);
            match oldest {
                Some(id) => self.remove(id),
                None => break,
            }
        }
    }

    fn remove(&mut self, id: u32) {
        if let Some(entry) = self.entries.remove(&id) {
            self.by_content.remove(entry.source.as_ref());
            self.by_pointer.retain(|_, (_, known)| *known != id);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::ImageEncoder as _;

    fn png_data_url(width: u32, height: u32, rgba: &[u8]) -> String {
        let mut bytes = Vec::new();
        image::codecs::png::PngEncoder::new(&mut bytes)
            .write_image(rgba, width, height, image::ExtendedColorType::Rgba8)
            .unwrap();
        format!("data:image/png;base64,{}", base64::engine::general_purpose::STANDARD.encode(bytes))
    }

    #[test]
    fn decodes_data_urls_and_builtin_sheets_premultiplied() {
        let url = png_data_url(2, 1, &[255, 0, 0, 255, 0, 0, 255, 128]);
        let image = decode_source(&url).unwrap();
        assert_eq!((image.width(), image.height()), (2, 1));
        assert_eq!(image.pixels(), &[255, 0, 0, 255, 0, 0, 128, 128]);
        assert!(decode_source("builtin://tiles.png").is_some());
        assert!(decode_source("idb://asset").is_none());
        assert!(decode_source("data:image/png,notbase64").is_none());
        assert!(decode_source("data:image/png;base64,!!!").is_none());
        // Whitespace inside the payload is ignored; the scheme is case-insensitive.
        let spaced = url.replacen("base64,", "BASE64,\n ", 1).replacen("data:", "DATA:", 1);
        assert_eq!(decode_source(&spaced).unwrap(), image);
    }

    #[test]
    fn refuses_decompression_bombs_before_decoding() {
        // A valid PNG header claiming 20000×20000 pixels.
        let mut bytes = Vec::new();
        image::codecs::png::PngEncoder::new(&mut bytes)
            .write_image(&[0; 4], 1, 1, image::ExtendedColorType::Rgba8)
            .unwrap();
        // IHDR width/height live at bytes 16..24, its CRC at 29..33 (over type + data).
        bytes[16..20].copy_from_slice(&20_000u32.to_be_bytes());
        bytes[20..24].copy_from_slice(&20_000u32.to_be_bytes());
        let crc = crc32(&bytes[12..29]);
        bytes[29..33].copy_from_slice(&crc.to_be_bytes());
        assert_eq!(decode_image(&bytes), Err(ImageError::TooLarge { width: 20_000, height: 20_000 }));
        assert!(matches!(decode_image(b"not an image"), Err(ImageError::Decode(_))));
    }

    fn crc32(bytes: &[u8]) -> u32 {
        let mut crc = !0u32;
        for &byte in bytes {
            crc ^= u32::from(byte);
            for _ in 0..8 {
                crc = if crc & 1 != 0 { (crc >> 1) ^ 0xEDB8_8320 } else { crc >> 1 };
            }
        }
        !crc
    }

    #[test]
    fn caches_failures_and_evicts_least_recently_used() {
        let mut store = ImageStore::new(2);
        let a = png_data_url(1, 1, &[1, 2, 3, 255]);
        let b = png_data_url(1, 1, &[4, 5, 6, 255]);
        let c = png_data_url(1, 1, &[7, 8, 9, 255]);
        assert!(store.get("idb://missing").is_none());
        assert_eq!(store.len(), 1, "failed decodes are cached");
        assert!(store.get("idb://missing").is_none());
        assert_eq!(store.len(), 1);
        let id_a = store.get(&a).unwrap();
        assert_eq!(store.get(&a), Some(id_a));
        store.begin_frame();
        let id_b = store.get(&b).unwrap();
        let id_c = store.get(&c).unwrap();
        // The missing entry and `a` were evicted; b and c are pinned by the frame.
        assert_eq!(store.len(), 2);
        assert!(store.image(id_a).is_none());
        assert_eq!(store.size(id_b), Some((1, 1)));
        assert_eq!(store.image(id_c).unwrap().pixels(), &[7, 8, 9, 255]);
        // A new id for a re-decoded source: ids are never reused.
        store.begin_frame();
        let again = store.get(&a).unwrap();
        assert_ne!(again, id_a);
    }

    #[test]
    fn frames_pin_everything_they_use() {
        let mut store = ImageStore::new(1);
        store.begin_frame();
        let urls: Vec<String> = (0..3u8).map(|i| png_data_url(1, 1, &[i, i, i, 255])).collect();
        let ids: Vec<ImageId> = urls.iter().map(|url| store.get(url).unwrap()).collect();
        assert_eq!(store.len(), 3);
        assert!(ids.iter().all(|id| store.image(*id).is_some()));
        // Eviction happens when a new source arrives, and spares what this frame used.
        store.begin_frame();
        store.get(&urls[2]);
        assert_eq!(store.len(), 3);
        let extra = store.get(&png_data_url(1, 1, &[9, 9, 9, 255])).unwrap();
        assert_eq!(store.len(), 2);
        assert!(store.image(ids[2]).is_some() && store.image(extra).is_some());
        assert!(store.image(ids[0]).is_none() && store.image(ids[1]).is_none());
    }

    #[test]
    fn shared_sources_hit_the_pointer_fast_path() {
        let mut store = ImageStore::default();
        let url: Arc<str> = Arc::from(png_data_url(1, 1, &[1, 2, 3, 255]));
        let copy: Arc<str> = Arc::from(url.as_ref());
        let first = store.get_shared(&url).unwrap();
        assert_eq!(store.get_shared(&url), Some(first));
        assert_eq!(store.get_shared(&copy), Some(first));
        assert_eq!(store.get(&url), Some(first));
        assert_eq!(store.len(), 1);
        let custom = store.insert("generated://minimap", Image::from_rgba(1, 1, vec![0, 0, 0, 0]).unwrap());
        assert_eq!(store.source(custom), Some("generated://minimap"));
        assert_eq!(store.get("generated://minimap"), Some(custom));
    }

    #[test]
    fn a_resolver_supplies_other_schemes() {
        let url = png_data_url(1, 1, &[9, 8, 7, 255]);
        let png = data_url_bytes(&url).unwrap();
        let mut store = ImageStore::default();
        store.set_resolver(move |source| (source == "asset:sprite").then(|| png.clone()));
        let id = store.get("asset:sprite").unwrap();
        assert_eq!(store.image(id).unwrap().pixels(), &[9, 8, 7, 255]);
        assert!(store.get("asset:other").is_none());
        assert!(store.get("builtin://tiles.png").is_some(), "unresolved sources use the built-in handling");
        assert!(format!("{store:?}").contains("resolver: true"));
    }
}
