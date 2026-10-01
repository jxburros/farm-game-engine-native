//! One running game: the deterministic engine loop of the web shell (`packages/game-shell`
//! `Shell.update`) and the editor's Play Mode (C# `PlaySession`), without any UI or platform.
//!
//! - All gameplay goes through engine commands and ticks. The only exception is
//!   [`PlaySession::debug`], the creator debug drawer, exactly as on the web.
//! - Frame time is fed in by the host ([`PlaySession::update`]) and converted to ticks by the
//!   fixed timestep, so tests drive it deterministically.
//! - Effects become [`SessionEvent`]s (toasts, sound cues, scene changes, new days) and floating
//!   pops; the host drains them once per frame.
//! - Plugins see every step's hook events (engine hooks, then one `onEffect` per effect), and
//!   their mutations enter the command log at one fixed point per tick (right before it), so a
//!   mutation answering tick `k` applies before tick `k + 1` at any frame rate, and a replay of
//!   the command log is deterministic.
//! - Minigames are hosted here: the UI forwards presses and choices, and a finished minigame
//!   enters the command log exactly once as `resolveMinigame`.

use farm_runtime::host::{HostedMinigame, MinigameInput, MinigameView};
use farm_runtime::input::{self, InputManager, Modifiers, MoveVector};
use farm_runtime::timestep::FixedTimestep;
use farm_sim::effects::message_levels;
use farm_sim::hooks::{EffectHookPayload, HookBus, HookEvent};
use farm_sim::schema::{GameContent, GameState, InventorySlot, Scene};
use farm_sim::{engine, game_time, quests, state, units, Command, Effect, EngineContext, StartState};
use serde_json::Value;

/// Play-mode tile size in world pixels (web `TILE_SIZE_PLAY`).
pub const TILE_SIZE: f64 = 32.0;
/// World padding around a scene in world pixels (web `CANVAS_PADDING`).
pub const PADDING: f64 = 12.0;
/// Preferred camera viewport in tiles (the editor's 20×13).
pub const VIEW_TILES_X: f64 = 20.0;
pub const VIEW_TILES_Y: f64 = 13.0;
/// Lifetime of a floating pop in ms (web `POP_LIFETIME_MS`).
pub const POP_LIFETIME_MS: f64 = 900.0;

/// Severity of a toast (web `toast.success/error/info`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ToastKind {
    Info,
    Success,
    Error,
}

/// Something the host should react to, in the order it happened.
#[derive(Debug, Clone, PartialEq, serde::Serialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum SessionEvent {
    /// A `message` effect (or a session notice) to show.
    Toast { text: String, kind: ToastKind },
    /// A sound cue (`farm_runtime::audio` preset name).
    Sound { cue: String },
    /// The player changed scene (the camera snaps; no interpolation across scenes).
    SceneChanged { scene_id: String },
    /// The overnight pass ran (hosts autosave here, as farming games do).
    DayStarted { day: u32, season: String, year: u32 },
    /// A quest was completed.
    QuestCompleted { quest_id: String },
}

/// Floating feedback text ("juice"): presentation only, never simulation state.
#[derive(Debug, Clone, PartialEq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Pop {
    /// Where it started, in tile coordinates (the player's position).
    pub x: f64,
    pub y: f64,
    pub text: String,
    pub color: String,
    /// 0 (just spawned) → 1 (expired).
    pub age: f64,
}

/// Host UI toggles requested by one frame's keyboard input.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameToggles {
    pub inventory: bool,
    pub quests: bool,
    pub crafting: bool,
    pub escape: bool,
}

impl FrameToggles {
    pub fn any(&self) -> bool {
        self.inventory || self.quests || self.crafting || self.escape
    }
}

