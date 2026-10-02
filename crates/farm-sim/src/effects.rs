//! Effects are things the shell/renderer reacts to (port of `Effects.cs` / effects.ts). They
//! never mutate simulation state — they are outputs of a step, consumed by the host (toasts,
//! sounds, camera snaps, …).

use crate::messages::{Localized, Message};
use serde::{Deserialize, Serialize};

pub mod message_levels {
    pub const SUCCESS: &str = "success";
    pub const ERROR: &str = "error";
    pub const INFO: &str = "info";
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all_fields = "camelCase")]
pub enum Effect {
    /// `level` is one of [`message_levels`]. `text` is English (or the creator's own text);
    /// `localized` is the catalog message it was made from, for players that show another
    /// language. Serialization and equality skip `localized` (see [`crate::messages`]).
    #[serde(rename = "message")]
    Message {
        level: String,
        text: String,
        #[serde(skip)]
        localized: Localized,
    },
    #[serde(rename = "sceneChanged")]
    SceneChanged {
        scene_id: String,
        #[serde(with = "crate::units::int")]
        x: i32,
        #[serde(with = "crate::units::int")]
        y: i32,
    },
    #[serde(rename = "playerMoved")]
    PlayerMoved {
        #[serde(with = "crate::units::int")]
        x: i32,
        #[serde(with = "crate::units::int")]
        y: i32,
    },
    #[serde(rename = "sound")]
    Sound { id: String },
    #[serde(rename = "questCompleted")]
    QuestCompleted { quest_id: String },
    #[serde(rename = "cropHarvested")]
    CropHarvested {
        crop_type: String,
        #[serde(with = "crate::units::count")]
        quantity: u32,
    },
    #[serde(rename = "dayStarted")]
    DayStarted {
        #[serde(with = "crate::units::count")]
        day: u32,
        season: String,
        #[serde(with = "crate::units::count")]
        year: u32,
    },
}

impl Effect {
    /// TS `message(level, text)`: a message with text that is not engine text (an event's or a
    /// plugin's own message). Engine sentences use [`Effect::say`].
    pub fn message(level: &str, text: impl Into<String>) -> Self {
        Self::Message { level: level.to_owned(), text: text.into(), localized: Localized(None) }
    }

    /// A message from the catalog ([`crate::messages`]): its English text, plus the message
    /// itself for players that translate it.
    pub fn say(level: &str, message: impl Into<Message>) -> Self {
        let message = message.into();
        Self::Message { level: level.to_owned(), text: message.english(), localized: Localized(Some(message)) }
    }

    /// The discriminator literal (TS `effect.type`).
    pub fn type_name(&self) -> &'static str {
        match self {
            Self::Message { .. } => "message",
            Self::SceneChanged { .. } => "sceneChanged",
            Self::PlayerMoved { .. } => "playerMoved",
            Self::Sound { .. } => "sound",
            Self::QuestCompleted { .. } => "questCompleted",
            Self::CropHarvested { .. } => "cropHarvested",
            Self::DayStarted { .. } => "dayStarted",
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn serializes_like_the_typescript_union() {
        let effect = Effect::message(message_levels::INFO, "hi");
        assert_eq!(serde_json::to_value(&effect).unwrap(), json!({"type": "message", "level": "info", "text": "hi"}));
        let parsed: Effect =
            serde_json::from_value(json!({"type": "dayStarted", "day": 2, "season": "spring", "year": 1})).unwrap();
        assert_eq!(parsed, Effect::DayStarted { day: 2, season: "spring".to_owned(), year: 1 });
    }
}
