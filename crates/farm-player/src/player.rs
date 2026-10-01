//! The embeddable graphical player: one [`PlaySession`], the in-game UI (`farm-ui`), the game
//! shell's state machine, saves, settings and rendering behind a single call per frame.
//!
//! ```text
//! frame(dt, events, width, height)
//!   1. route input (bindings, gamepad, pointer)            → game keys + UI input
//!   2. step the session unless a shell screen pauses it    → events: toasts, sounds, autosave
//!   3. build the world snapshot, draw the UI               → game and shell actions
//!   4. run the actions (engine commands, screens, saves)
//!   5. compose pixels: world at native size, scaled up; UI on top at full resolution
//! ```
//!
//! Two modes: [`PlayerMode::Standalone`] (an exported game: title screen, save slots, autosave
//! each morning, pause menu, settings, credits, quit) and [`PlayerMode::Embedded`] (the editor's
//! Play Mode: straight into the game, no title, saves or quitting; the host keeps "keep
//! changes", restart and the debug drawer and reaches the state through this API).
//!
//! `Player` is `Send`: a host may step it on a background thread and hand the pixels to its UI
//! thread. Engine panics are caught and returned as [`PlayerError`]s; the player is then
//! poisoned and refuses further frames.

use crate::audio::SoundRequest;
use crate::input::{FrameInput, GameKey, InputEvent, InputRouter};
use crate::render::{world_view, FrameRenderer, WorldView};
use crate::saves::{
    settings_from_toml, settings_to_toml, MemorySaveStore, MemorySettingsStore, SaveStore, SettingsStore, SLOT_COUNT,
};
use crate::session::{DebugAction, PlaySession, SessionEvent, ToastKind as SessionToast, PADDING, TILE_SIZE};
use farm_cart::save_file::{self, SavePreview, SaveTarget};
use farm_cart::{AssetTable, CartPlugin, GameInfoOwned, LoadedCartridge};
use farm_plugins::PluginHostOptions;
use farm_render::graphics::{art_assets, ArtAsset};
use farm_render::tiny_skia::Pixmap;
use farm_render::{
    apply_graphics, compute_camera, shell_snapshot, BuiltinArt, DrawList, GraphicsSource, SnapshotOptions, SnapshotPop,
    WorldSnapshot,
};
use farm_runtime::host::{calendar_view, MinigameInput};
use farm_runtime::panels::{self, PanelState};
use farm_sim::schema::{GameContent, GameProject, GameState};
use farm_sim::{overlay, state, units, Presentation, StartState};
use farm_ui::game::{GameAction, GameUi, GameView, ItemArt, Panel, ToastKind};
use farm_ui::settings::BindAction;
use farm_ui::shell::{
    self, ConfirmView, CreditsView, PauseView, SettingsScreen, ShellAction, SlotPreviewView, SlotView, SlotsMode,
    SlotsView, TitleView,
};
use farm_ui::{Lang, Settings, Theme, Ui};
use serde_json::Value;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::Arc;

/// A world snapshot, the world's size and the camera target (pixels).
type WorldFrame = (WorldSnapshot, (f64, f64), (f64, f64));

/// Where the player runs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlayerMode {
    /// An exported game: title screen, save slots, pause menu, quitting.
    Standalone,
    /// The editor's Play Mode: straight into the game; the host owns restart and saving.
    Embedded,
}

/// Something the host should do.
#[derive(Debug, Clone, PartialEq)]
pub enum PlayerRequest {
    /// Close the game (Quit on the title screen or in the pause menu).
    Quit,
    /// Switch to borderless fullscreen (`true`) or a window.
    SetFullscreen(bool),
    /// The window title (sent on the first frame).
    SetTitle(String),
}

/// Why the player could not do what was asked.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PlayerError {
    /// The cartridge or project could not be loaded.
    Load(String),
    /// The engine (or a renderer) panicked; the player is poisoned.
    Engine(String),
    /// A previous failure poisoned the player.
    Poisoned(String),
    /// The call needs a running game.
    NoGame,
}

impl std::fmt::Display for PlayerError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            PlayerError::Load(message) => write!(f, "The game could not be loaded: {message}"),
            PlayerError::Engine(message) => write!(f, "The game stopped: {message}"),
            PlayerError::Poisoned(message) => write!(f, "The game stopped earlier: {message}"),
            PlayerError::NoGame => write!(f, "No game is running."),
        }
    }
}

impl std::error::Error for PlayerError {}

/// What a frame produced.
#[derive(Debug)]
pub struct FrameOutput<'a> {
    /// The frame: premultiplied RGBA, `width`×`height`, opaque.
    pub pixels: &'a Pixmap,
    pub sounds: Vec<SoundRequest>,
    pub requests: Vec<PlayerRequest>,
}

/// What a frame produced when not rendered ([`Player::step`]).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StepOutput {
    pub sounds: Vec<SoundRequest>,
    pub requests: Vec<PlayerRequest>,
}

/// Which screen is in front (tests, hosts).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScreenKind {
    Title,
    Playing,
    Pause,
    Settings,
    Credits,
    LoadSlots,
    SaveSlots,
    NewGameSlots,
    Confirm,
}

/// How a player is set up.
pub struct PlayerOptions {
    pub mode: PlayerMode,
    /// Seed of new games (the start state's own seed when `None`).
    pub seed: Option<String>,
    pub saves: Box<dyn SaveStore>,
    pub settings: Box<dyn SettingsStore>,
    /// Unix seconds (save times); tests pin it.
    pub clock: Box<dyn Fn() -> i64 + Send>,
    pub theme: Theme,
    pub plugins: PluginHostOptions,
    /// Offer Quit buttons (desktop games).
    pub can_quit: bool,
    /// The system's locale (`es-MX`), which picks the interface language while the player's
    /// language setting is automatic. Hosts that know it set it; `None` skips to the game's own
    /// locale (screenshot runs and tests stay in English).
    pub system_locale: Option<String>,
}

impl std::fmt::Debug for PlayerOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PlayerOptions").field("mode", &self.mode).field("seed", &self.seed).finish_non_exhaustive()
    }
}

impl PlayerOptions {
    /// An exported game with in-memory saves and settings (hosts replace the stores).
    pub fn standalone() -> Self {
        Self {
            mode: PlayerMode::Standalone,
            seed: None,
            saves: Box::new(MemorySaveStore::new()),
            settings: Box::new(MemorySettingsStore::new()),
            clock: Box::new(|| 0),
            theme: Theme::cozy(),
            plugins: PluginHostOptions::default(),
            can_quit: true,
            system_locale: None,
        }
    }