/// The plugin sandbox as the session sees it (implemented over `farm-plugins`). `Send`, so a
/// session can run on a background thread (the editor steps frames off its UI thread).
pub trait SessionPlugins: Send {
    /// The hook events of one engine step, in order. `depth` is 0 for ticks and the player's
    /// commands, and a plugin mutation's depth + 1 for the step that ran it (plugins do not
    /// hear `onCommand` / `onEffect` from such steps, and deep chains are cut off).
    fn dispatch(&mut self, events: &[HookEvent], depth: u32);
    /// Plugin mutations to run now, as `pluginMutation` commands with their depth, in arrival
    /// order.
    fn drain_commands(&mut self) -> Vec<(Command, u32)>;
    /// The game is about to run tick `tick` (plugin budgets follow the game clock).
    fn begin_tick(&mut self, _tick: u64) {}
    /// The most recent plugin errors (init failures, throws, overruns), oldest first.
    fn recent_errors(&self) -> Vec<String> {
        Vec::new()
    }
}

/// A creator debug-drawer action (web `debugMutate`): tooling, never gameplay.
#[derive(Debug, Clone, PartialEq, serde::Deserialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum DebugAction {
    AddMoney {
        amount: f64,
    },
    FullEnergy,
    AddMinutes {
        minutes: f64,
    },
    SetSeason {
        season: String,
    },
    /// Five of the first item of this type (`seed`, `material`, …).
    GiveFirst {
        item_type: String,
    },
    Teleport {
        scene_id: String,
    },
    SetFlag {
        flag: String,
    },
    SkipDay,
}

struct MountedMinigame {
    /// The `state.minigame` this mount belongs to.
    active: farm_sim::schema::MinigameSession,
    game: HostedMinigame,
    view: MinigameView,
    reported: bool,
}

/// One running game. `Send` but not shared: one thread at a time drives it.
pub struct PlaySession {
    ctx: EngineContext,
    state: GameState,
    timestep: FixedTimestep,
    input: InputManager,
    last_intent: MoveVector,
    prev_player: Option<(f64, f64, String)>,
    pops: Vec<(Pop, f64)>,
    elapsed_ms: f64,
    alpha: f64,
    events: Vec<SessionEvent>,
    minigame: Option<MountedMinigame>,
    plugins: Option<Box<dyn SessionPlugins>>,
    reduced_motion: bool,
    /// Cosmetic randomness for minigames (never the simulation RNG).
    cosmetic: u64,
    /// The last commands run, as JSON with their tick (crash reports).
    recent: std::collections::VecDeque<String>,
}

/// Commands kept for crash reports.
pub const RECENT_COMMANDS: usize = 64;

impl std::fmt::Debug for PlaySession {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PlaySession")
            .field("tick", &self.state.clock.tick)
            .field("scene", &self.state.player.scene_id)
            .field("plugins", &self.plugins.is_some())
            .finish_non_exhaustive()
    }
}

/// A simulation position (1/8192 tile) in tiles, for drawing.
fn tiles(position: i32) -> f64 {
    units::position_to_tiles(position)
}

impl PlaySession {
    /// A session over `content` starting at `state` (a new game or a loaded save). A save written
    /// mid-walk carries a held intent; the first frame releases it (no key is held yet).
    pub fn new(content: GameContent, state: GameState) -> Self {
        let held = MoveVector::new(f64::from(state.player.move_intent.dx), f64::from(state.player.move_intent.dy));
        Self {
            ctx: EngineContext::with_hooks(content, HookBus::new()),
            state,
            timestep: FixedTimestep::new(),
            input: InputManager::new(),
            last_intent: held,
            prev_player: None,
            pops: Vec::new(),
            elapsed_ms: 0.0,
            alpha: 0.0,
            events: Vec::new(),
            minigame: None,
            plugins: None,
            reduced_motion: false,
            cosmetic: 0x9E37_79B9_7F4A_7C15,
            recent: std::collections::VecDeque::new(),
        }
    }

    /// A new game: the start state (with its default seed unless `seed` is given) with
    /// auto-start quests begun, as hosts do at game start.
    pub fn new_game(content: GameContent, start: &StartState, seed: Option<&str>) -> Self {
        let game_state = state::create_game_state_from_start(start, seed);
        let mut session = Self::new(content, game_state);
        // Hook events of the auto-start stay on the bus until plugins attach (see
        // [`Self::set_plugins`]), as the web and C# bridges listen before the game starts.
        quests::auto_start_quests(&session.ctx, &mut session.state);
        session.sync_minigame();
        session
    }

