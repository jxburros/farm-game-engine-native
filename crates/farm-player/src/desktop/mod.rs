//! The desktop game (feature `desktop`): a winit window presenting the [`Player`]'s frames
//! through softbuffer, keyboard, mouse and gilrs gamepads, cpal audio, user folders and crash
//! logs.
//!
//! - The window takes its title and size from the cartridge's game info; fullscreen is
//!   borderless and toggles with Alt+Enter or F11 (the player asks through
//!   [`PlayerRequest::SetFullscreen`]).
//! - Frames are paced at 60 per second: the loop sleeps until the next frame is due.
//! - Pixels are physical: HiDPI screens get a sharp frame, and the UI scales with the window.
// Wall-clock time paces frames and stamps saves and crash logs here; it never reaches the
// simulation (docs/LANGUAGES.md "Determinism rules").
#![allow(clippy::disallowed_types, clippy::disallowed_methods)]

pub mod crash;
pub mod folders;
mod gamepad;
mod keys;

use crate::input::{InputEvent, PointerButton};
use crate::player::{Player, PlayerOptions, PlayerRequest};
use crate::saves::{FsSaveStore, FsSettingsStore, MemorySaveStore, MemorySettingsStore};
use crate::speaker::Audio;
use gamepad::Gamepads;
use std::num::NonZeroU32;
use std::rc::Rc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use winit::application::ApplicationHandler;
use winit::dpi::{LogicalSize, PhysicalPosition};
use winit::event::{ElementState, MouseButton, MouseScrollDelta, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::window::{Fullscreen, Window, WindowId};

/// Frames per second the loop aims for.
pub const TARGET_FPS: f64 = 60.0;

/// How the desktop game runs.
#[derive(Debug, Clone, Default)]
pub struct DesktopOptions {
    /// Close after this many frames (smoke tests).
    pub exit_after_frames: Option<u64>,
    /// Force a window or fullscreen for this run.
    pub fullscreen: Option<bool>,
}

type Surface = softbuffer::Surface<Rc<Window>, Rc<Window>>;

struct App {
    player: Player,
    options: DesktopOptions,
    window: Option<Rc<Window>>,
    surface: Option<Surface>,
    events: Vec<InputEvent>,
    pointer: PhysicalPosition<f64>,
    last_frame: Option<Instant>,
    next_frame: Instant,
    frames: u64,
    audio: Option<Audio>,
    gamepads: Option<Gamepads>,
    error: Option<String>,
}

fn unix_now() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |time| time.as_secs() as i64)
}

/// Runs the desktop game for `cart` bytes until the player quits. Saves and settings live in
/// the user folders of the game.
pub fn run(cart: &[u8], options: DesktopOptions) -> Result<(), String> {
    let loaded = farm_cart::load_cartridge(cart)?;
    let folders = folders::UserFolders::for_game(&loaded.info.game_id, loaded.info.company.as_deref());
    let mut player_options = PlayerOptions::standalone();
    player_options.clock = Box::new(unix_now);
    // The interface follows the system's language until the player picks one.
    player_options.system_locale = sys_locale::get_locale();
    match &folders {
        Some(folders) => {
            player_options.saves = Box::new(FsSaveStore::new(&folders.saves));
            player_options.settings = Box::new(FsSettingsStore::new(&folders.settings));
        }
        None => {
            eprintln!("No user folder found; saves and settings last until the game closes.");
            player_options.saves = Box::new(MemorySaveStore::new());
            player_options.settings = Box::new(MemorySettingsStore::new());
        }
    }
    crash::install(folders.as_ref().map(|folders| folders.saves.clone()));
    let mut player = Player::from_cartridge(loaded, player_options).map_err(|error| error.to_string())?;
    if let Some(fullscreen) = options.fullscreen {
        let mut settings = player.settings().clone();
        settings.display.fullscreen = fullscreen;
        player.set_settings(settings);
    }
    crash::publish(player.crash_report());

    let event_loop = EventLoop::new().map_err(|error| format!("Cannot open a window: {error}"))?;
    let mut app = App {
        player,
        options,
        window: None,
        surface: None,
        events: Vec::new(),
        pointer: PhysicalPosition::new(0.0, 0.0),
        last_frame: None,
        next_frame: Instant::now(),
        frames: 0,
        audio: Audio::start(),
        gamepads: Gamepads::start(),
        error: None,
    };
    event_loop.run_app(&mut app).map_err(|error| format!("Window loop: {error}"))?;
    match app.error {
        Some(error) => Err(error),
        None => Ok(()),
    }
}

impl App {
    fn set_fullscreen(&self, fullscreen: bool) {
        if let Some(window) = &self.window {
            window.set_fullscreen(fullscreen.then_some(Fullscreen::Borderless(None)));
        }
    }

    fn fail(&mut self, event_loop: &ActiveEventLoop, message: String) {
        let report = self.player.crash_report();
        if let Some(path) = crash::write(&format!("{report}\n{message}\n")) {
            eprintln!("Crash log: {}", path.display());
        }
        self.error = Some(message);
        event_loop.exit();
    }