    /// The editor's Play Mode.
    pub fn embedded() -> Self {
        Self { mode: PlayerMode::Embedded, can_quit: false, ..Self::standalone() }
    }
}

/// The game a player runs, as loaded.
struct GameDef {
    info: GameInfoOwned,
    content: GameContent,
    start: StartState,
    presentation: Presentation,
    target: SaveTarget,
    plugins: Vec<CartPlugin>,
    /// Set when started from an editor project ("keep changes" writes the state back).
    project: Option<GameProject>,
    art: Vec<ArtAsset>,
}

struct Game {
    session: PlaySession,
    graphics: GraphicsSource,
    /// The slot autosaves go to (0 in embedded mode: none).
    slot: u32,
    play_seconds: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Confirmation {
    QuitToTitle,
    QuitGame,
    Overwrite(u32),
    Delete(u32),
    NewGameOver(u32),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Screen {
    Title,
    Pause,
    Settings,
    Credits,
    Slots(SlotsMode),
    Confirm(Confirmation),
}

#[derive(Debug, Clone, Default)]
struct SlotInfo {
    preview: Option<SavePreview>,
    /// A file exists but its preview could not be read.
    unreadable: bool,
    thumbnail: Option<(farm_render::ImageId, f32, f32)>,
}

/// The graphical player (see the module docs).
pub struct Player {
    mode: PlayerMode,
    def: GameDef,
    seed: Option<String>,
    plugin_options: PluginHostOptions,
    can_quit: bool,
    system_locale: Option<String>,
    game: Option<Game>,
    screens: Vec<Screen>,
    ui: Ui,
    game_ui: GameUi,
    settings: Settings,
    settings_screen: SettingsScreen,
    settings_store: Box<dyn SettingsStore>,
    saves: Box<dyn SaveStore>,
    slots: Vec<SlotInfo>,
    clock: Box<dyn Fn() -> i64 + Send>,
    router: InputRouter,
    renderer: FrameRenderer,
    ui_list: DrawList,
    world: Option<(WorldSnapshot, WorldView)>,
    title_state: Option<GameState>,
    sounds: Vec<SoundRequest>,
    requests: Vec<PlayerRequest>,
    poisoned: Option<String>,
    time: f64,
    started: bool,
    thumb_serial: u64,
}

impl std::fmt::Debug for Player {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Player")
            .field("mode", &self.mode)
            .field("game", &self.def.info.game_id)
            .field("screen", &self.screen())
            .field("poisoned", &self.poisoned)
            .finish_non_exhaustive()
    }
}

fn export_string(project: &GameProject, key: &str) -> Option<String> {
    project.extra.get("export")?.get(key).and_then(Value::as_str).filter(|s| !s.is_empty()).map(str::to_owned)
}

fn panic_message(payload: &(dyn std::any::Any + Send)) -> String {
    payload
        .downcast_ref::<&str>()
        .map(|s| (*s).to_owned())
        .or_else(|| payload.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "unknown panic".to_owned())
}

fn toast_kind(kind: SessionToast) -> ToastKind {
    match kind {
        SessionToast::Info => ToastKind::Info,
        SessionToast::Success => ToastKind::Success,
        SessionToast::Error => ToastKind::Error,
    }
}

impl Player {
    /// A player for compiled cartridge bytes (`game.cart`).
    pub fn from_cartridge_bytes(bytes: &[u8], options: PlayerOptions) -> Result<Self, PlayerError> {
        let cart = farm_cart::load_cartridge(bytes).map_err(PlayerError::Load)?;
        Self::from_cartridge(cart, options)
    }

    /// A player for a loaded cartridge.
    pub fn from_cartridge(cart: LoadedCartridge, options: PlayerOptions) -> Result<Self, PlayerError> {
        let target = SaveTarget::for_cartridge(&cart);
        let art = art_assets(&cart.presentation.custom_assets);
        let def = GameDef {
            info: cart.info,
            content: cart.content,
            start: cart.start,
            presentation: cart.presentation,
            target,
            plugins: cart.plugins,
            project: None,
            art,
        };
        Self::new(def, cart.assets, options)
    }

    /// A player for an editor project (the editor's Play Mode; standalone works too).
    pub fn from_project(project: GameProject, options: PlayerOptions) -> Result<Self, PlayerError> {
        let content = state::create_content_from_project(&project);
        let target = SaveTarget::for_project(&project, &content);
        let presentation = Presentation::from_project(&project);
        let info = GameInfoOwned {
            title: export_string(&project, "title").unwrap_or_else(|| project.name.clone()),
            version: target.game_version.clone(),
            game_id: target.game_id.clone(),
            author: export_string(&project, "author"),
            company: export_string(&project, "company"),
            executable_name: None,
            window_width: 1280,
            window_height: 800,
            fullscreen: false,
            pixel_scale: None,
            credits: export_string(&project, "credits"),
        };
        let def = GameDef {
            info,
            start: StartState::from_project(&project),
            art: art_assets(&presentation.custom_assets),
            presentation,
            content,
            target,
            plugins: Vec::new(),
            project: Some(project),
        };
        Self::new(def, AssetTable::default(), options)
    }

    fn new(def: GameDef, assets: AssetTable, options: PlayerOptions) -> Result<Self, PlayerError> {
        let settings = options.settings.load().and_then(|text| settings_from_toml(&text).ok()).unwrap_or_else(|| {
            // First run: the game's own defaults (GameInfo window and pixel scale).
            let mut settings = Settings::default();
            settings.display.fullscreen = def.info.fullscreen;
            settings.display.integer_scaling = def.info.pixel_scale.as_deref() != Some("fit");
            settings
        });
        let mut ui = Ui::new(options.theme);
        ui.set_reduced_motion(settings.accessibility.reduced_motion);
        ui.set_readable_font(settings.accessibility.readable_font);
        ui.set_lang(Lang::resolve(&settings.language, options.system_locale.as_deref(), &def.content.settings.locale));
        let mut player = Self {
            mode: options.mode,
            def,
            seed: options.seed,
            plugin_options: options.plugins,
            can_quit: options.can_quit,
            system_locale: options.system_locale,
            game: None,
            screens: Vec::new(),
            ui,
            game_ui: GameUi::new(),
            settings,
            settings_screen: SettingsScreen::default(),
            settings_store: options.settings,
            saves: options.saves,
            slots: Vec::new(),
            clock: options.clock,
            router: InputRouter::new(),
            renderer: FrameRenderer::new(Arc::new(assets)),
            ui_list: DrawList::new(),
            world: None,
            title_state: None,
            sounds: Vec::new(),
            requests: Vec::new(),
            poisoned: None,
            time: 0.0,
            started: false,
            thumb_serial: 0,
        };
        match player.mode {
            PlayerMode::Standalone => {
                player.screens.push(Screen::Title);
                player.refresh_slots();
            }
            PlayerMode::Embedded => {
                let result = catch_unwind(AssertUnwindSafe(|| player.start_game(0)));
                if let Err(panic) = result {
                    return Err(PlayerError::Engine(panic_message(&*panic)));
                }
            }
        }
        Ok(player)
    }

