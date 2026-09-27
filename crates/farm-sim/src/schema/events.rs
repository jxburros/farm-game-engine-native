//! Port of `Events.cs` (packages/engine-schemas/src/events.ts).
//!
//! Event & trigger system (M3). Events belong to a scene (or '' = global), fire on a trigger
//! kind, gate on combinable conditions, and run sequenced outcomes. Fire-once semantics use an
//! auto-managed flag; repeatable is opt-in. Evaluation order is deterministic (content order).

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

fn one() -> f64 {
    1.0
}

fn default_true() -> bool {
    true
}

/// TS `EventConditionSchema` — discriminated union on `type`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all_fields = "camelCase")]
pub enum EventCondition {
    /// Player entered a tile (or region when x2/y2 present).
    #[serde(rename = "enterTile")]
    EnterTile {
        #[serde(default)]
        x: f64,
        #[serde(default)]
        y: f64,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        x2: Option<f64>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        y2: Option<f64>,
    },
    /// Player interacted while facing a tile (or region).
    #[serde(rename = "interactTile")]
    InteractTile {
        #[serde(default)]
        x: f64,
        #[serde(default)]
        y: f64,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        x2: Option<f64>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        y2: Option<f64>,
    },
    #[serde(rename = "hasItem")]
    HasItem {
        #[serde(default)]
        item_id: String,
        #[serde(default = "one")]
        quantity: f64,
    },
    #[serde(rename = "inventorySpace")]
    InventorySpace {
        #[serde(default)]
        item_id: String,
        /// int, positive.
        #[serde(default = "one")]
        quantity: f64,
    },
    #[serde(rename = "flag")]
    Flag {
        #[serde(default)]
        flag: String,
        #[serde(default = "default_true")]
        value: bool,
    },
    #[serde(rename = "dayRange")]
    DayRange {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        min_day: Option<f64>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        max_day: Option<f64>,
    },
    #[serde(rename = "season")]
    Season {
        #[serde(default)]
        seasons: Vec<String>,
    },
    #[serde(rename = "yearRange")]
    YearRange {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        min_year: Option<f64>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        max_year: Option<f64>,
    },
    #[serde(rename = "timeOfDay")]
    TimeOfDay {
        #[serde(default)]
        min_minute: f64,
        #[serde(default)]
        max_minute: f64,
    },
    #[serde(rename = "questStatus")]
    QuestStatus {
        #[serde(default)]
        quest_id: String,
        /// One of [`super::quest_statuses`].
        #[serde(default)]
        status: String,
    },
    /// Friendship threshold with an NPC (M4 heart events).
    #[serde(rename = "friendship")]
    Friendship {
        #[serde(default)]
        npc_id: String,
        #[serde(default)]
        min: f64,
    },
    /// Current weather (M4).
    #[serde(rename = "weather")]
    Weather {
        #[serde(default)]
        weather_ids: Vec<String>,
    },
    /// Today is the named festival (M9 calendar).
    #[serde(rename = "festivalId")]
    FestivalId {
        #[serde(default)]
        festival_id: String,
    },
}

impl EventCondition {
    /// The discriminator literal (TS `condition.type`).
    pub fn type_name(&self) -> &'static str {
        match self {
            Self::EnterTile { .. } => "enterTile",
            Self::InteractTile { .. } => "interactTile",
            Self::HasItem { .. } => "hasItem",
            Self::InventorySpace { .. } => "inventorySpace",
            Self::Flag { .. } => "flag",
            Self::DayRange { .. } => "dayRange",
            Self::Season { .. } => "season",
            Self::YearRange { .. } => "yearRange",
            Self::TimeOfDay { .. } => "timeOfDay",
            Self::QuestStatus { .. } => "questStatus",
            Self::Friendship { .. } => "friendship",
            Self::Weather { .. } => "weather",
            Self::FestivalId { .. } => "festivalId",
        }
    }
}

