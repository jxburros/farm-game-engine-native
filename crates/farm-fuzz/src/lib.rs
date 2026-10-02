//! `farm-fuzz`: fuzz entry points for the places where input from outside reaches the engine
//! (#116): cartridges, saves and their migrations, images, project JSON, host requests (render,
//! preview, session) and plugin output, plus random play on new games.
//!
//! Every [`Target`] takes arbitrary bytes and must neither panic nor hang, and states the engine
//! reaches from a new game must keep the [`invariants`]. Two runners drive the targets:
//!
//! - `fuzz/` (outside the workspace, nightly): one cargo-fuzz target per [`Target`]
//!   (`cargo +nightly fuzz run <name>`), seeded with `cargo run -p farm-fuzz --example
//!   write_corpus`;
//! - this crate's tests (stable, `cargo test`): every target over its seed corpus, over arbitrary
//!   bytes and over mutated seeds (proptest), and the invariants under generated content.
#![forbid(unsafe_code)]

pub mod input;
pub mod invariants;

use farm_cart::save_file::{self, SaveTarget};
use farm_sim::replay::ReplayInput;
use farm_sim::schema::{GameContent, GameProject, GameState};
use farm_sim::{engine, state, Command, CommandRules, EngineContext, HookBus};
use input::{Choices, Ids};
use std::path::PathBuf;
use std::sync::LazyLock;

/// One untrusted-input boundary.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Target {
    /// `FGCT` cartridge bytes: `read_cartridge`, `load_cartridge`, then a new game from its start
    /// state played for a day.
    Cartridge,
    /// Save file bytes (binary `FGSV` or JSON) for the starter farm: `read_save_preview`,
    /// `load_save_bytes`; a loaded save saves and loads back unchanged and plays on.
    Save,
    /// Save state JSON of any version: `migrate_game_state_json`.
    SaveMigration,
    /// Image bytes: `farm_render::decode_image`.
    Image,
    /// Project JSON, a `0xFF` byte, then play: the project's content and new game, then the
    /// decoded commands, with the invariants after every step.
    Project,
    /// Play on the starter farm: the bytes decode to commands and ticks (see [`input`]), with
    /// the invariants after every step.
    Commands,
    /// A stateless render request (`farm_host::render::render_json`).
    RenderRequest,
    /// Map preview and visual requests on the starter farm (`HostPreview`).
    Preview,
    /// A headless session on the starter farm: the bytes as a command array, a save, a state.
    Session,
    /// A plugin handler's return value: `validate_mutations`, then every valid mutation applied
    /// to the starter farm, with the invariants after each.
    PluginMutations,
}

impl Target {
    /// Every target.
    pub const ALL: [Target; 10] = [
        Self::Cartridge,
        Self::Save,
        Self::SaveMigration,
        Self::Image,
        Self::Project,
        Self::Commands,
        Self::RenderRequest,
        Self::Preview,
        Self::Session,
        Self::PluginMutations,
    ];

