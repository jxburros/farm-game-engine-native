//! Port of `Packs.cs` (schemas; packages/engine-schemas/src/packs.ts).
//!
//! Content packs (M5) — the modding unit. A pack is a JSON document (zip and folder loaders can
//! wrap this later) with a manifest and declared content. Everything a mod can add is validated
//! by these schemas; malformed packs produce actionable errors, never crashes or silent partial
//! loads.

use super::actors::{Dialogue, Npc};
use super::animals::AnimalSpeciesDefinition;
use super::content::{CropDefinition, Item};
use super::crafting::{MachineTypeDefinition, RecipeDefinition};
use super::economy::ShopDefinition;
use super::events::GameEvent;
use super::extensibility::{ActionDef, MinigameDef};
use super::fishing::FishTable;
use super::nodes::NodeTypeDefinition;
use super::quests::Quest;
use super::weather::WeatherTypeDefinition;
use super::world::Scene;
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PackPermissions {
    /// Hook names the pack's plugins may subscribe to (user-approved at install).
    pub hooks: Vec<String>,
    /// May contribute content definitions (the normal case).
    pub content_inject: bool,
    /// Reserved: declarative UI panels (not yet implemented).
    pub ui_panels: bool,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for PackPermissions {
    fn default() -> Self {
        Self { hooks: Vec::new(), content_inject: true, ui_panels: false, extra: Map::new() }
    }
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PackDependency {
    pub pack_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
}

/// A sandboxed plugin: `source` is the body of `function (api) { ... }` and registers handlers
/// with `api.on(hookName, fn)`. Handlers return an array of mutations (validated as
/// [`PluginMutation`]) — never raw state access.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PackPlugin {
    pub id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// Hooks this plugin wants; effective set = intersection with manifest permissions.hooks.
    pub hooks: Vec<String>,
    pub source: String,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PackManifest {
    /// Must match `^[a-z0-9][a-z0-9-]*$` (see [`PACK_ID_PATTERN`]).
    pub id: String,
    pub name: String,
    pub version: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub author: Option<String>,
    /// Semver range: '*', exact '1.2.3', '^1.2.3' or '>=1.2.3'.
    pub engine_compatibility: String,
    /// Base packs (content-default) keep their plain IDs; all other packs get their definitions
    /// namespaced to `packId:localId` at load time.
    pub base: bool,
    pub dependencies: Vec<PackDependency>,
    /// Fully-qualified IDs this pack intentionally replaces. Redefining an existing ID without
    /// declaring it here is a conflict surfaced in the Problems panel (the earlier definition
    /// wins).
    pub overrides: Vec<String>,
    pub permissions: PackPermissions,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for PackManifest {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            version: String::new(),
            description: None,
            author: None,
            engine_compatibility: "*".to_owned(),
            base: false,
            dependencies: Vec::new(),
            overrides: Vec::new(),
            permissions: PackPermissions::default(),
            extra: Map::new(),
        }
    }
}

/// TS `PackPlayerStartSchema.inventory` item (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PackStartItem {
    pub item_id: String,
    /// int, min 1.
    pub quantity: f64,
}

