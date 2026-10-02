//! The graphical player (`farm_player::Player`) for hosts: the editor's Play Mode and the web.
//!
//! The host hands the player a project (or a compiled cartridge), forwards raw input events and
//! the size of its surface, and shows the frames. All of the game's HUD, dialogue, shop,
//! crafting, inventory, quest, minigame and toast UI is drawn in Rust by `farm-ui`; the host
//! keeps only its own tools (restart, keep changes, the debug drawer), which reach the game
//! through [`HostPlayer::debug`] and [`HostPlayer::synced_project`].

use crate::view_json;
use farm_player::{
    DebugAction, HostView, InputEvent, Player, PlayerError, PlayerOptions, PlayerRequest, ScreenKind, SoundRequest,
};
use farm_runtime::music::MusicCue;
use farm_sim::schema::GameProject;
use farm_sim::{stable_json, Command};
use serde::{Deserialize, Serialize};

/// Largest frame a host may request, in pixels (16 Mpx on the web, where a module has little
/// memory and a failed allocation aborts instead of throwing).
pub const MAX_PIXELS: u64 = if cfg!(target_arch = "wasm32") { 16 * 1024 * 1024 } else { 64 * 1024 * 1024 };

/// Options for a new player: `{"seed"?, "reducedMotion"?, "uiScale"?, "audio"?, "locale"?}`.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PlayerCreate {
    /// Seed of the game (the project's own seed when absent, like the C# `PlaySession`).
    pub seed: Option<String>,
    /// No floating pops, fades or flashes. `None` keeps the stored setting.
    pub reduced_motion: Option<bool>,
    /// Interface size (1 = 100 %). `None` keeps the stored setting.
    pub ui_scale: Option<f32>,
    /// Play the frames' sounds on the host's output device (farm-ffi's speaker thread; the web
    /// plays the returned cues itself).
    pub audio: bool,
    /// The host's language (`es`): the game interface follows it until the player picks one
    /// in Settings.
    pub locale: Option<String>,
}

impl PlayerCreate {
    /// Options from JSON; empty input means the defaults.
    pub fn parse(options: &[u8]) -> Result<Self, String> {
        if options.is_empty() {
            Ok(Self::default())
        } else {
            serde_json::from_slice(options).map_err(|e| format!("player options: {e}"))
        }
    }
}

/// `{dt, events, width, height, render}`: one frame.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameRequest {
    /// Seconds since the last frame (clamped to 0..=0.25).
    pub dt: f64,
    #[serde(default)]
    pub events: Vec<InputEvent>,
    pub width: u32,
    pub height: u32,
    /// False steps the game without drawing (`Player::step`).
    #[serde(default = "yes")]
    pub render: bool,
    /// Frame pixels per CSS pixel (`devicePixelRatio`, times any downscale the page applies):
    /// the interface keeps its size on dense screens. Default 1.
    #[serde(default = "one")]
    pub density: f32,
    /// The page shows on-screen touch controls: prompts name no keys.
    #[serde(default)]
    pub touch_controls: bool,
    /// Frame pixels at the bottom the page covers with its controls (the HUD, panels and the
    /// dialogue box stay above them).
    #[serde(default)]
    pub inset_bottom: f32,
}

fn one() -> f32 {
    1.0
}

impl FrameRequest {
    pub fn parse(request: &[u8]) -> Result<Self, String> {
        serde_json::from_slice(request).map_err(|e| format!("frame request: {e}"))
    }
}

fn yes() -> bool {
    true
}

/// What a frame tells the host besides the pixels.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct FrameInfo<'a> {
    /// Sound cues to play, with their gain.
    sounds: Vec<SoundInfo<'a>>,
    /// `quit`, `fullscreen:on`, `fullscreen:off`, `title:<text>`.
    requests: &'a [String],
    screen: &'static str,
    /// An in-game panel (inventory, quests, crafting) or an engine modal is open.
    modal: bool,
    /// The music and ambience loops to play now (`musicSamples(name, rate)`), with their gains.
    music: MusicInfo,
}

