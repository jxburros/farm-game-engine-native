//! `PluginRuntime` with a real `farm-sim` session: the bridge tests of `PluginsTests.cs`, the
//! plugin tests of `FarmingRpgMaker.App.Tests` (`MinigameAndPanelsTests`, `PlayEngineTests`),
//! and the golden "content packs and plugins" scenario.

mod common;

use common::{demo_mod, pack, plugin, read_json, starter_farm_project};
use farm_plugins::{
    parse_mutation, plugin_specs_from_packs, plugin_specs_from_project, to_engine_json, PluginErrorKind, PluginHost,
    PluginHostOptions, PluginRuntime, WasmPluginHost, MAX_RECENT_ERRORS,
};
use farm_sim::effects::Effect;
use farm_sim::hooks::{ActionHookPayload, HookEvent};
use farm_sim::replay::ReplayInput;
use farm_sim::schema::{GameProject, GameState, PackInstallation, PluginMutation};
use farm_sim::{apply_command, hash_state, quests, state, Command, EngineContext, HookBus};
use serde_json::Value;

/// A session the way a host runs one: engine context with a hook bus, state, plugin runtime.
struct Session {
    ctx: EngineContext,
    state: GameState,
    plugins: PluginRuntime,
    /// Every effect the host saw, in order.
    effects: Vec<Effect>,
}

impl Session {
    fn new(project: &GameProject, seed: &str) -> Self {
        let ctx = EngineContext::with_hooks(state::create_content_from_project(project), HookBus::new());
        let state = state::create_game_state(project, Some(seed));
        let plugins =
            PluginRuntime::from_project(project, PluginHostOptions::default()).expect("the project has plugins");
        Self { ctx, state, plugins, effects: Vec::new() }
    }

    /// One command, then its hook events and effects go to the plugins.
    fn run(&mut self, command: &Command) {
        let effects = apply_command(&self.ctx, &mut self.state, command);
        let events = self.ctx.drain_hook_events();
        let errors = self.plugins.handle_step(&events, &effects);
        assert!(errors.is_empty(), "plugin errors: {errors:?}");
        self.effects.extend(effects);
    }

    /// A frame: queued plugin mutations first (the one fixed point), then the frame's commands.
    fn frame(&mut self, commands: &[Command]) {
        for command in self.plugins.drain_commands() {
            self.run(&command);
        }
        for command in commands {
            self.run(command);
        }
    }

    fn messages(&self) -> Vec<&str> {
        self.effects
            .iter()
            .filter_map(|effect| match effect {
                Effect::Message { text, .. } => Some(text.as_str()),
                _ => None,
            })
            .collect()
    }

    fn flag(&self, name: &str) -> Option<&Value> {
        self.state.flags.get(name)
    }
}

fn with_packs(mut project: GameProject, packs: Vec<(farm_sim::schema::ContentPack, bool)>) -> GameProject {
    project.content_packs = packs.into_iter().map(|(pack, enabled)| PackInstallation { pack, enabled }).collect();
    project
}

// --- bridge: hooks → queued commands → command log (PluginsTests.cs) ------------------------