/// Optional player-start block so a base pack can express the whole starter game.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PackPlayerStart {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scene_id: Option<String>,
    /// int.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub x: Option<f64>,
    /// int.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub y: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub money: Option<f64>,
    pub inventory: Vec<PackStartItem>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PackContent {
    pub crops: Vec<CropDefinition>,
    pub items: Vec<Item>,
    pub recipes: Vec<RecipeDefinition>,
    pub machine_types: Vec<MachineTypeDefinition>,
    pub node_types: Vec<NodeTypeDefinition>,
    pub animal_species: Vec<AnimalSpeciesDefinition>,
    pub fish_tables: Vec<FishTable>,
    pub weather_types: Vec<WeatherTypeDefinition>,
    pub npcs: Vec<Npc>,
    pub dialogues: Vec<Dialogue>,
    pub scenes: Vec<Scene>,
    pub events: Vec<GameEvent>,
    pub quests: Vec<Quest>,
    pub shops: Vec<ShopDefinition>,
    pub actions: Vec<ActionDef>,
    pub minigames: Vec<MinigameDef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_start: Option<PackPlayerStart>,
    /// Per-locale string tables for game text (M7 i18n). Keys address content fields:
    /// `item:{id}:name`, `item:{id}:description`, `dialogue:{id}:text`, `quest:{id}:name`,
    /// `quest:{id}:description`. The authored text is the fallback locale.
    pub strings: IndexMap<String, IndexMap<String, String>>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ContentPack {
    pub manifest: PackManifest,
    pub content: PackContent,
    pub plugins: Vec<PackPlugin>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// How a project stores an installed pack. Array order = load order.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PackInstallation {
    pub pack: ContentPack,
    pub enabled: bool,
}

impl Default for PackInstallation {
    fn default() -> Self {
        Self { pack: ContentPack::default(), enabled: true }
    }
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PackValidationResult {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pack: Option<ContentPack>,
    pub errors: Vec<String>,
}

/// TS `PluginMutationSchema` — the only things a plugin hook may do: declared, validated
/// mutations that flow through the command pipeline (so replays stay deterministic). Reference
/// targets that don't exist fail soft with an error message — never a crash.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all_fields = "camelCase")]
pub enum PluginMutation {
    #[serde(rename = "giveItem")]
    GiveItem {
        item_id: String,
        /// int, 1..999.
        quantity: f64,
    },
    #[serde(rename = "takeItem")]
    TakeItem {
        item_id: String,
        /// int, 1..999.
        quantity: f64,
    },
    #[serde(rename = "giveMoney")]
    GiveMoney {
        /// int, 1..1_000_000.
        amount: f64,
    },
    #[serde(rename = "takeMoney")]
    TakeMoney {
        /// int, 1..1_000_000.
        amount: f64,
    },
    #[serde(rename = "setFlag")]
    SetFlag {
        flag: String,
        /// `boolean | number | string`.
        value: Value,
    },
    #[serde(rename = "message")]
    Message {
        /// max length 500.
        text: String,
    },
    #[serde(rename = "setWeather")]
    SetWeather { weather_id: String },
    #[serde(rename = "modifyFriendship")]
    ModifyFriendship {
        npc_id: String,
        /// int, -1000..1000.
        delta: f64,
    },
    #[serde(rename = "grantXp")]
    GrantXp {
        skill: String,
        /// int, 1..10_000.
        amount: f64,
    },
    #[serde(rename = "modifyEnergy")]
    ModifyEnergy {
        /// int, -1000..1000.
        delta: f64,
    },
    #[serde(rename = "startQuest")]
    StartQuest { quest_id: String },
    #[serde(rename = "warpPlayer")]
    WarpPlayer {
        scene_id: String,
        /// int, min 0.
        x: f64,
        /// int, min 0.
        y: f64,
    },
    #[serde(rename = "startDialogue")]
    StartDialogue {
        npc_id: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        dialogue_id: Option<String>,
    },
    #[serde(rename = "playSound")]
    PlaySound { sound_id: String },
    #[serde(rename = "performAction")]
    PerformAction { action_id: String },
    #[serde(rename = "startMinigame")]
    StartMinigame { minigame_id: String },
}

impl PluginMutation {
    /// The discriminator literal (TS `mutation.type`).
    pub fn type_name(&self) -> &'static str {
        match self {
            Self::GiveItem { .. } => "giveItem",
            Self::TakeItem { .. } => "takeItem",
            Self::GiveMoney { .. } => "giveMoney",
            Self::TakeMoney { .. } => "takeMoney",
            Self::SetFlag { .. } => "setFlag",
            Self::Message { .. } => "message",
            Self::SetWeather { .. } => "setWeather",
            Self::ModifyFriendship { .. } => "modifyFriendship",
            Self::GrantXp { .. } => "grantXp",
            Self::ModifyEnergy { .. } => "modifyEnergy",
            Self::StartQuest { .. } => "startQuest",
            Self::WarpPlayer { .. } => "warpPlayer",
            Self::StartDialogue { .. } => "startDialogue",
            Self::PlaySound { .. } => "playSound",
            Self::PerformAction { .. } => "performAction",
            Self::StartMinigame { .. } => "startMinigame",
        }
    }
}

/// Engine feature-set version packs declare compatibility against.
pub const ENGINE_VERSION: &str = "0.5.0";

/// TS `PackManifestSchema.id` regex.
pub const PACK_ID_PATTERN: &str = "^[a-z0-9][a-z0-9-]*$";

/// Whether a pack id satisfies the manifest id rule (`^[a-z0-9][a-z0-9-]*$`, JS semantics: `$`
/// does not match before a trailing newline).
pub fn is_valid_pack_id(id: &str) -> bool {
    let mut bytes = id.bytes();
    match bytes.next() {
        Some(first) if first.is_ascii_lowercase() || first.is_ascii_digit() => {}
        _ => return false,
    }
    bytes.all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == b'-')
}

/// Validate raw pack JSON. Never fails hard; errors are actionable paths. Approximates zod's
/// `safeParse` the way the C# port does: serde reports the first structural error only (not
/// every issue), and the manifest id rule is checked explicitly.
pub fn validate_content_pack(raw: &Value) -> PackValidationResult {
    let Some(object) = raw.as_object() else {
        return PackValidationResult {
            ok: false,
            pack: None,
            errors: vec!["Pack data is not an object — expected { manifest, content }".to_owned()],
        };
    };
    let mut errors = Vec::new();
    match object.get("manifest").and_then(Value::as_object) {
        None => errors.push("manifest: Required".to_owned()),
        Some(manifest) => {
            for key in ["id", "name", "version"] {
                if !manifest.get(key).is_some_and(Value::is_string) {
                    errors.push(format!("manifest.{key}: Required"));
                }
            }
            if let Some(id) = manifest.get("id").and_then(Value::as_str) {
                if !is_valid_pack_id(id) {
                    errors.push("manifest.id: pack ids must be lowercase letters, digits and dashes".to_owned());
                }
            }
        }
    }
    if !errors.is_empty() {
        return PackValidationResult { ok: false, pack: None, errors };
    }
    match serde_json::from_value::<ContentPack>(raw.clone()) {
        Ok(pack) => PackValidationResult { ok: true, pack: Some(pack), errors: Vec::new() },
        Err(error) => PackValidationResult { ok: false, pack: None, errors: vec![format!("(root): {error}")] },
    }
}