    /// Attach the plugin sandbox. Hook events not yet delivered (those of a new game's
    /// auto-started quests) go to it first; then it sees every later step.
    pub fn set_plugins(&mut self, plugins: Option<Box<dyn SessionPlugins>>) {
        self.plugins = plugins;
        self.dispatch_hooks(&[], 0);
    }

    pub fn has_plugins(&self) -> bool {
        self.plugins.is_some()
    }

    /// The most recent plugin errors, oldest first (empty without plugins).
    pub fn plugin_errors(&self) -> Vec<String> {
        self.plugins.as_ref().map(|plugins| plugins.recent_errors()).unwrap_or_default()
    }

    /// Seed for cosmetic randomness (minigame target placement); tests pin it.
    pub fn set_cosmetic_seed(&mut self, seed: u64) {
        self.cosmetic = seed | 1;
    }

    pub fn set_reduced_motion(&mut self, reduced: bool) {
        self.reduced_motion = reduced;
        if reduced {
            self.pops.clear();
        }
    }

    pub fn reduced_motion(&self) -> bool {
        self.reduced_motion
    }

    pub fn content(&self) -> &GameContent {
        &self.ctx.content
    }

    pub fn context(&self) -> &EngineContext {
        &self.ctx
    }

    pub fn state(&self) -> &GameState {
        &self.state
    }

    /// Raw keyboard state (the host forwards key events here, in farm-runtime key names).
    pub fn input_mut(&mut self) -> &mut InputManager {
        &mut self.input
    }

    pub fn key_down(&mut self, key: &str) -> bool {
        self.input.key_down(key, Modifiers::NONE)
    }

    pub fn key_up(&mut self, key: &str) {
        self.input.key_up(key);
    }

    /// The last commands run (oldest first), with the tick they ran at: what a crash report
    /// needs to replay the moments before a crash from the last save.
    pub fn recent_commands(&self) -> Vec<String> {
        self.recent.iter().cloned().collect()
    }

    /// Wall-clock time fed in so far, in ms (drives pops).
    pub fn elapsed_ms(&self) -> f64 {
        self.elapsed_ms
    }

    /// Interpolation alpha of the fixed timestep (0..1).
    pub fn alpha(&self) -> f64 {
        self.alpha
    }

    /// The scene the player is in (the first scene when the id is unknown).
    pub fn current_scene(&self) -> Option<&Scene> {
        let scenes = &self.state.world.scenes;
        scenes.iter().find(|scene| scene.id == self.state.player.scene_id).or_else(|| scenes.first())
    }

    /// True while an engine-owned modal (dialogue, shop, minigame) is open.
    pub fn engine_modal_open(&self) -> bool {
        self.state.dialogue.is_some() || self.state.shop.is_some() || self.state.minigame.is_some()
    }

    /// Events since the last call, in order.
    pub fn drain_events(&mut self) -> Vec<SessionEvent> {
        std::mem::take(&mut self.events)
    }

    /// Live pops with their current age (none under reduced motion).
    pub fn pops(&self) -> Vec<Pop> {
        if self.reduced_motion {
            return Vec::new();
        }
        self.pops
            .iter()
            .map(|(pop, born)| Pop { age: (self.elapsed_ms - born) / POP_LIFETIME_MS, ..pop.clone() })
            .collect()
    }

    /// Run one command from the UI and react to its effects.
    pub fn run_command(&mut self, command: &Command) {
        self.run_step(command, 0);
    }

    /// Run a command as a step at plugin depth `depth` (see [`SessionPlugins::dispatch`]).
    fn run_step(&mut self, command: &Command, depth: u32) {
        if self.recent.len() >= RECENT_COMMANDS {
            self.recent.pop_front();
        }
        let json = serde_json::to_string(command).unwrap_or_default();
        self.recent.push_back(format!("tick {}: {json}", self.state.clock.tick));
        let effects = engine::apply_command(&self.ctx, &mut self.state, command);
        self.after_step(effects, depth);
    }

