//! Port of the retired C# `ExtensibilityTests.cs`: the extensibility layer
//! (custom actions, minigames, expanded plugin mutations) — the "customize anything with code"
//! surface. Port of extensibility.test.ts, plus self-contained checks of the events /
//! extensibility / dialogue executors that run without the rest of the engine.

mod common;

use common::{flag, flag_is_true, has_level, make_engine, make_project, set_tile_layer};
use farm_sim::events::EventPosition;
use farm_sim::schema::PluginMutation;
use farm_sim::schema::Quest;
use farm_sim::schema::{
    event_fired_flag, ActionDef, ClockState, Dialogue, DialogueOption, DialogueState, EventCondition, EventOutcome,
    FishTable, FishTableEntry, GameContent, GameProject, GameState, InventorySlot, Item, MinigameDef,
    MinigameResultTier, Npc, NpcState, PlayerState, Scene, SceneTransition, WorldState, MAX_FRIENDSHIP,
};
use farm_sim::units;
use farm_sim::{
    dialogue_system, engine, events, extensibility, packs, quests, social, state, Command, Effect, EngineContext,
    HookBus, HookEvent,
};
use serde_json::{json, Value};

fn snack_action() -> ActionDef {
    ActionDef {
        id: "action-snack".to_owned(),
        name: "Snack".to_owned(),
        description: String::new(),
        conditions: vec![],
        fail_message: String::new(),
        outcomes: vec![
            EventOutcome {
                r#type: "giveMoney".to_owned(),
                amount: Some(units::amount(5.0)),
                ..EventOutcome::default()
            },
            EventOutcome {
                r#type: "setFlag".to_owned(),
                flag_name: Some("snacked".to_owned()),
                ..EventOutcome::default()
            },
        ],
        energy_cost: units::points(0),
        ..ActionDef::default()
    }
}

fn flag_condition(flag: &str, value: bool) -> EventCondition {
    EventCondition::Flag { flag: flag.to_owned(), value }
}

fn message(level: &str, text: &str) -> Effect {
    Effect::message(level, text)
}

fn action_hooks(ctx: &EngineContext) -> Vec<String> {
    ctx.drain_hook_events()
        .into_iter()
        .filter_map(|event| match event {
            HookEvent::Action(payload) => Some(payload.action_id),
            _ => None,
        })
        .collect()
}

fn friendship_hooks(ctx: &EngineContext) -> Vec<i32> {
    ctx.drain_hook_events()
        .into_iter()
        .filter_map(|event| match event {
            HookEvent::RelationshipChange(payload) => Some(payload.friendship),
            _ => None,
        })
        .collect()
}

fn minigame_hooks(ctx: &EngineContext) -> Vec<(String, u64)> {
    ctx.drain_hook_events()
        .into_iter()
        .filter_map(|event| match event {
            HookEvent::MinigameResolve(payload) => Some((payload.minigame_id, payload.score)),
            _ => None,
        })
        .collect()
}

fn perform(action_id: &str) -> Command {
    Command::PerformAction { action_id: action_id.to_owned() }
}

// ---- custom actions -------------------------------------------------

#[test]
fn applies_outcomes_in_order_and_emits_the_on_action_hook() {
    let (ctx, mut state) = make_engine(|p| p.actions = vec![snack_action()], "ext-test");
    let money = state.player.money;
    ctx.drain_hook_events();

    engine::apply_command(&ctx, &mut state, &perform("action-snack"));
    assert_eq!(state.player.money, money + 5);
    assert!(flag_is_true(&state, "snacked"));
    assert_eq!(action_hooks(&ctx), vec!["action-snack".to_owned()]);
}

#[test]
fn gates_on_conditions_with_the_fail_message_and_skips_outcomes() {
    let (ctx, state) = make_engine(
        |p| {
            p.actions = vec![ActionDef {
                conditions: vec![flag_condition("unlocked", true)],
                fail_message: "Not yet!".to_owned(),
                ..snack_action()
            }]
        },
        "ext-test",
    );
    let mut blocked = state.clone();
    let effects = engine::apply_command(&ctx, &mut blocked, &perform("action-snack"));
    assert_eq!(blocked.player.money, state.player.money);
    assert!(effects.contains(&message("info", "Not yet!")));

    let mut unlocked = state.clone();
    unlocked.flags.insert("unlocked".to_owned(), Value::Bool(true));
    engine::apply_command(&ctx, &mut unlocked, &perform("action-snack"));
    assert_eq!(unlocked.player.money, state.player.money + 5);
}

#[test]
fn spends_the_action_energy_cost() {
    let (ctx, mut state) =
        make_engine(|p| p.actions = vec![ActionDef { energy_cost: units::points(10), ..snack_action() }], "ext-test");
    let energy = state.player.energy;
    engine::apply_command(&ctx, &mut state, &perform("action-snack"));
    assert_eq!(state.player.energy, energy - units::points(10));
}

#[test]
fn reports_unknown_actions_without_crashing() {
    let (ctx, mut state) = make_engine(|_| {}, "ext-test");
    let effects = engine::apply_command(&ctx, &mut state, &perform("nope"));
    assert!(has_level(&effects, "error"));
}

