//! Port of `Social.cs` (packages/engine-schemas/src/social.ts).
//! NPC relationships & gifting (M4d).

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GiftTastes {
    pub loved: Vec<String>,
    pub liked: Vec<String>,
    pub disliked: Vec<String>,
    pub hated: Vec<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

/// Per-NPC social state (friendship points etc.).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NpcSocialState {
    pub friendship: f64,
    /// int.
    pub gifts_today: f64,
    /// int.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_gift_day: Option<f64>,
}

/// TS `GiftReaction` (`keyof typeof GIFT_FRIENDSHIP_DELTAS`).
pub mod gift_reactions {
    pub const LOVED: &str = "loved";
    pub const LIKED: &str = "liked";
    pub const NEUTRAL: &str = "neutral";
    pub const DISLIKED: &str = "disliked";
    pub const HATED: &str = "hated";

    pub const ALL: &[&str] = &[LOVED, LIKED, NEUTRAL, DISLIKED, HATED];
}

pub const FRIENDSHIP_PER_HEART: f64 = 125.0;
pub const MAX_FRIENDSHIP: f64 = 1250.0;

/// TS `GIFT_FRIENDSHIP_DELTAS`, keyed by [`gift_reactions`] (declaration order kept).
pub const GIFT_FRIENDSHIP_DELTAS: &[(&str, f64)] = &[
    (gift_reactions::LOVED, 80.0),
    (gift_reactions::LIKED, 45.0),
    (gift_reactions::NEUTRAL, 20.0),
    (gift_reactions::DISLIKED, -20.0),
    (gift_reactions::HATED, -40.0),
];

/// Friendship delta for a gift reaction (`GIFT_FRIENDSHIP_DELTAS[reaction]`).
pub fn gift_friendship_delta(reaction: &str) -> Option<f64> {
    GIFT_FRIENDSHIP_DELTAS.iter().find(|(key, _)| *key == reaction).map(|(_, delta)| *delta)
}
