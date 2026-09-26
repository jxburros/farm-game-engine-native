//! Port of `Fishing.cs` (packages/engine-schemas/src/fishing.ts).
//! Fishing (M4e).

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct FishTableEntry {
    pub item_id: String,
    /// positive.
    pub weight: f64,
    /// 0..1 — harder fish escape low-tier rods more often.
    pub difficulty: f64,
}

impl Default for FishTableEntry {
    fn default() -> Self {
        Self { item_id: String::new(), weight: 0.0, difficulty: 0.3 }
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
    pub junk_chance: f64,
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
            junk_chance: 0.15,
            junk_item_id: None,
            extra: Map::new(),
        }
    }
}