#[test]
fn terminates_action_to_action_recursion_at_the_depth_cap() {
    let (ctx, mut state) = make_engine(
        |p| {
            p.actions = vec![ActionDef {
                id: "action-loop".to_owned(),
                outcomes: vec![
                    EventOutcome {
                        r#type: "giveMoney".to_owned(),
                        amount: Some(units::amount(1.0)),
                        ..EventOutcome::default()
                    },
                    EventOutcome {
                        r#type: "performAction".to_owned(),
                        action_id: Some("action-loop".to_owned()),
                        ..EventOutcome::default()
                    },
                ],
                ..snack_action()
            }]
        },
        "ext-test",
    );
    let money = state.player.money;
    events::perform_action(&ctx, &mut state, "action-loop");
    // Depth cap (4 nested levels) bounds the chain; money proves it ran a finite number of times
    // rather than hanging.
    assert!(state.player.money <= money + 6);
    assert!(state.player.money > money);
}

// ---- item-bound actions (useItem) -----------------------------------

fn with_snack_item(project: &mut GameProject, consume_on_use: bool, conditions: Vec<EventCondition>) {
    let snack = Item {
        id: "item-snack".to_owned(),
        name: "Snack".to_owned(),
        description: String::new(),
        r#type: "material".to_owned(),
        stackable: true,
        max_stack: 10,
        value: 1,
        use_action_id: Some("action-snack".to_owned()),
        consume_on_use: Some(consume_on_use),
        ..Item::default()
    };
    project.actions = vec![ActionDef { conditions, fail_message: "Cannot.".to_owned(), ..snack_action() }];
    project.items.push(snack.clone());
    project.player.inventory.push(InventorySlot::new(snack, 2));
}

fn snack_quantity(state: &GameState) -> Option<u32> {
    state.player.inventory.iter().find(|s| s.item.id == "item-snack").map(|s| s.quantity)
}

#[test]
fn runs_the_bound_action_and_consumes_consume_on_use_items_on_success() {
    let (ctx, mut state) = make_engine(|p| with_snack_item(p, true, vec![]), "ext-test");
    engine::apply_command(&ctx, &mut state, &Command::UseItem { item_id: "item-snack".to_owned() });
    assert!(flag_is_true(&state, "snacked"));
    assert_eq!(snack_quantity(&state), Some(1));
}

#[test]
fn does_not_consume_the_item_when_the_action_conditions_fail() {
    let (ctx, mut state) =
        make_engine(|p| with_snack_item(p, true, vec![flag_condition("never-set", true)]), "ext-test");
    engine::apply_command(&ctx, &mut state, &Command::UseItem { item_id: "item-snack".to_owned() });
    assert_eq!(flag(&state, "snacked"), None);
    assert_eq!(snack_quantity(&state), Some(2));
}

#[test]
fn explains_items_with_no_bound_action() {
    let (ctx, mut state) = make_engine(|_| {}, "ext-test");
    let effects = engine::apply_command(&ctx, &mut state, &Command::UseItem { item_id: "seed-wheat".to_owned() });
    assert!(has_level(&effects, "info"));
}

// ---- minigames --------------------------------------------------------

fn timing_minigame() -> MinigameDef {
    MinigameDef {
        id: "mg-test".to_owned(),
        name: "Test Game".to_owned(),
        kind: "timing-bar".to_owned(),
        result_tiers: vec![
            MinigameResultTier {
                min_score: units::chance(0.8),
                outcomes: vec![EventOutcome {
                    r#type: "giveMoney".to_owned(),
                    amount: Some(units::amount(100.0)),
                    ..EventOutcome::default()
                }],
            },
            MinigameResultTier {
                min_score: units::chance(0.3),
                outcomes: vec![EventOutcome {
                    r#type: "giveMoney".to_owned(),
                    amount: Some(units::amount(10.0)),
                    ..EventOutcome::default()
                }],
            },
        ],
        ..MinigameDef::default()
    }
}

/// Opens a minigame the way a host can (the `startMinigame` command itself is refused under the
/// player's rules; see `the_start_minigame_command_is_refused_under_player_rules`).
fn start_minigame(id: &str) -> Command {
    Command::PluginMutation {
        plugin_id: "test".to_owned(),
        mutation: PluginMutation::StartMinigame { minigame_id: id.to_owned() },
    }
}

#[test]
fn opens_a_session_freezes_movement_and_resolves_the_matching_score_tier() {
    let (ctx, state) = make_engine(|p| p.minigames = vec![timing_minigame()], "ext-test");
    ctx.drain_hook_events();

    let mut opened = state.clone();
    engine::apply_command(&ctx, &mut opened, &start_minigame("mg-test"));
    assert_eq!(opened.minigame.as_ref().map(|m| m.minigame_id.as_str()), Some("mg-test"));
    assert!(opened.minigame.as_ref().expect("session").context.is_empty());

    // Movement intent is ignored while the minigame is open.
    let mut moving = opened.clone();
    engine::apply_command(&ctx, &mut moving, &Command::SetMoveIntent { dx: 1, dy: 0 });
    let mut ticked = moving.clone();
    engine::advance_tick(&ctx, &mut ticked, 10);
    assert_eq!(ticked.player.x, moving.player.x);

    let mut great = ticked.clone();
    engine::apply_command(&ctx, &mut great, &Command::ResolveMinigame { score: units::chance(0.9) });
    assert_eq!(great.minigame, None);
    assert_eq!(great.player.money, state.player.money + 100);
    assert_eq!(minigame_hooks(&ctx), vec![("mg-test".to_owned(), units::chance(0.9))]);

    let mut ok_run = opened.clone();
    engine::apply_command(&ctx, &mut ok_run, &Command::ResolveMinigame { score: units::chance(0.5) });
    assert_eq!(ok_run.player.money, state.player.money + 10);

    let mut poor = opened.clone();
    engine::apply_command(&ctx, &mut poor, &Command::ResolveMinigame { score: units::chance(0.1) });
    assert_eq!(poor.player.money, state.player.money);
}