    /// The cargo-fuzz target name (`fuzz/fuzz_targets/<name>.rs`) and corpus directory.
    pub fn name(self) -> &'static str {
        match self {
            Self::Cartridge => "cartridge",
            Self::Save => "save",
            Self::SaveMigration => "save_migration",
            Self::Image => "image",
            Self::Project => "project",
            Self::Commands => "commands",
            Self::RenderRequest => "render_request",
            Self::Preview => "preview",
            Self::Session => "session",
            Self::PluginMutations => "plugin_mutations",
        }
    }

    /// Run the target on `data`. Panics when the engine panics or breaks an invariant (that is
    /// the finding).
    pub fn run(self, data: &[u8]) {
        match self {
            Self::Cartridge => cartridge(data),
            Self::Save => save(data),
            Self::SaveMigration => save_migration(data),
            Self::Image => image(data),
            Self::Project => project(data),
            Self::Commands => commands(data),
            Self::RenderRequest => render_request(data),
            Self::Preview => preview(data),
            Self::Session => session(data),
            Self::PluginMutations => plugin_mutations(data),
        }
    }

    /// The seed corpus: fixtures of this kind and a few hand-written inputs.
    pub fn seeds(self) -> Vec<Vec<u8>> {
        match self {
            Self::Cartridge => fixtures("golden/cartridges", "cart"),
            Self::Save => {
                let mut seeds = fixtures("golden/saves", "json");
                let game = new_starter_game("save-seed");
                seeds.push(save_file::write_save(&game, &STARTER.target).into_bytes());
                seeds.push(save_file::write_save_binary(
                    &game,
                    &STARTER.target,
                    &save_file::SavePreview::of_state(&game),
                ));
                seeds
            }
            Self::SaveMigration => fixtures("golden/saves", "json"),
            Self::Image => {
                let mut seeds = fixtures("render", "png");
                seeds.truncate(3);
                seeds
            }
            Self::Project => {
                let mut seeds = Vec::new();
                for name in ["blank", "fixture-v8", "lab-calendar"] {
                    let mut seed = project_fixture_json(name);
                    seed.push(0xFF);
                    seed.extend_from_slice(b"\x01\x03\x00\x06\x01\x0e\x00\xff");
                    seeds.push(seed);
                }
                seeds
            }
            Self::Commands => vec![
                b"\x01\x03\x00\x01\x03\x00\x06\x01\x09\x0e".to_vec(),
                b"\x00\x40\x01\x1a\x02\x05\x00\xff\x01\x0e".to_vec(),
                (0..=255u8).collect(),
            ],
            Self::RenderRequest => vec![
                br#"{"type":"editorSnapshot","project":{},"sceneId":"scene-farm","tileSize":28,"padding":12}"#.to_vec(),
                br#"{"type":"rasterize","snapshot":{"width":2,"height":2,"tileSize":16,"padding":0},"scale":1}"#
                    .to_vec(),
                br#"{"type":"rasterize","snapshot":{"width":0,"height":0,"tileSize":0},"scale":1}"#.to_vec(),
            ],
            Self::Preview => vec![
                br#"{"sceneId":"scene-farm","tileSize":16,"padding":0,"scale":0.25}"#.to_vec(),
                br#"{"sceneId":"scene-farm","tileSize":8,"padding":2,"camera":{"x":0,"y":0,"width":64,"height":64},"scale":1}"#.to_vec(),
                br#"{"visual":null,"tick":0,"direction":"down","moving":true,"size":32,"scale":1}"#.to_vec(),
            ],
            Self::Session => vec![
                br#"[{"type":"move","dir":"up"},{"type":"useTool","tool":"hoe"},{"type":"sleep"}]"#.to_vec(),
                br#"[{"type":"setMoveIntent","dx":1.9,"dy":-7}]"#.to_vec(),
                save_file::write_save(&new_starter_game("session-seed"), &STARTER.target).into_bytes(),
            ],
            Self::PluginMutations => vec![
                br#"[{"type":"giveItem","itemId":"seed-wheat","quantity":5},{"type":"giveMoney","amount":100}]"#.to_vec(),
                br#"[{"type":"warpPlayer","sceneId":"scene-farm","x":3,"y":4},{"type":"modifyEnergy","delta":-200}]"#.to_vec(),
                br#"[{"type":"setFlag","flag":"f","value":1e308},{"type":"takeMoney","amount":999999999}]"#.to_vec(),
            ],
        }
    }
}