    // ── Frames ─────────────────────────────────────────────────────────

    /// One frame: input, simulation, UI, pixels. `dt_seconds` is the time since the last frame.
    pub fn frame(
        &mut self,
        dt_seconds: f64,
        events: &[InputEvent],
        width: u32,
        height: u32,
    ) -> Result<FrameOutput<'_>, PlayerError> {
        self.run(dt_seconds, events, width, height, true)?;
        Ok(FrameOutput {
            pixels: self.renderer.frame(),
            sounds: std::mem::take(&mut self.sounds),
            requests: std::mem::take(&mut self.requests),
        })
    }

    /// [`Player::frame`] without composing pixels (tests, headless hosts). The UI is still laid
    /// out at `width`×`height`, so clicks and focus behave the same.
    pub fn step(
        &mut self,
        dt_seconds: f64,
        events: &[InputEvent],
        width: u32,
        height: u32,
    ) -> Result<StepOutput, PlayerError> {
        self.run(dt_seconds, events, width, height, false)?;
        Ok(StepOutput { sounds: std::mem::take(&mut self.sounds), requests: std::mem::take(&mut self.requests) })
    }

    fn run(
        &mut self,
        dt: f64,
        events: &[InputEvent],
        width: u32,
        height: u32,
        render: bool,
    ) -> Result<(), PlayerError> {
        if let Some(message) = &self.poisoned {
            return Err(PlayerError::Poisoned(message.clone()));
        }
        let (width, height) = (width.max(1), height.max(1));
        let result = catch_unwind(AssertUnwindSafe(|| {
            self.advance(dt, events, width, height);
            if render {
                self.compose(width, height);
            }
        }));
        match result {
            Ok(()) => Ok(()),
            Err(panic) => {
                let message = panic_message(&*panic);
                self.poisoned = Some(message.clone());
                Err(PlayerError::Engine(message))
            }
        }
    }

    fn advance(&mut self, dt: f64, events: &[InputEvent], width: u32, height: u32) {
        let dt = if dt.is_finite() { dt.clamp(0.0, 0.25) } else { 0.0 };
        self.time += dt;
        if !self.started {
            self.started = true;
            self.requests.push(PlayerRequest::SetTitle(self.def.info.title.clone()));
            if self.mode == PlayerMode::Standalone && self.settings.display.fullscreen {
                self.requests.push(PlayerRequest::SetFullscreen(true));
            }
        }
        let capture = self.screens.last() == Some(&Screen::Settings) && self.settings_screen.capture.is_some();
        let input = self.router.frame(events, dt, &self.settings.controls, capture);
        if input.toggle_fullscreen {
            self.settings.display.fullscreen = !self.settings.display.fullscreen;
            self.requests.push(PlayerRequest::SetFullscreen(self.settings.display.fullscreen));
            self.store_settings();
        }

        let playing = self.game.is_some() && self.screens.is_empty();
        let game_modal = playing
            && self.game.as_ref().is_some_and(|game| self.game_ui.panel.is_some() || game.session.engine_modal_open());
        if playing {
            self.step_game(dt, &input);
        }
        self.game_ui.toasts.tick(dt);

        // The UI owns keyboard and gamepad when a menu or a game modal is up; otherwise they
        // drive the game and only the pointer reaches the HUD.
        let mut ui_input = input.ui;
        if playing && !game_modal {
            ui_input.nav.clear();
            ui_input.keys_pressed.clear();
            ui_input.accept_held = false;
        }
        self.build_world(width, height);
        self.draw_ui(ui_input, width, height, playing, game_modal);
    }

    fn step_game(&mut self, dt: f64, input: &FrameInput) {
        let Some(game) = self.game.as_mut() else { return };
        let session = &mut game.session;
        for key in &input.game {
            match key {
                GameKey::Down(key) => {
                    session.key_down(key);
                }
                GameKey::Up(key) => session.key_up(key),
            }
        }
        if input.focus_lost {
            session.release_input();
        }
        // Space / Enter / A press and release the minigame's button (choices are buttons).
        if session.minigame_view().is_some_and(|view| view.choices.is_empty()) {
            if input.accept_pressed {
                session.minigame_input(MinigameInput::Press);
            }
            if input.accept_released {
                session.minigame_input(MinigameInput::Release);
            }
        }
        // A panel's own hotkey closes it again (I / J / X), like the editor.
        let own_key = self.game_ui.panel.map(|panel| match panel {
            Panel::Inventory => BindAction::Inventory.canonical_key(),
            Panel::Quests => BindAction::Quests.canonical_key(),
            Panel::Crafting => BindAction::Craft.canonical_key(),
        });
        let close_own = own_key.is_some_and(|key| input.game_pressed.iter().any(|pressed| pressed == key));
        let toggles = session.update(dt, self.game_ui.panel.is_some());
        game.play_seconds += dt;
        if close_own {
            self.game_ui.panel = None;
        }
        if toggles.escape {
            if self.game_ui.panel.is_some() {
                self.game_ui.panel = None;
            } else {
                self.open_pause();
            }
        }
        for (toggle, panel) in [
            (toggles.inventory, Panel::Inventory),
            (toggles.quests, Panel::Quests),
            (toggles.crafting, Panel::Crafting),
        ] {
            if toggle {
                self.toggle_panel(panel);
            }
        }
        self.drain_session_events();
    }

