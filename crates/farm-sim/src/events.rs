//! Events, conditions, outcomes and creator actions (port of `Events.cs` / events.ts).
//!
//! Event & trigger runtime (M3). Deterministic: events evaluate in content order; fire-once is
//! tracked via an auto-managed flag; repeatable events opt in explicitly.

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::hooks::{ActionHookPayload, HookEvent, RelationshipChangeHookPayload};
use crate::schema::{
    event_fired_flag, DialogueState, EventCondition, EventOutcome, GameEvent, GameState, MinigameSession,
    NpcSocialState, NpcState, MAX_FRIENDSHIP,
};
use crate::text;
use crate::units;
use crate::world::{tiles, world_movement};
use crate::{energy, game_time, inventory, quests};
use indexmap::IndexMap;
use serde_json::Value;

/// TS `EventPosition`: the tile entered or interacted with.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct EventPosition {
    pub x: i32,
    pub y: i32,
}

/// TS `PerformActionResult` minus the state (updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PerformActionResult {
    pub effects: Effects,
    pub ran: bool,
}

/// Actions may perform actions; cap the chain so cycles terminate.
const MAX_ACTION_DEPTH: u32 = 4;

/// The most actions one top-level call (a command, a dialogue option, an event, a minigame tier,
/// a plugin mutation) may run in all. The depth cap alone bounds a chain, not its breadth: an
/// action that performs itself twelve times would run 22,621 times within four levels.
pub const MAX_ACTION_RUNS: u32 = 256;

/// What the action chain of one top-level call has used up.
#[derive(Debug, Default)]
struct ActionBudget {
    runs: u32,
    /// The "limit reached" warning was given (once per call).
    warned: bool,
}

impl ActionBudget {
    /// The warning creators see when a chain is cut short (once per top-level call).
    fn limit_reached(&mut self, action_id: &str) -> Effects {
        if self.warned {
            return Vec::new();
        }
        self.warned = true;
        vec![Effect::message(
            message_levels::ERROR,
            format!(
                "Action chain limit reached at '{action_id}': actions may perform actions {MAX_ACTION_DEPTH} levels deep and {MAX_ACTION_RUNS} times in all."
            ),
        )]
    }
}

const QUEST_STATUS_NOT_STARTED: &str = "not-started";

/// TS `x || y` on a string: an empty (or missing) string is falsy.
/// An outcome `amount` (thousandths) as a whole number: gold or friendship points.
fn whole(amount: i64) -> i64 {
    units::div_round(amount, i64::from(units::MILLI_ONE))
}

fn non_empty(value: Option<&str>) -> Option<&str> {
    value.filter(|text| !text.is_empty())
}

fn position_matches(x: i32, y: i32, x2: Option<i32>, y2: Option<i32>, pos: EventPosition) -> bool {
    let min_x = x.min(x2.unwrap_or(x));
    let max_x = x.max(x2.unwrap_or(x));
    let min_y = y.min(y2.unwrap_or(y));
    let max_y = y.max(y2.unwrap_or(y));
    pos.x >= min_x && pos.x <= max_x && pos.y >= min_y && pos.y <= max_y
}

pub fn condition_met(
    ctx: &EngineContext,
    state: &GameState,
    condition: &EventCondition,
    pos: Option<EventPosition>,
) -> bool {
    match condition {
        EventCondition::EnterTile { x, y, x2, y2 } => pos.is_some_and(|pos| position_matches(*x, *y, *x2, *y2, pos)),
        EventCondition::InteractTile { x, y, x2, y2 } => pos.is_some_and(|pos| position_matches(*x, *y, *x2, *y2, pos)),
        EventCondition::HasItem { item_id, quantity } => {
            let held = state
                .player
                .inventory
                .iter()
                .filter(|slot| slot.item.id == *item_id)
                .fold(0_u64, |sum, slot| sum + u64::from(slot.quantity));
            held >= u64::from(*quantity)
        }
        EventCondition::InventorySpace { item_id, quantity } => {
            let item = ctx.content.items.iter().find(|i| i.id == *item_id);
            item.is_some_and(|item| {
                inventory::add_item(&state.player.inventory, item, *quantity, state.player.max_inventory_size, None)
                    .added
            })
        }
        EventCondition::Flag { flag, value } => text::truthy(flag_value(state, flag)) == *value,
        EventCondition::DayRange { min_day, max_day } => {
            if min_day.is_some_and(|min_day| state.clock.day < min_day) {
                return false;
            }
            if max_day.is_some_and(|max_day| state.clock.day > max_day) {
                return false;
            }
            true
        }
        EventCondition::Season { seasons } => seasons.contains(&state.clock.season),
        EventCondition::YearRange { min_year, max_year } => {
            if min_year.is_some_and(|min_year| state.clock.year < min_year) {
                return false;
            }
            if max_year.is_some_and(|max_year| state.clock.year > max_year) {
                return false;
            }
            true
        }
        EventCondition::TimeOfDay { min_minute, max_minute } => {
            time_of_day_matches(state.clock.time_minutes, *min_minute, *max_minute)
        }
        EventCondition::QuestStatus { quest_id, status } => {
            state.quests.get(quest_id).map_or(QUEST_STATUS_NOT_STARTED, |progress| progress.status.as_str()) == status
        }
        EventCondition::Friendship { npc_id, min } => {
            state.social.get(npc_id).map_or(0, |social| social.friendship) >= *min
        }
        EventCondition::Weather { weather_ids } => weather_ids.contains(&state.clock.weather_id),
        EventCondition::FestivalId { festival_id } => {
            game_time::festival_today(&ctx.content.settings.calendar, &state.clock)
                .is_some_and(|festival| festival.id == *festival_id)
        }
    }
}

