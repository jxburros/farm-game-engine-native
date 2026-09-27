//! The engine is a pure reducer over (state, content, command | tick) (port of `Engine.cs` /
//! engine.ts). Same seed + same command/tick log ⇒ same state.

use crate::commands::Command;
use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::farming::farming_actions;
use crate::game_time::SleepOptions;
use crate::hooks::{CommandHookPayload, HookEvent, RelationshipChangeHookPayload};
use crate::js;
use crate::npcs::npc_movement;
use crate::schema::{EventOutcome, GameState, MoveIntent, NpcSocialState, PluginMutation, MAX_FRIENDSHIP};
use crate::world::world_movement;
use crate::{
    crafting, dialogue_system, economy, energy, events, extensibility, game_time, inventory, mines, skills, social,
    weather,
};

/// Simulation runs at a fixed rate; rendering interpolates between ticks.
pub const TICKS_PER_SECOND: f64 = 20.0;
pub const MS_PER_TICK: f64 = 1000.0 / TICKS_PER_SECOND;

/// Clock quantum: game-minute values are kept on a 1e-6 grid. Per-tick float accumulation would
/// otherwise drift (24 000 × 0.05 ≠ 1200 in IEEE-754) and make minute/day boundaries land one
/// tick late. Rounding is deterministic, so this preserves the replay guarantee.
const MINUTE_QUANTUM: f64 = 1e6;

/// JS `Math.min`: NaN propagates (Rust's `f64::min` returns the other operand).
fn js_min(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else {
        a.min(b)
    }
}

/// JS `Math.max`: NaN propagates (Rust's `f64::max` returns the other operand).
fn js_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else {
        a.max(b)
    }
}

pub fn apply_command(ctx: &EngineContext, state: &mut GameState, command: &Command) -> Effects {
    ctx.emit(HookEvent::Command(CommandHookPayload { command_type: command.type_name().to_owned() }));
    match command {
        Command::SetMoveIntent { dx, dy } => {
            let dx = js_max(-1.0, js_min(1.0, js::trunc(*dx)));
            let dy = js_max(-1.0, js_min(1.0, js::trunc(*dy)));
            let intent = MoveIntent { dx, dy };
            let direction = world_movement::direction_from_intent(&intent, &state.player.direction);
            state.player.move_intent = intent;
            state.player.direction = direction;
            vec![]
        }
        Command::Move { dir } => world_movement::handle_move(ctx, state, dir),
        Command::UseTool { tool } => farming_actions::handle_use_tool(ctx, state, tool),
        Command::Interact => farming_actions::handle_interact(ctx, state),
        Command::ChooseDialogueOption { index } => dialogue_system::handle_choose_dialogue_option(ctx, state, *index),
        Command::CloseDialogue => dialogue_system::handle_close_dialogue(state),
        Command::Sleep => game_time::perform_sleep(ctx, state, SleepOptions { collapsed: false }),
        Command::OpenShop { shop_id } => economy::handle_open_shop(ctx, state, shop_id),
        Command::CloseShop => economy::handle_close_shop(state),
        Command::BuyItem { item_id, quantity } => economy::handle_buy_item(ctx, state, item_id, *quantity),
        Command::SellItem { item_id, quantity } => economy::handle_sell_item(ctx, state, item_id, *quantity),
        Command::RepairTool { item_id } => economy::handle_repair_tool(ctx, state, item_id),
        Command::Craft { recipe_id } => crafting::handle_craft(ctx, state, recipe_id),
        Command::PlaceMachine { machine_type_id } => crafting::handle_place_machine(ctx, state, machine_type_id),
        Command::MachineLoad { recipe_id } => crafting::handle_machine_load(ctx, state, recipe_id),
        Command::GiveGift { item_id } => social::handle_give_gift(ctx, state, item_id),
        Command::DescendMine { floor } => mines::descend_mine(ctx, state, *floor),
        Command::ExitMine => mines::exit_mine(ctx, state),
        Command::PerformAction { action_id } => events::perform_action(ctx, state, action_id).effects,
        Command::UseItem { item_id } => extensibility::handle_use_item(ctx, state, item_id),
        Command::StartMinigame { minigame_id } => extensibility::handle_start_minigame(ctx, state, minigame_id),
        Command::ResolveMinigame { score } => extensibility::handle_resolve_minigame(ctx, state, *score),
        Command::CancelMinigame => extensibility::handle_cancel_minigame(state),
        Command::PluginMutation { plugin_id, mutation } => apply_plugin_mutation(ctx, state, plugin_id, mutation),
    }
}

