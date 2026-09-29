//! Verified FlatBuffers cartridge reader (`schemas/cart.fbs`).
//!
//! Format 2 splits a project into compiled content, the inputs of a new game
//! ([`farm_sim::StartState`]) and what presentation reads ([`farm_sim::Presentation`]), so the
//! player never reads project JSON. The sections are compatibility JSON (JavaScript number
//! semantics) until the native-numerics cutover. Embedded files live in an asset table and are
//! referenced from the JSON as `asset:<id>` strings.

use farm_cart_schema::farm_engine::cart::{cartridge_buffer_has_identifier, root_as_cartridge};
use farm_sim::schema::GameContent;
use farm_sim::{Presentation, StartState};
use std::collections::BTreeMap;

pub const CART_IDENTIFIER: &[u8; 4] = b"FGCT";

/// Prefix of the strings that reference an embedded asset (`asset:<id>`).
pub const ASSET_URL_PREFIX: &str = "asset:";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GameInfo<'a> {
    pub title: &'a str,
    pub version: &'a str,
    pub game_id: &'a str,
    pub author: Option<&'a str>,
    pub company: Option<&'a str>,
    pub executable_name: Option<&'a str>,
    pub window_width: u32,
    pub window_height: u32,
    pub fullscreen: bool,
    pub pixel_scale: Option<&'a str>,
    pub credits: Option<&'a str>,
}

/// One embedded file.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AssetRef<'a> {
    pub id: &'a str,
    pub mime: &'a str,
    pub data: &'a [u8],
}

/// A sandboxed plugin of an enabled content pack (`farm_plugins::PluginSpec`).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CartPlugin {
    /// `packId:pluginId`.
    pub id: String,
    pub pack_id: String,
    /// The body of `function (api) { … }`.
    pub source: String,
    /// The plugin's hooks that the pack manifest grants, in the plugin's order.
    pub granted_hooks: Vec<String>,
}

/// A verified cartridge, borrowing its sections from the buffer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Cartridge<'a> {
    pub info: GameInfo<'a>,
    pub project_schema_version: u32,
    pub content_json: &'a [u8],
    pub start_json: &'a [u8],
    pub presentation_json: &'a [u8],
    /// Sorted by id.
    pub assets: Vec<AssetRef<'a>>,
    /// In load order.
    pub plugins: Vec<CartPlugin>,
}

pub fn is_cartridge(bytes: &[u8]) -> bool {
    bytes.len() >= 8 && cartridge_buffer_has_identifier(bytes)
}

/// Verify the FlatBuffer before exposing its borrowed fields. Another cartridge format is
/// refused rather than interpreted as this layout.
pub fn read_cartridge(bytes: &[u8]) -> Result<Cartridge<'_>, String> {
    if !is_cartridge(bytes) {
        return Err("Not a Farm Engine cartridge (FGCT identifier missing).".to_owned());
    }
    let cart = root_as_cartridge(bytes).map_err(|error| format!("Invalid cartridge: {error}"))?;
    if cart.cart_format() != crate::CART_FORMAT {
        return Err(format!(
            "Cartridge format {} is not supported by this player (requires {}). Export the game again with a matching editor.",
            cart.cart_format(),
            crate::CART_FORMAT
        ));
    }
    if cart.project_schema_version() as f64 > farm_sim::schema::CURRENT_PROJECT_SCHEMA_VERSION {
        return Err(format!("Project schema {} is newer than this player supports.", cart.project_schema_version()));
    }
    let info = cart.info();
    if info.game_id().is_empty()
        || info.version().is_empty()
        || cart.content_json().is_empty()
        || cart.start_json().is_empty()
        || cart.presentation_json().is_empty()
    {
        return Err("Cartridge is missing its game identity or content.".to_owned());
    }
    let mut assets: Vec<AssetRef<'_>> = cart
        .assets()
        .map(|assets| {
            assets
                .iter()
                .map(|asset| AssetRef { id: asset.id(), mime: asset.mime(), data: asset.data().bytes() })
                .collect()
        })
        .unwrap_or_default();
    // The compiler writes them sorted; don't trust it for lookups.
    assets.sort_by(|a, b| a.id.cmp(b.id));
    let plugins = cart
        .plugins()
        .map(|plugins| {
            plugins
                .iter()
                .map(|plugin| CartPlugin {
                    id: plugin.id().to_owned(),
                    pack_id: plugin.pack_id().to_owned(),
                    source: plugin.source().to_owned(),
                    granted_hooks: plugin
                        .granted_hooks()
                        .map(|hooks| hooks.iter().map(str::to_owned).collect())
                        .unwrap_or_default(),
                })
                .collect()
        })
        .unwrap_or_default();
    Ok(Cartridge {
        info: GameInfo {
            title: info.title(),
            version: info.version(),
            game_id: info.game_id(),
            author: info.author(),
            company: info.company(),
            executable_name: info.executable_name(),
            window_width: info.window_width(),
            window_height: info.window_height(),
            fullscreen: info.fullscreen(),
            pixel_scale: info.pixel_scale(),
            credits: info.credits(),
        },
        project_schema_version: cart.project_schema_version(),
        content_json: cart.content_json().bytes(),
        start_json: cart.start_json().bytes(),
        presentation_json: cart.presentation_json().bytes(),
        assets,
        plugins,
    })
}

