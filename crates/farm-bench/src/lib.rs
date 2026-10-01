//! `farm-bench`: the benchmark scenarios of docs/LANGUAGES.md ("Tests") and their time budgets.
//!
//! The scenarios are built programmatically from the shared fixtures, so they need no files of
//! their own:
//!
//! - **overnight**: the `sleep` command on the starter farm grown to 64×64 tiles full of crops,
//!   and to 256×256 full of crops plus 50 machines with a job finishing overnight;
//! - **save**: a mid-size state (the starter farm grown to 64×64 with a 32×32 crop field and ten
//!   machines) serialized as stable JSON (the part that runs on the simulation thread), as a
//!   binary `FGSV` save and as a JSON save, and saved then loaded again;
//! - **cart load**: every cartridge in `fixtures/golden/cartridges`, parsed, and started as a new
//!   game in the player;
//! - **frame**: a gameplay frame of the starter farm at 1280×800 and 1920×1080, composed by the
//!   CPU rasterizer;
//! - **npcs**: 1,000 ticks of the starter farm grown to 64×64 with 20 NPCs walking to a
//!   schedule target, once reachable and once walled off (every path search fails and explores
//!   the whole farm, every game minute).
//!
//! Two front ends share them: `cargo bench -p farm-bench` (criterion, `benches/runtime.rs`) and
//! the `farm-bench` binary, which times each scenario a fixed number of times and, with
//! `--budget`, fails when a median is over its budget times a CI factor (the CI check).
#![forbid(unsafe_code)]
// Wall-clock timing is what this crate measures; it never feeds the simulation.
#![allow(clippy::disallowed_types)]

use farm_cart::save_file::{self, LoadedSave, SavePreview, SaveTarget};
use farm_cart::LoadedCartridge;
use farm_player::{InputEvent, Player, PlayerOptions};
use farm_sim::farming::crops;
use farm_sim::schema::{
    soil_states, tile_types, MachineProcessing, Npc, NpcScheduleEntry, NpcState, Scene, Tile, TileMachine,
};
use farm_sim::{stable_json, Command, EngineContext, GameContent, GameProject, GameState};
use std::hint::black_box;
use std::path::{Path, PathBuf};
use std::time::Instant;

/// Seed of every new game the scenarios start.
pub const SEED: &str = "farm-bench";

/// The time budgets of docs/LANGUAGES.md ("Benchmarks"), in milliseconds.
pub mod budgets {
    /// Overnight pass, 64×64 farm, full crops.
    pub const OVERNIGHT_64: f64 = 2.0;
    /// Overnight pass, 256×256 farm, full crops + 50 machines.
    pub const OVERNIGHT_256: f64 = 30.0;
    /// Save: serializing the state on the simulation thread.
    pub const SAVE: f64 = 1.0;
    /// What CI holds the save to until the serializer meets [`SAVE`]: 1.5x the stable JSON of
    /// the mid-size state (746 KB) on the machine the budgets were first measured on (10.7 ms).
    /// Stable JSON is built as a `serde_json::Value` tree (an `IndexMap` and a `String` key per
    /// field of every tile) and then every object's keys are sorted; allocation dominates the
    /// profile.
    pub const SAVE_CEILING: f64 = 16.0;
    /// Cartridge load, sample games.
    pub const CART_LOAD: f64 = 50.0;
    /// A gameplay frame on the CPU rasterizer. The documented frame target (< 4 ms at 1080p)
    /// is for the wgpu renderer on an integrated GPU; until that exists, the CPU path is held
    /// to one 60 Hz frame.
    pub const CPU_FRAME: f64 = 1000.0 / 60.0;
    /// 1,000 ticks (50 seconds of play) with 20 NPCs on schedules, 64×64 farm, reachable or
    /// walled-off target. A walled-off target once took 5 s (every NPC searched the whole farm
    /// every game minute); about 10 ms reachable and 2 ms walled off when this was written.
    pub const NPC_TICKS: f64 = 25.0;
}

/// The repository root (the fixtures live under it).
pub fn repo_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("..").join("..")
}

/// A project of `fixtures/golden/content` (the sample games and test projects, as the
/// TypeScript engine recorded them).
pub fn golden_project(name: &str) -> GameProject {
    let path = repo_root().join("fixtures").join("golden").join("content").join(format!("{name}.json"));
    let text = std::fs::read_to_string(&path).unwrap_or_else(|error| panic!("{}: {error}", path.display()));
    let fixture: serde_json::Value = serde_json::from_str(&text).expect("fixture is JSON");
    serde_json::from_value(fixture["project"].clone()).expect("fixture holds a project")
}

