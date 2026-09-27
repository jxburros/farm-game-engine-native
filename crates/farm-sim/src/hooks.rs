//! Typed hook/event bus (port of `Hooks.cs` / hooks.ts). Internal systems are its first
//! consumers; it is also the public mod extension surface — hooks receive structured,
//! JSON-serializable payloads.
//!
//! Unlike the C# bus, which calls listeners in place, the Rust core is a reducer: emitted hook
//! events are collected on the bus and leave the step together with the effects (the FFI design
//! in docs/LANGUAGES.md sends "effects plus hook events out"). Plugin reactions come back as
//! `pluginMutation` commands. The one synchronous hook, `onWeatherRoll`, is answered through the
//! [`WeatherRollListener`] slot that the plugin host fills.

use serde::{Deserialize, Serialize};
use std::fmt;

/// Hook names (the `HookPayloads` keys in hooks.ts).
pub mod hook_names {
    pub const ON_DAY_START: &str = "onDayStart";
    pub const ON_DAY_END: &str = "onDayEnd";
    pub const ON_SEASON_CHANGE: &str = "onSeasonChange";
    pub const ON_YEAR_START: &str = "onYearStart";
    pub const ON_CROP_HARVEST: &str = "onCropHarvest";
    pub const ON_RESOURCE_GATHER: &str = "onResourceGather";
    pub const ON_NPC_INTERACT: &str = "onNPCInteract";
    pub const ON_RECIPE_CRAFT: &str = "onRecipeCraft";
    pub const ON_GIFT_GIVEN: &str = "onGiftGiven";
    pub const ON_RELATIONSHIP_CHANGE: &str = "onRelationshipChange";
    pub const ON_WEATHER_ROLL: &str = "onWeatherRoll";
    pub const ON_COMMAND: &str = "onCommand";
    pub const ON_EFFECT: &str = "onEffect";
    /// A creator-defined action ran (extensibility layer).
    pub const ON_ACTION: &str = "onAction";
    /// A minigame resolved with a score in [0, 1].
    pub const ON_MINIGAME_RESOLVE: &str = "onMinigameResolve";

    pub const ALL: &[&str] = &[
        ON_DAY_START,
        ON_DAY_END,
        ON_SEASON_CHANGE,
        ON_YEAR_START,
        ON_CROP_HARVEST,
        ON_RESOURCE_GATHER,
        ON_NPC_INTERACT,
        ON_RECIPE_CRAFT,
        ON_GIFT_GIVEN,
        ON_RELATIONSHIP_CHANGE,
        ON_WEATHER_ROLL,
        ON_COMMAND,
        ON_EFFECT,
        ON_ACTION,
        ON_MINIGAME_RESOLVE,
    ];
}

