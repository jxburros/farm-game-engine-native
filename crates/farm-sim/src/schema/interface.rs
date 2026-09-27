//! Port of `Interface.cs` (packages/engine-schemas/src/interface.ts).

use serde::{Deserialize, Serialize};

/// TS `GamePanelSchema.entries[].kind` enum.
pub mod game_panel_entry_kinds {
    pub const TEXT: &str = "text";
    pub const MONEY: &str = "money";
    pub const ENERGY: &str = "energy";
    pub const DAY: &str = "day";
    pub const ITEM: &str = "item";
    pub const FLAG: &str = "flag";
    pub const ACTION: &str = "action";

    pub const ALL: &[&str] = &[TEXT, MONEY, ENERGY, DAY, ITEM, FLAG, ACTION];
}

/// TS `GamePanelSchema.entries` item (inline object).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GamePanelEntry {
    pub label: String,
    /// One of [`game_panel_entry_kinds`].
    pub kind: String,
    pub value: String,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GamePanel {
    pub id: String,
    pub title: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub visible_flag: Option<String>,
    /// At most 40 entries.
    pub entries: Vec<GamePanelEntry>,
}
