//! Port of `Mining.cs` (packages/engine-schemas/src/mining.ts).
//! Mining (M4f) — procedurally generated floors, deterministic per seed.

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

/// TS `MineBandSchema.rocks` item (inline object): a weighted node type.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MineRockWeight {
    pub node_type_id: String,
    /// positive.
    pub weight: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MineBand {
    /// int, positive.
    pub from_floor: f64,
    /// int, positive.
    pub to_floor: f64,
    /// Weighted node types spawned in this depth band.
    pub rocks: Vec<MineRockWeight>,
    /// Rock density (fraction of floor tiles occupied). 0..1.
    pub density: f64,
}

impl Default for MineBand {
    fn default() -> Self {
        Self { from_floor: 0.0, to_floor: 0.0, rocks: Vec::new(), density: 0.35 }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MineConfig {
    pub enabled: bool,
    /// Scene holding the mine entrance (descend via the entrance tile).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub entrance_scene_id: Option<String>,
    /// int.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub entrance_x: Option<f64>,
    /// int.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub entrance_y: Option<f64>,
    /// int, positive.
    pub floors: f64,
    /// int, positive.
    pub floor_width: f64,
    /// int, positive.
    pub floor_height: f64,
    pub bands: Vec<MineBand>,
    /// Chance a broken rock reveals the ladder down. 0..1.
    pub ladder_chance: f64,
    /// Elevator checkpoint every N floors. int, positive.
    pub elevator_every: f64,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for MineConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            entrance_scene_id: None,
            entrance_x: None,
            entrance_y: None,
            floors: 20.0,
            floor_width: 14.0,
            floor_height: 12.0,
            bands: Vec::new(),
            ladder_chance: 0.18,
            elevator_every: 5.0,
            extra: Map::new(),
        }
    }
}