#[test]
fn bridge_queues_mutations_from_hooks_and_drains_them_as_commands() {
    let project = with_packs(
        starter_farm_project(),
        vec![
            (
                pack(
                    "gifts",
                    &["onDayStart"],
                    vec![plugin(
                        "daily",
                        "api.on('onDayStart', p => [{ type: 'giveMoney', amount: p.day * 10 }, { type: 'message', text: 'Day ' + p.day }])",
                        &[],
                    )],
                ),
                true,
            ),
            (pack("off", &["onDayStart"], vec![plugin("never", "api.on('onDayStart', () => [{ type: 'giveMoney', amount: 1 }])", &[])]), false),
        ],
    );
    let mut session = Session::new(&project, "bridge");
    assert_eq!(session.plugins.hook_names(), ["onDayStart"]);

    session.run(&Command::Sleep);
    assert_eq!(session.plugins.queue().len(), 2);
    let commands = session.plugins.drain_commands();
    assert!(session.plugins.queue().is_empty());
    let ids: Vec<&str> = commands
        .iter()
        .map(|command| match command {
            Command::PluginMutation { plugin_id, .. } => plugin_id.as_str(),
            other => panic!("unexpected {other:?}"),
        })
        .collect();
    assert_eq!(ids, ["gifts:daily", "gifts:daily"]);

    let money = session.state.player.money;
    for command in &commands {
        apply_command(&session.ctx, &mut session.state, command);
    }
    assert_eq!(session.state.player.money, money + 20.0);

    // The drained commands are ordinary, serializable command-log entries.
    assert_eq!(
        to_engine_json(&commands[0]),
        r#"{"type":"pluginMutation","pluginId":"gifts:daily","mutation":{"type":"giveMoney","amount":20}}"#
    );
    let back: Command = serde_json::from_str(&to_engine_json(&commands[0])).expect("round trip");
    assert_eq!(back, commands[0]);
}

#[test]
fn bridge_reports_errors_and_ignores_throwing_plugins() {
    let specs = plugin_specs_from_packs([&pack(
        "p",
        &["onAction"],
        vec![
            plugin("bad", "api.on('onAction', () => { throw new Error('kaput') })", &["onAction"]),
            plugin("init", "throw new Error('no init')", &["onAction"]),
        ],
    )]);
    let host = WasmPluginHost::new(specs.clone(), PluginHostOptions::default());
    let mut runtime = PluginRuntime::new(host, &specs);
    let reported = runtime.dispatch_events(&[HookEvent::Action(ActionHookPayload { action_id: "a".to_owned() })]);
    assert!(runtime.queue().is_empty());
    let kinds: Vec<PluginErrorKind> = reported.iter().map(|e| e.kind).collect();
    assert_eq!(kinds, [PluginErrorKind::Threw]);
    assert_eq!(reported[0].message, "threw in onAction: kaput");
    let recent: Vec<PluginErrorKind> = runtime.recent_errors().map(|e| e.kind).collect();
    assert_eq!(recent, [PluginErrorKind::Init, PluginErrorKind::Threw]);
}

#[test]
fn projects_without_plugins_get_no_runtime() {
    assert!(PluginRuntime::from_project(&starter_farm_project(), PluginHostOptions::default()).is_none());
    let disabled = with_packs(starter_farm_project(), vec![(demo_mod(), false)]);
    assert!(plugin_specs_from_project(&disabled).is_empty());
    assert!(PluginRuntime::from_project(&disabled, PluginHostOptions::default()).is_none());
}

#[test]
fn specs_follow_load_order_and_skip_disabled_packs() {
    let project = with_packs(
        starter_farm_project(),
        vec![
            (
                pack(
                    "first",
                    &["onDayStart"],
                    vec![plugin("a", "", &[]), plugin("b", "", &["onDayStart", "onAction"])],
                ),
                true,
            ),
            (pack("off", &["onDayStart"], vec![plugin("c", "", &[])]), false),
            (pack("last", &[], vec![plugin("d", "", &[])]), true),
        ],
    );
    let specs = plugin_specs_from_project(&project);
    let summary: Vec<(&str, Vec<&str>)> =
        specs.iter().map(|s| (s.id.as_str(), s.granted_hooks.iter().map(String::as_str).collect())).collect();
    assert_eq!(summary, [("first:a", vec!["onDayStart"]), ("first:b", vec!["onDayStart"]), ("last:d", vec![])]);
}

