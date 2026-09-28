//! Plain data consumed by render backends. Mirrors the native Skia snapshot contract.
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
#[derive(Default)]
pub struct SnapshotCrop {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sprite: Option<SnapshotSprite>,
    pub color_index: i32,
    pub mature: bool,
    pub withered: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub crop_id: Option<String>,
    pub stages: i32,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SnapshotNode {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sprite: Option<SnapshotSprite>,
    pub color: String,
    pub depleted: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub type_id: Option<String>,
}
impl Default for SnapshotNode {
    fn default() -> Self {
        Self { sprite: None, color: "#7a5a3a".to_owned(), depleted: false, type_id: None }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SnapshotMachine {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sprite: Option<SnapshotSprite>,
    pub color: String,
    pub working: bool,
    pub output_ready: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub type_id: Option<String>,
}
impl Default for SnapshotMachine {
    fn default() -> Self {
        Self { sprite: None, color: "#9a7b4f".to_owned(), working: false, output_ready: false, type_id: None }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
#[derive(Default)]
pub struct SnapshotItem {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub image_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sprite: Option<SnapshotSprite>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_type: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SnapshotTile {
    pub background: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub overlay: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub object: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub image_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub art_layers: Option<Vec<Option<SnapshotSprite>>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub crop: Option<SnapshotCrop>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub node: Option<SnapshotNode>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub machine: Option<SnapshotMachine>,
    pub ladder_down: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item: Option<SnapshotItem>,
    pub tilled: bool,
    pub watered: bool,
    pub fertilized: bool,
}
impl Default for SnapshotTile {
    fn default() -> Self {
        Self {
            background: "grass".to_owned(),
            overlay: None,
            object: None,
            image_url: None,
            art_layers: None,
            crop: None,
            node: None,
            machine: None,
            ladder_down: false,
            item: None,
            tilled: false,
            watered: false,
            fertilized: false,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SnapshotSprite {
    pub image_url: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_x: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_y: Option<f64>,
    pub frame_width: f64,
    pub frame_height: f64,
    pub frame: f64,
    pub row: f64,
}
impl Default for SnapshotSprite {
    fn default() -> Self {
        Self {
            image_url: "".to_owned(),
            source_x: None,
            source_y: None,
            frame_width: 0.0,
            frame_height: 0.0,
            frame: 0.0,
            row: 0.0,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SnapshotEntity {
    pub x: f64,
    pub y: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub image_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sprite: Option<SnapshotSprite>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub color: Option<String>,
    pub kind: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub appearance: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub species_id: Option<String>,
    pub direction: String,
    pub moving: bool,
}
impl Default for SnapshotEntity {
    fn default() -> Self {
        Self {
            x: 0.0,
            y: 0.0,
            image_url: None,
            sprite: None,
            color: None,
            kind: "npc".to_owned(),
            appearance: None,
            species_id: None,
            direction: "down".to_owned(),
            moving: false,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
#[derive(Default)]
pub struct SnapshotPlayer {
    #[serde(flatten)]
    pub entity: SnapshotEntity,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pixel_x: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pixel_y: Option<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SnapshotPop {
    pub x: f64,
    pub y: f64,
    pub text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub color: Option<String>,
    pub age: f64,
}
impl Default for SnapshotPop {
    fn default() -> Self {
        Self { x: 0.0, y: 0.0, text: "".to_owned(), color: None, age: 0.0 }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct WorldSnapshot {
    pub width: i32,
    pub height: i32,
    pub tile_size: f64,
    pub padding: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tile_gap: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub camera: Option<SnapshotCamera>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pixel_art: Option<bool>,
    pub grid_overlay: bool,
    pub tick: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub atmosphere: Option<SnapshotAtmosphere>,
    pub tiles: Vec<Vec<SnapshotTile>>,
    pub npcs: Vec<SnapshotEntity>,
    pub player: SnapshotPlayer,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pops: Option<Vec<SnapshotPop>>,
}
impl Default for WorldSnapshot {
    fn default() -> Self {
        Self {
            width: 0,
            height: 0,
            tile_size: 0.0,
            padding: 0.0,
            tile_gap: None,
            camera: None,
            pixel_art: None,
            grid_overlay: false,
            tick: 0.0,
            atmosphere: None,
            tiles: Default::default(),
            npcs: Default::default(),
            player: Default::default(),
            pops: None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SnapshotCamera {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SnapshotAtmosphere {
    pub time_minutes: f64,
    pub weather_id: Option<String>,
    pub season: Option<String>,
    pub weather_overlay: Option<String>,
}
