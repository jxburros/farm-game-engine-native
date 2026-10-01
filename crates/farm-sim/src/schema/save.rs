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
use crate::units;
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::Value;

/// Serialized PRNG state — deterministic resume is a core guarantee (lives in [`crate::rng`]).
pub use crate::rng::RngState;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ClockState {
    /// Fixed-timestep tick counter since game start. int.
    #[serde(with = "crate::units::ticks")]
    pub tick: u64,
    /// Minute-of-day of the game clock (e.g. 360 = 6:00).
    #[serde(with = "crate::units::micro_minutes")]
    pub time_minutes: u32,
    /// Absolute in-game day, 1-based, monotonically increasing. int.
    #[serde(with = "crate::units::count")]
    pub day: u32,
    pub season: String,
    /// 1-based day within `season`: with `season` the clock's place in the calendar, which the
    /// absolute `day` does not determine (a game may start in any season, and season lengths
    /// may change under a save). 0 in saves written before it existed; loading derives it
    /// (see [`crate::game_time::reconcile_clock`]). int.
    #[serde(with = "crate::units::count")]
    pub day_of_season: u32,
    /// int.
    #[serde(with = "crate::units::count")]
    pub year: u32,
    /// Today's weather (rolled at day start, M4).
    pub weather_id: String,
}

