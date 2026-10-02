//! What a cartridge carries instead of the editable project (docs/EXPORT.md: the player never
//! reads project JSON).
//!
//! A project mixes three things: the authored content (compiled into `GameContent`), the state a
//! new game starts from, and what only the presentation reads (art bindings, creator panels).
//! [`StartState`] is exactly the input of [`crate::state::create_game_state`], and
//! [`Presentation`] is what the renderer and the game panels read. The F# cartridge compiler
//! writes both; [`StartState::from_project`] builds the same values from a project, so an editor
//! session and an exported game start from identical states.

use crate::packs;
use crate::schema::{
    AnimalState, CustomAsset, CustomCropDefinition, GamePanel, GameProject, GraphicsSettings, InventorySlot, KeptState,
    NpcSocialState, Player, ProjectSettings, RngState, SavePackRef, Scene, VisualRef,
};
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};

/// A quest's starting progress (`Quest.status` and its objectives).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct StartQuest {
    pub id: String,
    pub status: String,
    pub objectives: Vec<StartObjective>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct StartObjective {
    pub id: String,
    #[serde(with = "crate::units::count")]
    pub progress: u32,
    pub completed: bool,
}

/// Where an NPC stands when the game starts.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct StartNpc {
    pub id: String,
    #[serde(with = "crate::units::position")]
    pub x: i32,
    #[serde(with = "crate::units::position")]
    pub y: i32,
    pub scene_id: String,
}

/// Everything [`crate::state::create_game_state`] reads, in project terms.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct StartState {
    /// The project id; with `game_start_time` it forms the default engine seed.
    pub id: String,
    #[serde(with = "crate::units::exact")]
    pub game_start_time: f64,
    pub settings: ProjectSettings,
    pub player: Player,
    pub quests: Vec<StartQuest>,
    pub npcs: Vec<StartNpc>,
    /// Values are `boolean | number | string`.
    pub event_flags: IndexMap<String, serde_json::Value>,
    #[serde(with = "crate::units::micro_minutes")]
    pub current_time_minutes: u32,
    #[serde(with = "crate::units::count")]
    pub current_day: u32,
    pub current_season: String,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub current_day_of_season: Option<u32>,
    #[serde(with = "crate::units::count")]
    pub current_year: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_weather_id: Option<String>,
    pub scenes: Vec<Scene>,
    /// The enabled content packs in load order (`meta.packs` of the new state).
    pub packs: Vec<SavePackRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub social_state: Option<IndexMap<String, NpcSocialState>>,
    pub animals: Vec<AnimalState>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub mine_deepest_floor: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quarantined_items: Option<Vec<InventorySlot>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rng_state: Option<RngState>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub kept_state: Option<KeptState>,
}

impl StartState {
    /// The start of a new game in `project` (what the F# compiler writes to `start_json`).
    pub fn from_project(project: &GameProject) -> Self {
        Self {
            id: project.id.clone(),
            game_start_time: project.game_start_time,
            settings: project.settings.clone(),
            player: project.player.clone(),
            quests: project
                .quests
                .iter()
                .map(|quest| StartQuest {
                    id: quest.id.clone(),
                    status: quest.status.clone(),
                    objectives: quest
                        .objectives
                        .iter()
                        .map(|objective| StartObjective {
                            id: objective.id.clone(),
                            progress: objective.progress,
                            completed: objective.completed,
                        })
                        .collect(),
                })
                .collect(),
            npcs: project
                .npcs
                .iter()
                .map(|npc| StartNpc { id: npc.id.clone(), x: npc.x, y: npc.y, scene_id: npc.scene_id.clone() })
                .collect(),
            event_flags: project.event_flags.clone(),
            current_time_minutes: project.current_time_minutes,
            current_day: project.current_day,
            current_season: project.current_season.clone(),
            current_day_of_season: project.current_day_of_season,
            current_year: project.current_year,
            current_weather_id: project.current_weather_id.clone(),
            scenes: project.scenes.clone(),
            packs: packs::stamp_packs(&project.content_packs),
            social_state: project.social_state.clone(),
            animals: project.animals.clone(),
            mine_deepest_floor: project.mine_deepest_floor,
            quarantined_items: project.quarantined_items.clone(),
            rng_state: project.rng_state.clone(),
            kept_state: project.kept_state.clone(),
        }
    }
}

/// What the renderer and the in-game UI read besides content and state (the web
/// `GraphicsSource` plus the creator panels and credits).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Presentation {
    /// The project name (window title fallback, save slot names).
    pub name: String,
    pub custom_assets: Vec<CustomAsset>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_crops: Option<Vec<CustomCropDefinition>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_custom_image: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_visual: Option<VisualRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub graphics: Option<GraphicsSettings>,
    pub game_panels: Vec<GamePanel>,
    /// Show "Made with Farming RPG Maker" (project setting `showMadeWithCredit`).
    pub show_made_with_credit: bool,
}

impl Presentation {
    /// The start of `project`: its name, assets, crops, player look and starting state.
    pub fn from_project(project: &GameProject) -> Self {
        Self {
            name: project.name.clone(),
            custom_assets: project.custom_assets.clone(),
            custom_crops: project.custom_crops.clone(),
            player_custom_image: project.player_custom_image.clone(),
            player_visual: project.player_visual.clone(),
            graphics: project.graphics.clone(),
            game_panels: project.game_panels.clone().unwrap_or_default(),
            show_made_with_credit: project.settings.show_made_with_credit,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn start_state_round_trips_through_json() {
        let fixture: serde_json::Value =
            serde_json::from_str(include_str!("../../../fixtures/golden/content/starter-farm.json")).unwrap();
        let project: GameProject = serde_json::from_value(fixture["project"].clone()).unwrap();
        let start = StartState::from_project(&project);
        let json = serde_json::to_string(&start).unwrap();
        let back: StartState = serde_json::from_str(&json).unwrap();
        assert_eq!(back, start);
        assert_eq!(
            crate::hash_state(&crate::state::create_game_state_from_start(&back, Some("s"))),
            crate::hash_state(&crate::state::create_game_state(&project, Some("s")))
        );
    }
}
