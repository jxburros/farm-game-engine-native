//! Effects are things the shell/renderer reacts to (port of `Effects.cs` / effects.ts). They
//! never mutate simulation state — they are outputs of a step, consumed by the host (toasts,
//! sounds, camera snaps, …).

use serde::{Deserialize, Serialize};

pub mod message_levels {
    pub const SUCCESS: &str = "success";
    pub const ERROR: &str = "error";
    pub const INFO: &str = "info";
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all_fields = "camelCase")]
pub enum Effect {
    /// `level` is one of [`message_levels`].
    #[serde(rename = "message")]
    Message { level: String, text: String },
    #[serde(rename = "sceneChanged")]
    SceneChanged { scene_id: String, x: f64, y: f64 },
    #[serde(rename = "playerMoved")]
    PlayerMoved { x: f64, y: f64 },
    #[serde(rename = "sound")]
    Sound { id: String },
    #[serde(rename = "questCompleted")]
    QuestCompleted { quest_id: String },
    #[serde(rename = "cropHarvested")]
    CropHarvested { crop_type: String, quantity: f64 },
    #[serde(rename = "dayStarted")]
    DayStarted { day: f64, season: String, year: f64 },
}

impl Effect {
    /// TS `message(level, text)`.
    pub fn message(level: &str, text: impl Into<String>) -> Self {
        Self::Message { level: level.to_owned(), text: text.into() }
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
        assert_eq!(parsed, Effect::DayStarted { day: 2.0, season: "spring".to_owned(), year: 1.0 });
    }
}
