//! Port of `Quests.cs` (packages/engine-schemas/src/quests.ts).

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct QuestObjective {
    pub id: String,
    /// One of [`super::quest_objective_types`].
    pub r#type: String,
    pub description: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_item_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_item_quantity: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_crop_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_crop_quantity: Option<f64>,
    #[serde(rename = "targetNPCId", skip_serializing_if = "Option::is_none")]
    pub target_npc_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_scene_id: Option<String>,
    pub completed: bool,
    pub progress: f64,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// TS `QuestRewardsSchema.items` item (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct QuestRewardItem {
    pub item_id: String,
    pub quantity: f64,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct QuestRewards {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub money: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub items: Option<Vec<QuestRewardItem>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub experience: Option<f64>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Quest {
    pub id: String,
    pub name: String,
    pub description: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub giver: Option<String>,
    /// One of [`super::quest_statuses`].
    pub status: String,
    pub objectives: Vec<QuestObjective>,
    pub rewards: QuestRewards,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub prerequisites: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub auto_start: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub repeatable: Option<bool>,
    /// Seasonal availability (M3): quest only offered/auto-started in these seasons.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub available_seasons: Option<Vec<String>>,
    /// Absolute-day window (M3): offered from/until these days (inclusive).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub available_from_day: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub available_to_day: Option<f64>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