/// The starter farm: every system on (crops, shop, quests, crafting, machines, animals).
pub fn starter_project() -> GameProject {
    golden_project("starter-farm")
}

/// The shape of a grown farm.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FarmSpec {
    /// The farm scene is `size`×`size` tiles.
    pub size: usize,
    /// Crops fill the `crop_field`×`crop_field` square at the top left (tilled soil, a mix of
    /// the spring crops, three in four watered).
    pub crop_field: usize,
    /// Placed machines along the bottom row, each with a job that finishes overnight.
    pub machines: usize,
}

impl FarmSpec {
    /// Overnight, 64×64 farm, full crops.
    pub const OVERNIGHT_64: Self = Self { size: 64, crop_field: 64, machines: 0 };
    /// Overnight, 256×256 farm, full crops + 50 machines.
    pub const OVERNIGHT_256: Self = Self { size: 256, crop_field: 256, machines: 50 };
    /// The mid-size state the save scenarios write: a 64×64 farm, a 32×32 crop field, ten
    /// machines.
    pub const MID_SIZE: Self = Self { size: 64, crop_field: 32, machines: 10 };
}

/// A running game: the compiled content and a state.
#[derive(Debug)]
pub struct Farm {
    pub project: GameProject,
    pub ctx: EngineContext,
    pub state: GameState,
}

/// The starter farm's new game with its farm scene grown to `spec`.
pub fn farm(spec: FarmSpec) -> Farm {
    let project = starter_project();
    let ctx = EngineContext::new(farm_sim::create_content_from_project(&project));
    let mut state = farm_sim::create_game_state(&project, Some(SEED));
    let scene_id = state.player.scene_id.clone();
    let scene = state.world.scenes.iter_mut().find(|scene| scene.id == scene_id).expect("the player's scene exists");
    grow_scene(scene, &ctx.content, spec);
    Farm { project, ctx, state }
}

/// How many NPCs walk in [`npc_town`].
pub const NPC_COUNT: i32 = 20;

/// The schedule target of [`npc_town`]'s NPCs.
pub const NPC_TARGET: (i32, i32) = (56, 56);

/// The starter farm grown to 64×64 grass with [`NPC_COUNT`] NPCs along the top that walk to
/// [`NPC_TARGET`] from the first minute. `walled` rings the target with walls, so every path
/// search fails after exploring the whole farm.
pub fn npc_town(walled: bool) -> Farm {
    let mut farm = farm(FarmSpec { size: 64, crop_field: 0, machines: 0 });
    let scene_id = farm.state.player.scene_id.clone();
    if walled {
        let scene = farm.state.world.scenes.iter_mut().find(|scene| scene.id == scene_id).expect("the farm");
        let (tx, ty) = NPC_TARGET;
        for (x, y) in [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)] {
            let tile = scene.tile_mut(tx + x, ty + y).expect("inside the farm");
            tile.collision = true;
            tile.r#type = tile_types::WALL.to_owned();
        }
    }
    let content = &mut farm.ctx.content;
    content.npcs.clear();
    farm.state.npcs.clear();
    for i in 0..NPC_COUNT {
        let (x, y) = (farm_sim::units::tiles(2 + 3 * i % 60), farm_sim::units::tiles(2 + i / 20));
        content.npcs.push(Npc {
            id: format!("npc-{i}"),
            name: format!("Walker {i}"),
            x,
            y,
            scene_id: scene_id.clone(),
            can_move: true,
            schedule: Some(vec![NpcScheduleEntry {
                minute: 0,
                scene_id: scene_id.clone(),
                x: NPC_TARGET.0,
                y: NPC_TARGET.1,
                ..NpcScheduleEntry::default()
            }]),
            ..Npc::default()
        });
        farm.state
            .npcs
            .insert(format!("npc-{i}"), NpcState { x, y, scene_id: scene_id.clone(), ..NpcState::default() });
    }
    farm
}

/// [`npc_town`]'s run: 1,000 ticks.
pub fn npc_ticks(ctx: &EngineContext, state: &mut GameState) -> farm_sim::Effects {
    farm_sim::advance_tick(ctx, state, 1_000)
}

/// Crops that grow in the starting season (spring) of the starter farm.
const CROPS: &[&str] = &["wheat", "carrot", "potato", "strawberry", "cauliflower"];