/// TS `EventOutcomeSchema.type` enum.
pub mod event_outcome_types {
    pub const MESSAGE: &str = "message";
    pub const MODIFY_FRIENDSHIP: &str = "modifyFriendship";
    pub const MODIFY_ENERGY: &str = "modifyEnergy";
    pub const WATER_AREA: &str = "waterArea";
    pub const GIVE_ITEM: &str = "giveItem";
    pub const TAKE_ITEM: &str = "takeItem";
    pub const GIVE_MONEY: &str = "giveMoney";
    pub const TAKE_MONEY: &str = "takeMoney";
    pub const SET_FLAG: &str = "setFlag";
    pub const CLEAR_FLAG: &str = "clearFlag";
    pub const START_QUEST: &str = "startQuest";
    pub const COMPLETE_QUEST: &str = "completeQuest";
    pub const SPAWN_NPC: &str = "spawnNPC";
    pub const REMOVE_NPC: &str = "removeNPC";
    pub const CHANGE_TILE: &str = "changeTile";
    pub const WARP_PLAYER: &str = "warpPlayer";
    pub const START_DIALOGUE: &str = "startDialogue";
    pub const LOCK_TRANSITION: &str = "lockTransition";
    pub const UNLOCK_TRANSITION: &str = "unlockTransition";
    pub const PLAY_SOUND: &str = "playSound";
    /// Run a creator-defined action (extensibility layer).
    pub const PERFORM_ACTION: &str = "performAction";
    /// Open a declared minigame; its score resolves via the command log.
    pub const START_MINIGAME: &str = "startMinigame";
    /// legacy (pre-v5), still honored by the migration
    pub const UNLOCK_SCENE: &str = "unlockScene";

    pub const ALL: &[&str] = &[
        MESSAGE,
        MODIFY_FRIENDSHIP,
        MODIFY_ENERGY,
        WATER_AREA,
        GIVE_ITEM,
        TAKE_ITEM,
        GIVE_MONEY,
        TAKE_MONEY,
        SET_FLAG,
        CLEAR_FLAG,
        START_QUEST,
        COMPLETE_QUEST,
        SPAWN_NPC,
        REMOVE_NPC,
        CHANGE_TILE,
        WARP_PLAYER,
        START_DIALOGUE,
        LOCK_TRANSITION,
        UNLOCK_TRANSITION,
        PLAY_SOUND,
        PERFORM_ACTION,
        START_MINIGAME,
        UNLOCK_SCENE,
    ];
}

/// A single event/action outcome. Not a discriminated union: one flat object whose `type`
/// selects which optional fields apply.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EventOutcome {
    /// One of [`event_outcome_types`].
    pub r#type: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_quantity: Option<f64>,
    /// finite.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub amount: Option<f64>,
    /// int, 0..10.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub radius: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub flag_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quest_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub npc_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dialogue_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tile_x: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tile_y: Option<f64>,
    /// One of [`super::tile_types`].
    #[serde(skip_serializing_if = "Option::is_none")]
    pub new_tile_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scene_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub x: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub y: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sound_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub action_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub minigame_id: Option<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// TS `EventTriggerSchema`.
pub mod event_triggers {
    pub const ENTER: &str = "enter";
    pub const INTERACT: &str = "interact";
    pub const TICK: &str = "tick";

    pub const ALL: &[&str] = &[ENTER, INTERACT, TICK];
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameEvent {
    pub id: String,
    pub name: String,
    /// Scene the event lives in; empty string = evaluated in every scene.
    pub scene_id: String,
    /// One of [`event_triggers`].
    pub trigger: String,
    pub conditions: Vec<EventCondition>,
    pub outcomes: Vec<EventOutcome>,
    pub active: bool,
    pub repeatable: bool,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// Flag used to record that a non-repeatable event has fired.
pub fn event_fired_flag(event_id: &str) -> String {
    format!("event:{event_id}:fired")
}