    fn render(&mut self, event_loop: &ActiveEventLoop) {
        let (Some(window), Some(surface)) = (self.window.clone(), self.surface.as_mut()) else { return };
        let size = window.inner_size();
        let (Some(width), Some(height)) = (NonZeroU32::new(size.width), NonZeroU32::new(size.height)) else { return };
        let now = Instant::now();
        let dt = self.last_frame.map_or(0.0, |last| (now - last).as_secs_f64().min(0.1));
        self.last_frame = Some(now);
        if let Some(gamepads) = self.gamepads.as_mut() {
            gamepads.poll(&mut self.events);
        }
        let events = std::mem::take(&mut self.events);
        let output = match self.player.frame(dt, &events, width.get(), height.get()) {
            Ok(output) => output,
            Err(error) => {
                let message = error.to_string();
                self.fail(event_loop, message);
                return;
            }
        };
        if surface.resize(width, height).is_err() {
            return;
        }
        if let Ok(mut buffer) = surface.buffer_mut() {
            // Premultiplied RGBA (opaque) → 0RGB.
            for (out, pixel) in buffer.iter_mut().zip(output.pixels.data().chunks_exact(4)) {
                *out = (u32::from(pixel[0]) << 16) | (u32::from(pixel[1]) << 8) | u32::from(pixel[2]);
            }
            let _ = buffer.present();
        }
        if let Some(audio) = &self.audio {
            for sound in output.sounds {
                audio.play(sound);
            }
        }
        for request in output.requests {
            match request {
                PlayerRequest::Quit => event_loop.exit(),
                PlayerRequest::SetFullscreen(fullscreen) => self.set_fullscreen(fullscreen),
                PlayerRequest::SetTitle(title) => window.set_title(&title),
            }
        }
        self.frames += 1;
        if self.frames.is_multiple_of(60) {
            crash::publish(self.player.crash_report());
        }
        if self.options.exit_after_frames.is_some_and(|limit| self.frames >= limit) {
            event_loop.exit();
        }
    }
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.window.is_some() {
            return;
        }
        let info = self.player.info();
        let (width, height) = (info.window_width.max(320), info.window_height.max(200));
        let attributes = Window::default_attributes()
            .with_title(info.title.clone())
            .with_inner_size(LogicalSize::new(f64::from(width), f64::from(height)))
            .with_min_inner_size(LogicalSize::new(320.0, 200.0));
        let window = match event_loop.create_window(attributes) {
            Ok(window) => Rc::new(window),
            Err(error) => {
                self.error = Some(format!("Cannot open a window: {error}"));
                event_loop.exit();
                return;
            }
        };
        let context = match softbuffer::Context::new(Rc::clone(&window)) {
            Ok(context) => context,
            Err(error) => {
                self.error = Some(format!("Cannot draw to the window: {error}"));
                event_loop.exit();
                return;
            }
        };
        match softbuffer::Surface::new(&context, Rc::clone(&window)) {
            Ok(surface) => self.surface = Some(surface),
            Err(error) => {
                self.error = Some(format!("Cannot draw to the window: {error}"));
                event_loop.exit();
                return;
            }
        }
        if self.player.settings().display.fullscreen {
            window.set_fullscreen(Some(Fullscreen::Borderless(None)));
        }
        window.request_redraw();
        self.window = Some(window);
        self.next_frame = Instant::now();
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, _: WindowId, event: WindowEvent) {
        match event {
            WindowEvent::CloseRequested => event_loop.exit(),
            WindowEvent::RedrawRequested => self.render(event_loop),
            WindowEvent::KeyboardInput { event, .. } => {
                if let Some(key) = keys::key_name(&event.logical_key) {
                    self.events.push(match event.state {
                        ElementState::Pressed => InputEvent::KeyDown { key, repeat: event.repeat },
                        ElementState::Released => InputEvent::KeyUp { key },
                    });
                }
            }
            WindowEvent::CursorMoved { position, .. } => {
                self.pointer = position;
                self.events.push(InputEvent::PointerMove { x: position.x as f32, y: position.y as f32 });
            }
            WindowEvent::CursorLeft { .. } => self.events.push(InputEvent::PointerLeft),
            WindowEvent::MouseInput { state, button, .. } => {
                let button = match button {
                    MouseButton::Left => PointerButton::Primary,
                    MouseButton::Right => PointerButton::Secondary,
                    MouseButton::Middle => PointerButton::Middle,
                    _ => return,
                };
                let (x, y) = (self.pointer.x as f32, self.pointer.y as f32);
                self.events.push(match state {
                    ElementState::Pressed => InputEvent::PointerDown { x, y, button },
                    ElementState::Released => InputEvent::PointerUp { x, y, button },
                });
            }
            WindowEvent::MouseWheel { delta, .. } => {
                let (dx, dy) = match delta {
                    MouseScrollDelta::LineDelta(x, y) => (x, y),
                    MouseScrollDelta::PixelDelta(position) => (position.x as f32 / 40.0, position.y as f32 / 40.0),
                };
                self.events.push(InputEvent::Wheel { dx, dy });
            }
            WindowEvent::Focused(false) => self.events.push(InputEvent::FocusLost),
            WindowEvent::Resized(_) | WindowEvent::ScaleFactorChanged { .. } => {
                if let Some(window) = &self.window {
                    window.request_redraw();
                }
            }
            _ => {}
        }
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        let now = Instant::now();
        if now >= self.next_frame {
            let period = Duration::from_secs_f64(1.0 / TARGET_FPS);
            // Keep the cadence, but never try to catch up on missed frames.
            self.next_frame = (self.next_frame + period).max(now);
            if let Some(window) = &self.window {
                window.request_redraw();
            }
        }
        event_loop.set_control_flow(ControlFlow::WaitUntil(self.next_frame));
    }
}