#[test]
fn only_granted_hooks_are_dispatched_and_recent_errors_are_bounded() {
    /// A host that records what it was asked and fails every call.
    #[derive(Debug, Default)]
    struct Recorder {
        calls: Vec<(String, String)>,
    }
    impl PluginHost for Recorder {
        fn dispatch(&mut self, hook: &str, payload_json: &str) -> Vec<farm_plugins::PluginDispatchResult> {
            self.calls.push((hook.to_owned(), payload_json.to_owned()));
            vec![farm_plugins::PluginDispatchResult {
                plugin_id: "p:x".to_owned(),
                mutations: vec![PluginMutation::GiveMoney { amount: 1.0 }],
                errors: vec![farm_plugins::PluginError {
                    plugin_id: "p:x".to_owned(),
                    kind: PluginErrorKind::Threw,
                    message: format!("call {}", self.calls.len()),
                    hook: Some(hook.to_owned()),
                }],
            }]
        }
        fn init_errors(&self) -> &[farm_plugins::PluginError] {
            &[]
        }
    }
    let specs = plugin_specs_from_packs([&pack(
        "p",
        &["onEffect", "onDayStart"],
        vec![plugin("x", "", &["onEffect", "onDayStart"])],
    )]);
    let mut runtime = PluginRuntime::new(Recorder::default(), &specs);
    assert_eq!(runtime.hook_names(), ["onEffect", "onDayStart"]);
    let events = [
        HookEvent::Command(farm_sim::hooks::CommandHookPayload { command_type: "sleep".to_owned() }),
        HookEvent::DayStart(farm_sim::hooks::DayHookPayload { day: 2.0, season: "spring".to_owned(), year: 1.0 }),
    ];
    runtime.handle_step(&events, &[Effect::message("info", "hi")]);
    let calls: Vec<(&str, &str)> = runtime.host().calls.iter().map(|(h, p)| (h.as_str(), p.as_str())).collect();
    assert_eq!(
        calls,
        [("onDayStart", r#"{"day":2,"season":"spring","year":1}"#), ("onEffect", r#"{"effectType":"message"}"#)]
    );
    assert_eq!(runtime.queue().len(), 2);
    assert!(runtime.dispatch("onCommand", "{}").is_empty());

    for _ in 0..150 {
        runtime.dispatch("onDayStart", "{}");
    }
    assert_eq!(runtime.recent_errors().len(), MAX_RECENT_ERRORS);
    assert_eq!(runtime.recent_errors().last().map(|e| e.message.as_str()), Some("call 152"));
    assert_eq!(runtime.recent_errors().next().map(|e| e.message.as_str()), Some("call 53"));
}

// --- the app's plugin tests ------------------------------------------------------------------

/// `MinigameAndPanelsTests.EnabledPackPlugins_RunInTheSandbox_AndTheirMutationsEnterAsCommands`.
#[test]
fn enabled_pack_plugins_run_in_the_sandbox_and_their_mutations_enter_as_commands() {
    let project = with_packs(starter_farm_project(), vec![(demo_mod(), true)]);
    let mut session = Session::new(&project, "demo");
    session.frame(&[Command::Sleep]); // onDayStart → the plugin queues a message mutation
    assert!(!session.messages().iter().any(|m| m.contains("glowshrooms")));
    session.frame(&[]); // drained at the next frame's fixed point
    assert!(session.messages().contains(&"The glowshrooms hum softly on day 2..."), "{:?}", session.messages());
    assert_eq!(session.plugins.recent_errors().len(), 0);
}

/// `PlayEngineTests`' echo plugin: every hook it may see answers with a mutation that records
/// the payload, so hook order and payload JSON (key order included) end up in the state.
const ECHO_PLUGIN: &str = r#"
api.on('onCommand', function (p) { if (p.commandType === 'pluginMutation') return []; return [{ type: 'setFlag', flag: 'last-command', value: JSON.stringify(p) }]; });
api.on('onEffect', function (p) { return p.effectType === 'message' ? [{ type: 'giveMoney', amount: 1 }] : []; });
api.on('onDayStart', function (p) { return [{ type: 'message', text: 'echo ' + JSON.stringify(p) }]; });
api.on('onNPCInteract', function (p) { return [{ type: 'setFlag', flag: 'talked', value: Object.keys(p).join(',') + '=' + p.npcId }]; });
api.on('onWeatherRoll', function (p) { return [{ type: 'setFlag', flag: 'weather', value: JSON.stringify(p) }]; });
"#;

#[test]
fn the_echo_plugin_sees_engine_order_payloads_for_every_hook() {
    let hooks = ["onDayStart", "onCommand", "onEffect", "onNPCInteract", "onWeatherRoll"];
    let mut pack = demo_mod();
    pack.manifest.permissions.hooks = hooks.iter().map(|h| (*h).to_owned()).collect();
    let mut echo = plugin("echo", ECHO_PLUGIN, &hooks);
    echo.name = Some("Echo".to_owned());
    pack.plugins.push(echo);
    let project = with_packs(starter_farm_project(), vec![(pack, true)]);
    let mut session = Session::new(&project, "echo");
    assert_eq!(session.plugins.hook_names(), hooks);

    session.frame(&[Command::Sleep]);
    session.frame(&[]);
    session.frame(&[]);
    // Stand below the farmer, facing up, and talk.
    let (x, y) = {
        let npc = session.state.npcs.get("npc-farmer").expect("the starter farm has a farmer");
        (npc.x, npc.y)
    };
    session.state.player.x = x + 0.5;
    session.state.player.y = y + 1.5;
    session.state.player.direction = "up".to_owned();
    session.frame(&[Command::Interact]);
    session.frame(&[Command::CloseDialogue]);
    session.frame(&[]);

    assert_eq!(session.flag("talked").and_then(Value::as_str), Some("npcId=npc-farmer"));
    let weather = session.flag("weather").and_then(Value::as_str).expect("weather flag");
    assert!(weather.starts_with(r#"{"weatherId":"#) && weather.ends_with(r#","day":2}"#), "{weather}");
    assert_eq!(session.flag("last-command").and_then(Value::as_str), Some(r#"{"commandType":"closeDialogue"}"#));
    let messages = session.messages();
    assert!(messages.iter().any(|m| m.starts_with(r#"echo {"day":2,"season":"#)), "{messages:?}");
    assert!(messages.contains(&"The glowshrooms hum softly on day 2..."), "{messages:?}");
    // onEffect paid 1 gold for every message effect the host saw before the last frame.
    assert!(session.state.player.money > starter_farm_project().player.money);
    assert_eq!(session.plugins.recent_errors().len(), 0);
}

#[test]
fn a_session_with_plugins_is_deterministic() {
    let run = || {
        let project = with_packs(starter_farm_project(), vec![(demo_mod(), true)]);
        let mut session = Session::new(&project, "det");
        for _ in 0..8 {
            session.frame(&[Command::Sleep]);
        }
        session.frame(&[]);
        (hash_state(&session.state), session.messages().iter().map(|m| (*m).to_owned()).collect::<Vec<_>>())
    };
    let (hash, messages) = run();
    assert_eq!((hash.clone(), messages.clone()), run());
    // Day 7's handler also gives a glowshroom seed.
    assert!(messages.iter().any(|m| m.contains("Glowshroom")), "{messages:?}");
}

// --- the golden "content packs and plugins" scenario -------------------------------------------

/// The recorded `pluginMutation` commands of `content-packs-and-plugins` were written by the
/// recorder (every mutation type, valid and failing), not captured from a live plugin, so the
/// replay itself needs no plugins. This runs the scenario with the pack's real plugin attached
/// and checks that (1) the state hashes still match at every step, (2) the plugin answers
/// every `onDayStart` with the demo mod's message, plus a seed on day 7, and (3) its day 8
/// answer is exactly the recorded step 141 command, and every recorded mutation passes the
/// validator unchanged.
#[test]
fn the_golden_packs_scenario_replays_with_its_plugin_attached() {
    let fixture = read_json("fixtures/golden/replays/content-packs-and-plugins.json");
    let project: GameProject = serde_json::from_value(fixture["project"].clone()).expect("project");
    let ctx = EngineContext::with_hooks(state::create_content_from_project(&project), HookBus::new());
    let mut game = state::create_game_state(&project, fixture["seed"].as_str());
    if fixture["autoStartQuests"].as_bool() == Some(true) {
        quests::auto_start_quests(&ctx, &mut game);
    }
    assert_eq!(hash_state(&game), fixture["initialHash"].as_str().unwrap());
    let mut plugins =
        PluginRuntime::from_project(&project, PluginHostOptions::default()).expect("the demo mod has a plugin");
    assert_eq!(plugins.hook_names(), ["onDayStart"]);

    let mut produced: Vec<(usize, Command)> = Vec::new();
    let mut recorded: Vec<(usize, Command)> = Vec::new();
    for (index, step) in fixture["steps"].as_array().unwrap().iter().enumerate() {
        let input: ReplayInput = serde_json::from_value(step["input"].clone()).expect("input");
        let effects = match &input {
            ReplayInput::Command { command } => {
                if let Command::PluginMutation { mutation, .. } = command {
                    // The validator accepts every recorded mutation and parses it to the same value.
                    let json: Value = serde_json::from_str(&to_engine_json(mutation)).unwrap();
                    assert_eq!(parse_mutation(&json).as_ref(), Ok(mutation), "step {index}");
                    recorded.push((index, command.clone()));
                }
                apply_command(&ctx, &mut game, command)
            }
            ReplayInput::Tick { ticks } => farm_sim::advance_tick(&ctx, &mut game, *ticks),
        };
        let errors = plugins.handle_step(&ctx.drain_hook_events(), &effects);
        assert!(errors.is_empty(), "step {index}: {errors:?}");
        produced.extend(plugins.drain_commands().into_iter().map(|command| (index, command)));
        assert_eq!(hash_state(&game), step["hash"].as_str().unwrap(), "step {index}");
    }
    assert_eq!(hash_state(&game), fixture["finalHash"].as_str().unwrap());

    let describe = |commands: &[(usize, Command)]| -> Vec<String> {
        commands.iter().map(|(index, command)| format!("{index} {}", to_engine_json(command))).collect()
    };
    let hum = |day: u32| {
        format!(
            r#"{{"type":"pluginMutation","pluginId":"demo-glow-farm:morning-hum","mutation":{{"type":"message","text":"The glowshrooms hum softly on day {day}..."}}}}"#
        )
    };
    let seed = r#"{"type":"pluginMutation","pluginId":"demo-glow-farm:morning-hum","mutation":{"type":"giveItem","itemId":"demo-glow-farm:seed-glowshroom","quantity":1}}"#;
    // The scenario sleeps at steps 28, 42, 57, 71, 85, 99 and 113 (days 2–8).
    assert_eq!(
        describe(&produced),
        [
            format!("28 {}", hum(2)),
            format!("42 {}", hum(3)),
            format!("57 {}", hum(4)),
            format!("71 {}", hum(5)),
            format!("85 {}", hum(6)),
            format!("99 {}", hum(7)),
            format!("99 {seed}"),
            format!("113 {}", hum(8)),
        ]
    );
    // The recorder's day-8 message command is the one the plugin produces on day 8.
    let (step, command) = &recorded.iter().find(|(_, c)| to_engine_json(c).contains("hum softly")).unwrap();
    assert_eq!(*step, 141);
    assert_eq!(to_engine_json(command), hum(8));
    assert!(recorded.iter().all(
        |(_, c)| matches!(c, Command::PluginMutation { plugin_id, .. } if plugin_id == "demo-glow-farm:morning-hum")
    ));
}