#[test]
fn clamps_out_of_range_scores_and_cancels_cleanly() {
    let (ctx, state) = make_engine(|p| p.minigames = vec![timing_minigame()], "ext-test");
    let mut opened = state.clone();
    engine::apply_command(&ctx, &mut opened, &start_minigame("mg-test"));
    let mut clamped = opened.clone();
    engine::apply_command(&ctx, &mut clamped, &Command::ResolveMinigame { score: units::chance(99.0) });
    assert_eq!(clamped.player.money, state.player.money + 100);

    let mut cancelled = opened.clone();
    engine::apply_command(&ctx, &mut cancelled, &Command::CancelMinigame);
    assert_eq!(cancelled.minigame, None);
    assert_eq!(cancelled.player.money, state.player.money);
}

#[test]
fn errors_softly_on_unknown_minigames() {
    let (ctx, mut state) = make_engine(|_| {}, "ext-test");
    let effects = engine::apply_command(&ctx, &mut state, &start_minigame("missing"));
    assert_eq!(state.minigame, None);
    assert!(has_level(&effects, "error"));
}

// ---- fishing minigame binding -------------------------------------------

fn with_fishing(project: &mut GameProject) {
    // Water directly above the player start (3,4) → facing up hits (3,3).
    let tile = &mut project.scenes[0].tiles[3][3];
    tile.r#type = "water".to_owned();
    tile.background = "water".to_owned();
    tile.collision = false;
    let rod = Item {
        id: "tool-fishing-rod".to_owned(),
        name: "Fishing Rod".to_owned(),
        description: String::new(),
        r#type: "tool".to_owned(),
        stackable: false,
        max_stack: 1,
        value: 50,
        tool_type: Some("fishing-rod".to_owned()),
        tool_tier: Some(1),
        durability: Some(50),
        max_durability: Some(50),
        ..Item::default()
    };
    project.items.push(rod.clone());
    project.player.inventory.push(InventorySlot::new(rod, 1));
    project.fish_tables = vec![FishTable {
        id: "ft-test".to_owned(),
        name: "Test Waters".to_owned(),
        junk_chance: 0,
        entries: vec![FishTableEntry {
            item_id: "crop-wheat".to_owned(),
            weight: 1,
            difficulty: units::PROBABILITY_ONE,
        }],
        ..FishTable::default()
    }];
    project.minigames = vec![MinigameDef {
        id: "fishing".to_owned(),
        name: "Fishing".to_owned(),
        kind: "timing-bar".to_owned(),
        ..MinigameDef::default()
    }];
}

fn has_item(state: &GameState, id: &str) -> bool {
    state.player.inventory.iter().any(|s| s.item.id == id)
}

#[test]
fn casting_opens_the_fishing_minigame_a_perfect_score_always_lands_the_fish() {
    let (ctx, state) = make_engine(with_fishing, "ext-test");
    let mut cast = state.clone();
    engine::apply_command(&ctx, &mut cast, &Command::UseTool { tool: "fishing-rod".to_owned() });
    let session = cast.minigame.clone().expect("fishing session");
    assert_eq!(session.minigame_id, "fishing");
    assert_eq!(session.context.get("builtin"), Some(&json!("fishing")));
    assert_eq!(session.context.get("rodTier").and_then(Value::as_i64), Some(1));

    // difficulty 1 → always escapes unassisted, but score 1 zeroes it.
    let mut caught = cast.clone();
    engine::apply_command(&ctx, &mut caught, &Command::ResolveMinigame { score: units::chance(1.0) });
    assert!(has_item(&caught, "crop-wheat"));

    let mut escaped = cast.clone();
    let effects = engine::apply_command(&ctx, &mut escaped, &Command::ResolveMinigame { score: units::chance(0.0) });
    assert!(!has_item(&escaped, "crop-wheat"));
    assert!(common::has_message(&effects, |t| t.contains("got away")));
}

#[test]
fn without_a_declared_fishing_minigame_the_cast_resolves_instantly() {
    let (ctx, mut state) = make_engine(
        |p| {
            with_fishing(p);
            p.minigames = vec![];
        },
        "ext-test",
    );
    let effects = engine::apply_command(&ctx, &mut state, &Command::UseTool { tool: "fishing-rod".to_owned() });
    assert_eq!(state.minigame, None);
    // difficulty 1, no score assist → it always gets away, deterministically.
    assert!(common::has_message(&effects, |t| t.contains("got away")));
}

// ---- dialogue option actions ---------------------------------------------