// Typed payloads — plain records so plugins receive the same JSON the TS engine sends.

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DayHookPayload {
    pub day: f64,
    pub season: String,
    pub year: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SeasonChangeHookPayload {
    pub season: String,
    pub previous_season: String,
    pub year: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct YearStartHookPayload {
    pub year: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CropHarvestHookPayload {
    pub crop_type: String,
    pub quantity: f64,
    pub quality: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GatherDrop {
    pub item_id: String,
    pub quantity: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceGatherHookPayload {
    pub node_type_id: String,
    pub drops: Vec<GatherDrop>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NpcInteractHookPayload {
    pub npc_id: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecipeCraftHookPayload {
    pub recipe_id: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GiftGivenHookPayload {
    pub npc_id: String,
    pub item_id: String,
    pub reaction: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RelationshipChangeHookPayload {
    pub npc_id: String,
    pub friendship: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WeatherRollHookPayload {
    pub weather_id: String,
    pub day: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandHookPayload {
    pub command_type: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EffectHookPayload {
    pub effect_type: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActionHookPayload {
    pub action_id: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MinigameResolveHookPayload {
    pub minigame_id: String,
    pub score: f64,
}

/// One emitted hook: the hook name plus its typed payload, as `{"hook": …, "payload": …}`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "hook", content = "payload")]
pub enum HookEvent {
    #[serde(rename = "onDayStart")]
    DayStart(DayHookPayload),
    #[serde(rename = "onDayEnd")]
    DayEnd(DayHookPayload),
    #[serde(rename = "onSeasonChange")]
    SeasonChange(SeasonChangeHookPayload),
    #[serde(rename = "onYearStart")]
    YearStart(YearStartHookPayload),
    #[serde(rename = "onCropHarvest")]
    CropHarvest(CropHarvestHookPayload),
    #[serde(rename = "onResourceGather")]
    ResourceGather(ResourceGatherHookPayload),
    #[serde(rename = "onNPCInteract")]
    NpcInteract(NpcInteractHookPayload),
    #[serde(rename = "onRecipeCraft")]
    RecipeCraft(RecipeCraftHookPayload),
    #[serde(rename = "onGiftGiven")]
    GiftGiven(GiftGivenHookPayload),
    #[serde(rename = "onRelationshipChange")]
    RelationshipChange(RelationshipChangeHookPayload),
    #[serde(rename = "onWeatherRoll")]
    WeatherRoll(WeatherRollHookPayload),
    #[serde(rename = "onCommand")]
    Command(CommandHookPayload),
    #[serde(rename = "onEffect")]
    Effect(EffectHookPayload),
    #[serde(rename = "onAction")]
    Action(ActionHookPayload),
    #[serde(rename = "onMinigameResolve")]
    MinigameResolve(MinigameResolveHookPayload),
}

impl HookEvent {
    /// The hook name this event was emitted on (one of [`hook_names`]).
    pub fn hook(&self) -> &'static str {
        match self {
            Self::DayStart(_) => hook_names::ON_DAY_START,
            Self::DayEnd(_) => hook_names::ON_DAY_END,
            Self::SeasonChange(_) => hook_names::ON_SEASON_CHANGE,
            Self::YearStart(_) => hook_names::ON_YEAR_START,
            Self::CropHarvest(_) => hook_names::ON_CROP_HARVEST,
            Self::ResourceGather(_) => hook_names::ON_RESOURCE_GATHER,
            Self::NpcInteract(_) => hook_names::ON_NPC_INTERACT,
            Self::RecipeCraft(_) => hook_names::ON_RECIPE_CRAFT,
            Self::GiftGiven(_) => hook_names::ON_GIFT_GIVEN,
            Self::RelationshipChange(_) => hook_names::ON_RELATIONSHIP_CHANGE,
            Self::WeatherRoll(_) => hook_names::ON_WEATHER_ROLL,
            Self::Command(_) => hook_names::ON_COMMAND,
            Self::Effect(_) => hook_names::ON_EFFECT,
            Self::Action(_) => hook_names::ON_ACTION,
            Self::MinigameResolve(_) => hook_names::ON_MINIGAME_RESOLVE,
        }
    }
}

/// The synchronous `onWeatherRoll` listener (hooks.ts: a listener may return `{ weatherId }` to
/// override the roll). The plugin host answers for every subscribed plugin, in subscription
/// order; the caller keeps the last override naming a known weather type.
pub trait WeatherRollListener {
    /// Override weather ids, one per responding listener, in subscription order.
    fn on_weather_roll(&mut self, payload: &WeatherRollHookPayload) -> Vec<String>;
}

/// Collects the hook events a step emits, and holds the one synchronous listener slot.
#[derive(Default)]
pub struct HookBus {
    events: Vec<HookEvent>,
    weather_roll: Option<Box<dyn WeatherRollListener>>,
}

impl fmt::Debug for HookBus {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("HookBus")
            .field("events", &self.events)
            .field("weather_roll", &self.weather_roll.as_ref().map(|_| "WeatherRollListener"))
            .finish()
    }
}

impl HookBus {
    pub fn new() -> Self {
        Self::default()
    }

    /// Install the plugin host's synchronous `onWeatherRoll` answer.
    pub fn set_weather_roll_listener(&mut self, listener: Box<dyn WeatherRollListener>) {
        self.weather_roll = Some(listener);
    }

    /// Record an emitted hook (TS `emit`). Listener reactions arrive later as commands.
    pub fn emit(&mut self, event: HookEvent) {
        self.events.push(event);
    }

    /// Emit `onWeatherRoll` and gather the override answers in subscription order (TS `collect`).
    pub fn collect_weather_roll(&mut self, payload: WeatherRollHookPayload) -> Vec<String> {
        let responses = match self.weather_roll.as_mut() {
            Some(listener) => listener.on_weather_roll(&payload),
            None => Vec::new(),
        };
        self.events.push(HookEvent::WeatherRoll(payload));
        responses
    }

    /// The events emitted so far, in order.
    pub fn events(&self) -> &[HookEvent] {
        &self.events
    }

    /// Take the emitted events out of the bus (the host reads them after each step).
    pub fn drain(&mut self) -> Vec<HookEvent> {
        std::mem::take(&mut self.events)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    struct Reroll(&'static str);

    impl WeatherRollListener for Reroll {
        fn on_weather_roll(&mut self, _payload: &WeatherRollHookPayload) -> Vec<String> {
            vec![self.0.to_owned()]
        }
    }

    #[test]
    fn collects_events_and_answers_weather_rolls() {
        let mut bus = HookBus::new();
        bus.emit(HookEvent::DayStart(DayHookPayload { day: 2.0, season: "spring".to_owned(), year: 1.0 }));
        assert!(bus.collect_weather_roll(WeatherRollHookPayload { weather_id: "sun".to_owned(), day: 2.0 }).is_empty());
        bus.set_weather_roll_listener(Box::new(Reroll("rain")));
        assert_eq!(
            bus.collect_weather_roll(WeatherRollHookPayload { weather_id: "sun".to_owned(), day: 3.0 }),
            vec!["rain".to_owned()]
        );
        let events = bus.drain();
        assert_eq!(events.len(), 3);
        assert_eq!(events[0].hook(), hook_names::ON_DAY_START);
        assert_eq!(
            crate::stable_json::stringify(&events[2]),
            crate::stable_json::stringify_value(
                &json!({"hook": "onWeatherRoll", "payload": {"weatherId": "sun", "day": 3}})
            )
        );
        assert!(bus.events().is_empty());
    }
}