    fn toggle_panel(&mut self, panel: Panel) {
        self.game_ui.toggle(panel);
        if self.game_ui.panel.is_some() {
            if let Some(game) = self.game.as_mut() {
                game.session.release_input();
            }
        }
    }

    fn open_pause(&mut self) {
        if let Some(game) = self.game.as_mut() {
            game.session.release_input();
        }
        self.screens.push(Screen::Pause);
        self.ui.set_focus(farm_ui::WidgetId::new("pause").with("Resume"));
    }

    fn drain_session_events(&mut self) {
        let Some(game) = self.game.as_mut() else { return };
        let gain = self.settings.audio.effects_gain();
        let mut autosave = false;
        for event in game.session.drain_events() {
            match event {
                SessionEvent::Toast { text, kind } => self.game_ui.toasts.push(text, toast_kind(kind)),
                SessionEvent::Sound { cue } => {
                    if gain > 0.0 {
                        self.sounds.push(SoundRequest { cue, gain });
                    }
                }
                SessionEvent::DayStarted { .. } => autosave = true,
                SessionEvent::SceneChanged { .. } | SessionEvent::QuestCompleted { .. } => {}
            }
        }
        if autosave && self.mode == PlayerMode::Standalone {
            let slot = self.game.as_ref().map_or(0, |game| game.slot);
            if slot > 0 {
                self.save_to(slot, true);
            }
        }
    }

    fn ui_sound(&mut self) {
        let gain = self.settings.audio.effects_gain();
        if gain > 0.0 {
            self.sounds.push(SoundRequest { cue: "ui".to_owned(), gain: gain * 0.6 });
        }
    }

    // ── World ──────────────────────────────────────────────────────────

    /// The play snapshot of a state (interpolated player, pops, creator art), with the world's
    /// size and the camera target in pixels.
    fn snapshot_of(
        content: &GameContent,
        graphics: &mut GraphicsSource,
        session: &PlaySession,
        pops: bool,
    ) -> Option<WorldFrame> {
        let state = session.state();
        let scene = session.current_scene()?;
        let (ix, iy) = session.interpolated_player();
        let (px, py) = (PADDING + (ix - 0.5) * TILE_SIZE, PADDING + (iy - 0.5) * TILE_SIZE);
        let options = SnapshotOptions {
            tile_size: TILE_SIZE,
            padding: PADDING,
            pixel_x: Some(px),
            pixel_y: Some(py),
            camera: None,
        };
        let mut snapshot = shell_snapshot(content, state, scene, &options);
        if pops {
            let live = session.pops();
            if !live.is_empty() {
                snapshot.pops = Some(
                    live.into_iter()
                        .map(|pop| SnapshotPop {
                            x: pop.x,
                            y: pop.y,
                            text: pop.text,
                            color: Some(pop.color),
                            age: pop.age,
                        })
                        .collect(),
                );
            }
        }
        graphics.set_live_state(content, state);
        let moving = state.player.move_intent.dx != 0 || state.player.move_intent.dy != 0;
        apply_graphics(&mut snapshot, graphics, scene, state.clock.tick as f64, moving);
        Some((snapshot, session.world_size(), (px + TILE_SIZE / 2.0, py + TILE_SIZE / 2.0)))
    }

    fn build_world(&mut self, width: u32, height: u32) {
        let integer = self.settings.display.integer_scaling;
        let reduced = self.settings.accessibility.reduced_motion;
        self.world = None;
        if let Some(game) = self.game.as_mut() {
            if let Some((snapshot, world, target)) =
                Self::snapshot_of(&self.def.content, &mut game.graphics, &game.session, !reduced)
            {
                self.world = Some((snapshot, world_view(width, height, integer, world, target)));
            }
            return;
        }
        // The title screen shows the start of the game behind the menu.
        if self.screens.first() == Some(&Screen::Title) {
            let state = self
                .title_state
                .get_or_insert_with(|| state::create_game_state_from_start(&self.def.start, self.seed.as_deref()));
            let Some(scene) = state
                .world
                .scenes
                .iter()
                .find(|scene| scene.id == state.player.scene_id)
                .or_else(|| state.world.scenes.first())
            else {
                return;
            };
            let (x, y) = (units::position_to_tiles(state.player.x), units::position_to_tiles(state.player.y));
            let (px, py) = (PADDING + (x - 0.5) * TILE_SIZE, PADDING + (y - 0.5) * TILE_SIZE);
            let options = SnapshotOptions {
                tile_size: TILE_SIZE,
                padding: PADDING,
                pixel_x: Some(px),
                pixel_y: Some(py),
                camera: None,
            };
            let mut snapshot = shell_snapshot(&self.def.content, state, scene, &options);
            let tick = if reduced { 0.0 } else { (self.time * 20.0).floor() };
            snapshot.tick = tick;
            let mut graphics = GraphicsSource::from_state(&self.def.presentation, &self.def.content, state);
            graphics.set_live_state(&self.def.content, state);
            apply_graphics(&mut snapshot, &graphics, scene, tick, false);
            let world = (
                f64::from(scene.width) * TILE_SIZE + PADDING * 2.0,
                f64::from(scene.height) * TILE_SIZE + PADDING * 2.0,
            );
            self.world = Some((
                snapshot,
                world_view(width, height, integer, world, (px + TILE_SIZE / 2.0, py + TILE_SIZE / 2.0)),
            ));
        }
    }

    fn compose(&mut self, width: u32, height: u32) {
        let background = self.ui.theme().colors.background;
        let tints = self.ui.world_tints().to_vec();
        // Letterbox areas are flat background: they take the tints as one flat colour.
        let letterbox =
            tints.iter().fold(background, |color, (top, bottom)| over(color, farm_ui::theme::mix(*top, *bottom, 0.5)));
        self.renderer.begin(width, height, letterbox);
        if let Some((snapshot, view)) = self.world.as_mut() {
            let view = *view;
            self.renderer.draw_world(snapshot, &view, background, &tints);
        }
        self.renderer.draw_ui(&self.ui_list);
    }

    // ── UI ─────────────────────────────────────────────────────────────

    fn ui_scale(&self, width: u32, height: u32) -> f32 {
        let fit = (width as f32 / 1280.0).min(height as f32 / 800.0).clamp(0.75, 3.0);
        fit * self.settings.display.ui_scale.clamp(0.5, 2.0)
    }