/// A `timeOfDay` range against the clock (all in micro-minutes). The clock counts on past
/// midnight until the day ends (26:00 by default) while the game shows 0:00–2:00, so a range
/// matches the clock as it counts or as it is shown: "0:00–2:00" matches 24:00–26:00. A range
/// whose start is after its end wraps past midnight: "22:00–2:00" matches 22:00 to 2:00 (#37).
pub fn time_of_day_matches(time: u32, min: u32, max: u32) -> bool {
    let day = units::MINUTES_PER_DAY * units::MINUTE;
    let shown = time % day;
    if min <= max {
        (min..=max).contains(&time) || (min..=max).contains(&shown)
    } else {
        shown >= min % day || shown <= max % day
    }
}

/// TS `state.flags[name]` (undefined → `None`).
pub fn flag_value<'a>(state: &'a GameState, name: &str) -> Option<&'a Value> {
    state.flags.get(name)
}

fn apply_outcome(
    ctx: &EngineContext,
    state: &mut GameState,
    outcome: &EventOutcome,
    depth: u32,
    budget: &mut ActionBudget,
) -> Effects {
    match outcome.r#type.as_str() {
        "message" => match non_empty(outcome.message.as_deref()) {
            Some(message) => vec![Effect::message(message_levels::INFO, message)],
            None => vec![],
        },

        "modifyFriendship" => {
            let Some(npc_id) = non_empty(outcome.npc_id.as_deref()) else { return vec![] };
            if !ctx.content.npcs.iter().any(|n| n.id == npc_id) {
                return vec![];
            }
            let current = state.social.get(npc_id).cloned().unwrap_or(NpcSocialState {
                friendship: 0,
                gifts_today: 0,
                last_gift_day: None,
            });
            // Friendship is whole points: the amount rounds.
            let delta = whole(outcome.amount.unwrap_or(0));
            let friendship = (i64::from(current.friendship) + delta).clamp(0, i64::from(MAX_FRIENDSHIP)) as i32;
            ctx.emit(HookEvent::RelationshipChange(RelationshipChangeHookPayload {
                npc_id: npc_id.to_owned(),
                friendship,
            }));
            state.social.insert(npc_id.to_owned(), NpcSocialState { friendship, ..current });
            vec![]
        }
        "modifyEnergy" => {
            // Thousandths of a point: the energy unit.
            let amount = i32::try_from(outcome.amount.unwrap_or(0)).unwrap_or(0);
            if !ctx.content.settings.energy_enabled {
                return vec![];
            }
            if amount < 0 {
                return energy::spend_energy(ctx, state, amount.saturating_neg()).effects;
            }
            state.player.energy = state.player.max_energy.min(state.player.energy.saturating_add(amount));
            vec![]
        }
        "waterArea" => {
            let radius = i64::from(outcome.radius.unwrap_or(1).min(10));
            let center = world_movement::player_tile(state);
            let (cx, cy) = (i64::from(center.x), i64::from(center.y));
            let day = state.clock.day;
            let scene_id = state.player.scene_id.clone();
            for scene in &mut state.world.scenes {
                if scene.id != scene_id {
                    continue;
                }
                for row in &mut scene.tiles {
                    for tile in row {
                        if (i64::from(tile.x) - cx).abs() > radius
                            || (i64::from(tile.y) - cy).abs() > radius
                            || tile.background != "soil"
                        {
                            continue;
                        }
                        tile.soil_state = Some("watered".to_owned());
                        tile.soil_moisture = 100;
                        if let Some(crop) = &mut tile.crop {
                            crop.watered = true;
                            crop.last_watered_day = Some(day);
                        }
                    }
                }
            }
            vec![Effect::message(message_levels::SUCCESS, "The surrounding soil is watered.")]
        }
        "giveItem" => {
            let item = outcome.item_id.as_deref().and_then(|id| ctx.content.items.iter().find(|i| i.id == id));
            let Some(item) = item else { return vec![] };
            let quantity = outcome.item_quantity.unwrap_or(1);
            let result =
                inventory::add_item(&state.player.inventory, item, quantity, state.player.max_inventory_size, None);
            if !result.added {
                return vec![Effect::message(message_levels::ERROR, "Inventory is full!")];
            }
            state.player.inventory = result.inventory;
            let suffix = if quantity > 1 { format!(" x{quantity}") } else { String::new() };
            vec![Effect::message(message_levels::SUCCESS, format!("Received {}{suffix}", item.name))]
        }

        "takeItem" => {
            let Some(item_id) = non_empty(outcome.item_id.as_deref()) else { return vec![] };
            state.player.inventory =
                inventory::remove_item(&state.player.inventory, item_id, outcome.item_quantity.unwrap_or(1));
            vec![]
        }

        "giveMoney" => {
            // Money is whole gold: the amount rounds.
            let amount = whole(outcome.amount.unwrap_or(0));
            if amount <= 0 {
                return vec![];
            }
            state.player.money = state.player.money.saturating_add(amount);
            vec![Effect::message(message_levels::SUCCESS, format!("Received ${amount}"))]
        }

        "takeMoney" => {
            let amount = whole(outcome.amount.unwrap_or(0)).min(state.player.money);
            if amount <= 0 {
                return vec![];
            }
            state.player.money -= amount;
            vec![Effect::message(message_levels::INFO, format!("Paid ${amount}"))]
        }

        "setFlag" => {
            let Some(flag_name) = non_empty(outcome.flag_name.as_deref()) else { return vec![] };
            state.flags.insert(flag_name.to_owned(), Value::Bool(true));
            vec![]
        }

        "clearFlag" => {
            let Some(flag_name) = non_empty(outcome.flag_name.as_deref()) else { return vec![] };
            state.flags.insert(flag_name.to_owned(), Value::Bool(false));
            vec![]
        }

        "startQuest" => {
            let Some(quest_id) = non_empty(outcome.quest_id.as_deref()) else { return vec![] };
            quests::start_quest_by_id(ctx, state, quest_id)
        }

        "completeQuest" => {
            let Some(quest_id) = non_empty(outcome.quest_id.as_deref()) else { return vec![] };
            quests::complete_quest_by_id(ctx, state, quest_id)
        }

        "spawnNPC" => {
            let Some(npc_id) = non_empty(outcome.npc_id.as_deref()) else { return vec![] };
            let Some(npc_def) = ctx.content.npcs.iter().find(|n| n.id == npc_id) else { return vec![] };
            let npc_state = NpcState {
                x: outcome.x.map_or(npc_def.x, units::tiles),
                y: outcome.y.map_or(npc_def.y, units::tiles),
                scene_id: outcome.scene_id.clone().unwrap_or_else(|| npc_def.scene_id.clone()),
                path: None,
                patrol_index: None,
            };
            state.npcs.insert(npc_id.to_owned(), npc_state);
            vec![]
        }

        "removeNPC" => {
            let Some(npc_id) = non_empty(outcome.npc_id.as_deref()) else { return vec![] };
            if !state.npcs.contains_key(npc_id) {
                return vec![];
            }
            state.npcs.shift_remove(npc_id);
            vec![]
        }

        "changeTile" => {
            let (Some(tile_x), Some(tile_y)) = (outcome.tile_x, outcome.tile_y) else { return vec![] };
            let Some(new_tile_type) = non_empty(outcome.new_tile_type.as_deref()) else { return vec![] };
            let scene_id = non_empty(outcome.scene_id.as_deref()).unwrap_or(&state.player.scene_id).to_owned();
            let Some(scene_index) = state.world.scenes.iter().position(|s| s.id == scene_id) else { return vec![] };
            let scene = &mut state.world.scenes[scene_index];
            if tile_y < 0 || tile_y >= scene.height || tile_x < 0 || tile_x >= scene.width {
                return vec![];
            }
            if let Some(tile) = scene.tiles.get_mut(tile_y as usize).and_then(|row| row.get_mut(tile_x as usize)) {
                *tile = tiles::set_tile_layer(tile, new_tile_type, None);
            }
            vec![]
        }

        "warpPlayer" => {
            let Some(scene_id) = non_empty(outcome.scene_id.as_deref()) else { return vec![] };
            let (Some(x), Some(y)) = (outcome.x, outcome.y) else { return vec![] };
            // A scene of an enabled content pack joins the world on first visit.
            if world_movement::ensure_scene(ctx, state, scene_id).is_none() {
                return vec![];
            }
            // Warp targets are authored as tile coordinates; land on the center, or on the
            // nearest walkable tile when the target is outside the scene or blocked.
            let (landed, landing_effects) = world_movement::land_player(ctx, state, scene_id, x, y);
            let mut effects = vec![Effect::SceneChanged { scene_id: scene_id.to_owned(), x: landed.x, y: landed.y }];
            effects.extend(landing_effects);
            effects
        }

        "startDialogue" => {
            let Some(npc_id) = non_empty(outcome.npc_id.as_deref()) else { return vec![] };
            let npc_def = ctx.content.npcs.iter().find(|n| n.id == npc_id);
            let dialogue_id = outcome
                .dialogue_id
                .clone()
                .or_else(|| npc_def.and_then(|npc| npc.dialogue.first()).map(|dialogue| dialogue.id.clone()));
            let Some(dialogue_id) = dialogue_id.filter(|id| !id.is_empty()) else { return vec![] };
            state.dialogue = Some(DialogueState { npc_id: npc_id.to_owned(), dialogue_id });
            vec![]
        }

        "lockTransition" | "unlockTransition" => {
            let scene_id = non_empty(outcome.scene_id.as_deref()).unwrap_or(&state.player.scene_id).to_owned();
            let Some(scene_index) = state.world.scenes.iter().position(|s| s.id == scene_id) else { return vec![] };
            let (Some(x), Some(y)) = (outcome.x, outcome.y) else { return vec![] };
            let locked = outcome.r#type == "lockTransition";
            for transition in &mut state.world.scenes[scene_index].transitions {
                if transition.from_x == x && transition.from_y == y {
                    transition.locked = Some(locked);
                }
            }
            vec![]
        }

        "playSound" => match non_empty(outcome.sound_id.as_deref()) {
            Some(sound_id) => vec![Effect::Sound { id: sound_id.to_owned() }],
            None => vec![],
        },

        "performAction" => {
            let Some(action_id) = non_empty(outcome.action_id.as_deref()) else { return vec![] };
            perform_action_detailed(ctx, state, action_id, depth + 1, budget).effects
        }

        "startMinigame" => {
            let Some(minigame_id) = non_empty(outcome.minigame_id.as_deref()) else { return vec![] };
            start_minigame_session(ctx, state, minigame_id, None)
        }

        "unlockScene" => {
            // Legacy outcome (pre-v5): unlock every transition that leads to the named scene. It
            // was authorable in the editor but a runtime no-op.
            let Some(scene_id) = non_empty(outcome.scene_id.as_deref()) else { return vec![] };
            for scene in &mut state.world.scenes {
                for transition in &mut scene.transitions {
                    if transition.to_scene_id == scene_id && transition.locked == Some(true) {
                        transition.locked = Some(false);
                    }
                }
            }
            vec![]
        }

        _ => vec![],
    }
}