fn grow_scene(scene: &mut Scene, content: &GameContent, spec: FarmSpec) {
    // The machine types that process something, with one of their recipes.
    let jobs: Vec<(&str, &str)> = content
        .recipes
        .iter()
        .filter_map(|recipe| Some((recipe.machine_type_id.as_deref()?, recipe.id.as_str())))
        .collect();
    assert!(spec.machines == 0 || !jobs.is_empty(), "the starter farm has machine recipes");

    let mut tiles = Vec::with_capacity(spec.size);
    for y in 0..spec.size {
        let mut row = Vec::with_capacity(spec.size);
        for x in 0..spec.size {
            let mut tile = Tile {
                x: x as i32,
                y: y as i32,
                r#type: tile_types::GRASS.to_owned(),
                background: tile_types::GRASS.to_owned(),
                ..Tile::default()
            };
            let machine = (y == spec.size - 1 && x < spec.machines).then(|| jobs[x % jobs.len()]);
            if let Some((type_id, recipe_id)) = machine {
                tile.collision = true;
                tile.machine = Some(TileMachine {
                    type_id: type_id.to_owned(),
                    // Finished before the night: settled by the overnight pass.
                    processing: Some(MachineProcessing { recipe_id: recipe_id.to_owned(), completes_at_minute: 0 }),
                    ..TileMachine::default()
                });
            } else if x < spec.crop_field && y < spec.crop_field {
                let watered = (x + 2 * y) % 4 != 0;
                let mut crop = crops::create_planted_crop(CROPS[(x + y) % CROPS.len()], 1, false);
                crop.watered = watered;
                tile.r#type = tile_types::SOIL.to_owned();
                tile.background = tile_types::SOIL.to_owned();
                tile.soil_state = Some(if watered { soil_states::WATERED } else { soil_states::DRY }.to_owned());
                tile.soil_moisture = if watered { 100 } else { 0 };
                tile.crop = Some(crop);
            }
            row.push(tile);
        }
        tiles.push(row);
    }
    scene.width = spec.size as i32;
    scene.height = spec.size as i32;
    scene.tiles = tiles;
}

/// The overnight pass: the `sleep` command.
pub fn sleep(ctx: &EngineContext, state: &mut GameState) -> farm_sim::Effects {
    farm_sim::apply_command(ctx, state, &Command::Sleep)
}

/// A state to save, and the game it belongs to.
#[derive(Debug)]
pub struct SaveFixture {
    pub content: GameContent,
    pub target: SaveTarget,
    pub state: GameState,
}

impl SaveFixture {
    /// The mid-size state ([`FarmSpec::MID_SIZE`]).
    pub fn mid_size() -> Self {
        let farm = farm(FarmSpec::MID_SIZE);
        let target = SaveTarget::for_project(&farm.project, &farm.ctx.content);
        Self { content: farm.ctx.content, target, state: farm.state }
    }

    /// The state as stable JSON: what the simulation thread produces before handing the save
    /// to a worker.
    pub fn stable_json(&self) -> String {
        stable_json::stringify(&self.state)
    }

    /// A binary `FGSV` save (what the player writes to its slots).
    pub fn write_binary(&self) -> Vec<u8> {
        save_file::write_save_binary(&self.state, &self.target, &SavePreview::of_state(&self.state))
    }

    /// A JSON save (editor debug tools, the web version).
    pub fn write_json(&self) -> String {
        save_file::write_save(&self.state, &self.target)
    }

    /// Loads a save of this game in either envelope.
    pub fn load(&self, bytes: &[u8]) -> LoadedSave {
        let loaded = save_file::load_save_bytes(bytes, &self.target, &self.content);
        assert!(loaded.ok, "the save loads: {:?}", loaded.errors);
        loaded
    }
}

/// Every cartridge in `fixtures/golden/cartridges`, by file stem, in name order.
pub fn sample_cartridges() -> Vec<(String, Vec<u8>)> {
    let folder = repo_root().join("fixtures").join("golden").join("cartridges");
    let mut carts: Vec<(String, Vec<u8>)> = std::fs::read_dir(&folder)
        .unwrap_or_else(|error| panic!("{}: {error}", folder.display()))
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.extension().is_some_and(|extension| extension == "cart"))
        .map(|path| {
            let name = path.file_stem().unwrap_or_default().to_string_lossy().into_owned();
            let bytes = std::fs::read(&path).unwrap_or_else(|error| panic!("{}: {error}", path.display()));
            (name, bytes)
        })
        .collect();
    carts.sort_by(|a, b| a.0.cmp(&b.0));
    assert!(!carts.is_empty(), "no cartridges in {}", folder.display());
    carts
}