    /// The interface language: the setting, else the system's, else the game's, else English.
    pub fn lang(&self) -> Lang {
        Lang::resolve(&self.settings.language, self.system_locale.as_deref(), &self.def.content.settings.locale)
    }

    fn draw_ui(&mut self, input: farm_ui::UiInput, width: u32, height: u32, playing: bool, game_modal: bool) {
        let scale = self.ui_scale(width, height);
        self.ui.set_reduced_motion(self.settings.accessibility.reduced_motion);
        self.ui.set_readable_font(self.settings.accessibility.readable_font);
        self.ui.set_lang(self.lang());
        self.ui.begin_frame(
            input,
            (width as f32, height as f32),
            scale,
            self.settings.accessibility.text_size,
            self.time,
        );

        // The game and its HUD (under any shell screen).
        let mut game_actions = Vec::new();
        if let Some(game) = self.game.as_ref() {
            let session = &game.session;
            let state = session.state();
            let content = session.content();
            let overlay = overlay::overlay_view(session.context(), state);
            let calendar = calendar_view(content, state);
            let panel_views = panels::render(
                &self.def.presentation.game_panels,
                &PanelState::from_game_state(state, self.game_ui.panel.is_some() || !playing),
            );
            let view = GameView {
                state,
                content,
                overlay: &overlay,
                calendar: &calendar,
                panels: &panel_views,
                minigame: session.minigame_view(),
                art: ItemArt {
                    assets: &self.def.art,
                    pixel_art: self.def.presentation.graphics.as_ref().is_none_or(|graphics| graphics.pixel_art),
                    builtin: Some(BuiltinArt::embedded()),
                },
                bindings: &self.settings.controls,
                show_made_with: self.def.presentation.show_made_with_credit,
                keys_active: playing && game_modal,
            };
            game_actions = self.game_ui.draw(&mut self.ui, &view, &mut self.renderer.ui_images);
        } else {
            self.game_ui.toasts.top = 16.0;
        }

        // The shell screen in front.
        let shell_action = match self.screens.last().copied() {
            None => None,
            Some(Screen::Title) => {
                let view = self.title_view();
                shell::title(&mut self.ui, &view)
            }
            Some(Screen::Pause) => {
                let view = PauseView {
                    title: self.def.info.title.clone(),
                    full: self.mode == PlayerMode::Standalone,
                    can_quit: self.can_quit,
                };
                shell::pause(&mut self.ui, &view)
            }
            Some(Screen::Settings) => shell::settings(
                &mut self.ui,
                &mut self.settings_screen,
                &mut self.settings,
                self.mode == PlayerMode::Embedded,
            ),
            Some(Screen::Credits) => {
                let info = &self.def.info;
                let view = CreditsView {
                    title: info.title.clone(),
                    version: info.version.clone(),
                    author: info.author.clone(),
                    company: info.company.clone(),
                    credits: info.credits.clone(),
                };
                shell::credits(&mut self.ui, &view)
            }
            Some(Screen::Slots(mode)) => {
                let view = self.slots_view(mode);
                shell::slots(&mut self.ui, &view)
            }
            Some(Screen::Confirm(kind)) => {
                let view = confirm_view(kind, self.ui.lang());
                shell::confirm(&mut self.ui, &view)
            }
        };
        self.game_ui.draw_toasts(&mut self.ui);
        self.ui_list = self.ui.end_frame();

        if playing {
            for action in game_actions {
                self.apply_game_action(action);
            }
        }
        if let Some(action) = shell_action {
            self.apply_shell_action(action);
        }
    }

    fn apply_game_action(&mut self, action: GameAction) {
        match action {
            GameAction::Command(command) => {
                if let Some(game) = self.game.as_mut() {
                    game.session.run_command(&command);
                }
                self.drain_session_events();
            }
            GameAction::TogglePanel(panel) => {
                self.ui_sound();
                self.toggle_panel(panel);
            }
            GameAction::ClosePanel => self.game_ui.panel = None,
            GameAction::OpenMenu => {
                self.ui_sound();
                self.open_pause();
            }
            GameAction::Minigame(input) => {
                if let Some(game) = self.game.as_mut() {
                    game.session.minigame_input(input);
                }
                self.drain_session_events();
            }
        }
    }

    fn title_view(&self) -> TitleView {
        let info = &self.def.info;
        let lang = self.ui.lang();
        let subtitle = match info.author.as_deref().or(info.company.as_deref()).filter(|s| !s.is_empty()) {
            Some(author) => lang.format("title.versionBy", &[&info.version, &author]),
            None => lang.format("title.version", &[&info.version]),
        };
        let latest = self.latest_slot();
        let continue_detail = latest.and_then(|slot| {
            let preview = self.slots.get(slot as usize - 1)?.preview.as_ref()?;
            let date = shell::date_line(&self.slot_preview_view(preview, None), lang);
            Some(lang.format("title.dateInSlot", &[&date, &slot]))
        });
        TitleView {
            title: info.title.clone(),
            subtitle,
            can_continue: latest.is_some(),
            continue_detail,
            has_saves: self.slots.iter().any(|slot| slot.preview.is_some()),
            show_made_with: self.def.presentation.show_made_with_credit,
            can_quit: self.can_quit,
        }
    }

    fn slot_preview_view(
        &self,
        preview: &SavePreview,
        thumbnail: Option<(farm_render::ImageId, f32, f32)>,
    ) -> SlotPreviewView {
        let calendar = &self.def.content.settings.calendar;
        SlotPreviewView {
            farm_name: preview.farm_name.clone(),
            day: preview.day,
            season: preview.season.clone(),
            year: preview.year,
            money: preview.money,
            play_seconds: preview.play_seconds,
            saved_at: preview.saved_at,
            thumbnail,
            // Preview days are whole numbers (the FlatBuffers fields are doubles). Saves written
            // before the day of season was recorded place the absolute day in the calendar.
            day_of_season: Some(if preview.day_of_season > 0.0 {
                preview.day_of_season
            } else {
                f64::from(farm_sim::game_time::day_of_season(calendar, preview.day as u32))
            }),
        }
    }

