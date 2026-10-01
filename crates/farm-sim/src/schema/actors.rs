//! Port of `Actors.cs` (packages/engine-schemas/src/actors.ts).

use super::content::InventorySlot;
use super::graphics::VisualRef;
use super::save::SkillState;
use super::social::GiftTastes;
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DialogueOption {
    pub text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub next_dialogue_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub give_item: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub give_item_quantity: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::money::opt")]
    pub take_money: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::money::opt")]
    pub give_money: Option<i64>,
    /// Choosing this option sets this flag (true).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub event_flag: Option<String>,
    /// Only once: the option is hidden after it has been chosen (it remembers through its
    /// `eventFlag`, or a flag of its own when it has none).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub once: Option<bool>,
    /// The option is hidden while this flag is set.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hidden_if_flag: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub requires_item: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub requires_flag: Option<String>,
    /// Choosing this option closes the dialogue and opens the given shop (M2).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub open_shop_id: Option<String>,
    /// Choosing this option offers/starts the given quest (M3 quest-giver binding).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub offer_quest_id: Option<String>,
    /// Option only shown at/above this friendship (M4 heart-gated dialogue).
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
    pub requires_friendship: Option<i32>,
    /// Choosing this option performs a creator-defined action.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub action_id: Option<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Dialogue {
    pub id: String,
    pub npc_id: String,
    pub text: String,
    pub options: Vec<DialogueOption>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// A scheduled destination: at `minute` (of day), head to (sceneId, x, y).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NpcScheduleEntry {
    #[serde(with = "crate::units::count")]
    pub minute: u32,
    pub scene_id: String,
    /// int.
    #[serde(with = "crate::units::int")]
    pub x: i32,
    /// int.
    #[serde(with = "crate::units::int")]
    pub y: i32,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// An integer tile coordinate: TS inline `{ x: int, y: int }` objects in
/// `NPCSchema.patrolPoints` and `NpcStateSchema.path`.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GridPoint {
    /// int.
    #[serde(with = "crate::units::int")]
    pub x: i32,
    /// int.
    #[serde(with = "crate::units::int")]
    pub y: i32,
}

/// TS `NPCSchema.movePattern` enum.
pub mod npc_move_patterns {
    pub const STATIONARY: &str = "stationary";
    pub const WANDER: &str = "wander";
    pub const PATROL: &str = "patrol";

    pub const ALL: &[&str] = &[STATIONARY, WANDER, PATROL];
}

/// TS `NPCSchema.birthday` (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NpcBirthday {
    /// One of the classic seasons ('spring' | 'summer' | 'fall' | 'winter').
    pub season: String,
    /// int.
    #[serde(with = "crate::units::count")]
    pub day: u32,
}

/// TS `NPC` (`NPCSchema`).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Npc {
    pub id: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub visual: Option<VisualRef>,
    #[serde(with = "crate::units::position")]
    pub x: i32,
    #[serde(with = "crate::units::position")]
    pub y: i32,
    pub scene_id: String,
    pub dialogue: Vec<Dialogue>,
    pub can_move: bool,
    /// One of [`npc_move_patterns`].
    #[serde(skip_serializing_if = "Option::is_none")]
    pub move_pattern: Option<String>,
    /// Max tiles from home for the wander pattern (default 3). int, positive.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub wander_radius: Option<u32>,
    /// Waypoints for the patrol pattern (visited in order, looping).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub patrol_points: Option<Vec<GridPoint>>,
    /// Time-based schedule (M3): sorted by minute; latest passed entry wins.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub schedule: Option<Vec<NpcScheduleEntry>>,
    /// Gift preferences (M4 social); unlisted items are neutral.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub gift_tastes: Option<GiftTastes>,
    /// Birthday: day-of-season (1-28) within birthSeason, doubles gift effects.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub birthday: Option<NpcBirthday>,
    pub appearance: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_image: Option<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// The project's player start/record (TS `PlayerSchema`); runtime uses [`super::save::PlayerState`].
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Player {
    #[serde(with = "crate::units::position")]
    pub x: i32,
    #[serde(with = "crate::units::position")]
    pub y: i32,
    /// One of [`super::directions`].
    pub direction: String,
    pub scene_id: String,
    pub inventory: Vec<InventorySlot>,
    #[serde(with = "crate::units::count")]
    pub max_inventory_size: u32,
    #[serde(with = "crate::units::money")]
    pub money: i64,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::energy::opt")]
    pub energy: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::energy::opt")]
    pub max_energy: Option<i32>,
    /// Per-category skill XP/levels (M4g); mirrored from GameState on save.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub skills: Option<IndexMap<String, SkillState>>,
    pub active_quests: Vec<String>,
    pub completed_quests: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub equipped_tool: Option<String>,
    /// Pixel X position for smooth movement interpolation
    #[serde(with = "crate::units::screen")]
    pub pixel_x: f64,
    /// Pixel Y position for smooth movement interpolation
    #[serde(with = "crate::units::screen")]
    pub pixel_y: f64,
    /// Target pixel X for interpolation
    #[serde(with = "crate::units::screen")]
    pub target_x: f64,
    /// Target pixel Y for interpolation
    #[serde(with = "crate::units::screen")]
    pub target_y: f64,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