    /// The fixed point before each tick: queued plugin mutations enter the command log, each
    /// as a step one level deeper than the mutation.
    fn apply_plugin_mutations(&mut self) {
        let Some(plugins) = self.plugins.as_mut() else { return };
        plugins.begin_tick(self.state.clock.tick);
        for (command, depth) in plugins.drain_commands() {
            self.run_step(&command, depth.saturating_add(1));
        }
    }

    /// One display frame: sync the held movement intent, advance the fixed-timestep
    /// simulation one tick at a time (queued plugin mutations apply before each tick), then
    /// turn this frame's one-shot key presses into commands. `host_modal_open` is true while a
    /// host panel (inventory, quests, crafting, a menu) is open: world input pauses. Non-finite
    /// or negative frame times count as zero.
    pub fn update(&mut self, delta_seconds: f64, host_modal_open: bool) -> FrameToggles {
        let delta = if delta_seconds.is_finite() { delta_seconds.max(0.0) } else { 0.0 };
        self.elapsed_ms += delta * 1000.0;
        self.pops.retain(|(_, born)| self.elapsed_ms - born < POP_LIFETIME_MS);

        // Free movement: only CHANGES of the held intent become commands; zero while any
        // modal is open.
        let intent =
            if host_modal_open { MoveVector::default() } else { input::move_intent(&self.input, &self.state, None) };
        let ticks = self.timestep.advance(delta);
        self.alpha = self.timestep.alpha();
        if intent != self.last_intent {
            self.last_intent = intent;
            // The intent is −1, 0 or 1 on each axis.
            self.run_command(&Command::SetMoveIntent { dx: intent.dx as i32, dy: intent.dy as i32 });
        }
        // One tick at a time, so plugin mutations answering tick k apply before tick k + 1
        // whether the frame holds one tick or five: the same input reaches the same state at
        // any frame rate. (advance_tick(n) is exactly n × advance_tick(1).)
        for tick in 0..ticks {
            self.apply_plugin_mutations();
            if tick == 0 {
                let (x, y) = (tiles(self.state.player.x), tiles(self.state.player.y));
                self.prev_player = Some((x, y, self.state.player.scene_id.clone()));
            }
            let effects = engine::advance_tick(&self.ctx, &mut self.state, 1);
            self.after_step(effects, 0);
        }

        let frame = input::poll_play_frame(&self.input, &self.state, &self.ctx.content, host_modal_open);
        for command in &frame.commands {
            self.run_command(command);
        }
        self.update_minigame(delta);
        self.input.end_frame();
        FrameToggles {
            inventory: frame.toggle_inventory,
            quests: frame.toggle_quests,
            crafting: frame.toggle_crafting,
            escape: frame.escape,
        }
    }

    /// Clears held keys and the sent intent (focus loss, opening a menu, leaving play).
    pub fn release_input(&mut self) {
        self.input.clear();
        if self.last_intent != MoveVector::default() {
            self.last_intent = MoveVector::default();
            self.run_command(&Command::SetMoveIntent { dx: 0, dy: 0 });
        }
    }

    /// The player's position between the last two tick states (tile units), for rendering.
    pub fn interpolated_player(&self) -> (f64, f64) {
        let player = &self.state.player;
        let (px, py) = (tiles(player.x), tiles(player.y));
        match &self.prev_player {
            Some((x, y, scene)) if *scene == player.scene_id => (x + (px - x) * self.alpha, y + (py - y) * self.alpha),
            _ => (px, py),
        }
    }

    /// World size of the current scene in pixels (contiguous tiles, padding on every side).
    pub fn world_size(&self) -> (f64, f64) {
        let (w, h) = self.current_scene().map_or((1.0, 1.0), |scene| (f64::from(scene.width), f64::from(scene.height)));
        (w * TILE_SIZE + PADDING * 2.0, h * TILE_SIZE + PADDING * 2.0)
    }

    /// The camera viewport in world pixels for a host area at `zoom`: never larger than the
    /// world (which is then centered).
    pub fn viewport_for(&self, host_width: f64, host_height: f64, zoom: f64) -> (f64, f64) {
        let (world_w, world_h) = self.world_size();
        (world_w.min((host_width / zoom).max(TILE_SIZE)), world_h.min((host_height / zoom).max(TILE_SIZE)))
    }