/// Apply a sequence of outcomes (the shared executor behind events, custom actions, minigame
/// result tiers and plugin mutations). A top-level call: the actions its outcomes perform share
/// one [`MAX_ACTION_RUNS`] budget.
pub fn apply_outcomes(ctx: &EngineContext, state: &mut GameState, outcomes: &[EventOutcome], depth: u32) -> Effects {
    apply_outcomes_within(ctx, state, outcomes, depth, &mut ActionBudget::default())
}

fn apply_outcomes_within(
    ctx: &EngineContext,
    state: &mut GameState,
    outcomes: &[EventOutcome],
    depth: u32,
    budget: &mut ActionBudget,
) -> Effects {
    let mut effects = Vec::new();
    for outcome in outcomes {
        effects.extend(apply_outcome(ctx, state, outcome, depth, budget));
    }
    effects
}

/// Run a creator-defined action (extensibility layer): check its conditions, spend energy, apply
/// its outcomes, and notify plugins via `onAction`. The actions it performs in turn run at most
/// [`MAX_ACTION_DEPTH`] levels deep and [`MAX_ACTION_RUNS`] times in all; a chain cut short says
/// so with an error message.
pub fn perform_action(ctx: &EngineContext, state: &mut GameState, action_id: &str) -> PerformActionResult {
    perform_action_detailed(ctx, state, action_id, 0, &mut ActionBudget::default())
}