/// Parses a cartridge.
pub fn load_cartridge(bytes: &[u8]) -> LoadedCartridge {
    farm_cart::load_cartridge(bytes).unwrap_or_else(|error| panic!("cartridge does not load: {error}"))
}

/// Loads a cartridge and starts a new game in the embedded player (plugins, fonts, first frame
/// state): what a player does between choosing a game and showing it.
pub fn start_cartridge(bytes: &[u8]) -> Player {
    let options = PlayerOptions { seed: Some(SEED.to_owned()), ..PlayerOptions::embedded() };
    Player::from_cartridge(load_cartridge(bytes), options).unwrap_or_else(|error| panic!("{error:?}"))
}

/// Length of one frame at 60 Hz, in seconds.
pub const FRAME: f64 = 1.0 / 60.0;

/// The starter farm in play (the embedded player starts the game right away), after a few
/// frames to settle fonts and caches.
pub fn gameplay_player(width: u32, height: u32) -> Player {
    playing(starter_project(), width, height)
}

/// The starter farm with its farm scene grown to [`FarmSpec::OVERNIGHT_256`] (65,536 tiles, full
/// crops, 50 machines), in play: a frame must cost what the camera shows, not the scene's size.
pub fn large_map_player(width: u32, height: u32) -> Player {
    let mut project = starter_project();
    let content = farm_sim::create_content_from_project(&project);
    let scene_id = project.player.scene_id.clone();
    let scene = project.scenes.iter_mut().find(|scene| scene.id == scene_id).expect("the player's scene exists");
    grow_scene(scene, &content, FarmSpec::OVERNIGHT_256);
    playing(project, width, height)
}

/// A player in play at a frame size ([`gameplay_player`], [`large_map_player`]).
pub type NewPlayer = fn(u32, u32) -> Player;

fn playing(project: GameProject, width: u32, height: u32) -> Player {
    let options = PlayerOptions { seed: Some(SEED.to_owned()), ..PlayerOptions::embedded() };
    let mut player = Player::from_project(project, options).unwrap_or_else(|error| panic!("{error:?}"));
    for frame in 0..30 {
        render_frame(&mut player, frame, width, height);
    }
    player
}

/// The input of gameplay frame `frame`: walking right, then left, so the camera, animation and
/// interpolation move.
pub fn walk_events(frame: usize) -> Vec<InputEvent> {
    let down = |key: &str| InputEvent::KeyDown { key: key.to_owned(), repeat: false };
    let up = |key: &str| InputEvent::KeyUp { key: key.to_owned() };
    match frame % 120 {
        0 => vec![down("d")],
        60 => vec![up("d"), down("a")],
        119 => vec![up("a")],
        _ => Vec::new(),
    }
}

/// One gameplay frame: input, simulation, UI and pixels.
pub fn render_frame(player: &mut Player, frame: usize, width: u32, height: u32) {
    let output = player.frame(FRAME, &walk_events(frame), width, height).unwrap_or_else(|error| panic!("{error:?}"));
    black_box(output.pixels.data().len());
}

// ── Budget runs ──────────────────────────────────────────────────────────

/// Times `routine` on a fresh `setup()` input `runs` times, in milliseconds. The input is built
/// and the output dropped outside the timed part.
pub fn sample<T, O>(runs: usize, mut setup: impl FnMut() -> T, mut routine: impl FnMut(T) -> O) -> Vec<f64> {
    let mut samples = Vec::with_capacity(runs);
    for _ in 0..runs {
        let input = setup();
        let started = Instant::now();
        let output = routine(black_box(input));
        let elapsed = started.elapsed();
        drop(black_box(output));
        samples.push(elapsed.as_secs_f64() * 1000.0);
    }
    samples
}

/// The median of `samples` (the mean of the middle two for an even count).
pub fn median(samples: &[f64]) -> f64 {
    let mut sorted = samples.to_vec();
    sorted.sort_by(f64::total_cmp);
    let n = sorted.len();
    match n {
        0 => f64::NAN,
        _ if n % 2 == 1 => sorted[n / 2],
        _ => (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0,
    }
}

/// One timed scenario of the budget run.
pub struct Scenario {
    pub name: String,
    /// The budget of its median, in milliseconds (`None`: reported only).
    pub budget_ms: Option<f64>,
    /// What the CI check holds the median to (before its factor): the budget, or for a budget
    /// the code is known to miss, a ceiling at today's measurement that catches regressions.
    pub enforced_ms: Option<f64>,
    /// How many times it runs (after its fixture is built).
    pub runs: usize,
    run: Box<dyn FnOnce(usize) -> Vec<f64>>,
}

impl std::fmt::Debug for Scenario {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Scenario")
            .field("name", &self.name)
            .field("budget_ms", &self.budget_ms)
            .field("enforced_ms", &self.enforced_ms)
            .finish_non_exhaustive()
    }
}