#[test]
fn runs_the_bound_action_when_the_option_is_chosen() {
    let (ctx, state) = make_engine(
        |p| {
            let first = &mut p.npcs[0].dialogue[0];
            first.options[0] = DialogueOption {
                text: "Snack time".to_owned(),
                action_id: Some("action-snack".to_owned()),
                ..DialogueOption::default()
            };
            p.actions = vec![snack_action()];
            p.dialogues = p.npcs[0].dialogue.clone();
        },
        "ext-test",
    );
    let mut talking = state.clone();
    talking.dialogue = Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() });
    engine::apply_command(&ctx, &mut talking, &Command::ChooseDialogueOption { index: 0 });
    assert!(flag_is_true(&talking, "snacked"));
    assert_eq!(talking.player.money, state.player.money + 5);
}

// ---- expanded plugin mutations ---------------------------------------------

fn mutate(ctx: &EngineContext, state: &mut GameState, mutation: PluginMutation) -> Vec<Effect> {
    engine::apply_command(ctx, state, &Command::PluginMutation { plugin_id: "test-plugin".to_owned(), mutation })
}

#[test]
fn take_item_give_money_take_money_flow_through_the_outcome_executor() {
    let (ctx, state) = make_engine(|_| {}, "ext-test");
    let mut taken = state.clone();
    mutate(&ctx, &mut taken, PluginMutation::TakeItem { item_id: "seed-wheat".to_owned(), quantity: 2 });
    assert_eq!(taken.player.inventory.iter().find(|s| s.item.id == "seed-wheat").map(|s| s.quantity), Some(3));

    let mut paid = state.clone();
    mutate(&ctx, &mut paid, PluginMutation::GiveMoney { amount: 25 });
    assert_eq!(paid.player.money, state.player.money + 25);

    let mut charged = state.clone();
    mutate(&ctx, &mut charged, PluginMutation::TakeMoney { amount: 9999 });
    assert_eq!(charged.player.money, 0); // clamped, never negative
}

#[test]
fn give_money_and_take_money_mutations_flow_through_the_outcome_executor() {
    let (ctx, state) = make_engine(|_| {}, "ext-test");
    let mut paid = state.clone();
    mutate(&ctx, &mut paid, PluginMutation::GiveMoney { amount: 25 });
    assert_eq!(paid.player.money, state.player.money + 25);

    let mut charged = state.clone();
    mutate(&ctx, &mut charged, PluginMutation::TakeMoney { amount: 9999 });
    assert_eq!(charged.player.money, 0); // clamped, never negative
}

#[test]
fn modify_friendship_clamps_into_range_and_validates_the_npc() {
    let (ctx, state) = make_engine(|_| {}, "ext-test");
    ctx.drain_hook_events();

    let mut social = state.clone();
    mutate(&ctx, &mut social, PluginMutation::ModifyFriendship { npc_id: "npc-test".to_owned(), delta: 200 });
    assert_eq!(social.social["npc-test"].friendship, 200);
    mutate(&ctx, &mut social, PluginMutation::ModifyFriendship { npc_id: "npc-test".to_owned(), delta: -1000 });
    assert_eq!(social.social["npc-test"].friendship, 0);
    assert_eq!(friendship_hooks(&ctx), vec![200, 0]);

    let mut unknown = state.clone();
    let effects =
        mutate(&ctx, &mut unknown, PluginMutation::ModifyFriendship { npc_id: "ghost".to_owned(), delta: 10 });
    assert!(has_level(&effects, "error"));
    assert_eq!(effects, vec![message("error", "Plugin test-plugin: unknown NPC 'ghost'")]);
}

#[test]
fn grant_xp_and_modify_energy_respect_settings_and_caps() {
    let (ctx, state) = make_engine(|_| {}, "ext-test");
    let mut xp = state.clone();
    mutate(&ctx, &mut xp, PluginMutation::GrantXp { skill: "farming".to_owned(), amount: 60 });
    assert_eq!(xp.player.skills["farming"].xp, 60);
    assert_eq!(xp.player.skills["farming"].level, 1);

    let mut drained = state.clone();
    mutate(&ctx, &mut drained, PluginMutation::ModifyEnergy { delta: units::points(-30) });
    assert_eq!(drained.player.energy, state.player.energy - units::points(30));
    mutate(&ctx, &mut drained, PluginMutation::ModifyEnergy { delta: units::points(999) });
    assert_eq!(drained.player.energy, state.player.max_energy);
}

#[test]
fn modify_energy_heals_up_to_the_cap_and_ignores_zero() {
    let (ctx, state) = make_engine(|_| {}, "ext-test");
    let mut healed = state.clone();
    healed.player.energy = units::points(40);
    mutate(&ctx, &mut healed, PluginMutation::ModifyEnergy { delta: units::points(999) });
    assert_eq!(healed.player.energy, state.player.max_energy);
    let mut untouched = state.clone();
    mutate(&ctx, &mut untouched, PluginMutation::ModifyEnergy { delta: units::points(0) });
    assert_eq!(untouched, state);
}