    /// The mounted minigame's view (after the host mounted it through [`Self::update`]).
    pub fn minigame_view(&self) -> Option<&MinigameView> {
        self.minigame.as_ref().map(|mounted| &mounted.view)
    }

    /// Forward a press/release/choice to the running minigame. A finished minigame reports its
    /// score through the command log exactly once.
    pub fn minigame_input(&mut self, input: MinigameInput) {
        let Some(mounted) = self.minigame.as_mut() else { return };
        if mounted.reported || mounted.view.is_done {
            return;
        }
        if let Ok(view) = mounted.game.apply(input) {
            mounted.view = view;
        }
        self.report_minigame();
    }

    /// Creator debug tooling: bypasses the command pipeline on purpose. Never used by gameplay.
    pub fn debug(&mut self, action: &DebugAction) {
        match action {
            DebugAction::AddMoney { amount } if amount.is_finite() => {
                // Money is whole gold.
                self.state.player.money = self.state.player.money.saturating_add(amount.round() as i64);
            }
            DebugAction::FullEnergy => self.state.player.energy = self.state.player.max_energy,
            DebugAction::AddMinutes { minutes } if minutes.is_finite() => {
                let micro =
                    i64::from(self.state.clock.time_minutes) + (minutes * f64::from(units::MINUTE)).round() as i64;
                self.state.clock.time_minutes = micro.clamp(0, i64::from(u32::MAX)) as u32;
            }
            DebugAction::SetSeason { season } => self.state.clock.season.clone_from(season),
            DebugAction::GiveFirst { item_type } => {
                let Some(item) = self.ctx.content.items.iter().find(|item| &item.r#type == item_type) else {
                    return;
                };
                let inventory = &mut self.state.player.inventory;
                match inventory.iter_mut().find(|slot| slot.item.id == item.id) {
                    Some(slot) => slot.quantity = slot.quantity.saturating_add(5),
                    None => inventory.push(InventorySlot { item: item.clone(), quantity: 5 }),
                }
            }
            DebugAction::Teleport { scene_id } => {
                let Some(scene) = self.state.world.scenes.iter().find(|scene| &scene.id == scene_id) else {
                    return;
                };
                // Free movement: land on the center tile's center.
                let (x, y) =
                    (units::tile_center(scene.width.div_euclid(2)), units::tile_center(scene.height.div_euclid(2)));
                self.state.player.scene_id.clone_from(scene_id);
                self.state.player.x = x;
                self.state.player.y = y;
            }
            DebugAction::SetFlag { flag } => {
                let flag = flag.trim();
                if !flag.is_empty() {
                    self.state.flags.insert(flag.to_owned(), Value::Bool(true));
                }
            }
            DebugAction::SkipDay => {
                game_time::perform_sleep(&self.ctx, &mut self.state, game_time::SleepOptions { collapsed: false });
                self.dispatch_hooks(&[], 0);
            }
            DebugAction::AddMoney { .. } | DebugAction::AddMinutes { .. } => {}
        }
        self.prev_player = None;
        self.sync_minigame();
    }

    /// Replace the state as it is (creator tools, loading a save). Minigame mounts, held input
    /// and interpolation reset.
    pub fn replace_state(&mut self, state: GameState) {
        self.state = state;
        self.prev_player = None;
        self.minigame = None;
        self.timestep.reset();
        self.input.clear();
        let intent = &self.state.player.move_intent;
        if intent.dx != 0 || intent.dy != 0 {
            // A save written mid-walk carries a held intent; the keys aren't held any more.
            self.last_intent = MoveVector::new(f64::from(intent.dx), f64::from(intent.dy));
            self.release_input();
        } else {
            self.last_intent = MoveVector::default();
        }
    }