/// Apply a plugin-declared mutation (M5). Plugins run sandboxed and return these instead of
/// mutating state; each is validated by the schema layer before it becomes a command, and unknown
/// references fail soft here.
fn apply_plugin_mutation(
    ctx: &EngineContext,
    state: &mut GameState,
    plugin_id: &str,
    mutation: &PluginMutation,
) -> Effects {
    let plugin_error =
        |text: String| vec![Effect::message(message_levels::ERROR, format!("Plugin {plugin_id}: {text}"))];

    match mutation {
        PluginMutation::GiveItem { item_id, quantity } => {
            let Some(item) = ctx.content.items.iter().find(|entry| entry.id == *item_id) else {
                return plugin_error(format!("unknown item '{item_id}'"));
            };
            let result =
                inventory::add_item(&state.player.inventory, item, *quantity, state.player.max_inventory_size, None);
            if !result.added {
                return vec![Effect::message(message_levels::INFO, "Inventory full!")];
            }
            state.player.inventory = result.inventory;
            vec![Effect::message(message_levels::INFO, format!("Received {}× {}", js::num(*quantity), item.name))]
        }
        PluginMutation::SetFlag { flag, value } => {
            state.flags.insert(flag.clone(), value.clone());
            vec![]
        }
        PluginMutation::Message { text } => vec![Effect::message(message_levels::INFO, text.clone())],
        PluginMutation::SetWeather { weather_id } => {
            if weather::weather_type_by_id(ctx, weather_id).is_none() {
                return plugin_error(format!("unknown weather '{weather_id}'"));
            }
            state.clock.weather_id = weather_id.clone();
            vec![]
        }

        // Mutations sharing the event-outcome executor (identical semantics to the equivalent
        // event/action outcome, including soft failure).
        PluginMutation::TakeItem { item_id, quantity } => events::apply_outcomes(
            ctx,
            state,
            &[EventOutcome {
                r#type: "takeItem".to_owned(),
                item_id: Some(item_id.clone()),
                item_quantity: Some(*quantity),
                ..EventOutcome::default()
            }],
            0.0,
        ),
        PluginMutation::GiveMoney { amount } => events::apply_outcomes(
            ctx,
            state,
            &[EventOutcome { r#type: "giveMoney".to_owned(), amount: Some(*amount), ..EventOutcome::default() }],
            0.0,
        ),
        PluginMutation::TakeMoney { amount } => events::apply_outcomes(
            ctx,
            state,
            &[EventOutcome { r#type: "takeMoney".to_owned(), amount: Some(*amount), ..EventOutcome::default() }],
            0.0,
        ),
        PluginMutation::StartQuest { quest_id } => events::apply_outcomes(
            ctx,
            state,
            &[EventOutcome {
                r#type: "startQuest".to_owned(),
                quest_id: Some(quest_id.clone()),
                ..EventOutcome::default()
            }],
            0.0,
        ),
        PluginMutation::WarpPlayer { scene_id, x, y } => events::apply_outcomes(
            ctx,
            state,
            &[EventOutcome {
                r#type: "warpPlayer".to_owned(),
                scene_id: Some(scene_id.clone()),
                x: Some(*x),
                y: Some(*y),
                ..EventOutcome::default()
            }],
            0.0,
        ),
        PluginMutation::StartDialogue { npc_id, dialogue_id } => events::apply_outcomes(
            ctx,
            state,
            &[EventOutcome {
                r#type: "startDialogue".to_owned(),
                npc_id: Some(npc_id.clone()),
                dialogue_id: dialogue_id.clone(),
                ..EventOutcome::default()
            }],
            0.0,
        ),
        PluginMutation::PlaySound { sound_id } => events::apply_outcomes(
            ctx,
            state,
            &[EventOutcome {
                r#type: "playSound".to_owned(),
                sound_id: Some(sound_id.clone()),
                ..EventOutcome::default()
            }],
            0.0,
        ),

        PluginMutation::ModifyFriendship { npc_id, delta } => {
            if !ctx.content.npcs.iter().any(|npc| npc.id == *npc_id) {
                return plugin_error(format!("unknown NPC '{npc_id}'"));
            }
            let current = state.social.get(npc_id).cloned().unwrap_or(NpcSocialState {
                friendship: 0.0,
                gifts_today: 0.0,
                last_gift_day: None,
            });
            let friendship = js_max(0.0, js_min(MAX_FRIENDSHIP, current.friendship + delta));
            state.social.insert(npc_id.clone(), NpcSocialState { friendship, ..current });
            ctx.emit(HookEvent::RelationshipChange(RelationshipChangeHookPayload {
                npc_id: npc_id.clone(),
                friendship,
            }));
            vec![]
        }

        PluginMutation::GrantXp { skill, amount } => skills::grant_xp(ctx, state, skill, *amount),

        PluginMutation::ModifyEnergy { delta } => {
            if !ctx.content.settings.energy_enabled || *delta == 0.0 {
                return vec![];
            }
            if *delta < 0.0 {
                return energy::spend_energy(ctx, state, -delta).effects;
            }
            state.player.energy = js_min(state.player.max_energy, state.player.energy + delta);
            vec![]
        }

        PluginMutation::PerformAction { action_id } => events::perform_action(ctx, state, action_id).effects,
        PluginMutation::StartMinigame { minigame_id } => extensibility::handle_start_minigame(ctx, state, minigame_id),
    }
}

/// Advance simulation time by whole ticks. The game clock accrues in-game minutes; passing the
/// configured day end forces a collapse (the world moves on without you).
pub fn advance_tick(ctx: &EngineContext, state: &mut GameState, ticks: f64) -> Effects {
    if ticks <= 0.0 {
        return vec![];
    }

    // Ticks are processed one at a time so that advanceTick(N) is exactly equivalent to
    // N × advanceTick(1). The batched fast-path used to evaluate minute-boundary systems once per
    // *call*, which made live simulation frame-rate dependent (a slow frame delivering 3 ticks
    // skipped minute boundaries a fast machine would have hit).
    let mut effects = Vec::new();
    let mut i = 0.0;
    while i < ticks {
        effects.extend(advance_single_tick(ctx, state));
        i += 1.0;
    }
    effects
}

fn advance_single_tick(ctx: &EngineContext, state: &mut GameState) -> Effects {
    let settings = &ctx.content.settings;
    let minutes_per_tick = settings.time.minutes_per_real_second / TICKS_PER_SECOND;

    let before_minute = state.clock.time_minutes.floor();
    state.clock.tick += 1.0;
    state.clock.time_minutes =
        js::round((state.clock.time_minutes + minutes_per_tick) * MINUTE_QUANTUM) / MINUTE_QUANTUM;
    let mut effects = Vec::new();

    // Free movement integrates every tick from the held intent. Frozen while a dialogue, shop or
    // minigame is open (modal interactions pause the body).
    if state.dialogue.is_none() && state.shop.is_none() && state.minigame.is_none() {
        effects.extend(world_movement::integrate_movement(ctx, state, 1.0 / TICKS_PER_SECOND));
    }

    // Minute boundary: NPC movement/schedules step and tick-events evaluate once per whole in-game
    // minute (bounds evaluation cost).
    let after_minute = state.clock.time_minutes.floor();
    if after_minute > before_minute {
        npc_movement::advance_npcs(ctx, state, after_minute - before_minute);
        crafting::settle_machines(ctx, state);
        effects.extend(events::evaluate_events(ctx, state, "tick", None));
    }

    if state.clock.time_minutes >= settings.time.day_end_minute {
        effects.extend(game_time::perform_sleep(ctx, state, SleepOptions { collapsed: true }));
    }

    effects
}