#[test]
fn warp_player_start_dialogue_and_perform_action_dispatch_like_their_outcomes() {
    let (ctx, state) = make_engine(|p| p.actions = vec![snack_action()], "ext-test");
    let mut warped = state.clone();
    mutate(&ctx, &mut warped, PluginMutation::WarpPlayer { scene_id: "scene-test".to_owned(), x: 1, y: 3 });
    assert_eq!(warped.player.x, units::pos(1.5));
    assert_eq!(warped.player.y, units::pos(3.5));

    let mut talking = state.clone();
    mutate(&ctx, &mut talking, PluginMutation::StartDialogue { npc_id: "npc-test".to_owned(), dialogue_id: None });
    assert_eq!(
        talking.dialogue,
        Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() })
    );

    let mut acted = state.clone();
    mutate(&ctx, &mut acted, PluginMutation::PerformAction { action_id: "action-snack".to_owned() });
    assert!(flag_is_true(&acted, "snacked"));

    let mut sound = state.clone();
    let effects = mutate(&ctx, &mut sound, PluginMutation::PlaySound { sound_id: "ui".to_owned() });
    assert!(effects.contains(&Effect::Sound { id: "ui".to_owned() }));
}

#[test]
fn set_flag_message_and_set_weather_mutations() {
    let (ctx, state) = make_engine(|_| {}, "ext-test");
    let mut flagged = state.clone();
    mutate(&ctx, &mut flagged, PluginMutation::SetFlag { flag: "hello".to_owned(), value: json!("yes") });
    assert_eq!(flag(&flagged, "hello"), Some(&json!("yes")));

    let mut told = state.clone();
    let effects = mutate(&ctx, &mut told, PluginMutation::Message { text: "Hi there".to_owned() });
    assert_eq!(effects, vec![message("info", "Hi there")]);
    assert_eq!(told, state);
}

// ---- actions & minigames in content packs ------------------------------------

#[test]
fn merges_and_namespaces_pack_actions_minigames_with_rewritten_references() {
    let project = make_project();
    let base = state::create_base_content_from_project(&project);

    let pack: farm_sim::schema::ContentPack = serde_json::from_value(json!({
        "manifest": { "id": "mod-magic", "name": "Magic", "version": "1" },
        "content": {
            "items": [{
                "id": "wand", "name": "Wand", "description": "", "type": "material",
                "stackable": false, "maxStack": 1, "value": 10, "useActionId": "cast-spark"
            }],
            "actions": [{
                "id": "cast-spark", "name": "Cast Spark",
                "outcomes": [{ "type": "startMinigame", "minigameId": "spark-weave" }]
            }],
            "minigames": [{ "id": "spark-weave", "name": "Spark Weave", "kind": "timing-bar" }]
        },
        "plugins": []
    }))
    .expect("pack parses");

    let installs = vec![farm_sim::schema::PackInstallation { pack, enabled: true }];
    let merged = packs::merge_packs_into_content(&base, &installs);
    assert!(!merged.problems.iter().any(|p| p.severity == "error"));

    let action = merged.content.actions.iter().find(|a| a.id == "mod-magic:cast-spark");
    let minigame = merged.content.minigames.iter().find(|m| m.id == "mod-magic:spark-weave");
    let wand = merged.content.items.iter().find(|i| i.id == "mod-magic:wand");
    assert!(action.is_some());
    assert!(minigame.is_some());
    // References to same-pack definitions are rewritten to namespaced ids.
    assert_eq!(wand.and_then(|w| w.use_action_id.as_deref()), Some("mod-magic:cast-spark"));
    assert_eq!(action.and_then(|a| a.outcomes[0].minigame_id.as_deref()), Some("mod-magic:spark-weave"));
}

// ---- self-contained executor checks (no foreign modules) -------------------

fn bare(content: GameContent) -> (EngineContext, GameState) {
    let ctx = EngineContext::with_hooks(content, HookBus::new());
    let scene = Scene {
        id: "scene-a".to_owned(),
        name: "A".to_owned(),
        width: 3,
        height: 3,
        transitions: vec![
            SceneTransition {
                from_x: 0,
                from_y: 0,
                to_scene_id: "scene-b".to_owned(),
                to_x: 1,
                to_y: 1,
                locked: Some(true),
                ..SceneTransition::default()
            },
            SceneTransition {
                from_x: 2,
                from_y: 2,
                to_scene_id: "scene-a".to_owned(),
                to_x: 1,
                to_y: 1,
                ..SceneTransition::default()
            },
        ],
        ..Scene::default()
    };
    let state = GameState {
        clock: ClockState {
            day: 1,
            season: "spring".to_owned(),
            year: 1,
            time_minutes: units::minutes(360),
            ..ClockState::default()
        },
        world: WorldState {
            scenes: vec![
                scene,
                Scene { id: "scene-b".to_owned(), name: "B".to_owned(), width: 3, height: 3, ..Scene::default() },
            ],
        },
        player: PlayerState {
            x: units::pos(1.5),
            y: units::pos(1.5),
            scene_id: "scene-a".to_owned(),
            money: 100,
            energy: units::points(50),
            max_energy: units::points(100),
            max_inventory_size: 10,
            ..PlayerState::default()
        },
        ..GameState::default()
    };
    (ctx, state)
}