impl Scenario {
    fn new(
        name: impl Into<String>,
        budget_ms: Option<f64>,
        runs: usize,
        run: impl FnOnce(usize) -> Vec<f64> + 'static,
    ) -> Self {
        Self { name: name.into(), budget_ms, enforced_ms: budget_ms, runs, run: Box::new(run) }
    }

    /// The budget is known to be missed: CI holds the median to `ceiling_ms` instead.
    fn known_over(self, ceiling_ms: f64) -> Self {
        Self { enforced_ms: Some(ceiling_ms), ..self }
    }

    /// Builds the fixture and times the scenario `runs` times (at least once).
    pub fn measure(self, runs: usize) -> Measurement {
        let samples = (self.run)(runs.max(1));
        Measurement {
            name: self.name,
            budget_ms: self.budget_ms,
            enforced_ms: self.enforced_ms,
            median_ms: median(&samples),
            samples,
        }
    }
}

/// What a budget run measured for one scenario.
#[derive(Debug, Clone, PartialEq)]
pub struct Measurement {
    pub name: String,
    pub budget_ms: Option<f64>,
    pub enforced_ms: Option<f64>,
    pub median_ms: f64,
    pub samples: Vec<f64>,
}

impl Measurement {
    /// The fastest run, in milliseconds.
    pub fn min_ms(&self) -> f64 {
        self.samples.iter().copied().fold(f64::INFINITY, f64::min)
    }

    /// Is the median within the budget (always, without one)?
    pub fn meets_budget(&self) -> bool {
        self.budget_ms.is_none_or(|budget| self.median_ms <= budget)
    }

    /// The CI limit: the enforced time times `factor`.
    pub fn limit_ms(&self, factor: f64) -> Option<f64> {
        self.enforced_ms.map(|enforced| enforced * factor)
    }

    /// Is the median within the CI limit (always, without one)?
    pub fn within(&self, factor: f64) -> bool {
        self.limit_ms(factor).is_none_or(|limit| self.median_ms <= limit)
    }
}