/// `fixtures/<dir>/*.<extension>`, sorted by name.
fn fixtures(dir: &str, extension: &str) -> Vec<Vec<u8>> {
    let dir: PathBuf = [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", dir].iter().collect();
    let mut paths: Vec<PathBuf> = std::fs::read_dir(&dir)
        .map(|entries| entries.filter_map(|entry| entry.ok().map(|entry| entry.path())).collect())
        .unwrap_or_default();
    paths.retain(|path| path.extension().is_some_and(|ext| ext == extension));
    paths.sort();
    paths.iter().filter_map(|path| std::fs::read(path).ok()).collect()
}

/// The project JSON of `fixtures/golden/content/<name>.json`.
fn project_fixture_json(name: &str) -> Vec<u8> {
    let path: PathBuf =
        [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", &format!("{name}.json")]
            .iter()
            .collect();
    let text = std::fs::read(&path).unwrap_or_default();
    let fixture: serde_json::Value = serde_json::from_slice(&text).unwrap_or_default();
    serde_json::to_vec(&fixture["project"]).unwrap_or_default()
}

/// The starter farm, compiled once.
struct Starter {
    project: GameProject,
    project_json: Vec<u8>,
    content: GameContent,
    target: SaveTarget,
    ids: Ids,
}

static STARTER: LazyLock<Starter> = LazyLock::new(|| {
    let fixture: serde_json::Value =
        serde_json::from_str(include_str!("../../../fixtures/golden/content/starter-farm.json"))
            .expect("the starter farm fixture is JSON");
    let project_json = serde_json::to_vec(&fixture["project"]).expect("a JSON value serializes");
    let project: GameProject = serde_json::from_value(fixture["project"].clone()).expect("the starter farm project");
    let content = state::create_content_from_project(&project);
    let target = SaveTarget::for_project(&project, &content);
    let ids = Ids::of(&content);
    Starter { project, project_json, content, target, ids }
});

fn new_starter_game(seed: &str) -> GameState {
    state::create_game_state(&STARTER.project, Some(seed))
}

/// Play `log` on `state`, checking the invariants before and after every input.
pub fn play_checked(ctx: &EngineContext, state: &mut GameState, log: &[ReplayInput]) {
    assert_invariants(state, "the new game");
    for (index, entry) in log.iter().enumerate() {
        match entry {
            ReplayInput::Command { command } => {
                engine::apply_command(ctx, state, command);
            }
            ReplayInput::Tick { ticks } => {
                engine::advance_tick(ctx, state, *ticks);
            }
        }
        assert_invariants(state, &format!("input {index} ({entry:?})"));
    }
}

fn assert_invariants(state: &GameState, after: &str) {
    if let Err(broken) = invariants::check(state) {
        panic!("invariant broken after {after}: {broken}");
    }
}

/// Commands apply as for a player (refusals), or as scripted, by the first byte.
fn rules(c: &mut Choices<'_>) -> CommandRules {
    if c.byte().is_multiple_of(2) {
        CommandRules::Player
    } else {
        CommandRules::Scripted
    }
}

fn cartridge(data: &[u8]) {
    let _ = farm_cart::read_cartridge(data).map(|cart| cart.assets.len());
    let Ok(cart) = farm_cart::load_cartridge(data) else { return };
    let ctx = EngineContext::with_hooks(cart.content, HookBus::new()).with_rules(CommandRules::Scripted);
    let mut game = state::create_game_state_from_start(&cart.start, Some("fuzz"));
    engine::advance_tick(&ctx, &mut game, 30);
    engine::apply_command(&ctx, &mut game, &Command::Sleep);
    let _ = farm_sim::hash_state(&game);
}

fn save(data: &[u8]) {
    let _ = save_file::read_save_preview(data);
    let loaded = save_file::load_save_bytes(data, &STARTER.target, &STARTER.content);
    let Some(game) = loaded.state.filter(|_| loaded.ok) else { return };
    // What loads, saves and loads back unchanged, in both envelopes.
    for bytes in [
        save_file::write_save(&game, &STARTER.target).into_bytes(),
        save_file::write_save_binary(&game, &STARTER.target, &save_file::SavePreview::of_state(&game)),
    ] {
        let again = save_file::load_save_bytes(&bytes, &STARTER.target, &STARTER.content);
        assert!(again.ok, "a re-saved save is refused: {:?}", again.errors);
        assert!(again.state.as_ref() == Some(&game), "a save changed in a save/load round trip");
    }
    let ctx = EngineContext::new(STARTER.content.clone());
    let mut game = game;
    engine::advance_tick(&ctx, &mut game, 30);
    engine::apply_command(&ctx, &mut game, &Command::Sleep);
}

fn save_migration(data: &[u8]) {
    let Ok(text) = std::str::from_utf8(data) else { return };
    let result = farm_cart::migrate_game_state_json(text);
    if let Some(state) = result.data.filter(|_| result.ok) {
        let _ = farm_cart::save::validate_game_state(&state);
    }
}

fn image(data: &[u8]) {
    if let Ok(image) = farm_render::images::decode_image(data) {
        assert!(image.width() > 0 && image.height() > 0, "a decoded image has pixels");
    }
}

fn project(data: &[u8]) {
    let split = data.iter().position(|byte| *byte == 0xFF).unwrap_or(data.len());
    let Ok(project) = serde_json::from_slice::<GameProject>(&data[..split]) else { return };
    let mut c = Choices::new(data.get(split + 1..).unwrap_or_default());
    let content = state::create_content_from_project(&project);
    let ids = Ids::of(&content);
    let rules = rules(&mut c);
    let log = input::inputs(&ids, &mut c);
    let ctx = EngineContext::with_hooks(content, HookBus::new()).with_rules(rules);
    let mut game = state::create_game_state(&project, Some("fuzz"));
    play_checked(&ctx, &mut game, &log);
}

fn commands(data: &[u8]) {
    let mut c = Choices::new(data);
    let rules = rules(&mut c);
    let log = input::inputs(&STARTER.ids, &mut c);
    let ctx = EngineContext::with_hooks(STARTER.content.clone(), HookBus::new()).with_rules(rules);
    let mut game = new_starter_game("fuzz");
    play_checked(&ctx, &mut game, &log);
}

fn render_request(data: &[u8]) {
    let _ = farm_host::render::render_json(data);
}

fn preview(data: &[u8]) {
    let Ok(mut preview) = farm_host::HostPreview::new(&STARTER.project_json) else {
        panic!("the starter farm previews");
    };
    let _ = preview.render(data);
    let _ = preview.render_visual(data);
}

fn session(data: &[u8]) {
    let Ok(mut session) = farm_host::HostSession::new(&STARTER.project_json, Some("fuzz"), true) else {
        panic!("the starter farm starts a session");
    };
    let _ = session.apply(data);
    let _ = session.load_save(data);
    session.tick(3);
    if session.set_state(data).is_ok() {
        session.tick(3);
        session.skip_day();
        let _ = session.hash();
        let _ = session.save();
    }
}

fn plugin_mutations(data: &[u8]) {
    let Ok(raw) = serde_json::from_slice::<serde_json::Value>(data) else { return };
    let (mutations, _errors) = farm_plugins::validate_mutations("fuzz-plugin", &raw, Some("onDayStart"));
    let ctx = EngineContext::with_hooks(STARTER.content.clone(), HookBus::new());
    let mut game = new_starter_game("fuzz");
    let log: Vec<ReplayInput> = mutations
        .into_iter()
        .map(|mutation| ReplayInput::Command {
            command: Command::PluginMutation { plugin_id: "fuzz-plugin".to_owned(), mutation },
        })
        .collect();
    play_checked(&ctx, &mut game, &log);
}