    fn after_step(&mut self, effects: Vec<Effect>, depth: u32) {
        self.dispatch_hooks(&effects, depth);
        for effect in &effects {
            if let Some(cue) = farm_runtime::audio::sfx_for_effect(effect) {
                self.events.push(SessionEvent::Sound { cue: cue.to_owned() });
            }
            match effect {
                Effect::CropHarvested { quantity, .. } => {
                    self.add_pop(format!("+{quantity}"), "#8fd06c");
                }
                Effect::QuestCompleted { quest_id } => {
                    self.add_pop("Quest ✓".to_owned(), "#ffd94a");
                    self.events.push(SessionEvent::QuestCompleted { quest_id: quest_id.clone() });
                }
                Effect::Message { level, text } => {
                    let kind = match level.as_str() {
                        message_levels::SUCCESS => ToastKind::Success,
                        message_levels::ERROR => ToastKind::Error,
                        _ => ToastKind::Info,
                    };
                    self.events.push(SessionEvent::Toast { text: text.clone(), kind });
                }
                Effect::SceneChanged { scene_id, .. } => {
                    // Teleports/transitions never interpolate across scenes.
                    self.prev_player = None;
                    self.events.push(SessionEvent::SceneChanged { scene_id: scene_id.clone() });
                }
                Effect::DayStarted { day, season, year } => {
                    self.events.push(SessionEvent::DayStarted { day: *day, season: season.clone(), year: *year });
                }
                Effect::PlayerMoved { .. } | Effect::Sound { .. } => {}
            }
        }
        self.sync_minigame();
    }

    /// Engine hooks of the step, then one `onEffect` per effect (the order plugins see on the
    /// web and in the C# bridge).
    fn dispatch_hooks(&mut self, effects: &[Effect], depth: u32) {
        let mut events = self.ctx.drain_hook_events();
        let Some(plugins) = self.plugins.as_mut() else { return };
        events.extend(
            effects.iter().map(|e| HookEvent::Effect(EffectHookPayload { effect_type: e.type_name().to_owned() })),
        );
        if !events.is_empty() {
            plugins.dispatch(&events, depth);
        }
    }

    fn add_pop(&mut self, text: String, color: &str) {
        if self.reduced_motion {
            return;
        }
        let (x, y) = (tiles(self.state.player.x), tiles(self.state.player.y));
        let pop = Pop { x, y, text, color: color.to_owned(), age: 0.0 };
        self.pops.push((pop, self.elapsed_ms));
    }

    /// Mount a minigame when the engine opened one; drop a mount the engine closed.
    fn sync_minigame(&mut self) {
        match self.state.minigame.clone() {
            None => self.minigame = None,
            Some(active) => {
                if self.minigame.as_ref().is_some_and(|mounted| mounted.active == active) {
                    return;
                }
                let random = self.next_cosmetic();
                let definition = self.ctx.content.minigames.iter().find(|def| def.id == active.minigame_id);
                match HostedMinigame::mount(definition, random) {
                    Ok(game) => {
                        let view = game.view();
                        self.minigame = Some(MountedMinigame { active, game, view, reported: false });
                    }
                    Err(_) => self.minigame = None,
                }
            }
        }
    }

    fn update_minigame(&mut self, delta: f64) {
        if let Some(mounted) = self.minigame.as_mut() {
            if !mounted.reported && !mounted.view.is_done {
                if let Ok(view) = mounted.game.apply(MinigameInput::Update { seconds: delta }) {
                    mounted.view = view;
                }
            }
        }
        self.report_minigame();
    }

    fn report_minigame(&mut self) {
        let score = match self.minigame.as_mut() {
            Some(mounted) if !mounted.reported => match mounted.view.score {
                Some(score) => {
                    mounted.reported = true;
                    score
                }
                None => return,
            },
            _ => return,
        };
        // The host scores 0–1 as a double; the command carries it on the probability grid.
        self.run_command(&Command::ResolveMinigame { score: units::chance(score) });
    }

    /// Uniform [0, 1) from a xorshift64* stream (cosmetic only).
    fn next_cosmetic(&mut self) -> f64 {
        let mut x = self.cosmetic;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.cosmetic = x;
        (x.wrapping_mul(0x2545_F491_4F6C_DD1D) >> 11) as f64 / (1u64 << 53) as f64
    }
}
