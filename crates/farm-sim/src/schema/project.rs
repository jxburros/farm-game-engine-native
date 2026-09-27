//! Port of `Project.cs` (packages/engine-schemas/src/project.ts).

use super::actors::{Dialogue, Npc, Player};
use super::animals::{AnimalSpeciesDefinition, AnimalState};
use super::content::{CustomAsset, CustomCropDefinition, InventorySlot, Item};
use super::crafting::{MachineTypeDefinition, RecipeDefinition};
use super::economy::ShopDefinition;
use super::events::GameEvent;
use super::extensibility::{ActionDef, MinigameDef};
use super::fishing::FishTable;
use super::graphics::{GraphicsSettings, VisualRef};
use super::interface::GamePanel;
use super::mining::MineConfig;
use super::nodes::NodeTypeDefinition;
use super::packs::PackInstallation;
use super::quests::Quest;
use super::save::RngState;
use super::settings::ProjectSettings;
use super::social::NpcSocialState;
use super::weather::WeatherConfig;
use super::world::Scene;
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameProject {
    /// int.
    pub schema_version: f64,
    pub id: String,
    pub name: String,
    pub version: String,
    pub scenes: Vec<Scene>,
    pub npcs: Vec<Npc>,
    pub items: Vec<Item>,
    pub events: Vec<GameEvent>,
    pub dialogues: Vec<Dialogue>,
    pub quests: Vec<Quest>,
    pub player: Player,
    pub event_flags: IndexMap<String, bool>,
    pub start_scene_id: String,
    /// One of [`super::editor_modes`].
    pub mode: String,
    /// One of [`super::tile_types`].
    pub selected_tile_type: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub selected_tile_visual: Option<VisualRef>,
    /// Present-as-null.
    #[serde(rename = "selectedNPCId")]
    pub selected_npc_id: Option<String>,
    /// Present-as-null.
    pub selected_item_id: Option<String>,
    pub current_time: f64,
    pub custom_assets: Vec<CustomAsset>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_crops: Option<Vec<CustomCropDefinition>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_custom_image: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_visual: Option<VisualRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub graphics: Option<GraphicsSettings>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_panels: Option<Vec<GamePanel>>,
    pub current_season: String,
    pub current_day: f64,
    /// Minute-of-day of the game clock (v4+).
    pub current_time_minutes: f64,
    /// int.
    pub current_year: f64,
    pub game_start_time: f64,
    pub shops: Vec<ShopDefinition>,
    pub node_types: Vec<NodeTypeDefinition>,
    pub settings: ProjectSettings,
    pub recipes: Vec<RecipeDefinition>,
    pub machine_types: Vec<MachineTypeDefinition>,
    pub weather: WeatherConfig,
    pub animal_species: Vec<AnimalSpeciesDefinition>,
    pub animals: Vec<AnimalState>,
    pub fish_tables: Vec<FishTable>,
    pub mine: MineConfig,
    pub actions: Vec<ActionDef>,
    pub minigames: Vec<MinigameDef>,
    /// Installed content packs (M5). Array order = load order.
    pub content_packs: Vec<PackInstallation>,
    /// Serialized PRNG state so play sessions resume deterministically (additive, optional).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rng_state: Option<RngState>,
    // Runtime state mirrored by applyStateToProject. These used to ride the .passthrough()
    // escape hatch and reach the simulation unvalidated.
    /// Today's weather id (mirrors GameState.clock.weatherId).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_weather_id: Option<String>,
    /// Friendship & gifting state per NPC (mirrors GameState.social).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub social_state: Option<IndexMap<String, NpcSocialState>>,
    /// Deepest mine floor reached (mirrors GameState.mine.deepestFloor). int, nonnegative.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mine_deepest_floor: Option<f64>,
    /// Items whose owning pack is missing/disabled (mirrors GameState.quarantinedItems).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quarantined_items: Option<Vec<InventorySlot>>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ExportedGame {
    /// int.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub schema_version: Option<f64>,
    pub version: String,
    pub name: String,
    pub scenes: Vec<Scene>,
    pub npcs: Vec<Npc>,
    pub items: Vec<Item>,
    pub events: Vec<GameEvent>,
    pub dialogues: Vec<Dialogue>,
    pub quests: Vec<Quest>,
    pub start_scene_id: String,
    pub custom_assets: Vec<CustomAsset>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_crops: Option<Vec<CustomCropDefinition>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_custom_image: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_visual: Option<VisualRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub graphics: Option<GraphicsSettings>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_panels: Option<Vec<GamePanel>>,
    pub current_season: String,
    pub current_day: f64,
    pub current_time_minutes: f64,
    /// int.
    pub current_year: f64,
    pub game_start_time: f64,
    pub shops: Vec<ShopDefinition>,
    pub node_types: Vec<NodeTypeDefinition>,
    pub settings: ProjectSettings,
    pub recipes: Vec<RecipeDefinition>,
    pub machine_types: Vec<MachineTypeDefinition>,
    pub weather: WeatherConfig,
    pub animal_species: Vec<AnimalSpeciesDefinition>,
    pub animals: Vec<AnimalState>,
    pub fish_tables: Vec<FishTable>,
    pub mine: MineConfig,
    pub actions: Vec<ActionDef>,
    pub minigames: Vec<MinigameDef>,
    pub content_packs: Vec<PackInstallation>,
    /// Player start state (M6): included so exported games start identically.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player: Option<Player>,
    // Runtime state mirrored into saves (exported-game saves are projected through
    // applyStateToProject); see GameProject.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_weather_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub social_state: Option<IndexMap<String, NpcSocialState>>,
    /// int, nonnegative.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mine_deepest_floor: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quarantined_items: Option<Vec<InventorySlot>>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// The project schema version. Bump when the persisted shape changes and add a migration plus a
/// fixture in tests/.
///
/// - v1 — original prototype (tiles without layer fields)
/// - v2 — layered tiles (background/overlay/object)
/// - v3 — explicit schemaVersion + guaranteed presence of customAssets, season/day fields, quests
///   and player pixel/quest fields (replaces the ad-hoc backfill effects that lived in App.tsx)
/// - v4 — day-based game time (M2): crop growthDays/plantedOnDay/daysGrown, shops, gathering node
///   types, project settings (energy/time), player energy, currentTimeMinutes
/// - v5 — events v2 (M3): trigger + combinable conditions + sequenced outcomes; NPC schedules;
///   lockable transitions; quest availability
/// - v6 — simulation depth (M4): recipes, machine types, weather config, animal species +
///   animals, fish tables, mine config, gift tastes, player skills
/// - v7 — content packs (M5): installed packs (with load order + enable flags) travel inside the
///   project
/// - v8 — graphics settings (pixel-art rendering on by default)
pub const CURRENT_PROJECT_SCHEMA_VERSION: f64 = 8.0;
