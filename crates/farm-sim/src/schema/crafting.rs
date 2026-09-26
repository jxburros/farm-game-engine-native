//! Port of `Crafting.cs` (packages/engine-schemas/src/crafting.ts).
//! Crafting & machines (M4a) — the "central system".

use super::graphics::VisualRef;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct RecipeIngredient {
    pub item_id: String,
    /// int, positive.
    pub quantity: f64,
}

/// TS `RecipeUnlockSchema.skill` (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct RecipeSkillRequirement {
    pub skill: String,
    /// int.
    pub level: f64,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct RecipeUnlock {
    /// Requires a skill at a minimum level.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub skill: Option<RecipeSkillRequirement>,
    /// Requires a completed quest.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quest_id: Option<String>,
    /// Only craftable in these seasons.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seasons: Option<Vec<String>>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct RecipeDefinition {
    pub id: String,
    pub name: String,
    pub inputs: Vec<RecipeIngredient>,
    pub outputs: Vec<RecipeIngredient>,
    /// In-game minutes a machine needs; 0 = instant hand-craft. nonnegative.
    pub processing_minutes: f64,
    /// Machine type required; absent = craftable by hand.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub machine_type_id: Option<String>,
    /// Freeform crafting discipline for UI grouping ('cooking', 'magic', 'carpentry', …).
    pub category: String,
    /// Hand-craftable only while near a machine providing this station category.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub requires_station_category: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unlock: Option<RecipeUnlock>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for RecipeDefinition {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            inputs: Vec::new(),
            outputs: Vec::new(),
            processing_minutes: 0.0,
            machine_type_id: None,
            category: "crafting".to_owned(),
            requires_station_category: None,
            unlock: None,
            extra: Map::new(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MachineTypeDefinition {
    pub id: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub visual: Option<VisualRef>,
    pub description: String,
    /// Renderer hint.
    pub color: String,
    /// Item consumed to place this machine (crafted or bought).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_id: Option<String>,
    pub blocks_movement: bool,
    /// Station categories a placed machine of this type provides to nearby hand-crafting (e.g. a
    /// Kitchen provides 'cooking').
    pub station_categories: Vec<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for MachineTypeDefinition {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            visual: None,
            description: String::new(),
            color: "#9a7b4f".to_owned(),
            item_id: None,
            blocks_movement: true,
            station_categories: Vec::new(),
            extra: Map::new(),
        }
    }
}

/// TS `TileMachineSchema.processing` (inline object): an in-flight machine job.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MachineProcessing {
    pub recipe_id: String,
    pub completes_at_minute: f64,
}

/// Live machine instance on a tile.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TileMachine {
    pub type_id: String,
    /// In-flight job: recipe + absolute game-minute it completes.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub processing: Option<MachineProcessing>,
    /// Finished output awaiting collection.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub output: Option<Vec<RecipeIngredient>>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