/// Owned copy of a cartridge's game info.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct GameInfoOwned {
    pub title: String,
    pub version: String,
    pub game_id: String,
    pub author: Option<String>,
    pub company: Option<String>,
    pub executable_name: Option<String>,
    pub window_width: u32,
    pub window_height: u32,
    pub fullscreen: bool,
    pub pixel_scale: Option<String>,
    pub credits: Option<String>,
}

impl From<&GameInfo<'_>> for GameInfoOwned {
    fn from(info: &GameInfo<'_>) -> Self {
        Self {
            title: info.title.to_owned(),
            version: info.version.to_owned(),
            game_id: info.game_id.to_owned(),
            author: info.author.map(str::to_owned),
            company: info.company.map(str::to_owned),
            executable_name: info.executable_name.map(str::to_owned),
            window_width: info.window_width,
            window_height: info.window_height,
            fullscreen: info.fullscreen,
            pixel_scale: info.pixel_scale.map(str::to_owned),
            credits: info.credits.map(str::to_owned),
        }
    }
}

/// An embedded file, owned.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Asset {
    pub mime: String,
    pub data: Vec<u8>,
}

/// The embedded files of a cartridge by id.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct AssetTable {
    assets: BTreeMap<String, Asset>,
}

impl AssetTable {
    pub fn insert(&mut self, id: impl Into<String>, mime: impl Into<String>, data: Vec<u8>) {
        self.assets.insert(id.into(), Asset { mime: mime.into(), data });
    }

    /// The file an `asset:<id>` string refers to (`None` for other strings or unknown ids).
    pub fn resolve(&self, url: &str) -> Option<&Asset> {
        self.assets.get(url.strip_prefix(ASSET_URL_PREFIX)?)
    }

    pub fn get(&self, id: &str) -> Option<&Asset> {
        self.assets.get(id)
    }

    pub fn len(&self) -> usize {
        self.assets.len()
    }

    pub fn is_empty(&self) -> bool {
        self.assets.is_empty()
    }

    pub fn iter(&self) -> impl Iterator<Item = (&str, &Asset)> {
        self.assets.iter().map(|(id, asset)| (id.as_str(), asset))
    }
}

/// A cartridge parsed into the values a player runs: everything owned, nothing borrowed.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct LoadedCartridge {
    pub info: GameInfoOwned,
    pub project_schema_version: u32,
    pub content: GameContent,
    pub start: StartState,
    pub presentation: Presentation,
    pub assets: AssetTable,
    /// Plugins of the enabled packs, in load order.
    pub plugins: Vec<CartPlugin>,
}

/// Verify and parse a cartridge.
pub fn load_cartridge(bytes: &[u8]) -> Result<LoadedCartridge, String> {
    let cart = read_cartridge(bytes)?;
    let content: GameContent = parse_section(cart.content_json, "content")?;
    let start: StartState = parse_section(cart.start_json, "start")?;
    let presentation: Presentation = parse_section(cart.presentation_json, "presentation")?;
    let mut assets = AssetTable::default();
    for asset in &cart.assets {
        assets.insert(asset.id, asset.mime, asset.data.to_vec());
    }
    Ok(LoadedCartridge {
        info: GameInfoOwned::from(&cart.info),
        project_schema_version: cart.project_schema_version,
        content,
        start,
        presentation,
        assets,
        plugins: cart.plugins,
    })
}

fn parse_section<T: serde::de::DeserializeOwned>(bytes: &[u8], name: &str) -> Result<T, String> {
    let mut deserializer = serde_json::Deserializer::from_slice(bytes);
    serde_path_to_error::deserialize(&mut deserializer).map_err(|error| format!("Cartridge {name}: {error}"))
}