fn perform_action_detailed(
    ctx: &EngineContext,
    state: &mut GameState,
    action_id: &str,
    depth: u32,
    budget: &mut ActionBudget,
) -> PerformActionResult {
    if depth > MAX_ACTION_DEPTH || budget.runs >= MAX_ACTION_RUNS {
        return PerformActionResult { effects: budget.limit_reached(action_id), ran: false };
    }
    let Some(action) = ctx.content.actions.iter().find(|def| def.id == action_id) else {
        return PerformActionResult {
            effects: vec![Effect::message(message_levels::ERROR, format!("Unknown action '{action_id}'"))],
            ran: false,
        };
    };

    for condition in &action.conditions {
        if !condition_met(ctx, state, condition, None) {
            return PerformActionResult {
                effects: match non_empty(Some(&action.fail_message)) {
                    Some(fail_message) => vec![Effect::message(message_levels::INFO, fail_message)],
                    None => vec![],
                },
                ran: false,
            };
        }
    }

    budget.runs += 1;
    let mut effects = Vec::new();

    if action.energy_cost > 0 {
        let spend = energy::spend_energy(ctx, state, action.energy_cost);
        effects.extend(spend.effects);
        if spend.collapsed {
            return PerformActionResult { effects, ran: false };
        }
    }

    effects.extend(apply_outcomes_within(ctx, state, &action.outcomes, depth, budget));

    ctx.emit(HookEvent::Action(ActionHookPayload { action_id: action.id.clone() }));
    PerformActionResult { effects, ran: true }
}

