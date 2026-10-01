//! The engine is a pure reducer over (state, content, command | tick) (port of `Engine.cs` /
//! engine.ts). Same seed + same command/tick log ⇒ same state.

use crate::commands::Command;
use crate::effects::{message_levels, Effect};
use crate::engine_types::{CommandRules, Effects, EngineContext};
use crate::farming::farming_actions;
use crate::game_time::SleepOptions;
use crate::hooks::{CommandHookPayload, HookEvent, RelationshipChangeHookPayload};
use crate::npcs::npc_movement;
use crate::schema::{EventOutcome, GameState, MoveIntent, NpcSocialState, OutcomeKind, PluginMutation, MAX_FRIENDSHIP};
use crate::units;
use crate::world::world_movement;
use crate::{
    crafting, dialogue_system, economy, energy, events, extensibility, game_time, inventory, mines, skills, social,
    weather,
};

/// Simulation runs at a fixed rate; rendering interpolates between ticks.
pub const TICKS_PER_SECOND: u32 = units::TICKS_PER_SECOND;

pub fn apply_command(ctx: &EngineContext, state: &mut GameState, command: &Command) -> Effects {
    if ctx.rules == CommandRules::Player {
        if let Some(refused) = refusal(ctx, state, command) {
            return refused;
        }
    }
    ctx.emit(HookEvent::Command(CommandHookPayload { command_type: command.type_name().to_owned() }));
    match command {
        Command::SetMoveIntent { dx, dy } => {
            // Read with Math.trunc; clamped to −1..1.
            let dx = (*dx).clamp(-1, 1);
            let dy = (*dy).clamp(-1, 1);
            let intent = MoveIntent { dx, dy };
            let direction = world_movement::direction_from_intent(&intent, &state.player.direction);
            state.player.move_intent = intent;
            state.player.direction = direction;
            vec![]
        }
        Command::Move { dir } => world_movement::handle_move(ctx, state, dir),
        Command::UseTool { tool } => farming_actions::handle_use_tool(ctx, state, tool),
        Command::Interact => farming_actions::handle_interact(ctx, state),
        Command::InteractWith { seed_item_id, fertilizer_item_id } => farming_actions::handle_interact_with(
            ctx,
            state,
            farming_actions::PlantChoice {
                seed_item_id: seed_item_id.as_deref(),
                fertilizer_item_id: fertilizer_item_id.as_deref(),
                chosen: true,
            },
        ),
        Command::ChooseDialogueOption { index } => dialogue_system::handle_choose_dialogue_option(ctx, state, *index),
        Command::CloseDialogue => dialogue_system::handle_close_dialogue(state),
        Command::Sleep => game_time::perform_sleep(ctx, state, SleepOptions { collapsed: false }),
        Command::OpenShop { shop_id } => economy::handle_open_shop(ctx, state, shop_id),
        Command::CloseShop => economy::handle_close_shop(state),
        Command::BuyItem { item_id, quantity } => economy::handle_buy_item(ctx, state, item_id, *quantity),
        Command::SellItem { item_id, quantity, quality } => {
            economy::handle_sell_item(ctx, state, item_id, *quantity, quality.as_deref())
        }
        Command::RepairTool { item_id } => economy::handle_repair_tool(ctx, state, item_id),
        Command::Craft { recipe_id } => crafting::handle_craft(ctx, state, recipe_id),
        Command::PlaceMachine { machine_type_id } => crafting::handle_place_machine(ctx, state, machine_type_id),
        Command::MachineLoad { recipe_id } => crafting::handle_machine_load(ctx, state, recipe_id),
        Command::PickUpMachine => crafting::handle_pick_up_machine(ctx, state),
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

/// Why `command` does not apply now under [`CommandRules::Player`] (`None` when it does): the
/// refusal's effects, often none. Nothing else of the command happens, not even its `onCommand`
/// hook.
fn refusal(ctx: &EngineContext, state: &GameState, command: &Command) -> Option<Effects> {
    // While a modal is open only its own commands apply (the player UI sends nothing else); the
    // held intent is stored but movement stays frozen, and plugin mutations are content.
    let dialogue = state.dialogue.is_some();
    let shop = state.shop.is_some();
    let minigame = state.minigame.is_some();
    if dialogue || shop || minigame {
        let allowed = match command {
            Command::SetMoveIntent { .. } | Command::PluginMutation { .. } => true,
            Command::ChooseDialogueOption { .. } | Command::CloseDialogue => dialogue,
            Command::BuyItem { .. } | Command::SellItem { .. } | Command::RepairTool { .. } | Command::CloseShop => {
                shop
            }
            Command::ResolveMinigame { .. } | Command::CancelMinigame => minigame,
            _ => false,
        };
        if !allowed {
            return Some(Vec::new());
        }
    }
    match command {
        Command::DescendMine { floor } => mines::descend_refusal(ctx, state, *floor),
        Command::ExitMine => mines::exit_refusal(state),
        Command::OpenShop { shop_id } => economy::open_shop_refusal(ctx, state, shop_id),
        // Minigames open from actions, items, tools and plugins, which decide what they are
        // for; opening one on demand would hand out its rewards for any score.
        Command::StartMinigame { .. } => Some(vec![Effect::message(message_levels::INFO, "Nothing to play here.")]),
        _ => None,
    }
}

/// A whole-gold plugin amount as an outcome `amount` (thousandths).
fn money_amount(gold: i64) -> i64 {
    gold.saturating_mul(i64::from(units::MILLI_ONE))
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
    // Mutations sharing the event-outcome executor (identical semantics to the equivalent
    // event/action outcome, including soft failure).
    let run_outcome = |state: &mut GameState, outcome: EventOutcome| events::apply_outcomes(ctx, state, &[outcome], 0);

    match mutation {
        PluginMutation::GiveItem { item_id, quantity } => {
            let Some(item) = ctx.item(item_id) else {
                return plugin_error(format!("unknown item '{item_id}'"));
            };
            let result =
                inventory::add_item(&state.player.inventory, item, *quantity, state.player.max_inventory_size, None);
            if !result.added {
                return vec![Effect::message(message_levels::INFO, "Inventory full!")];
            }
            state.player.inventory = result.inventory;
            vec![Effect::message(message_levels::INFO, format!("Received {quantity}× {}", item.name))]
        }
        PluginMutation::SetFlag { flag, value } => {
            // Canonical numbers (`1.0` is `1`) so the flag survives a save/load round trip (#142).
            state.flags.insert(flag.clone(), units::canonical_json(value.clone()));
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

        PluginMutation::TakeItem { item_id, quantity } => run_outcome(
            state,
            EventOutcome {
                item_id: Some(item_id.clone()),
                item_quantity: Some(*quantity),
                ..EventOutcome::of(OutcomeKind::TakeItem)
            },
        ),
        PluginMutation::GiveMoney { amount } => run_outcome(
            state,
            EventOutcome { amount: Some(money_amount(*amount)), ..EventOutcome::of(OutcomeKind::GiveMoney) },
        ),
        PluginMutation::TakeMoney { amount } => run_outcome(
            state,
            EventOutcome { amount: Some(money_amount(*amount)), ..EventOutcome::of(OutcomeKind::TakeMoney) },
        ),
        PluginMutation::StartQuest { quest_id } => run_outcome(
            state,
            EventOutcome { quest_id: Some(quest_id.clone()), ..EventOutcome::of(OutcomeKind::StartQuest) },
        ),
        PluginMutation::WarpPlayer { scene_id, x, y } => run_outcome(
            state,
            EventOutcome {
                scene_id: Some(scene_id.clone()),
                x: Some(*x),
                y: Some(*y),
                ..EventOutcome::of(OutcomeKind::WarpPlayer)
            },
        ),
        PluginMutation::StartDialogue { npc_id, dialogue_id } => run_outcome(
            state,
            EventOutcome {
                npc_id: Some(npc_id.clone()),
                dialogue_id: dialogue_id.clone(),
                ..EventOutcome::of(OutcomeKind::StartDialogue)
            },
        ),
        PluginMutation::PlaySound { sound_id } => run_outcome(
            state,
            EventOutcome { sound_id: Some(sound_id.clone()), ..EventOutcome::of(OutcomeKind::PlaySound) },
        ),

        PluginMutation::ModifyFriendship { npc_id, delta } => {
            if ctx.npc(npc_id).is_none() {
                return plugin_error(format!("unknown NPC '{npc_id}'"));
            }
            plugin_modify_friendship(ctx, state, npc_id, *delta)
        }

        PluginMutation::GrantXp { skill, amount } => skills::grant_xp(ctx, state, skill, *amount),

        PluginMutation::ModifyEnergy { delta } => plugin_modify_energy(ctx, state, *delta),

        PluginMutation::PerformAction { action_id } => events::perform_action(ctx, state, action_id).effects,
        PluginMutation::StartMinigame { minigame_id } => extensibility::handle_start_minigame(ctx, state, minigame_id),
    }
}

/// `modifyFriendship` from a plugin: whole points, clamped to the friendship range.
fn plugin_modify_friendship(ctx: &EngineContext, state: &mut GameState, npc_id: &str, delta: i32) -> Effects {
    let current = state.social.get(npc_id).cloned().unwrap_or(NpcSocialState {
        friendship: 0,
        gifts_today: 0,
        last_gift_day: None,
    });
    let friendship = current.friendship.saturating_add(delta).clamp(0, MAX_FRIENDSHIP);
    state.social.insert(npc_id.to_owned(), NpcSocialState { friendship, ..current });
    ctx.emit(HookEvent::RelationshipChange(RelationshipChangeHookPayload { npc_id: npc_id.to_owned(), friendship }));
    vec![]
}

/// `modifyEnergy` from a plugin: a loss spends energy (and can collapse the player), a gain
/// refills up to the maximum. Nothing happens with energy off.
fn plugin_modify_energy(ctx: &EngineContext, state: &mut GameState, delta: i32) -> Effects {
    if !ctx.content.settings.energy_enabled || delta == 0 {
        return vec![];
    }
    if delta < 0 {
        return energy::spend_energy(ctx, state, delta.saturating_neg()).effects;
    }
    state.player.energy = state.player.max_energy.min(state.player.energy.saturating_add(delta));
    vec![]
}

/// Advance simulation time by whole ticks. The game clock accrues in-game minutes; passing the
/// configured day end forces a collapse (the world moves on without you).
pub fn advance_tick(ctx: &EngineContext, state: &mut GameState, ticks: u64) -> Effects {
    // Ticks are processed one at a time so that advanceTick(N) is exactly equivalent to
    // N × advanceTick(1). The batched fast-path used to evaluate minute-boundary systems once per
    // *call*, which made live simulation frame-rate dependent (a slow frame delivering 3 ticks
    // skipped minute boundaries a fast machine would have hit).
    let mut effects = Vec::new();
    for _ in 0..ticks {
        effects.extend(advance_single_tick(ctx, state));
    }
    effects
}

/// Is a dialogue, shop or minigame open? The player's body stays put while one is, and with
/// `time.pauseInModals` the clock does too.
pub fn modal_open(state: &GameState) -> bool {
    state.dialogue.is_some() || state.shop.is_some() || state.minigame.is_some()
}

fn advance_single_tick(ctx: &EngineContext, state: &mut GameState) -> Effects {
    // An invalid day window would collapse the player every tick (#24).
    let time = game_time::time_config(&ctx.content.settings.time);
    state.clock.tick = state.clock.tick.saturating_add(1);
    let modal = modal_open(state);
    // With `pauseInModals` an open dialogue, shop or minigame stops the clock: no minutes pass,
    // so no minute-boundary systems run and the day cannot end mid-conversation (#37).
    if modal && time.pauses_in_modals() {
        return Vec::new();
    }
    // The time rate is stored in micro-minutes per tick.
    let before_minute = units::whole_minute(state.clock.time_minutes);
    state.clock.time_minutes = state.clock.time_minutes.saturating_add(time.minutes_per_real_second);
    let mut effects = Vec::new();

    // Free movement integrates every tick from the held intent. Frozen while a dialogue, shop or
    // minigame is open (modal interactions pause the body).
    if !modal {
        effects.extend(world_movement::integrate_movement(ctx, state));
    }

    // Minute boundary: NPC movement/schedules step and tick-events evaluate once per whole in-game
    // minute (bounds evaluation cost).
    let after_minute = units::whole_minute(state.clock.time_minutes);
    if after_minute > before_minute {
        advance_npcs_per_minute(ctx, state, before_minute, after_minute);
        crafting::settle_machines(ctx, state);
        effects.extend(events::evaluate_events(ctx, state, "tick", None));
    }

    if u64::from(state.clock.time_minutes) >= u64::from(time.day_end_minute) * u64::from(units::MINUTE) {
        effects.extend(game_time::perform_sleep(ctx, state, SleepOptions { collapsed: true }));
    }

    effects
}

/// NPCs take one step per whole minute that passed, each seeing the clock at that minute. At
/// more than one game minute per tick (`minutesPerRealSecond` above 20) one call per tick fell
/// behind and skipped the wander minutes (#37). The last step sees the clock as it is, so at
/// most one minute per tick (every shipped rate) nothing changes.
fn advance_npcs_per_minute(ctx: &EngineContext, state: &mut GameState, before_minute: u32, after_minute: u32) {
    let now = state.clock.time_minutes;
    for minute in before_minute + 1..after_minute {
        state.clock.time_minutes = minute.saturating_mul(units::MINUTE);
        npc_movement::advance_npcs(ctx, state, 1);
    }
    state.clock.time_minutes = now;
    npc_movement::advance_npcs(ctx, state, 1);
}
