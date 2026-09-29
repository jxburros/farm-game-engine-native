//! Port of `Content.cs` (packages/engine-schemas/src/content.ts).
//!
//! Content definitions — immutable at runtime, authored in the editor or provided by content
//! packs.

use super::graphics::{AnimationClip, VisualRef};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

/// TS `CropDefinitionSchema.multiTile` (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CropMultiTile {
    /// int, positive.
    #[serde(with = "crate::units::count")]
    pub width: u32,
    /// int, positive.
    #[serde(with = "crate::units::count")]
    pub height: u32,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CropDefinition {
    pub id: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub visual: Option<VisualRef>,
    #[serde(with = "crate::units::money")]
    pub seed_cost: i64,
    #[serde(with = "crate::units::money")]
    pub base_harvest_value: i64,
    /// Legacy wall-clock growth duration (ms). Kept for pre-v4 data; the engine uses growthDays.
    #[serde(with = "crate::units::long")]
    pub growth_time: i64,
    /// In-game days from planting to maturity (authoritative from schema v4). positive.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub growth_days: Option<u32>,
    /// int, positive.
    #[serde(with = "crate::units::count")]
    pub stages: u32,
    pub seasons: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::long::opt")]
    pub regrowth_time: Option<i64>,
    /// In-game days between repeat harvests for regrowing crops. positive.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub regrowth_days: Option<u32>,
    pub can_regrow: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub multi_tile: Option<CropMultiTile>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::probability::opt")]
    pub mutation_chance: Option<u64>,
    #[serde(with = "crate::units::count")]
    pub yield_min: u32,
    #[serde(with = "crate::units::count")]
    pub yield_max: u32,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// TS `CustomCropDefinitionSchema = CropDefinitionSchema.extend({ customAsset })` (zod 3
/// `extend` keeps passthrough). The fields are repeated, as in the C# port, so the two shapes
/// stay explicit.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CustomCropDefinition {
    pub id: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub visual: Option<VisualRef>,
    #[serde(with = "crate::units::money")]
    pub seed_cost: i64,
    #[serde(with = "crate::units::money")]
    pub base_harvest_value: i64,
    /// Legacy wall-clock growth duration (ms). Kept for pre-v4 data; the engine uses growthDays.
    #[serde(with = "crate::units::long")]
    pub growth_time: i64,
    /// In-game days from planting to maturity (authoritative from schema v4). positive.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub growth_days: Option<u32>,
    /// int, positive.
    #[serde(with = "crate::units::count")]
    pub stages: u32,
    pub seasons: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::long::opt")]
    pub regrowth_time: Option<i64>,
    /// In-game days between repeat harvests for regrowing crops. positive.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub regrowth_days: Option<u32>,
    pub can_regrow: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub multi_tile: Option<CropMultiTile>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::probability::opt")]
    pub mutation_chance: Option<u64>,
    #[serde(with = "crate::units::count")]
    pub yield_min: u32,
    #[serde(with = "crate::units::count")]
    pub yield_max: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_asset: Option<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Item {
    pub id: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub visual: Option<VisualRef>,
    pub description: String,
    /// One of [`super::item_types`].
    pub r#type: String,
    pub stackable: bool,
    #[serde(with = "crate::units::count")]
    pub max_stack: u32,
    #[serde(with = "crate::units::money")]
    pub value: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub crop_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_image: Option<String>,
    /// One of [`super::tool_types`].
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tool_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
    pub tool_power: Option<i32>,
    /// Tool tier: 1 = basic. Higher tiers hit harder, cost less energy, gain AoE. int, positive.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
    pub tool_tier: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
    pub durability: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
    pub max_durability: Option<i32>,
    /// Action performed when the item is used from the inventory.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub use_action_id: Option<String>,
    /// Consume one of the item on a successful use.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub consume_on_use: Option<bool>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct InventorySlot {
    pub item: Item,
    #[serde(with = "crate::units::count")]
    pub quantity: u32,
}

/// A dropped/growing crop instance on a tile (game state, not content).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Crop {
    pub r#type: String,
    /// Legacy wall-clock plant timestamp (ms). Superseded by plantedOnDay from schema v4.
    #[serde(with = "crate::units::long")]
    pub planted_at: i64,
    /// Absolute in-game day the crop was planted (schema v4+). int.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub planted_on_day: Option<u32>,
    /// Watered in-game days accumulated toward growthDays (schema v4+).
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub days_grown: Option<u32>,
    /// Killed by season change; renders dead and can be cleared.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub withered: Option<bool>,
    #[serde(with = "crate::units::count")]
    pub stage: u32,
    pub watered: bool,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub last_watered_day: Option<u32>,
    /// One of [`super::crop_qualities`].
    pub quality: String,
    /// One of [`super::crop_mutations`] or `null` (required key, present-as-null).
    pub mutation: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub is_multi_tile_root: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub multi_tile_id: Option<String>,
    #[serde(with = "crate::units::count")]
    pub harvest_count: u32,
    #[serde(with = "crate::units::count")]
    pub days_without_water: u32,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// Sprite-sheet slicing metadata (M7). A sheet is a grid: columns = walk frames, rows =
/// directions (down, left, right, up) when `directional`. Assets without this stay static
/// images; games with zero art still render via the colored-rectangle fallback.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SpriteSheet {
    /// int, positive.
    #[serde(with = "crate::units::screen")]
    pub frame_width: f64,
    /// int, positive.
    #[serde(with = "crate::units::screen")]
    pub frame_height: f64,
    /// int, positive.
    #[serde(with = "crate::units::screen")]
    pub frames: f64,
    /// int, positive.
    #[serde(with = "crate::units::screen")]
    pub ticks_per_frame: f64,
    pub directional: bool,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for SpriteSheet {
    fn default() -> Self {
        Self {
            frame_width: 0.0,
            frame_height: 0.0,
            frames: 0.0,
            ticks_per_frame: 6.0,
            directional: true,
            extra: Map::new(),
        }
    }
}

/// TS `CustomAssetSchema.type` enum.
pub mod custom_asset_types {
    pub const TILE: &str = "tile";
    pub const NPC: &str = "npc";
    pub const ITEM: &str = "item";
    pub const PLAYER: &str = "player";
    pub const ART: &str = "art";

    pub const ALL: &[&str] = &[TILE, NPC, ITEM, PLAYER, ART];
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CustomAsset {
    pub id: String,
    pub name: String,
    /// One of [`custom_asset_types`].
    pub r#type: String,
    /// int, positive.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::screen::opt")]
    pub width: Option<f64>,
    /// int, positive.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::screen::opt")]
    pub height: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub animations: Option<Vec<AnimationClip>>,
    pub data_url: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tile_type: Option<String>,
    /// Present when the image is an animated sprite sheet (M7).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sheet: Option<SpriteSheet>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
