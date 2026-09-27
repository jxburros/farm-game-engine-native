//! Port of `Save.cs` (packages/engine-schemas/src/save.ts: schemas, constants and pure helpers;
//! `SAVE_MIGRATIONS` / `migrateGameState` are ported separately into `farm-cart`).
//!
//! Save-data family (`GameState`) — a player's running game, fully serializable, versioned
//! independently from project and content data.
//!
//! State is plain data: no classes, no functions. All mutation flows through engine commands and
//! ticks.
//!
//! - v1 — M1 extraction (wall-clock mirror for legacy growth)
//! - v2 — M2 game clock: minute-of-day time, day/season/year calendar, player energy, shop
//!   session + daily purchase tracking
//! - v3 — M4 simulation depth: weather, skills, social state, animals, mine progress
//! - v4 — free movement: fractional player position (tile units, player center) + held movement
//!   intent

use super::actors::GridPoint;
use super::animals::AnimalState;
use super::content::InventorySlot;
use super::extensibility::MinigameSession;
use super::social::NpcSocialState;
use super::world::Scene;
use crate::js;
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::Value;

/// Serialized PRNG state — deterministic resume is a core guarantee (lives in [`crate::rng`]).
pub use crate::rng::RngState;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ClockState {
    /// Fixed-timestep tick counter since game start. int.
    pub tick: f64,
    /// Minute-of-day of the game clock (e.g. 360 = 6:00).
    pub time_minutes: f64,
    /// Absolute in-game day, 1-based, monotonically increasing. int.
    pub day: f64,
    pub season: String,
    /// int.
    pub year: f64,
    /// Today's weather (rolled at day start, M4).
    pub weather_id: String,
}

impl Default for ClockState {
    fn default() -> Self {
        Self { tick: 0.0, time_minutes: 0.0, day: 0.0, season: String::new(), year: 0.0, weather_id: "sun".to_owned() }
    }
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SkillState {
    pub xp: f64,
    /// int.
    pub level: f64,
}

/// Held movement input (v4 free movement). Player intent enters the command log as
/// `setMoveIntent`; the engine integrates position from it every tick, so replays stay
/// deterministic without logging per-tick positions.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MoveIntent {
    /// int, -1..1.
    pub dx: f64,
    /// int, -1..1.
    pub dy: f64,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PlayerState {
    /// Player position in tile units — the CENTER of the player's collision box. Fractional
    /// since v4 (free movement); the occupied tile is `Math.floor(x/y)`. Tiles are
    /// layout/terrain, not a movement grid.
    pub x: f64,
    pub y: f64,
    pub move_intent: MoveIntent,
    /// One of [`super::directions`].
    pub direction: String,
    pub scene_id: String,
    pub inventory: Vec<InventorySlot>,
    pub max_inventory_size: f64,
    pub money: f64,
    pub energy: f64,
    pub max_energy: f64,
    /// Per-category skill XP/levels (M4g).
    pub skills: IndexMap<String, SkillState>,
    pub active_quests: Vec<String>,
    pub completed_quests: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub equipped_tool: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NpcState {
    /// int.
    pub x: f64,
    /// int.
    pub y: f64,
    pub scene_id: String,
    /// Remaining A* path steps toward the current destination (M3 schedules).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<Vec<GridPoint>>,
    /// Index of the patrol waypoint the NPC is heading to. int.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub patrol_index: Option<f64>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct QuestObjectiveProgress {
    pub progress: f64,
    pub completed: bool,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct QuestProgress {
    /// One of [`super::quest_statuses`].
    pub status: String,
    /// Per-objective progress. `None` when the TS engine never wrote the key: `completeQuest`
    /// spreads a missing entry (`{ ...undefined, status: 'completed' }`), so a quest completed
    /// without a progress entry has no `objectives` key and hashes without it. Every normally
    /// created entry has `Some(map)`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub objectives: Option<IndexMap<String, QuestObjectiveProgress>>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DialogueState {
    pub npc_id: String,
    pub dialogue_id: String,
}

/// An open shop session.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ShopSession {
    pub shop_id: String,
}

/// TS `GameStateSchema.meta.packs` item (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SavePackRef {
    pub id: String,
    pub version: String,
}

/// TS `GameStateSchema.meta` (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameStateMeta {
    /// int.
    pub save_version: f64,
    pub engine_seed: String,
    /// Packs this save was created with ("this save uses packs X, Y").
    pub packs: Vec<SavePackRef>,
}

/// TS `GameStateSchema.world` (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct WorldState {
    /// Live scene state during play (tiles, crops, nodes, dropped items).
    pub scenes: Vec<Scene>,
}

/// TS `GameStateSchema.mine` (inline object): mining progress (M4f).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MineProgress {
    /// int.
    pub deepest_floor: f64,
    /// int.
    pub current_floor: f64,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameState {
    pub meta: GameStateMeta,
    pub clock: ClockState,
    pub world: WorldState,
    pub player: PlayerState,
    pub npcs: IndexMap<String, NpcState>,
    pub quests: IndexMap<String, QuestProgress>,
    /// Present-as-null when closed.
    pub dialogue: Option<DialogueState>,
    /// Present-as-null when closed.
    pub shop: Option<ShopSession>,
    /// Open minigame session (modal, like dialogue/shop). Present-as-null when closed.
    pub minigame: Option<MinigameSession>,
    /// Per-shop, per-item units bought today (daily stock limits); reset nightly.
    pub shop_purchases_today: IndexMap<String, IndexMap<String, f64>>,
    /// Friendship & gifting state per NPC (M4d).
    pub social: IndexMap<String, NpcSocialState>,
    /// Live animals (M4c).
    pub animals: Vec<AnimalState>,
    /// Mining progress (M4f): deepest floor reached + current floor.
    pub mine: MineProgress,
    /// Values are `boolean | number | string` (see [`crate::js::value`], [`crate::js::truthy`]).
    pub flags: IndexMap<String, Value>,
    /// Inventory items whose owning pack is missing/disabled — quarantined, not dropped; they
    /// return to the inventory when the pack comes back (M5).
    pub quarantined_items: Vec<InventorySlot>,
    pub rng: RngState,
}

/// TS `SaveMigrationResult` (returned by the save migration pipeline).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SaveMigrationResult {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub data: Option<GameState>,
    pub from_version: f64,
    pub migrated: bool,
    pub errors: Vec<String>,
}

pub const CURRENT_SAVE_VERSION: f64 = 4.0;

/// TS `SKILL_NAMES`.
pub const SKILL_NAMES: &[&str] = &["farming", "foraging", "fishing", "mining", "social"];

/// Tile index → tile center; already-fractional coordinates pass through.
pub fn center_coordinate(value: f64) -> f64 {
    if js::is_integer(value) {
        value + 0.5
    } else {
        value
    }
}