/// Replace every `asset:<id>` string in `value` with the `data:` URL of that asset (the inverse
/// of the compiler's extraction): debugging, and comparing a cartridge with its project.
pub fn inline_assets(value: &mut serde_json::Value, assets: &AssetTable) {
    match value {
        serde_json::Value::String(text) => {
            if let Some(asset) = assets.resolve(text) {
                *text = format!("data:{};base64,{}", asset.mime, base64_encode(&asset.data));
            }
        }
        serde_json::Value::Array(items) => items.iter_mut().for_each(|item| inline_assets(item, assets)),
        serde_json::Value::Object(map) => map.values_mut().for_each(|item| inline_assets(item, assets)),
        _ => {}
    }
}

/// Standard base64 with padding (the encoding of the `data:` URLs the editor writes).
pub fn base64_encode(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let b = [chunk[0], *chunk.get(1).unwrap_or(&0), *chunk.get(2).unwrap_or(&0)];
        let n = (u32::from(b[0]) << 16) | (u32::from(b[1]) << 8) | u32::from(b[2]);
        out.push(char::from(ALPHABET[(n >> 18) as usize & 63]));
        out.push(char::from(ALPHABET[(n >> 12) as usize & 63]));
        out.push(if chunk.len() > 1 { char::from(ALPHABET[(n >> 6) as usize & 63]) } else { '=' });
        out.push(if chunk.len() > 2 { char::from(ALPHABET[n as usize & 63]) } else { '=' });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use farm_sim::schema::GameProject;

    const CART: &[u8] = include_bytes!("../../../fixtures/golden/cartridges/project-v8.cart");
    /// The golden cartridge is compiled from the v8 fixture project; the migration golden holds
    /// the same project migrated (by the TypeScript reference, which the F# migrations match).
    const MIGRATION: &str = include_str!("../../../fixtures/golden/migrations/project-v8.json");

    fn project() -> GameProject {
        let golden: serde_json::Value = serde_json::from_str(MIGRATION).unwrap();
        serde_json::from_value(golden["result"]["data"].clone()).unwrap()
    }

    /// The F# compiler and the Rust project path agree on content, the new-game inputs and
    /// presentation once embedded assets are inlined again.
    #[test]
    fn fsharp_compiled_cartridge_matches_the_project() {
        let cart = read_cartridge(CART).expect("F# cartridge parses in Rust");
        assert_eq!(cart.project_schema_version, 9);
        assert_eq!(cart.info.game_id, "local.project-1");
        assert_eq!(cart.info.title, "Current V7 Farm");
        let loaded = load_cartridge(CART).unwrap();
        let project = project();
        let inline = |value: serde_json::Value| {
            let mut value = value;
            inline_assets(&mut value, &loaded.assets);
            value
        };
        assert_eq!(
            inline(serde_json::to_value(&loaded.content).unwrap()),
            serde_json::to_value(farm_sim::state::create_content_from_project(&project)).unwrap()
        );
        assert_eq!(
            inline(serde_json::to_value(&loaded.start).unwrap()),
            serde_json::to_value(StartState::from_project(&project)).unwrap()
        );
        assert_eq!(
            inline(serde_json::to_value(&loaded.presentation).unwrap()),
            serde_json::to_value(Presentation::from_project(&project)).unwrap()
        );
        assert_eq!(
            farm_sim::hash_state(&farm_sim::state::create_game_state_from_start(&loaded.start, None)),
            farm_sim::hash_state(&farm_sim::state::create_game_state(&project, None))
        );
    }

    #[test]
    fn malformed_cartridges_are_rejected() {
        assert!(read_cartridge(&[]).is_err());
        assert!(read_cartridge(b"1234FGCT").is_err());
        let mut truncated = CART.to_vec();
        truncated.truncate(20);
        assert!(read_cartridge(&truncated).is_err());
    }

    #[test]
    fn base64_matches_the_standard_alphabet() {
        assert_eq!(base64_encode(b""), "");
        assert_eq!(base64_encode(b"f"), "Zg==");
        assert_eq!(base64_encode(b"fo"), "Zm8=");
        assert_eq!(base64_encode(b"foo"), "Zm9v");
        assert_eq!(base64_encode(&[0xff, 0xfe, 0xfd, 0x00]), "//79AA==");
    }

    #[test]
    fn asset_urls_resolve_only_known_ids() {
        let mut table = AssetTable::default();
        table.insert("abc", "image/png", vec![1, 2, 3]);
        assert_eq!(table.resolve("asset:abc").map(|a| a.data.as_slice()), Some(&[1u8, 2, 3][..]));
        assert!(table.resolve("asset:nope").is_none());
        assert!(table.resolve("data:image/png;base64,AAAA").is_none());
        let mut value = serde_json::json!({"a": ["asset:abc", "asset:zzz"]});
        inline_assets(&mut value, &table);
        assert_eq!(value, serde_json::json!({"a": ["data:image/png;base64,AQID", "asset:zzz"]}));
    }
}