    fn slots_view(&mut self, mode: SlotsMode) -> SlotsView {
        // Thumbnails may have left the image cache since they were decoded.
        for index in 0..self.slots.len() {
            let evicted =
                self.slots[index].thumbnail.is_some_and(|(id, _, _)| self.renderer.ui_images.image(id).is_none());
            if evicted {
                self.decode_thumbnail(index);
            }
        }
        let current = self.game.as_ref().map(|game| game.slot);
        let slots = self
            .slots
            .iter()
            .enumerate()
            .map(|(index, info)| {
                let slot = index as u32 + 1;
                SlotView {
                    slot,
                    preview: info.preview.as_ref().map(|preview| self.slot_preview_view(preview, info.thumbnail)),
                    current: current == Some(slot),
                    unreadable: info.unreadable,
                }
            })
            .collect();
        SlotsView { mode, slots, now: (self.clock)() }
    }

    fn apply_shell_action(&mut self, action: ShellAction) {
        if !matches!(action, ShellAction::SettingsChanged | ShellAction::StartCapture(_) | ShellAction::CancelCapture) {
            self.ui_sound();
        }
        match action {
            ShellAction::NewGame => match (1..=SLOT_COUNT)
                .find(|slot| self.slot_info(*slot).is_none_or(|info| info.preview.is_none() && !info.unreadable))
            {
                Some(slot) => self.start_game(slot),
                None => self.screens.push(Screen::Slots(SlotsMode::NewGame)),
            },
            ShellAction::NewGameInSlot(slot) => {
                if self.slot_info(slot).is_some_and(|info| info.preview.is_some() || info.unreadable) {
                    self.screens.push(Screen::Confirm(Confirmation::NewGameOver(slot)));
                } else {
                    self.start_game(slot);
                }
            }
            ShellAction::Continue => {
                if let Some(slot) = self.latest_slot() {
                    self.load_slot(slot);
                }
            }
            ShellAction::OpenLoad => self.screens.push(Screen::Slots(SlotsMode::Load)),
            ShellAction::OpenSave => self.screens.push(Screen::Slots(SlotsMode::Save)),
            ShellAction::OpenSettings => {
                self.settings_screen = SettingsScreen::default();
                self.screens.push(Screen::Settings);
            }
            ShellAction::OpenCredits => self.screens.push(Screen::Credits),
            ShellAction::Quit => {
                if self.game.is_some() {
                    self.screens.push(Screen::Confirm(Confirmation::QuitGame));
                } else {
                    self.requests.push(PlayerRequest::Quit);
                }
            }
            ShellAction::QuitToTitle => self.screens.push(Screen::Confirm(Confirmation::QuitToTitle)),
            ShellAction::Resume | ShellAction::Back | ShellAction::Cancel => {
                if self.screens.last() != Some(&Screen::Title) {
                    self.screens.pop();
                }
            }
            ShellAction::LoadSlot(slot) => self.load_slot(slot),
            ShellAction::SaveSlot(slot) => {
                let current = self.game.as_ref().map(|game| game.slot);
                if current != Some(slot)
                    && self.slot_info(slot).is_some_and(|info| info.preview.is_some() || info.unreadable)
                {
                    self.screens.push(Screen::Confirm(Confirmation::Overwrite(slot)));
                } else {
                    self.save_to(slot, false);
                    self.screens.pop();
                }
            }
            ShellAction::DeleteSlot(slot) => self.screens.push(Screen::Confirm(Confirmation::Delete(slot))),
            ShellAction::Confirm => {
                let Some(Screen::Confirm(kind)) = self.screens.pop() else { return };
                match kind {
                    Confirmation::QuitToTitle => self.quit_to_title(),
                    Confirmation::QuitGame => self.requests.push(PlayerRequest::Quit),
                    Confirmation::Overwrite(slot) => {
                        self.save_to(slot, false);
                        if matches!(self.screens.last(), Some(Screen::Slots(_))) {
                            self.screens.pop();
                        }
                    }
                    Confirmation::Delete(slot) => {
                        if let Err(error) = self.saves.delete(slot) {
                            self.game_ui.toasts.push(error, ToastKind::Error);
                        }
                        self.refresh_slots();
                    }
                    Confirmation::NewGameOver(slot) => self.start_game(slot),
                }
            }
            ShellAction::SettingsChanged => self.apply_settings(),
            ShellAction::StartCapture(_) | ShellAction::CancelCapture => {}
        }
    }

    fn apply_settings(&mut self) {
        // Messages sent before the next frame (an autosave toast) use the new language already.
        self.ui.set_lang(self.lang());
        self.ui.set_readable_font(self.settings.accessibility.readable_font);
        let fullscreen = self.settings.display.fullscreen;
        if self.mode == PlayerMode::Standalone {
            self.requests.retain(|request| !matches!(request, PlayerRequest::SetFullscreen(_)));
            self.requests.push(PlayerRequest::SetFullscreen(fullscreen));
        }
        if let Some(game) = self.game.as_mut() {
            game.session.set_reduced_motion(self.settings.accessibility.reduced_motion);
        }
        self.store_settings();
    }

    fn store_settings(&mut self) {
        if let Err(error) = self.settings_store.save(&settings_to_toml(&self.settings)) {
            let message = self.ui.lang().format("toast.settingsNotSaved", &[&error]);
            self.game_ui.toasts.push(message, ToastKind::Error);
        }
    }

    // ── Games and saves ────────────────────────────────────────────────

    fn attach(&mut self, mut session: PlaySession, slot: u32, play_seconds: f64) {
        let plugins = match &self.def.project {
            Some(project) => crate::plugins::for_project(project, self.plugin_options.clone()),
            None => crate::plugins::for_cartridge(&self.def.plugins, self.plugin_options.clone()),
        };
        session.set_plugins(plugins);
        session.set_reduced_motion(self.settings.accessibility.reduced_motion);
        let graphics = GraphicsSource::from_state(&self.def.presentation, session.content(), session.state());
        for error in session.plugin_errors() {
            let message = self.ui.lang().format("toast.pluginError", &[&error]);
            self.game_ui.toasts.push(message, ToastKind::Error);
        }
        self.game = Some(Game { session, graphics, slot, play_seconds });
        self.game_ui = GameUi { toasts: std::mem::take(&mut self.game_ui.toasts), ..GameUi::new() };
        self.screens.clear();
        self.drain_session_events();
    }

    fn start_game(&mut self, slot: u32) {
        let session = PlaySession::new_game(self.def.content.clone(), &self.def.start, self.seed.as_deref());
        self.attach(session, slot, 0.0);
    }

    fn quit_to_title(&mut self) {
        self.game = None;
        self.game_ui = GameUi::new();
        self.screens = vec![Screen::Title];
        self.refresh_slots();
    }