/// `^([0-9]+)\.([0-9]+)\.([0-9]+)` on the trimmed text (JS `\d` is ASCII-only).
fn parse_version(text: &str) -> Option<[f64; 3]> {
    let trimmed = text.trim();
    let mut parts = [0.0; 3];
    let mut rest = trimmed;
    for (index, part) in parts.iter_mut().enumerate() {
        let digits = rest.bytes().take_while(u8::is_ascii_digit).count();
        if digits == 0 {
            return None;
        }
        // Digits only, so the slice is valid UTF-8 and parses as a number (as JS Number would).
        *part = rest[..digits].parse::<f64>().ok()?;
        rest = &rest[digits..];
        if index < 2 {
            rest = rest.strip_prefix('.')?;
        }
    }
    Some(parts)
}

/// Minimal semver-range check ('*', exact, '^x.y.z', '>=x.y.z') — enough for pack compatibility
/// warnings without a dependency.
pub fn is_engine_compatible(range: Option<&str>, version: &str) -> bool {
    let trimmed = range.unwrap_or("*").trim();
    if trimmed.is_empty() || trimmed == "*" {
        return true;
    }
    let Some([a, b, c]) = parse_version(version) else {
        return true;
    };
    if let Some(rest) = trimmed.strip_prefix(">=") {
        let Some([x, y, z]) = parse_version(rest) else {
            return false;
        };
        return if a != x {
            a > x
        } else if b != y {
            b > y
        } else {
            c >= z
        };
    }
    if let Some(rest) = trimmed.strip_prefix('^') {
        let Some([x, y, z]) = parse_version(rest) else {
            return false;
        };
        if a != x {
            return false;
        }
        return if b != y { b > y } else { c >= z };
    }
    let Some([x, y, z]) = parse_version(trimmed) else {
        return false;
    };
    x == a && y == b && z == c
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validates_pack_ids() {
        assert!(is_valid_pack_id("demo-glow-farm"));
        assert!(is_valid_pack_id("a1"));
        assert!(!is_valid_pack_id("Demo"));
        assert!(!is_valid_pack_id("-lead"));
        assert!(!is_valid_pack_id(""));
        assert!(!is_valid_pack_id("demo\n"));
    }

    #[test]
    fn checks_engine_compatibility_ranges() {
        assert!(is_engine_compatible(None, ENGINE_VERSION));
        assert!(is_engine_compatible(Some("*"), ENGINE_VERSION));
        assert!(is_engine_compatible(Some(" "), ENGINE_VERSION));
        assert!(is_engine_compatible(Some(">=0.4.0"), ENGINE_VERSION));
        assert!(is_engine_compatible(Some(">=0.5.0"), ENGINE_VERSION));
        assert!(!is_engine_compatible(Some(">=0.6.0"), ENGINE_VERSION));
        assert!(is_engine_compatible(Some("^0.5.0"), ENGINE_VERSION));
        assert!(!is_engine_compatible(Some("^9.0.0"), ENGINE_VERSION));
        assert!(is_engine_compatible(Some("0.5.0"), ENGINE_VERSION));
        assert!(!is_engine_compatible(Some("0.5.1"), ENGINE_VERSION));
        assert!(!is_engine_compatible(Some("nonsense"), ENGINE_VERSION));
        assert!(is_engine_compatible(Some("1.2.3"), "1.2.3-beta"));
    }

    #[test]
    fn validate_content_pack_reports_actionable_errors() {
        let result = validate_content_pack(&serde_json::json!([]));
        assert!(!result.ok);
        assert_eq!(result.errors, ["Pack data is not an object — expected { manifest, content }"]);

        let result = validate_content_pack(&serde_json::json!({ "manifest": { "id": "Bad Id" } }));
        assert!(!result.ok);
        assert_eq!(
            result.errors,
            [
                "manifest.name: Required",
                "manifest.version: Required",
                "manifest.id: pack ids must be lowercase letters, digits and dashes"
            ]
        );

        let result =
            validate_content_pack(&serde_json::json!({ "manifest": { "id": "ok", "name": "Ok", "version": "1.0.0" } }));
        assert!(result.ok);
        assert_eq!(result.pack.map(|pack| pack.manifest.id), Some("ok".to_owned()));
    }
}
