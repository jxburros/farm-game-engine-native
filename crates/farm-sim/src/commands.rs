//! Commands are the ONLY way player intent mutates game state (port of `Commands.cs` /
//! commands.ts). Determinism contract: same seed + same command log ⇒ same state. JSON shape is
//! identical to the TS union (`{"type":"move","dir":"up"}`).

use crate::schema::PluginMutation;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all_fields = "camelCase")]
pub enum Command {
    /// Free movement (v4): set the held movement intent (each axis −1/0/1).
    #[serde(rename = "setMoveIntent")]
    SetMoveIntent { dx: f64, dy: f64 },
    /// Discrete one-tile step — scripted movement / legacy primitive.
    #[serde(rename = "move")]
    Move { dir: String },
    #[serde(rename = "useTool")]
    UseTool { tool: String },
    #[serde(rename = "interact")]
    Interact,
    #[serde(rename = "chooseDialogueOption")]
    ChooseDialogueOption { index: f64 },
    #[serde(rename = "closeDialogue")]
    CloseDialogue,
    #[serde(rename = "sleep")]
    Sleep,
    #[serde(rename = "openShop")]
    OpenShop { shop_id: String },
    #[serde(rename = "closeShop")]
    CloseShop,
    #[serde(rename = "buyItem")]
    BuyItem { item_id: String, quantity: f64 },
    #[serde(rename = "sellItem")]
    SellItem { item_id: String, quantity: f64 },
    #[serde(rename = "repairTool")]
    RepairTool { item_id: String },
    #[serde(rename = "craft")]
    Craft { recipe_id: String },
    #[serde(rename = "placeMachine")]
    PlaceMachine { machine_type_id: String },
    #[serde(rename = "machineLoad")]
    MachineLoad { recipe_id: String },
    #[serde(rename = "giveGift")]
    GiveGift { item_id: String },
    #[serde(rename = "descendMine")]
    DescendMine { floor: f64 },
    #[serde(rename = "exitMine")]
    ExitMine,
    /// Run a creator-defined action (extensibility layer).
    #[serde(rename = "performAction")]
    PerformAction { action_id: String },
    /// Use an inventory item (runs its bound action).
    #[serde(rename = "useItem")]
    UseItem { item_id: String },
    #[serde(rename = "startMinigame")]
    StartMinigame { minigame_id: String },
    /// Resolve the open minigame with a score in [0, 1].
    #[serde(rename = "resolveMinigame")]
    ResolveMinigame { score: f64 },
    #[serde(rename = "cancelMinigame")]
    CancelMinigame,
    /// A validated mutation returned by a sandboxed plugin hook (M5).
    #[serde(rename = "pluginMutation")]
    PluginMutation { plugin_id: String, mutation: PluginMutation },
}

impl Command {
    /// The discriminator literal (TS `command.type`).
    pub fn type_name(&self) -> &'static str {
        match self {
            Self::SetMoveIntent { .. } => "setMoveIntent",
            Self::Move { .. } => "move",
            Self::UseTool { .. } => "useTool",
            Self::Interact => "interact",
            Self::ChooseDialogueOption { .. } => "chooseDialogueOption",
            Self::CloseDialogue => "closeDialogue",
            Self::Sleep => "sleep",
            Self::OpenShop { .. } => "openShop",
            Self::CloseShop => "closeShop",
            Self::BuyItem { .. } => "buyItem",
            Self::SellItem { .. } => "sellItem",
            Self::RepairTool { .. } => "repairTool",
            Self::Craft { .. } => "craft",
            Self::PlaceMachine { .. } => "placeMachine",
            Self::MachineLoad { .. } => "machineLoad",
            Self::GiveGift { .. } => "giveGift",
            Self::DescendMine { .. } => "descendMine",
            Self::ExitMine => "exitMine",
            Self::PerformAction { .. } => "performAction",
            Self::UseItem { .. } => "useItem",
            Self::StartMinigame { .. } => "startMinigame",
            Self::ResolveMinigame { .. } => "resolveMinigame",
            Self::CancelMinigame => "cancelMinigame",
            Self::PluginMutation { .. } => "pluginMutation",
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn round_trips_the_typescript_json_shape() {
        let command: Command = serde_json::from_value(json!({"type": "move", "dir": "up"})).unwrap();
        assert_eq!(command, Command::Move { dir: "up".to_owned() });
        assert_eq!(serde_json::to_value(&command).unwrap(), json!({"type": "move", "dir": "up"}));

        let unit: Command = serde_json::from_value(json!({"type": "interact"})).unwrap();
        assert_eq!(unit, Command::Interact);
        assert_eq!(serde_json::to_value(&unit).unwrap(), json!({"type": "interact"}));

        let nested: Command = serde_json::from_value(json!({
            "type": "pluginMutation",
            "pluginId": "morning-hum",
            "mutation": {"type": "giveItem", "itemId": "seed-wheat", "quantity": 2}
        }))
        .unwrap();
        assert_eq!(
            nested,
            Command::PluginMutation {
                plugin_id: "morning-hum".to_owned(),
                mutation: PluginMutation::GiveItem { item_id: "seed-wheat".to_owned(), quantity: 2.0 },
            }
        );
        assert_eq!(nested.type_name(), "pluginMutation");
    }
}