    fn slot_info(&self, slot: u32) -> Option<&SlotInfo> {
        self.slots.get((slot as usize).checked_sub(1)?)
    }

    fn latest_slot(&self) -> Option<u32> {
        self.slots
            .iter()
            .enumerate()
            .filter_map(|(index, info)| info.preview.as_ref().map(|preview| (preview.saved_at, index as u32 + 1)))
            .max()
            .map(|(_, slot)| slot)
    }

    fn decode_thumbnail(&mut self, index: usize) {
        let Some(preview) = self.slots[index].preview.as_ref() else { return };
        if preview.thumbnail_png.is_empty() {
            return;
        }
        self.thumb_serial += 1;
        let key = format!("save-thumbnail:{}:{}", index + 1, self.thumb_serial);
        self.slots[index].thumbnail = farm_render::images::decode_image(&preview.thumbnail_png).ok().map(|image| {
            let (width, height) = (image.width() as f32, image.height() as f32);
            (self.renderer.ui_images.insert(&key, image), width, height)
        });
    }

    fn refresh_slots(&mut self) {
        self.slots = (1..=SLOT_COUNT)
            .map(|slot| match self.saves.read(slot) {
                None => SlotInfo::default(),
                Some(bytes) => match save_file::read_save_preview(&bytes) {
                    Ok((_, preview)) => SlotInfo { preview: Some(preview), unreadable: false, thumbnail: None },
                    // JSON saves carry no preview: show what the state says.
                    Err(_) => match save_file::load_save_bytes(&bytes, &self.def.target, &self.def.content) {
                        loaded if loaded.ok => SlotInfo {
                            preview: loaded.state.as_ref().map(SavePreview::of_state),
                            unreadable: false,
                            thumbnail: None,
                        },
                        _ => SlotInfo { preview: None, unreadable: true, thumbnail: None },
                    },
                },
            })
            .collect();
        for index in 0..self.slots.len() {
            self.decode_thumbnail(index);
        }
    }

    fn load_slot(&mut self, slot: u32) {
        let Some(bytes) = self.saves.read(slot) else {
            let message = self.ui.lang().format("toast.slotEmpty", &[&slot]);
            self.game_ui.toasts.push(message, ToastKind::Error);
            return;
        };
        let loaded = save_file::load_save_bytes(&bytes, &self.def.target, &self.def.content);
        let Some(state) = loaded.state.filter(|_| loaded.ok) else {
            let message = if loaded.errors.is_empty() {
                self.ui.lang().tr("toast.loadFailed").to_owned()
            } else {
                loaded.errors.join(" ")
            };
            self.game_ui.toasts.push(message, ToastKind::Error);
            return;
        };
        let play_seconds = save_file::read_save_preview(&bytes).map_or(0.0, |(_, preview)| preview.play_seconds);
        self.attach(PlaySession::new(self.def.content.clone(), state), slot, play_seconds);
        for warning in loaded.warnings {
            self.game_ui.toasts.push(warning, ToastKind::Info);
        }
        let message = self.ui.lang().format("toast.loaded", &[&slot]);
        self.game_ui.toasts.push(message, ToastKind::Info);
    }

    /// Writes the running game to `slot` (with a thumbnail of the scene).
    fn save_to(&mut self, slot: u32, autosave: bool) {
        let background = self.ui.theme().colors.background;
        let lang = self.ui.lang();
        let Some(game) = self.game.as_mut() else { return };
        let mut preview = SavePreview::of_state(game.session.state());
        preview.farm_name = self.def.info.title.clone();
        preview.play_seconds = game.play_seconds.floor();
        preview.saved_at = (self.clock)();
        if let Some((mut snapshot, world, target)) =
            Self::snapshot_of(&self.def.content, &mut game.graphics, &game.session, false)
        {
            let (width, height) = (world.0.min(384.0), world.1.min(240.0));
            snapshot.camera = Some(compute_camera(target.0, target.1, world.0, world.1, width, height));
            preview.thumbnail_png = self.renderer.thumbnail(&snapshot, 192, 120, background);
        }
        let bytes = save_file::write_save_binary(game.session.state(), &self.def.target, &preview);
        match self.saves.write(slot, &bytes) {
            Ok(()) => {
                game.slot = slot;
                let key = if autosave { "toast.autosaved" } else { "toast.saved" };
                self.game_ui.toasts.push(lang.format(key, &[&slot]), ToastKind::Success);
            }
            Err(error) => self.game_ui.toasts.push(lang.format("toast.saveFailed", &[&error]), ToastKind::Error),
        }
        self.refresh_slots();
    }

    // ── Host API ───────────────────────────────────────────────────────

    pub fn mode(&self) -> PlayerMode {
        self.mode
    }

    /// The game's identity (title, version, id, window defaults, credits).
    pub fn info(&self) -> &GameInfoOwned {
        &self.def.info
    }

    /// The screen in front.
    pub fn screen(&self) -> ScreenKind {
        match self.screens.last() {
            None if self.game.is_some() => ScreenKind::Playing,
            None | Some(Screen::Title) => ScreenKind::Title,
            Some(Screen::Pause) => ScreenKind::Pause,
            Some(Screen::Settings) => ScreenKind::Settings,
            Some(Screen::Credits) => ScreenKind::Credits,
            Some(Screen::Slots(SlotsMode::Load)) => ScreenKind::LoadSlots,
            Some(Screen::Slots(SlotsMode::Save)) => ScreenKind::SaveSlots,
            Some(Screen::Slots(SlotsMode::NewGame)) => ScreenKind::NewGameSlots,
            Some(Screen::Confirm(_)) => ScreenKind::Confirm,
        }
    }

    /// The open host panel (inventory, quests, crafting).
    pub fn panel(&self) -> Option<Panel> {
        self.game_ui.panel
    }

    /// The running session (read-only).
    pub fn session(&self) -> Option<&PlaySession> {
        self.game.as_ref().map(|game| &game.session)
    }

    /// The live game state.
    pub fn state(&self) -> Option<&GameState> {
        self.session().map(PlaySession::state)
    }

    /// Creator debug tooling (the editor's debug drawer).
    pub fn debug(&mut self, action: &DebugAction) -> Result<(), PlayerError> {
        self.with_game(|game| game.session.debug(action))?;
        self.drain_session_events();
        Ok(())
    }

