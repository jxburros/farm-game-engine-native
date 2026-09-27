//! Port of `World.cs` (packages/engine-schemas/src/world.ts).

use super::content::{Crop, Item};
use super::crafting::TileMachine;
use super::graphics::VisualRef;
use super::nodes::TileNode;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SceneTransition {
    pub from_x: f64,
    pub from_y: f64,
    pub to_scene_id: String,
    pub to_x: f64,
    pub to_y: f64,
    /// Locked transitions don't fire; events can lock/unlock them (M3).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub locked: Option<bool>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// TS `TileSchema.visuals` (inline object): per-layer art overrides.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TileVisuals {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub background: Option<VisualRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub overlay: Option<VisualRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub object: Option<VisualRef>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Tile {
    pub x: f64,
    pub y: f64,
    /// One of [`super::tile_types`].
    pub r#type: String,
    /// Background layer: grass, soil, water, floor
    pub background: String,
    /// Overlay layer: path, rug, … renders on top of background (present-as-null).
    pub overlay: Option<String>,
    /// Object layer: wall, door, … renders on top of overlay (present-as-null).
    pub object: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub crop: Option<Crop>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item: Option<Item>,
    /// Gathering node instance (tree/rock/…) occupying this tile.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub node: Option<TileNode>,
    /// Placed machine instance (M4 crafting).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub machine: Option<TileMachine>,
    /// Mine ladder going down (M4 mining, generated floors).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ladder_down: Option<bool>,
    pub collision: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_image: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub visuals: Option<TileVisuals>,
    /// One of [`super::soil_states`].
    #[serde(skip_serializing_if = "Option::is_none")]
    pub soil_state: Option<String>,
    pub soil_moisture: f64,
    pub soil_fertility: f64,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Scene {
    pub id: String,
    pub name: String,
    /// int, positive.
    pub width: f64,
    /// int, positive.
    pub height: f64,
    /// Row-major: `tiles[y][x]`.
    pub tiles: Vec<Vec<Tile>>,
    pub transitions: Vec<SceneTransition>,
    pub npcs: Vec<String>,
    pub events: Vec<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}