/// Every scenario of the budget run, fixtures not yet built. `scale` multiplies the default run
/// counts.
pub fn scenarios(scale: f64) -> Vec<Scenario> {
    let runs = |base: usize| ((base as f64 * scale).ceil() as usize).max(1);
    let mut all = vec![
        Scenario::new("overnight/64x64 full crops", Some(budgets::OVERNIGHT_64), runs(60), |runs| {
            let farm = farm(FarmSpec::OVERNIGHT_64);
            sample(
                runs,
                || farm.state.clone(),
                |mut state| {
                    sleep(&farm.ctx, &mut state);
                    state
                },
            )
        }),
        Scenario::new("overnight/256x256 full crops + 50 machines", Some(budgets::OVERNIGHT_256), runs(15), |runs| {
            let farm = farm(FarmSpec::OVERNIGHT_256);
            sample(
                runs,
                || farm.state.clone(),
                |mut state| {
                    sleep(&farm.ctx, &mut state);
                    state
                },
            )
        }),
        // Missed by about 10x: see `budgets::SAVE_CEILING`.
        Scenario::new("save/stable json (sim thread)", Some(budgets::SAVE), runs(60), |runs| {
            let save = SaveFixture::mid_size();
            sample(runs, || (), |()| save.stable_json())
        })
        .known_over(budgets::SAVE_CEILING),
        Scenario::new("save/state snapshot (clone)", None, runs(60), |runs| {
            let save = SaveFixture::mid_size();
            sample(runs, || (), |()| save.state.clone())
        }),
        Scenario::new("save/binary FGSV", None, runs(30), |runs| {
            let save = SaveFixture::mid_size();
            sample(runs, || (), |()| save.write_binary())
        }),
        Scenario::new("save/json", None, runs(30), |runs| {
            let save = SaveFixture::mid_size();
            sample(runs, || (), |()| save.write_json())
        }),
        Scenario::new("save+load/binary FGSV", None, runs(20), |runs| {
            let save = SaveFixture::mid_size();
            sample(runs, || (), |()| save.load(&save.write_binary()))
        }),
    ];
    for (name, bytes) in sample_cartridges() {
        let parse = bytes.clone();
        all.push(Scenario::new(format!("cart load/{name}"), Some(budgets::CART_LOAD), runs(30), move |runs| {
            sample(runs, || (), |()| load_cartridge(&parse))
        }));
        all.push(Scenario::new(format!("cart start/{name}"), Some(budgets::CART_LOAD), runs(15), move |runs| {
            sample(runs, || (), |()| start_cartridge(&bytes))
        }));
    }
    for (name, walled) in
        [("npcs/20 walking, 64x64, 1000 ticks", false), ("npcs/20 walled off, 64x64, 1000 ticks", true)]
    {
        all.push(Scenario::new(name, Some(budgets::NPC_TICKS), runs(10), move |runs| {
            let town = npc_town(walled);
            sample(
                runs,
                || town.state.clone(),
                |mut state| {
                    npc_ticks(&town.ctx, &mut state);
                    state
                },
            )
        }));
    }
    // High resolutions are reported only: the CPU path recomposes the whole frame (see
    // docs/PLAYER.md "How a frame is drawn").
    let frames: [(&str, u32, u32, Option<f64>, NewPlayer); 5] = [
        ("", 1280, 800, Some(budgets::CPU_FRAME), gameplay_player),
        ("", 1920, 1080, Some(budgets::CPU_FRAME), gameplay_player),
        ("", 2560, 1600, None, gameplay_player),
        ("", 3840, 2160, None, gameplay_player),
        (" 256x256 map", 1920, 1080, Some(budgets::CPU_FRAME), large_map_player),
    ];
    for (map, width, height, budget, player) in frames {
        all.push(Scenario::new(format!("frame/{width}x{height}{map} cpu"), budget, runs(120), move |runs| {
            let mut player = player(width, height);
            let mut frame = 30;
            sample(
                runs,
                || (),
                |()| {
                    render_frame(&mut player, frame, width, height);
                    frame += 1;
                },
            )
        }));
    }
    all
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The fixtures build and every scenario does real work (a quick check in `cargo test`; the
    /// timing runs in release).
    #[test]
    fn the_scenarios_do_what_they_say() {
        let mut farm = farm(FarmSpec { size: 24, crop_field: 24, machines: 5 });
        let scene = &farm.state.world.scenes[0];
        assert_eq!((scene.width, scene.tiles.len(), scene.tiles[0].len()), (24, 24, 24));
        assert_eq!(scene.tiles.iter().flatten().filter(|tile| tile.crop.is_some()).count(), 24 * 24 - 5);
        let day = farm.state.clock.day;
        sleep(&farm.ctx, &mut farm.state);
        assert_eq!(farm.state.clock.day, day + 1);
        let tiles = || farm.state.world.scenes[0].tiles.iter().flatten();
        assert!(tiles().filter_map(|tile| tile.crop.as_ref()).any(|crop| crop.days_grown == Some(1)));
        assert_eq!(
            tiles().filter_map(|tile| tile.machine.as_ref()).filter(|machine| machine.output.is_some()).count(),
            5
        );

        let save = SaveFixture::mid_size();
        let loaded = save.load(&save.write_binary());
        assert_eq!(farm_sim::hash_state(&loaded.state.unwrap()), farm_sim::hash_state(&save.state));
        assert!(save.stable_json().len() > 100_000);

        let carts = sample_cartridges();
        assert!(carts.iter().any(|(name, _)| name == "project-v8"));
        for (_, bytes) in &carts {
            start_cartridge(bytes);
        }

        for walled in [false, true] {
            let mut town = npc_town(walled);
            let start = town.state.npcs["npc-0"].clone();
            npc_ticks(&town.ctx, &mut town.state);
            let moved = town.state.npcs["npc-0"] != start;
            assert_eq!(moved, !walled, "walkers walk, walled-off walkers stay");
        }

        // The large map plays: the player stands on its 256×256 farm.
        let large = large_map_player(320, 200);
        let scene = large.session().unwrap().current_scene().unwrap();
        assert_eq!((scene.width, scene.height), (256, 256));

        assert!(!scenarios(1.0).is_empty());
        assert_eq!(median(&[3.0, 1.0, 2.0]), 2.0);
        assert_eq!(median(&[4.0, 1.0, 2.0, 3.0]), 2.5);
    }
}
