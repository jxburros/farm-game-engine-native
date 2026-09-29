//! Port of `Economy.cs` (packages/engine-schemas/src/economy.ts).

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

/// A single line in a shop's stock list.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ShopStockEntry {
    pub item_id: String,
    /// Override price; defaults to the item's base value.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::money::opt")]
    pub price: Option<i64>,
    /// Restrict availability to these seasons (empty/absent = always).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seasons: Option<Vec<String>>,
    /// Max units purchasable per in-game day (absent = unlimited). int, positive.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub daily_limit: Option<u32>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ShopDefinition {
    pub id: String,
    pub name: String,
    pub stock: Vec<ShopStockEntry>,
    /// Multiplier applied to item base value when the player sells here.
    #[serde(with = "crate::units::milli")]
    pub sell_price_multiplier: u32,
    /// Whether this shop buys player items at all.
    pub buys_items: bool,
    /// Whether this shop repairs broken tools (for durability * repairCostPerPoint).
    pub repairs_tools: bool,
    #[serde(with = "crate::units::milli")]
    pub repair_cost_per_point: u32,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for ShopDefinition {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            stock: Vec::new(),
            sell_price_multiplier: 1000,
            buys_items: true,
            repairs_tools: false,
            repair_cost_per_point: 500,
            extra: Map::new(),
        }
    }
}