#[derive(Debug, Clone, Serialize)]
struct SoundInfo<'a> {
    cue: &'a str,
    gain: f32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct MusicInfo {
    music: Option<&'static str>,
    music_gain: f32,
    ambience: Option<&'static str>,
    ambience_gain: f32,
}

/// The result of [`HostPlayer::frame`]; the pixels are [`HostPlayer::pixels`].
#[derive(Debug, Clone, PartialEq)]
pub struct FrameOutcome {
    /// The frame size, or `None` when the request did not render.
    pub size: Option<(u32, u32)>,
    pub sounds: Vec<SoundRequest>,
    /// `quit`, `fullscreen:on`, `fullscreen:off`, `title:<text>`.
    pub requests: Vec<String>,
    /// [`screen_name`] of the screen in front.
    pub screen: &'static str,
    /// An in-game panel (inventory, quests, crafting) or an engine modal is open.
    pub modal: bool,
    /// The music and ambience that should play now.
    pub music: MusicCue,
}

impl FrameOutcome {
    /// `{"sounds":[{"cue","gain"}],"requests":[…],"screen":"playing","modal":false,
    /// "music":{"music":"day","musicGain":0.5,"ambience":"birds","ambienceGain":0.4}}`.
    pub fn info_json(&self) -> String {
        let cue = self.music;
        view_json::to_json(&FrameInfo {
            sounds: self.sounds.iter().map(|sound| SoundInfo { cue: &sound.cue, gain: sound.gain }).collect(),
            requests: &self.requests,
            screen: self.screen,
            modal: self.modal,
            music: MusicInfo {
                music: cue.music,
                music_gain: cue.music_gain,
                ambience: cue.ambience,
                ambience_gain: cue.ambience_gain,
            },
        })
    }
}

/// Read-only queries for [`HostPlayer::query_json`].
#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
enum Query {
    /// What the debug drawer shows: clock, place, seed, scenes and seasons.
    Summary,
    /// The rectangle of a UI widget by its id path (`["pause", "Save"]`, numbers as slot or
    /// list indices), in frame pixels; `null` when it was not drawn last frame (tests).
    WidgetRect { path: Vec<serde_json::Value> },
    /// Toasts shown so far, oldest first (tests).
    Toasts,
    /// Recent plugin errors, oldest first.
    PluginErrors,
    /// Panics inside the call, as an engine bug would: hosts' tests drive the boundary's
    /// poisoning path with it (the handle is poisoned afterwards).
    Panic,
}

/// The host-facing name of a screen.
pub fn screen_name(screen: ScreenKind) -> &'static str {
    match screen {
        ScreenKind::Title => "title",
        ScreenKind::Playing => "playing",
        ScreenKind::Pause => "pause",
        ScreenKind::Settings => "settings",
        ScreenKind::Credits => "credits",
        ScreenKind::LoadSlots => "loadSlots",
        ScreenKind::SaveSlots => "saveSlots",
        ScreenKind::NewGameSlots => "newGameSlots",
        ScreenKind::Confirm => "confirm",
    }
}

/// The host-facing text of a request.
pub fn request_name(request: &PlayerRequest) -> String {
    match request {
        PlayerRequest::Quit => "quit".to_owned(),
        PlayerRequest::SetFullscreen(on) => format!("fullscreen:{}", if *on { "on" } else { "off" }),
        PlayerRequest::SetTitle(title) => format!("title:{title}"),
    }
}

fn error_text(error: &PlayerError) -> String {
    error.to_string()
}

/// An embedded player (see the module docs).
#[derive(Debug)]
pub struct HostPlayer {
    player: Player,
    /// The project JSON as the host gave it (None for a cartridge): "keep changes" writes the
    /// state into it, so content values stay as the creator typed them.
    project_json: Option<serde_json::Value>,
}

