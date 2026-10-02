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
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub target_item_quantity: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_crop_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub target_crop_quantity: Option<u32>,
    #[serde(rename = "targetNPCId", skip_serializing_if = "Option::is_none")]
    pub target_npc_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_scene_id: Option<String>,
    pub completed: bool,
    #[serde(with = "crate::units::count")]
    pub progress: u32,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// TS `QuestRewardsSchema.items` item (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct QuestRewardItem {
    pub item_id: String,
    #[serde(with = "crate::units::count")]
    pub quantity: u32,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct QuestRewards {
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::money::opt")]
    pub money: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub items: Option<Vec<QuestRewardItem>>,
    /// Skill XP granted on completion, to `skill`.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub experience: Option<u32>,
    /// The skill `experience` goes to (farming, mining, foraging, fishing, social, …).
    /// Absent: farming.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub skill: Option<String>,
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
    /// A completed repeatable quest can be started again (from a dialogue offer or an event)
    /// with its objectives reset; an auto-start one restarts by itself the next morning.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub repeatable: Option<bool>,
    /// Seasonal availability (M3): quest only offered/auto-started in these seasons.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub available_seasons: Option<Vec<String>>,
    /// Absolute-day window (M3): offered from/until these days (inclusive).
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub available_from_day: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub available_to_day: Option<u32>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