    /// Replaces the live state (the debug drawer's write path).
    pub fn replace_state(&mut self, state: GameState) -> Result<(), PlayerError> {
        self.with_game(move |game| game.session.replace_state(state))
    }

    /// Runs an engine command as if the UI had (hosts, tests).
    pub fn run_command(&mut self, command: &farm_sim::Command) -> Result<(), PlayerError> {
        self.with_game(|game| game.session.run_command(command))?;
        self.drain_session_events();
        Ok(())
    }

    fn with_game<R>(&mut self, act: impl FnOnce(&mut Game) -> R) -> Result<R, PlayerError> {
        if let Some(message) = &self.poisoned {
            return Err(PlayerError::Poisoned(message.clone()));
        }
        let game = self.game.as_mut().ok_or(PlayerError::NoGame)?;
        match catch_unwind(AssertUnwindSafe(|| act(game))) {
            Ok(result) => Ok(result),
            Err(panic) => {
                let message = panic_message(&*panic);
                self.poisoned = Some(message.clone());
                Err(PlayerError::Engine(message))
            }
        }
    }

    /// The project with the live state written back ("keep changes"), when the player started
    /// from a project and a game runs.
    pub fn synced_project(&self) -> Option<GameProject> {
        let project = self.def.project.as_ref()?;
        Some(state::apply_state_to_project(project, self.state()?))
    }

    /// Recent plugin errors (empty without plugins).
    pub fn plugin_errors(&self) -> Vec<String> {
        self.session().map(PlaySession::plugin_errors).unwrap_or_default()
    }

    pub fn settings(&self) -> &Settings {
        &self.settings
    }

    /// Replaces the settings (and stores them).
    pub fn set_settings(&mut self, settings: Settings) {
        self.settings = settings;
        self.apply_settings();
    }

    /// Toasts on screen now.
    pub fn toasts(&self) -> &[farm_ui::game::Toast] {
        self.game_ui.toasts.visible()
    }

    /// Every toast shown so far (bounded).
    pub fn toast_history(&self) -> &[(String, ToastKind)] {
        self.game_ui.toasts.history()
    }

    /// The previews of the save slots (index 0 is slot 1).
    pub fn slot_previews(&self) -> Vec<Option<SavePreview>> {
        self.slots.iter().map(|info| info.preview.clone()).collect()
    }

    /// Reads the save slots again after the host changed its store behind the player's back
    /// (the web version restoring saves from browser storage).
    pub fn reload_saves(&mut self) {
        self.refresh_slots();
    }

    /// The slot the running game saves to.
    pub fn current_slot(&self) -> Option<u32> {
        self.game.as_ref().map(|game| game.slot).filter(|slot| *slot > 0)
    }

    /// Where a widget was drawn last frame (physical pixels), for scripted clicks.
    pub fn widget_rect(&self, id: farm_ui::WidgetId) -> Option<farm_render::Rect> {
        let scale = self.ui.scale();
        self.ui
            .last_rect(id)
            .map(|rect| farm_render::Rect::new(rect.x * scale, rect.y * scale, rect.width * scale, rect.height * scale))
    }

    /// The UI draw list of the last frame (tests, other rasterizers).
    pub fn ui_draw_list(&self) -> &DrawList {
        &self.ui_list
    }

    /// The last composed frame.
    pub fn pixels(&self) -> &Pixmap {
        self.renderer.frame()
    }

    /// Where saves are kept, when on disk.
    pub fn save_folder(&self) -> Option<std::path::PathBuf> {
        self.saves.folder()
    }

    /// A crash report: the game, where it was and the commands before the failure. With the
    /// last save this replays the moments before a crash (the engine is deterministic).
    pub fn crash_report(&self) -> String {
        let info = &self.def.info;
        let mut report = format!(
            "Game: {} {} ({})\nCartridge hash: {}\n",
            info.title, info.version, info.game_id, self.def.target.cart_hash
        );
        if let Some(message) = &self.poisoned {
            report.push_str(&format!("Failure: {message}\n"));
        }
        if let Some(game) = &self.game {
            let state = game.session.state();
            report.push_str(&format!(
                "Slot: {}\nTick: {}\nDay: {} ({} year {})\nScene: {}\nState hash: {}\nRecent commands (oldest first):\n",
                game.slot,
                state.clock.tick,
                state.clock.day,
                state.clock.season,
                state.clock.year,
                state.player.scene_id,
                farm_sim::hash_state(state)
            ));
            for command in game.session.recent_commands() {
                report.push_str("  ");
                report.push_str(&command);
                report.push('\n');
            }
            for error in game.session.plugin_errors() {
                report.push_str(&format!("Plugin error: {error}\n"));
            }
        }
        report
    }
}

/// `top` (straight alpha) over an opaque colour.
fn over(base: farm_render::Color, top: farm_render::Color) -> farm_render::Color {
    let alpha = u32::from(top.a);
    let blend = |b: u8, t: u8| ((u32::from(t) * alpha + u32::from(b) * (255 - alpha) + 127) / 255) as u8;
    farm_render::Color::rgb(blend(base.r, top.r), blend(base.g, top.g), blend(base.b, top.b))
}

fn confirm_view(kind: Confirmation, lang: Lang) -> ConfirmView {
    let (title, message, confirm) = match kind {
        Confirmation::QuitToTitle => {
            ("confirm.quitToTitleTitle", lang.tr("confirm.lostProgress").to_owned(), "confirm.quitToTitle")
        }
        Confirmation::QuitGame => {
            ("confirm.quitGameTitle", lang.tr("confirm.lostProgress").to_owned(), "confirm.quitGame")
        }
        Confirmation::Overwrite(slot) => {
            ("confirm.overwriteTitle", lang.format("confirm.overwriteMessage", &[&slot]), "confirm.overwrite")
        }
        Confirmation::Delete(slot) => {
            ("confirm.deleteTitle", lang.format("confirm.deleteMessage", &[&slot]), "confirm.delete")
        }
        Confirmation::NewGameOver(slot) => {
            ("confirm.newGameTitle", lang.format("confirm.newGameMessage", &[&slot]), "confirm.newGame")
        }
    };
    ConfirmView { title: lang.tr(title).to_owned(), message, confirm: lang.tr(confirm).to_owned() }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn players_can_move_between_threads() {
        fn assert_send<T: Send>() {}
        assert_send::<Player>();
    }
}