impl Default for ClockState {
    fn default() -> Self {
        Self {
            tick: 0,
            time_minutes: 0,
            day: 0,
            season: String::new(),
            day_of_season: 0,
            year: 0,
            weather_id: "sun".to_owned(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SkillState {
    #[serde(with = "crate::units::count")]
    pub xp: u32,
    /// int.
    #[serde(with = "crate::units::count")]
    pub level: u32,
}

/// Held movement input (v4 free movement). Player intent enters the command log as
/// `setMoveIntent`; the engine integrates position from it every tick, so replays stay
/// deterministic without logging per-tick positions.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MoveIntent {
    /// int, -1..1.
    #[serde(with = "crate::units::truncated")]
    pub dx: i32,
    /// int, -1..1.
    #[serde(with = "crate::units::truncated")]
    pub dy: i32,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PlayerState {
    /// Player position in tile units — the CENTER of the player's collision box. Fractional
    /// since v4 (free movement); the occupied tile is `Math.floor(x/y)`. Tiles are
    /// layout/terrain, not a movement grid.
    #[serde(with = "crate::units::position")]
    pub x: i32,
    #[serde(with = "crate::units::position")]
    pub y: i32,
    pub move_intent: MoveIntent,
    /// One of [`super::directions`].
    pub direction: String,
    pub scene_id: String,
    pub inventory: Vec<InventorySlot>,
    #[serde(with = "crate::units::count")]
    pub max_inventory_size: u32,
    #[serde(with = "crate::units::money")]
    pub money: i64,
    #[serde(with = "crate::units::energy")]
    pub energy: i32,
    #[serde(with = "crate::units::energy")]
    pub max_energy: i32,
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
    #[serde(with = "crate::units::position")]
    pub x: i32,
    /// int.
    #[serde(with = "crate::units::position")]
    pub y: i32,
    pub scene_id: String,
    /// Remaining A* path steps toward the current destination (M3 schedules).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<Vec<GridPoint>>,
    /// Index of the patrol waypoint the NPC is heading to. int.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub patrol_index: Option<u32>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct QuestObjectiveProgress {
    #[serde(with = "crate::units::count")]
    pub progress: u32,
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
    #[serde(with = "crate::units::count")]
    pub save_version: u32,
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
    #[serde(with = "crate::units::count")]
    pub deepest_floor: u32,
    /// int.
    #[serde(with = "crate::units::count")]
    pub current_floor: u32,
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
    #[serde(with = "crate::units::count::nested_map")]
    pub shop_purchases_today: IndexMap<String, IndexMap<String, u32>>,
    /// Friendship & gifting state per NPC (M4d).
    pub social: IndexMap<String, NpcSocialState>,
    /// Live animals (M4c).
    pub animals: Vec<AnimalState>,
    /// Mining progress (M4f): deepest floor reached + current floor.
    pub mine: MineProgress,
    /// Values are `boolean | number | string` (see [`crate::units::value`], [`crate::text::truthy`]).
    pub flags: IndexMap<String, Value>,
    /// Inventory items whose owning pack is missing/disabled — quarantined, not dropped; they
    /// return to the inventory when the pack comes back (M5).
    pub quarantined_items: Vec<InventorySlot>,
    pub rng: RngState,
}

/// What "Keep changes" carries from a playtest into the next one beyond the project's own
/// fields (the project's `keptState`), so the next playtest starts where the last one ended
/// (#36). Absent when there is nothing to carry (tick 0, nothing open, no NPC mid-walk, on the
/// surface).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct KeptState {
    /// [`ClockState::tick`].
    #[serde(with = "crate::units::ticks")]
    pub tick: u64,
    /// [`GameState::shop_purchases_today`].
    #[serde(with = "crate::units::count::nested_map")]
    pub shop_purchases_today: IndexMap<String, IndexMap<String, u32>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dialogue: Option<DialogueState>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub shop: Option<ShopSession>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub minigame: Option<MinigameSession>,
    /// Walking NPCs by id: their remaining path and patrol waypoint (positions are the
    /// project's NPC fields).
    pub npcs: IndexMap<String, KeptNpc>,
    /// [`MineProgress::current_floor`].
    #[serde(with = "crate::units::count")]
    pub mine_current_floor: u32,
}

/// An NPC's walk in [`KeptState::npcs`] (the [`NpcState`] fields the project's NPC lacks).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct KeptNpc {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<Vec<GridPoint>>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub patrol_index: Option<u32>,
}

impl KeptState {
    /// The parts of `state` a project does not hold, or `None` when there are none.
    pub fn of_state(state: &GameState) -> Option<Self> {
        let npcs: IndexMap<String, KeptNpc> = state
            .npcs
            .iter()
            .filter(|(_, npc)| npc.path.is_some() || npc.patrol_index.is_some())
            .map(|(id, npc)| (id.clone(), KeptNpc { path: npc.path.clone(), patrol_index: npc.patrol_index }))
            .collect();
        let kept = Self {
            tick: state.clock.tick,
            shop_purchases_today: state.shop_purchases_today.clone(),
            dialogue: state.dialogue.clone(),
            shop: state.shop.clone(),
            minigame: state.minigame.clone(),
            npcs,
            mine_current_floor: state.mine.current_floor,
        };
        (kept != Self::default()).then_some(kept)
    }

    /// Puts the kept parts back into a new game's `state` (NPCs the state lacks are skipped).
    pub fn apply_to(&self, state: &mut GameState) {
        state.clock.tick = self.tick;
        state.shop_purchases_today.clone_from(&self.shop_purchases_today);
        state.dialogue.clone_from(&self.dialogue);
        state.shop.clone_from(&self.shop);
        state.minigame.clone_from(&self.minigame);
        for (id, kept) in &self.npcs {
            if let Some(npc) = state.npcs.get_mut(id) {
                npc.path.clone_from(&kept.path);
                npc.patrol_index = kept.patrol_index;
            }
        }
        state.mine.current_floor = self.mine_current_floor;
    }
}

/// TS `SaveMigrationResult` (returned by the save migration pipeline).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SaveMigrationResult {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub data: Option<GameState>,
    /// The version the input declared, as read (a report, not state: `2.5` stays `2.5`).
    pub from_version: f64,
    pub migrated: bool,
    pub errors: Vec<String>,
}

pub const CURRENT_SAVE_VERSION: u32 = 5;

/// TS `SKILL_NAMES`.
pub const SKILL_NAMES: &[&str] = &["farming", "foraging", "fishing", "mining", "social"];

/// Tile index → tile center; positions already inside a tile pass through (projects may store
/// either).
pub fn center_coordinate(position: i32) -> i32 {
    if position % units::TILE == 0 {
        position.saturating_add(units::TILE / 2)
    } else {
        position
    }
}