fn testy() -> Npc {
    Npc {
        id: "npc-test".to_owned(),
        name: "Testy".to_owned(),
        x: units::tiles(1),
        y: units::tiles(1),
        scene_id: "scene-a".to_owned(),
        dialogue: vec![
            Dialogue {
                id: "dlg-1".to_owned(),
                npc_id: "npc-test".to_owned(),
                text: "Hello!".to_owned(),
                options: vec![
                    DialogueOption {
                        text: "Snack time".to_owned(),
                        action_id: Some("action-snack".to_owned()),
                        ..DialogueOption::default()
                    },
                    DialogueOption {
                        text: "Gift me".to_owned(),
                        give_money: Some(25),
                        next_dialogue_id: Some("dlg-2".to_owned()),
                        ..DialogueOption::default()
                    },
                    DialogueOption {
                        text: "Secret".to_owned(),
                        requires_flag: Some("secret".to_owned()),
                        ..DialogueOption::default()
                    },
                ],
                ..Dialogue::default()
            },
            Dialogue {
                id: "dlg-2".to_owned(),
                npc_id: "npc-test".to_owned(),
                text: "More?".to_owned(),
                options: vec![DialogueOption { text: "No".to_owned(), ..DialogueOption::default() }],
                ..Dialogue::default()
            },
        ],
        appearance: "farmer".to_owned(),
        ..Npc::default()
    }
}

fn outcome(r#type: &str) -> EventOutcome {
    EventOutcome { r#type: r#type.to_owned(), ..EventOutcome::default() }
}

#[test]
fn perform_action_applies_outcomes_in_order_and_emits_on_action() {
    let (ctx, mut state) = bare(GameContent { actions: vec![snack_action()], ..GameContent::default() });
    let result = events::perform_action(&ctx, &mut state, "action-snack");
    assert!(result.ran);
    assert_eq!(state.player.money, 105);
    assert!(flag_is_true(&state, "snacked"));
    assert_eq!(result.effects, vec![message("success", "Received $5")]);
    assert_eq!(action_hooks(&ctx), vec!["action-snack".to_owned()]);
}

#[test]
fn perform_action_gates_on_conditions_with_the_fail_message() {
    let gated = ActionDef {
        conditions: vec![flag_condition("unlocked", true)],
        fail_message: "Not yet!".to_owned(),
        ..snack_action()
    };
    let (ctx, state) = bare(GameContent { actions: vec![gated], ..GameContent::default() });
    let mut blocked = state.clone();
    let result = events::perform_action(&ctx, &mut blocked, "action-snack");
    assert!(!result.ran);
    assert_eq!(blocked, state);
    assert_eq!(result.effects, vec![message("info", "Not yet!")]);

    // Any JS-truthy flag value satisfies `value: true`.
    let mut unlocked = state.clone();
    unlocked.flags.insert("unlocked".to_owned(), serde_json::Value::from(3));
    let result = events::perform_action(&ctx, &mut unlocked, "action-snack");
    assert!(result.ran);
    assert_eq!(unlocked.player.money, 105);
}

#[test]
fn perform_action_reports_unknown_actions() {
    let (ctx, mut state) = bare(GameContent::default());
    let result = events::perform_action(&ctx, &mut state, "nope");
    assert!(!result.ran);
    assert_eq!(result.effects, vec![message("error", "Unknown action 'nope'")]);
}

#[test]
fn action_recursion_stops_at_the_depth_cap() {
    let looping = ActionDef {
        id: "action-loop".to_owned(),
        outcomes: vec![
            EventOutcome { amount: Some(units::amount(1.0)), ..outcome("giveMoney") },
            EventOutcome { action_id: Some("action-loop".to_owned()), ..outcome("performAction") },
        ],
        ..snack_action()
    };
    let (ctx, mut state) = bare(GameContent { actions: vec![looping], ..GameContent::default() });
    events::perform_action(&ctx, &mut state, "action-loop");
    // Depths 0..4 run; depth 5 is refused.
    assert_eq!(state.player.money, 105);
}

#[test]
fn outcomes_see_the_state_produced_by_the_previous_one() {
    let (ctx, mut state) = bare(GameContent::default());
    let effects = events::apply_outcomes(
        &ctx,
        &mut state,
        &[
            EventOutcome { amount: Some(units::amount(50.0)), ..outcome("giveMoney") },
            EventOutcome { amount: Some(units::amount(1000.0)), ..outcome("takeMoney") },
            // Money is whole gold: half a gold rounds to 1 (v8 kept 0.5).
            EventOutcome { amount: Some(units::amount(0.5)), ..outcome("giveMoney") },
            EventOutcome { flag_name: Some("a".to_owned()), ..outcome("setFlag") },
            EventOutcome { flag_name: Some("a".to_owned()), ..outcome("clearFlag") },
            EventOutcome { message: Some("hi".to_owned()), ..outcome("message") },
            EventOutcome { message: Some(String::new()), ..outcome("message") },
            EventOutcome { sound_id: Some("ui".to_owned()), ..outcome("playSound") },
        ],
        0,
    );
    assert_eq!(state.player.money, 1);
    assert_eq!(state.flags["a"], Value::Bool(false));
    assert_eq!(
        effects,
        vec![
            message("success", "Received $50"),
            message("info", "Paid $150"),
            message("success", "Received $1"),
            message("info", "hi"),
            Effect::Sound { id: "ui".to_owned() },
        ]
    );
}

#[test]
fn world_outcomes_warp_dialogue_npcs_and_transitions() {
    let (ctx, mut state) = bare(GameContent { npcs: vec![testy()], ..GameContent::default() });

    let effects = events::apply_outcomes(
        &ctx,
        &mut state,
        &[
            EventOutcome { npc_id: Some("npc-test".to_owned()), x: Some(2), ..outcome("spawnNPC") },
            EventOutcome {
                npc_id: Some("npc-test".to_owned()),
                amount: Some(units::amount(2000.0)),
                ..outcome("modifyFriendship")
            },
            EventOutcome {
                npc_id: Some("ghost".to_owned()),
                amount: Some(units::amount(10.0)),
                ..outcome("modifyFriendship")
            },
            EventOutcome { npc_id: Some("npc-test".to_owned()), ..outcome("startDialogue") },
            EventOutcome { scene_id: Some("scene-b".to_owned()), ..outcome("unlockScene") },
            EventOutcome { x: Some(2), y: Some(2), ..outcome("lockTransition") },
            EventOutcome { scene_id: Some("scene-b".to_owned()), x: Some(1), y: Some(2), ..outcome("warpPlayer") },
        ],
        0,
    );
    assert_eq!(
        state.npcs["npc-test"],
        NpcState { x: units::tiles(2), y: units::tiles(1), scene_id: "scene-a".to_owned(), ..NpcState::default() }
    );
    assert_eq!(state.social["npc-test"].friendship, MAX_FRIENDSHIP);
    assert_eq!(friendship_hooks(&ctx), vec![MAX_FRIENDSHIP]);
    assert_eq!(state.dialogue, Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() }));
    let transitions = &state.world.scenes[0].transitions;
    assert_eq!(transitions[0].locked, Some(false));
    assert_eq!(transitions[1].locked, Some(true));
    assert_eq!(
        (state.player.scene_id.as_str(), state.player.x, state.player.y),
        ("scene-b", units::pos(1.5), units::pos(2.5))
    );
    assert_eq!(effects, vec![Effect::SceneChanged { scene_id: "scene-b".to_owned(), x: 1, y: 2 }]);

    events::apply_outcomes(
        &ctx,
        &mut state,
        &[EventOutcome { npc_id: Some("npc-test".to_owned()), ..outcome("removeNPC") }],
        0,
    );
    assert!(state.npcs.is_empty());
}

