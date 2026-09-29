//! Port of `Fishing.cs` (packages/engine-schemas/src/fishing.ts).
//! Fishing (M4e).

use crate::units;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct FishTableEntry {
    pub item_id: String,
    /// positive.
    #[serde(with = "crate::units::count")]
    pub weight: u32,
    /// 0..1 — harder fish escape low-tier rods more often.
    #[serde(with = "crate::units::probability")]
    pub difficulty: u64,
}

impl Default for FishTableEntry {
    fn default() -> Self {
        Self { item_id: String::new(), weight: 0, difficulty: units::from_authoring::<units::Probability>(0.3) }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct FishTable {
    pub id: String,
    pub name: String,
    /// Restrict to seasons (absent = all).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seasons: Option<Vec<String>>,
    /// Restrict to scenes (absent = any water).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scene_ids: Option<Vec<String>>,
    pub entries: Vec<FishTableEntry>,
    /// Chance (0..1) a cast catches junk instead of rolling the table.
    #[serde(with = "crate::units::probability")]
    pub junk_chance: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub junk_item_id: Option<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for FishTable {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            seasons: None,
            scene_ids: None,
            entries: Vec::new(),
            junk_chance: units::from_authoring::<units::Probability>(0.15),
            junk_item_id: None,
            extra: Map::new(),
        }
    }
}
