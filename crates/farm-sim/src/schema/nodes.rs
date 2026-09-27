//! Port of `Nodes.cs` (packages/engine-schemas/src/nodes.ts).

use super::graphics::VisualRef;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

/// Weighted drop-table entry for a gathering node.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NodeDrop {
    pub item_id: String,
    /// int, nonnegative.
    pub min: f64,
    /// int, nonnegative.
    pub max: f64,
    /// positive.
    pub weight: f64,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// A gathering node type (tree, rock, weeds, …) — content-defined so mods and projects can add
/// their own.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NodeTypeDefinition {
    pub id: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub visual: Option<VisualRef>,
    /// Number of tool hits required to break the node. int, positive.
    pub health: f64,
    /// One of [`super::tool_types`].
    pub required_tool: String,
    /// Minimum tool tier required (tools default to tier 1). int, positive.
    pub required_tool_tier: f64,
    /// Weighted drop table; each hit that depletes the node rolls once per entry range.
    pub drops: Vec<NodeDrop>,
    /// Days until a depleted node respawns; null/absent = never. int, positive.
    /// TS is `.nullable().optional()`; like the C# port this cannot tell absent from null, so it
    /// is always written (as `null` when unset) — the built-in content always spells the key out.
    pub respawn_days: Option<f64>,
    /// Renderer hint (hex color).
    pub color: String,
    /// Whether the node blocks movement while present.
    pub blocks_movement: bool,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for NodeTypeDefinition {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            visual: None,
            health: 0.0,
            required_tool: String::new(),
            required_tool_tier: 1.0,
            drops: Vec::new(),
            respawn_days: None,
            color: "#7a5a3a".to_owned(),
            blocks_movement: true,
            extra: Map::new(),
        }
    }
}

/// Live node instance state stored on a tile.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TileNode {
    pub type_id: String,
    /// int.
    pub remaining_health: f64,
    /// Set when depleted; used for respawn scheduling. int.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub depleted_on_day: Option<f64>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