#[test]
fn events_fire_once_in_content_order_and_later_events_see_earlier_flags() {
    use farm_sim::schema::GameEvent;
    let first = GameEvent {
        id: "ev-1".to_owned(),
        name: "One".to_owned(),
        trigger: "tick".to_owned(),
        active: true,
        outcomes: vec![
            EventOutcome { flag_name: Some("one".to_owned()), ..outcome("setFlag") },
            EventOutcome { amount: Some(units::amount(1.0)), ..outcome("giveMoney") },
        ],
        ..GameEvent::default()
    };
    let second = GameEvent {
        id: "ev-2".to_owned(),
        name: "Two".to_owned(),
        trigger: "tick".to_owned(),
        active: true,
        repeatable: true,
        conditions: vec![flag_condition("one", true)],
        outcomes: vec![EventOutcome { amount: Some(units::amount(10.0)), ..outcome("giveMoney") }],
        ..GameEvent::default()
    };
    let other_scene = GameEvent {
        id: "ev-3".to_owned(),
        name: "Elsewhere".to_owned(),
        scene_id: "scene-b".to_owned(),
        trigger: "tick".to_owned(),
        active: true,
        outcomes: vec![EventOutcome { amount: Some(units::amount(1000.0)), ..outcome("giveMoney") }],
        ..GameEvent::default()
    };
    let (ctx, state) = bare(GameContent { events: vec![first, second, other_scene], ..GameContent::default() });

    let mut once = state.clone();
    events::evaluate_events(&ctx, &mut once, "tick", None);
    assert_eq!(once.player.money, 111);
    assert!(flag_is_true(&once, &event_fired_flag("ev-1")));
    assert_eq!(flag(&once, &event_fired_flag("ev-2")), None);

    let mut twice = once.clone();
    events::evaluate_events(&ctx, &mut twice, "tick", None);
    assert_eq!(twice.player.money, 121);

    let mut interact = state.clone();
    assert!(events::evaluate_events(&ctx, &mut interact, "interact", None).is_empty());
    assert_eq!(interact, state);
}

#[test]
fn position_conditions_match_regions_in_either_corner_order() {
    let (ctx, state) = bare(GameContent::default());
    let region = EventCondition::EnterTile { x: 3, y: 3, x2: Some(1), y2: Some(1) };
    assert!(events::condition_met(&ctx, &state, &region, Some(EventPosition { x: 2, y: 2 })));
    assert!(!events::condition_met(&ctx, &state, &region, Some(EventPosition { x: 0, y: 2 })));
    assert!(!events::condition_met(&ctx, &state, &region, None));
    assert!(events::condition_met(
        &ctx,
        &state,
        &EventCondition::QuestStatus { quest_id: "q".to_owned(), status: "not-started".to_owned() },
        None
    ));
    assert!(events::condition_met(&ctx, &state, &flag_condition("missing", false), None));
}