impl HostPlayer {
    /// A player for a (migrated) project's JSON or a compiled cartridge. `options` carries the
    /// mode and stores; `create` the seed and the settings the host overrides.
    pub fn new(game: &[u8], create: &PlayerCreate, mut options: PlayerOptions) -> Result<Self, String> {
        options.seed = create.seed.clone().filter(|seed| !seed.is_empty());
        options.system_locale = create.locale.clone().filter(|locale| !locale.is_empty());
        let mut project_json = None;
        let mut player = if farm_cart::is_cartridge(game) {
            Player::from_cartridge_bytes(game, options)
        } else {
            let json: serde_json::Value = serde_json::from_slice(game).map_err(|e| format!("project JSON: {e}"))?;
            let project: GameProject =
                serde_json::from_value(json.clone()).map_err(|e| format!("project JSON: {e}"))?;
            project_json = Some(json);
            Player::from_project(project, options)
        }
        .map_err(|e| error_text(&e))?;
        let scale = create.ui_scale.filter(|scale| scale.is_finite() && *scale > 0.0);
        if create.reduced_motion.is_some() || scale.is_some() {
            let mut settings = player.settings().clone();
            if let Some(reduced_motion) = create.reduced_motion {
                settings.accessibility.reduced_motion = reduced_motion;
            }
            if let Some(scale) = scale {
                settings.display.ui_scale = scale;
            }
            player.set_settings(settings);
        }
        Ok(Self { player, project_json })
    }

    pub fn player(&self) -> &Player {
        &self.player
    }

    /// Whether the game stopped after an engine failure (the player refuses further frames, so
    /// the host handle is poisoned too). Pass to [`crate::Guarded::poisoning_when`].
    pub fn stopped(&self) -> bool {
        self.player.is_poisoned()
    }

    pub fn player_mut(&mut self) -> &mut Player {
        &mut self.player
    }

    fn modal(&self) -> bool {
        self.player.panel().is_some()
            || self
                .player
                .state()
                .is_some_and(|state| state.dialogue.is_some() || state.shop.is_some() || state.minigame.is_some())
    }

    /// Runs one frame. When it rendered, [`HostPlayer::pixels`] holds the frame: premultiplied
    /// RGBA8 and opaque (so it is also straight alpha).
    pub fn frame(&mut self, request: &FrameRequest) -> Result<FrameOutcome, String> {
        if request.width == 0 || request.height == 0 {
            return Err("A frame needs a size.".to_owned());
        }
        if u64::from(request.width) * u64::from(request.height) > MAX_PIXELS {
            return Err(format!("The requested frame is too large ({}×{}).", request.width, request.height));
        }
        let dt = if request.dt.is_finite() { request.dt.clamp(0.0, 0.25) } else { 0.0 };
        self.player.set_host_view(HostView {
            density: request.density,
            touch_controls: request.touch_controls,
            inset_bottom: request.inset_bottom,
        });
        let (sounds, requests, music, size) = if request.render {
            let output =
                self.player.frame(dt, &request.events, request.width, request.height).map_err(|e| error_text(&e))?;
            let size = (output.pixels.width(), output.pixels.height());
            (output.sounds, output.requests, output.music, Some(size))
        } else {
            let output =
                self.player.step(dt, &request.events, request.width, request.height).map_err(|e| error_text(&e))?;
            (output.sounds, output.requests, output.music, None)
        };
        Ok(FrameOutcome {
            size,
            sounds,
            music,
            requests: requests.iter().map(request_name).collect(),
            screen: screen_name(self.player.screen()),
            modal: self.modal(),
        })
    }

    /// The last rendered frame's premultiplied RGBA8 pixels.
    pub fn pixels(&self) -> &[u8] {
        self.player.pixels().data()
    }

    /// A creator debug-drawer action (`{"type":"addMoney","amount":500}`, `fullEnergy`,
    /// `addMinutes`, `setSeason`, `giveFirst`, `teleport`, `setFlag`, `skipDay`).
    pub fn debug(&mut self, action: &[u8]) -> Result<(), String> {
        let action: DebugAction = serde_json::from_slice(action).map_err(|e| format!("debug action: {e}"))?;
        self.player.debug(&action).map_err(|e| error_text(&e))
    }

    /// Runs engine commands (a JSON array) as if the player had done them (tests and tools).
    pub fn commands(&mut self, commands: &[u8]) -> Result<(), String> {
        let commands: Vec<Command> = serde_json::from_slice(commands).map_err(|e| format!("commands JSON: {e}"))?;
        for command in &commands {
            self.player.run_command(command).map_err(|e| error_text(&e))?;
        }
        Ok(())
    }

