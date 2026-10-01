//! Port of `Events.cs` (packages/engine-schemas/src/events.ts).
//!
//! Event & trigger system (M3). Events belong to a scene (or '' = global), fire on a trigger
//! kind, gate on combinable conditions, and run sequenced outcomes. Fire-once semantics use an
//! auto-managed flag; repeatable is opt-in. Evaluation order is deterministic (content order).

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

fn one() -> u32 {
    1
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
        #[serde(default, with = "crate::units::int")]
        x: i32,
        #[serde(default, with = "crate::units::int")]
        y: i32,
        #[serde(default, skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
        x2: Option<i32>,
        #[serde(default, skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
        y2: Option<i32>,
    },
    /// Player interacted while facing a tile (or region).
    #[serde(rename = "interactTile")]
    InteractTile {
        #[serde(default, with = "crate::units::int")]
        x: i32,
        #[serde(default, with = "crate::units::int")]
        y: i32,
        #[serde(default, skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
        x2: Option<i32>,
        #[serde(default, skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
        y2: Option<i32>,
    },
    #[serde(rename = "hasItem")]
    HasItem {
        #[serde(default)]
        item_id: String,
        #[serde(default = "one", with = "crate::units::count")]
        quantity: u32,
    },
    #[serde(rename = "inventorySpace")]
    InventorySpace {
        #[serde(default)]
        item_id: String,
        /// int, positive.
        #[serde(default = "one", with = "crate::units::count")]
        quantity: u32,
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
        #[serde(default, skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
        min_day: Option<u32>,
        #[serde(default, skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
        max_day: Option<u32>,
    },
    #[serde(rename = "season")]
    Season {
        #[serde(default)]
        seasons: Vec<String>,
    },
    #[serde(rename = "yearRange")]
    YearRange {
        #[serde(default, skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
        min_year: Option<u32>,
        #[serde(default, skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
        max_year: Option<u32>,
    },
    #[serde(rename = "timeOfDay")]
    TimeOfDay {
        #[serde(default, with = "crate::units::micro_minutes")]
        min_minute: u32,
        #[serde(default, with = "crate::units::micro_minutes")]
        max_minute: u32,
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
        #[serde(default, with = "crate::units::int")]
        min: i32,
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

/// The outcome types as an enum: [`event_outcome_types`] typed, so the engine dispatches on a
/// variant instead of comparing strings. The authored `type` stays a string in
/// [`EventOutcome`] (an unknown type round-trips and is reported by the authoring checks, then
/// does nothing at run time); [`EventOutcome::kind`] parses it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OutcomeKind {
    Message,
    ModifyFriendship,
    ModifyEnergy,
    WaterArea,
    GiveItem,
    TakeItem,
    GiveMoney,
    TakeMoney,
    SetFlag,
    ClearFlag,
    StartQuest,
    CompleteQuest,
    SpawnNpc,
    RemoveNpc,
    ChangeTile,
    WarpPlayer,
    StartDialogue,
    LockTransition,
    UnlockTransition,
    PlaySound,
    PerformAction,
    StartMinigame,
    UnlockScene,
}

impl OutcomeKind {
    /// Every kind, in [`event_outcome_types::ALL`] order.
    pub const ALL: [OutcomeKind; 23] = [
        Self::Message,
        Self::ModifyFriendship,
        Self::ModifyEnergy,
        Self::WaterArea,
        Self::GiveItem,
        Self::TakeItem,
        Self::GiveMoney,
        Self::TakeMoney,
        Self::SetFlag,
        Self::ClearFlag,
        Self::StartQuest,
        Self::CompleteQuest,
        Self::SpawnNpc,
        Self::RemoveNpc,
        Self::ChangeTile,
        Self::WarpPlayer,
        Self::StartDialogue,
        Self::LockTransition,
        Self::UnlockTransition,
        Self::PlaySound,
        Self::PerformAction,
        Self::StartMinigame,
        Self::UnlockScene,
    ];

    /// The authored `type` literal (one of [`event_outcome_types`]).
    pub fn name(self) -> &'static str {
        use event_outcome_types as t;
        match self {
            Self::Message => t::MESSAGE,
            Self::ModifyFriendship => t::MODIFY_FRIENDSHIP,
            Self::ModifyEnergy => t::MODIFY_ENERGY,
            Self::WaterArea => t::WATER_AREA,
            Self::GiveItem => t::GIVE_ITEM,
            Self::TakeItem => t::TAKE_ITEM,
            Self::GiveMoney => t::GIVE_MONEY,
            Self::TakeMoney => t::TAKE_MONEY,
            Self::SetFlag => t::SET_FLAG,
            Self::ClearFlag => t::CLEAR_FLAG,
            Self::StartQuest => t::START_QUEST,
            Self::CompleteQuest => t::COMPLETE_QUEST,
            Self::SpawnNpc => t::SPAWN_NPC,
            Self::RemoveNpc => t::REMOVE_NPC,
            Self::ChangeTile => t::CHANGE_TILE,
            Self::WarpPlayer => t::WARP_PLAYER,
            Self::StartDialogue => t::START_DIALOGUE,
            Self::LockTransition => t::LOCK_TRANSITION,
            Self::UnlockTransition => t::UNLOCK_TRANSITION,
            Self::PlaySound => t::PLAY_SOUND,
            Self::PerformAction => t::PERFORM_ACTION,
            Self::StartMinigame => t::START_MINIGAME,
            Self::UnlockScene => t::UNLOCK_SCENE,
        }
    }

    /// The kind an authored `type` names; `None` for an unknown type (case-sensitive, like the
    /// schema).
    pub fn parse(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|kind| kind.name() == name)
    }
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
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub item_quantity: Option<u32>,
    /// finite.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::signed_milli::opt")]
    pub amount: Option<i64>,
    /// int, 0..10.
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::count::opt")]
    pub radius: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub flag_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quest_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub npc_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dialogue_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
    pub tile_x: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
    pub tile_y: Option<i32>,
    /// One of [`super::tile_types`].
    #[serde(skip_serializing_if = "Option::is_none")]
    pub new_tile_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scene_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
    pub x: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none", with = "crate::units::int::opt")]
    pub y: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sound_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub action_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub minigame_id: Option<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl EventOutcome {
    /// An outcome of `kind` with no fields set (the plugin mutations that share the outcome
    /// executor build theirs this way).
    pub fn of(kind: OutcomeKind) -> Self {
        Self { r#type: kind.name().to_owned(), ..Self::default() }
    }

    /// The typed [`OutcomeKind`] of `type`; `None` when the type is unknown.
    pub fn kind(&self) -> Option<OutcomeKind> {
        OutcomeKind::parse(&self.r#type)
    }
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn outcome_kinds_cover_every_outcome_type_in_order() {
        let names: Vec<&str> = OutcomeKind::ALL.iter().map(|kind| kind.name()).collect();
        assert_eq!(names, event_outcome_types::ALL);
        for kind in OutcomeKind::ALL {
            assert_eq!(OutcomeKind::parse(kind.name()), Some(kind));
            assert_eq!(EventOutcome::of(kind).kind(), Some(kind));
        }
        assert_eq!(OutcomeKind::parse("givItem"), None);
        assert_eq!(OutcomeKind::parse("GiveItem"), None);
        assert_eq!(EventOutcome::default().kind(), None);
    }
}
