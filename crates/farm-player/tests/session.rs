//! The platform-independent play loop (`farm_player::session::PlaySession`).

use farm_player::session::{DebugAction, FrameToggles, PlaySession, SessionEvent, SessionPlugins, ToastKind};
use farm_runtime::host::MinigameInput;
use farm_sim::hooks::HookEvent;
use farm_sim::schema::{GameProject, MinigameDef, PluginMutation};
use farm_sim::{hash_state, state, Command, StartState};
use std::cell::RefCell;
use std::rc::Rc;

const FRAME: f64 = 1.0 / 60.0;

fn starter() -> GameProject {
    let fixture: serde_json::Value =
        serde_json::from_str(include_str!("../../../fixtures/golden/content/starter-farm.json")).unwrap();
    serde_json::from_value(fixture["project"].clone()).unwrap()
}

fn session_for(project: &GameProject) -> PlaySession {
    let content = state::create_content_from_project(project);
    PlaySession::new_game(content, &StartState::from_project(project), Some("session-test"))
}

fn frames(session: &mut PlaySession, count: usize) -> Vec<FrameToggles> {
    (0..count).map(|_| session.update(FRAME, false)).collect()
}

#[test]
fn held_keys_walk_the_player_and_release_stops_it() {
    let mut session = session_for(&starter());
    let start = (session.state().player.x, session.state().player.y);
    session.key_down("d");
    frames(&mut session, 30);
    assert!(session.state().player.x > start.0, "moved right: {start:?} → {}", session.state().player.x);
    assert_eq!(session.state().player.move_intent.dx, 1.0);
    session.key_up("d");
    frames(&mut session, 2);
    assert_eq!(session.state().player.move_intent.dx, 0.0);
    let stopped = session.state().player.x;
    frames(&mut session, 20);
    assert_eq!(session.state().player.x, stopped);
}

#[test]
fn a_host_modal_pauses_movement_and_hotkeys_report_toggles() {
    let mut session = session_for(&starter());
    session.key_down("d");
    let toggles = session.update(FRAME, true);
    assert_eq!(toggles, FrameToggles::default());
    assert_eq!(session.state().player.move_intent.dx, 0.0);
    session.key_up("d");
    session.key_down("i");
    assert!(session.update(FRAME, false).inventory);
    session.key_up("i");
    session.key_down("escape");
    let toggles = session.update(FRAME, true);
    assert!(toggles.escape && !toggles.inventory);
}

#[test]
fn effects_become_events_and_pops() {
    let mut session = session_for(&starter());
    session.run_command(&Command::Sleep);
    let events = session.drain_events();
    assert!(
        events.iter().any(|event| matches!(event, SessionEvent::DayStarted { day, .. } if *day == 2.0)),
        "{events:?}"
    );
    assert!(events.iter().any(|event| matches!(event, SessionEvent::Toast { .. })), "{events:?}");
    assert!(session.drain_events().is_empty());
    // Sleeping neither harvests nor completes a quest: no floating pops.
    assert!(session.pops().is_empty());
}

#[test]
fn identical_input_gives_identical_games() {
    let run = || {
        let mut session = session_for(&starter());
        session.key_down("s");
        frames(&mut session, 25);
        session.key_up("s");
        session.key_down("t");
        frames(&mut session, 3);
        session.key_up("t");
        session.key_down("z");
        frames(&mut session, 3);
        hash_state(session.state())
    };
    assert_eq!(run(), run());
}

fn timing_bar() -> MinigameDef {
    MinigameDef { id: "mg-test".into(), name: "Test".into(), kind: "timing-bar".into(), ..MinigameDef::default() }
}

#[test]
fn minigames_mount_and_report_their_score_once() {
    let mut project = starter();
    project.minigames = vec![timing_bar()];
    let mut session = session_for(&project);
    session.set_cosmetic_seed(7);
    session.run_command(&Command::StartMinigame { minigame_id: "mg-test".into() });
    assert!(session.minigame_view().is_some(), "mounted");
    assert!(session.engine_modal_open());
    frames(&mut session, 5);
    session.minigame_input(MinigameInput::Press);
    session.minigame_input(MinigameInput::Release);
    // The score entered the command log and closed the minigame.
    assert!(session.state().minigame.is_none());
    assert!(session.minigame_view().is_none());
    session.minigame_input(MinigameInput::Press);
    assert!(session.state().minigame.is_none());
}

#[test]
fn escape_cancels_a_minigame() {
    let mut project = starter();
    project.minigames = vec![timing_bar()];
    let mut session = session_for(&project);
    session.run_command(&Command::StartMinigame { minigame_id: "mg-test".into() });
    session.key_down("escape");
    session.update(FRAME, false);
    assert!(session.state().minigame.is_none());
}

/// Records what it saw and answers every `onDayStart` with a gift of money.
#[derive(Default)]
struct FakePlugins {
    seen: Rc<RefCell<Vec<String>>>,
    queued: Vec<Command>,
}

impl SessionPlugins for FakePlugins {
    fn dispatch(&mut self, events: &[HookEvent]) {
        for event in events {
            self.seen.borrow_mut().push(event.hook().to_owned());
            if event.hook() == "onDayStart" {
                self.queued.push(Command::PluginMutation {
                    plugin_id: "pack:gift".into(),
                    mutation: serde_json::from_value::<PluginMutation>(
                        serde_json::json!({"type": "giveMoney", "amount": 25}),
                    )
                    .unwrap(),
                });
            }
        }
    }