    /// The live game state as stable JSON.
    pub fn state_json(&self) -> Result<String, String> {
        let state = self.player.state().ok_or("No game is running.")?;
        Ok(stable_json::stringify(state))
    }

    /// The state hash (`hashState`) of the live game.
    pub fn hash(&self) -> Result<String, String> {
        let state = self.player.state().ok_or("No game is running.")?;
        Ok(farm_sim::hash_state(state))
    }

    /// The editor project with the live state written back ("keep changes"), as stable JSON.
    /// Fails for a player started from a cartridge.
    pub fn synced_project(&self) -> Result<String, String> {
        let project = self.project_json.as_ref().ok_or("This game was not started from an editor project.")?;
        let state = self.player.state().ok_or("No game is running.")?;
        Ok(stable_json::stringify(&farm_sim::state::apply_state_to_project_json(project, state)?))
    }

    /// Read-only queries (`{"type":"summary"}`, `{"type":"widgetRect","path":[…]}`,
    /// `{"type":"toasts"}`, `{"type":"pluginErrors"}`; `{"type":"panic"}` for tests), answered
    /// as JSON.
    pub fn query_json(&self, query: &[u8]) -> Result<String, String> {
        let query: Query = serde_json::from_slice(query).map_err(|e| format!("player query: {e}"))?;
        match query {
            Query::Summary => {
                let state = self.player.state().ok_or("No game is running.")?;
                let content = self.player.session().map(|session| session.content()).ok_or("No game is running.")?;
                let calendar = farm_runtime::host::calendar_view(content, state);
                let summary = serde_json::json!({
                    "tick": state.clock.tick,
                    "day": state.clock.day,
                    "season": state.clock.season,
                    "year": state.clock.year,
                    "timeText": calendar.time_text,
                    "sceneId": state.player.scene_id,
                    "x": state.player.x,
                    "y": state.player.y,
                    "money": state.player.money,
                    "seed": state.meta.engine_seed,
                    "scenes": state.world.scenes.iter().map(|scene| serde_json::json!({"id": scene.id, "name": scene.name})).collect::<Vec<_>>(),
                    "seasons": calendar.seasons.iter().map(|season| serde_json::json!({"id": season.id, "name": season.name})).collect::<Vec<_>>(),
                });
                Ok(view_json::to_json(&summary))
            }
            Query::WidgetRect { path } => {
                let mut parts = path.iter();
                let first =
                    parts.next().and_then(serde_json::Value::as_str).ok_or("A widget path starts with a name.")?;
                let mut id = farm_ui::WidgetId::new(first);
                for part in parts {
                    id = match part {
                        serde_json::Value::String(text) => id.with(text.as_str()),
                        serde_json::Value::Number(number) => {
                            let index = number.as_u64().ok_or("Widget path numbers must be whole and positive.")?;
                            id.with(u32::try_from(index).map_err(|_| "Widget path number too large.")?)
                        }
                        _ => return Err("Widget path parts are strings or numbers.".to_owned()),
                    };
                }
                let rect = self
                    .player
                    .widget_rect(id)
                    .map(|r| serde_json::json!({"x": r.x, "y": r.y, "width": r.width, "height": r.height}));
                Ok(view_json::to_json(&rect))
            }
            Query::Toasts => {
                let toasts: Vec<_> = self
                    .player
                    .toast_history()
                    .iter()
                    .map(|(text, kind)| {
                        let kind = match kind {
                            farm_ui::game::ToastKind::Info => "info",
                            farm_ui::game::ToastKind::Success => "success",
                            farm_ui::game::ToastKind::Error => "error",
                        };
                        serde_json::json!({"text": text, "kind": kind})
                    })
                    .collect();
                Ok(view_json::to_json(&toasts))
            }
            Query::PluginErrors => Ok(view_json::to_json(&self.player.plugin_errors())),
            Query::Panic => panic!("A test asked the player to panic."),
        }
    }
}
