//! Port of `Animals.cs` (packages/engine-schemas/src/animals.ts).
//! Animals & ranching (M4c).

use super::graphics::VisualRef;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AnimalSpeciesDefinition {
    pub id: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub visual: Option<VisualRef>,
    /// nonnegative.
    pub purchase_cost: f64,
    /// Item consumed daily; absent = grazes for free.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub feed_item_id: Option<String>,
    pub product_item_id: String,
    /// Days between products (when fed and adult). int, positive.
    pub product_interval_days: f64,
    /// int, nonnegative.
    pub days_to_adult: f64,
    /// Renderer hint.
    pub color: String,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for AnimalSpeciesDefinition {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            visual: None,
            purchase_cost: 0.0,
            feed_item_id: None,
            product_item_id: String::new(),
            product_interval_days: 1.0,
            days_to_adult: 3.0,
            color: "#e8d8c3".to_owned(),
            extra: Map::new(),
        }
    }
}

/// A live animal (persisted per project/save).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AnimalState {
    pub id: String,
    pub species_id: String,
    pub name: String,
    pub scene_id: String,
    /// int.
    pub x: f64,
    /// int.
    pub y: f64,
    /// 0..100; fed & petted raise it, neglect lowers it.
    pub mood: f64,
    pub fed_today: bool,
    pub petted_today: bool,
    /// int.
    pub age_days: f64,
    /// int.
    pub days_since_product: f64,
    /// Product waiting to be collected.
    pub product_ready: bool,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for AnimalState {
    fn default() -> Self {
        Self {
            id: String::new(),
            species_id: String::new(),
            name: String::new(),
            scene_id: String::new(),
            x: 0.0,
            y: 0.0,
            mood: 70.0,
            fed_today: false,
            petted_today: false,
            age_days: 0.0,
            days_since_product: 0.0,
            product_ready: false,
            extra: Map::new(),
        }
    }
}
