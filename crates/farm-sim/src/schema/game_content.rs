//! Port of `GameContent.cs` (packages/engine-schemas/src/game-content.ts).
//!
//! Content family — everything that defines a game and is immutable during play. Content comes
//! from the built-in pack + the project's custom content (+ enabled mods from M5), merged and
//! validated at load time.

use super::actors::{Dialogue, Npc};
use super::animals::AnimalSpeciesDefinition;
use super::content::{CropDefinition, Item};
use super::crafting::{MachineTypeDefinition, RecipeDefinition};
use super::economy::ShopDefinition;
use super::events::GameEvent;
use super::extensibility::{ActionDef, MinigameDef};
use super::fishing::FishTable;
use super::mining::MineConfig;
use super::nodes::NodeTypeDefinition;
use super::quests::Quest;
use super::settings::ProjectSettings;
use super::weather::WeatherConfig;
use super::world::Scene;
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameContent {
    /// int.
    pub content_version: f64,
    /// Crop definitions by id (built-in merged with project custom crops).
    pub crops: IndexMap<String, CropDefinition>,
    pub items: Vec<Item>,
    pub npcs: Vec<Npc>,
    pub dialogues: Vec<Dialogue>,
    pub quests: Vec<Quest>,
    pub events: Vec<GameEvent>,
    pub shops: Vec<ShopDefinition>,
    pub node_types: Vec<NodeTypeDefinition>,
    pub settings: ProjectSettings,
    pub recipes: Vec<RecipeDefinition>,
    pub machine_types: Vec<MachineTypeDefinition>,
    pub weather: WeatherConfig,
    pub animal_species: Vec<AnimalSpeciesDefinition>,
    pub fish_tables: Vec<FishTable>,
    pub mine: MineConfig,
    /// Creator-defined actions (extensibility layer).
    pub actions: Vec<ActionDef>,
    /// Declared minigames (extensibility layer).
    pub minigames: Vec<MinigameDef>,
    /// Authored initial scenes — the template the world state is created from.
    pub scenes: Vec<Scene>,
    pub start_scene_id: String,
}

pub const CURRENT_CONTENT_VERSION: f64 = 1.0;