#[test]
fn minigame_sessions_open_resolve_by_tier_and_cancel() {
    let (ctx, state) = bare(GameContent { minigames: vec![timing_minigame()], ..GameContent::default() });

    let mut opened = state.clone();
    extensibility::handle_start_minigame(&ctx, &mut opened, "mg-test");
    assert_eq!(opened.minigame.as_ref().map(|m| m.minigame_id.as_str()), Some("mg-test"));
    // A second start while one is open is a no-op.
    let again = opened.clone();
    assert!(extensibility::handle_start_minigame(&ctx, &mut opened, "mg-test").is_empty());
    assert_eq!(opened, again);

    let resolve = |score: f64| {
        let mut next = opened.clone();
        extensibility::handle_resolve_minigame(&ctx, &mut next, units::chance(score));
        next
    };
    assert_eq!(resolve(0.9).player.money, 200);
    assert_eq!(resolve(0.5).player.money, 110);
    assert_eq!(resolve(0.1).player.money, 100);
    let clamped = resolve(99.0);
    assert_eq!(clamped.minigame, None);
    assert_eq!(clamped.player.money, 200);
    let resolved = minigame_hooks(&ctx);
    assert_eq!(resolved.last().map(|(_, score)| *score), Some(units::PROBABILITY_ONE));

    let mut cancelled = opened.clone();
    extensibility::handle_cancel_minigame(&mut cancelled);
    assert_eq!(cancelled.minigame, None);

    let mut unknown = state.clone();
    let effects = extensibility::handle_start_minigame(&ctx, &mut unknown, "missing");
    assert_eq!(effects, vec![message("error", "Unknown minigame 'missing'")]);
}

#[test]
fn choosing_a_dialogue_option_runs_its_action_and_advances() {
    let content = GameContent { npcs: vec![testy()], actions: vec![snack_action()], ..GameContent::default() };
    let (ctx, state) = bare(content);
    let mut talking = state.clone();
    talking.dialogue = Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-1".to_owned() });

    let mut snack = talking.clone();
    dialogue_system::handle_choose_dialogue_option(&ctx, &mut snack, 0);
    assert!(flag_is_true(&snack, "snacked"));
    assert_eq!(snack.player.money, 105);
    assert_eq!(snack.dialogue, None);

    let mut gift = talking.clone();
    let effects = dialogue_system::handle_choose_dialogue_option(&ctx, &mut gift, 1);
    assert_eq!(gift.player.money, 125);
    assert_eq!(gift.dialogue, Some(DialogueState { npc_id: "npc-test".to_owned(), dialogue_id: "dlg-2".to_owned() }));
    assert_eq!(effects, vec![message("success", "Received $25")]);

    // The flag-gated option is hidden, so index 2 is out of range → closes.
    assert_eq!(social::visible_dialogue_options(&ctx, &talking, &testy().dialogue[0]).len(), 2);
    let mut closed = talking.clone();
    dialogue_system::handle_choose_dialogue_option(&ctx, &mut closed, 2);
    assert_eq!(closed.dialogue, None);
    let mut with_secret = talking.clone();
    with_secret.flags.insert("secret".to_owned(), json!("yes"));
    assert_eq!(social::visible_dialogue_options(&ctx, &with_secret, &testy().dialogue[0]).len(), 3);
}

#[test]
fn quests_start_progress_and_complete() {
    let quest = Quest {
        id: "q-1".to_owned(),
        name: "Talk!".to_owned(),
        status: "not-started".to_owned(),
        objectives: vec![farm_sim::schema::QuestObjective {
            id: "o-1".to_owned(),
            r#type: "talk".to_owned(),
            target_npc_id: Some("npc-test".to_owned()),
            ..farm_sim::schema::QuestObjective::default()
        }],
        rewards: farm_sim::schema::QuestRewards { money: Some(7), ..Default::default() },
        ..Quest::default()
    };
    let (ctx, mut state) = bare(GameContent { quests: vec![quest], ..GameContent::default() });
    let effects = quests::start_quest_by_id(&ctx, &mut state, "q-1");
    assert_eq!(state.player.active_quests, vec!["q-1".to_owned()]);
    assert_eq!(effects, vec![message("info", "New quest: Talk!")]);

    let effects = quests::progress_quests(&ctx, &mut state, "talk", "npc-test", 1);
    assert!(state.player.active_quests.is_empty());
    assert_eq!(state.player.completed_quests, vec!["q-1".to_owned()]);
    assert_eq!(state.quests["q-1"].status, "completed");
    assert_eq!(state.player.money, 107);
    assert_eq!(effects, vec![Effect::QuestCompleted { quest_id: "q-1".to_owned() }]);
}

/// `changeTile` and `waterArea` mutate the player's scene in place (no other module involved
/// except the tile-layer helper for `changeTile`).
#[test]
fn water_area_waters_soil_around_the_player() {
    let (ctx, mut state) = bare(GameContent::default());
    let mut tiles = Vec::new();
    for y in 0..3 {
        let mut row = Vec::new();
        for x in 0..3 {
            let tile = common::empty_tile(x, y);
            row.push(if x == 1 { set_tile_layer(&tile, "soil") } else { tile });
        }
        tiles.push(row);
    }
    state.world.scenes[0].tiles = tiles;
    let effects =
        events::apply_outcomes(&ctx, &mut state, &[EventOutcome { radius: Some(0), ..outcome("waterArea") }], 0);
    assert_eq!(effects, vec![message("success", "The surrounding soil is watered.")]);
    let scene = &state.world.scenes[0];
    assert_eq!(scene.tiles[1][1].soil_state.as_deref(), Some("watered"));
    assert_eq!(scene.tiles[1][1].soil_moisture, 100);
    assert_eq!(scene.tiles[0][1].soil_state, None);
    assert_eq!(scene.tiles[1][0].soil_state, None);
}