/// Open a declared minigame session (modal, resolves via the command log).
pub fn start_minigame_session(
    ctx: &EngineContext,
    state: &mut GameState,
    minigame_id: &str,
    context: Option<&IndexMap<String, Value>>,
) -> Effects {
    if !ctx.content.minigames.iter().any(|def| def.id == minigame_id) {
        return vec![Effect::message(message_levels::ERROR, format!("Unknown minigame '{minigame_id}'"))];
    }
    if state.minigame.is_some() {
        return vec![];
    }
    state.minigame =
        Some(MinigameSession { minigame_id: minigame_id.to_owned(), context: context.cloned().unwrap_or_default() });
    vec![]
}

pub fn fire_event(ctx: &EngineContext, state: &mut GameState, event: &GameEvent) -> Effects {
    let effects = apply_outcomes(ctx, state, &event.outcomes, 0);
    if !event.repeatable {
        state.flags.insert(event_fired_flag(&event.id), Value::Bool(true));
    }
    effects
}

/// Evaluate all events for a trigger kind. `pos` is the tile entered or interacted with (for
/// position conditions).
pub fn evaluate_events(
    ctx: &EngineContext,
    state: &mut GameState,
    trigger: &str,
    pos: Option<EventPosition>,
) -> Effects {
    evaluate_events_detailed(ctx, state, trigger, pos).effects
}

/// What [`evaluate_events_detailed`] did.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct EvaluateEventsResult {
    pub effects: Effects,
    /// At least one event fired (TS: `evaluateEvents` returned a new state).
    pub fired: bool,
}

/// [`evaluate_events`], also saying whether any event fired.
pub fn evaluate_events_detailed(
    ctx: &EngineContext,
    state: &mut GameState,
    trigger: &str,
    pos: Option<EventPosition>,
) -> EvaluateEventsResult {
    let mut effects = Vec::new();
    let mut fired = false;

    for event in &ctx.content.events {
        if !event.active || event.trigger != trigger {
            continue;
        }
        if !event.scene_id.is_empty() && event.scene_id != state.player.scene_id {
            continue;
        }
        if !event.repeatable && text::truthy(flag_value(state, &event_fired_flag(&event.id))) {
            continue;
        }
        if !event.conditions.iter().all(|condition| condition_met(ctx, state, condition, pos)) {
            continue;
        }

        effects.extend(fire_event(ctx, state, event));
        fired = true;
    }

    EvaluateEventsResult { effects, fired }
}