    fn drain_commands(&mut self) -> Vec<Command> {
        std::mem::take(&mut self.queued)
    }
}

#[test]
fn plugin_mutations_enter_the_command_log_at_the_next_frame() {
    let mut session = session_for(&starter());
    let seen = Rc::new(RefCell::new(Vec::new()));
    session.set_plugins(Some(Box::new(FakePlugins { seen: seen.clone(), queued: Vec::new() })));
    let money = session.state().player.money;
    session.run_command(&Command::Sleep);
    assert!(seen.borrow().iter().any(|hook| hook == "onDayStart"), "{:?}", seen.borrow());
    // Engine hooks come first, then one onEffect per effect.
    let first_effect = seen.borrow().iter().position(|hook| hook == "onEffect").unwrap();
    assert!(seen.borrow()[..first_effect].iter().all(|hook| hook != "onEffect"));
    assert_eq!(session.state().player.money, money, "not applied mid-step");
    session.update(0.0, false);
    assert_eq!(session.state().player.money, money + 25.0);
}

/// The golden plugin scenario's project with the real sandbox: its `onDayStart` plugin speaks
/// after every sleep, and the message enters the game at the next frame.
#[test]
fn the_wasm_sandbox_runs_pack_plugins_in_a_session() {
    let fixture: serde_json::Value =
        serde_json::from_str(include_str!("../../../fixtures/golden/replays/content-packs-and-plugins.json")).unwrap();
    let project: GameProject = serde_json::from_value(fixture["project"].clone()).unwrap();
    let specs = farm_plugins::plugin_specs_from_project(&project);
    assert!(!specs.is_empty(), "the scenario ships plugins");
    let cart_plugins: Vec<farm_cart::CartPlugin> = specs
        .iter()
        .map(|spec| farm_cart::CartPlugin {
            id: spec.id.clone(),
            pack_id: spec.pack_id.clone(),
            source: spec.source.clone(),
            granted_hooks: spec.granted_hooks.clone(),
        })
        .collect();
    let mut session = session_for(&project);
    session.set_plugins(farm_player::plugins::for_cartridge(&cart_plugins, Default::default()));
    assert!(session.has_plugins());
    assert!(session.plugin_errors().is_empty(), "{:?}", session.plugin_errors());
    session.run_command(&Command::Sleep);
    session.drain_events();
    session.update(0.0, false);
    let toasts: Vec<String> = session
        .drain_events()
        .into_iter()
        .filter_map(|event| match event {
            SessionEvent::Toast { text, .. } => Some(text),
            _ => None,
        })
        .collect();
    assert!(!toasts.is_empty(), "the plugin's day-start message reached the game");
    assert!(farm_player::plugins::for_project(&starter(), Default::default()).is_none());
}

#[test]
fn debug_actions_change_state_without_commands() {
    let mut session = session_for(&starter());
    let money = session.state().player.money;
    session.debug(&DebugAction::AddMoney { amount: 500.0 });
    assert_eq!(session.state().player.money, money + 500.0);
    session.debug(&DebugAction::AddMoney { amount: f64::NAN });
    assert_eq!(session.state().player.money, money + 500.0);
    session.debug(&DebugAction::SetSeason { season: "winter".into() });
    assert_eq!(session.state().clock.season, "winter");
    session.debug(&DebugAction::SetFlag { flag: "  met-mayor ".into() });
    assert_eq!(session.state().flags.get("met-mayor"), Some(&serde_json::Value::Bool(true)));
    session.debug(&DebugAction::GiveFirst { item_type: "seed".into() });
    session.debug(&DebugAction::GiveFirst { item_type: "seed".into() });
    let seed = session.content().items.iter().find(|item| item.r#type == "seed").unwrap().id.clone();
    let quantity: f64 =
        session.state().player.inventory.iter().filter(|slot| slot.item.id == seed).map(|slot| slot.quantity).sum();
    assert!(quantity >= 10.0);
    let day = session.state().clock.day;
    session.debug(&DebugAction::SkipDay);
    assert_eq!(session.state().clock.day, day + 1.0);
    let scene = session.state().world.scenes.last().unwrap().clone();
    session.debug(&DebugAction::Teleport { scene_id: scene.id.clone() });
    assert_eq!(session.state().player.scene_id, scene.id);
    assert_eq!(session.state().player.x, (scene.width / 2.0).floor() + 0.5);
    let parsed: DebugAction = serde_json::from_str(r#"{"type":"giveFirst","itemType":"material"}"#).unwrap();
    assert_eq!(parsed, DebugAction::GiveFirst { item_type: "material".into() });
}

#[test]
fn replacing_the_state_releases_a_held_walk() {
    let mut session = session_for(&starter());
    session.key_down("d");
    frames(&mut session, 5);
    let walking = session.state().clone();
    assert_eq!(walking.player.move_intent.dx, 1.0);
    let mut fresh = session_for(&starter());
    fresh.replace_state(walking);
    assert_eq!(fresh.state().player.move_intent.dx, 0.0);
}

#[test]
fn toasts_keep_the_message_level() {
    let mut session = session_for(&starter());
    // Using a tool on an unsuitable tile answers with a message.
    session.run_command(&Command::UseTool { tool: "watering-can".into() });
    let toasts: Vec<ToastKind> = session
        .drain_events()
        .into_iter()
        .filter_map(|event| match event {
            SessionEvent::Toast { kind, .. } => Some(kind),
            _ => None,
        })
        .collect();
    assert!(!toasts.is_empty());
}
