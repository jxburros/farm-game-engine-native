//! Port of `Extensibility.cs` (packages/engine-schemas/src/extensibility.ts).
//!
//! Creator-defined actions & minigames — the "customize anything" layer.
//!
//! An ACTION is a callable, condition-gated bundle of event outcomes plus a plugin hook
//! (`onAction`). Actions can be triggered from item use, dialogue options, event outcomes
//! (`performAction`), hotkeys, or plugin mutations — so creators add new verbs without engine
//! changes, and plugins attach arbitrary sandboxed logic to them.
//!
//! A MINIGAME is a declared interactive challenge. The simulation opens it (`state.minigame` set
//! via the `startMinigame` command/outcome), the host runs an implementation registered for its
//! `kind` (timing-bar built in; game code can register more), and the result re-enters the
//! deterministic command log as `resolveMinigame { score }`. Score-tiered outcomes and the
//! `onMinigameResolve` hook turn the score into game consequences.

use super::events::{EventCondition, EventOutcome};
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ActionDef {
    pub id: String,
    pub name: String,
    pub description: String,
    /// All must hold for the action to run (same vocabulary as events).
    pub conditions: Vec<EventCondition>,
    /// Shown when a condition fails; silent when empty.
    pub fail_message: String,
    /// Applied in order when the action runs (same vocabulary as events).
    pub outcomes: Vec<EventOutcome>,
    /// Energy spent on a successful run (respects the energy toggle). nonnegative.
    pub energy_cost: f64,
    /// Optional single-character play-mode hotkey (max length 1). Reserved gameplay keys
    /// (movement/tools/panels) are ignored by hosts.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hotkey: Option<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MinigameResultTier {
    /// Tier applies when score ≥ minScore; the highest matching tier wins. 0..1.
    pub min_score: f64,
    pub outcomes: Vec<EventOutcome>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MinigameDef {
    pub id: String,
    pub name: String,
    /// Implementation key looked up in the host's minigame registry ('timing-bar' ships with the
    /// engine; game code registers custom kinds). Unknown kinds fall back to a neutral confirm
    /// that scores 0.5.
    pub kind: String,
    /// Kind-specific tuning, passed verbatim to the implementation (`z.unknown()` values).
    pub config: IndexMap<String, Value>,
    /// Declarative consequences by score tier (highest matching minScore).
    pub result_tiers: Vec<MinigameResultTier>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Default for MinigameDef {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            kind: "timing-bar".to_owned(),
            config: IndexMap::new(),
            result_tiers: Vec::new(),
            extra: Map::new(),
        }
    }
}

/// Live minigame session in GameState. `context` carries deterministic resolution inputs (e.g.
/// the rod tier for the built-in fishing binding).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MinigameSession {
    pub minigame_id: String,
    /// Values are `string | number | boolean`.
    pub context: IndexMap<String, Value>,
}

/// Minigame id that, when defined in content, gates fishing on a cast.
pub const FISHING_MINIGAME_ID: &str = "fishing";
