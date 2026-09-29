//! Platform-neutral input: the [`InputEvent`]s a host feeds [`crate::Player::frame`], and the
//! [`InputRouter`] that turns them into game keys (through the rebindable [`Bindings`]) and UI
//! input (navigation, pointer, raw keys).
//!
//! - Keys use farm-runtime's names (`"w"`, `"arrowup"`, `" "`, `"enter"`, `"escape"`, …). A key
//!   bound to an action reaches the engine as that action's canonical key; a default game key
//!   that was rebound elsewhere is swallowed; any other key passes through (digits, creator
//!   hotkeys).
//! - Gamepads are abstract: buttons ([`GamepadButton`]) and axes ([`GamepadAxis`], +X right, +Y
//!   down). The left stick and D-pad move and navigate, A interacts and confirms, B closes and goes
//!   back, the other buttons follow the gamepad bindings.
//! - Held gamepad directions repeat in menus (0.4 s, then every 0.12 s).
//! - On-screen controls (the web demo's touch buttons) send [`InputEvent::Action`]: a game
//!   action held or let go, whatever keys it is bound to. Move actions navigate menus and
//!   repeat like the D-pad, Interact confirms and Menu goes back.

use farm_ui::settings::{BindAction, Bindings, KeyRoute};
use farm_ui::{GamepadButton, InputDevice, NavAction, UiInput};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

/// A gamepad axis in -1..=1 (+X right, +Y down).
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum GamepadAxis {
    LeftX,
    LeftY,
    RightX,
    RightY,
}

/// A pointer button.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PointerButton {
    #[default]
    Primary,
    Secondary,
    Middle,
}

/// One input event from the host. Pointer positions are physical pixels of the frame.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum InputEvent {
    /// A key went down (farm-runtime key names); `repeat` for auto-repeat.
    KeyDown {
        key: String,
        #[serde(default)]
        repeat: bool,
    },
    KeyUp {
        key: String,
    },
    /// Typed text (reserved for text fields).
    Text {
        text: String,
    },
    PointerMove {
        x: f32,
        y: f32,
    },
    PointerDown {
        x: f32,
        y: f32,
        #[serde(default)]
        button: PointerButton,
    },
    PointerUp {
        x: f32,
        y: f32,
        #[serde(default)]
        button: PointerButton,
    },
    /// The pointer left the frame.
    PointerLeft,
    /// Scroll in lines (+Y scrolls content up, like a wheel turned away from the user).
    Wheel {
        #[serde(default)]
        dx: f32,
        dy: f32,
    },
    GamepadButton {
        button: GamepadButton,
        pressed: bool,
    },
    GamepadAxis {
        axis: GamepadAxis,
        value: f32,
    },
    /// An on-screen control held (`pressed`) or let go: the action itself, not a key, so
    /// rebinding keys never breaks touch controls.
    Action {
        action: BindAction,
        pressed: bool,
    },
    /// The window lost focus: every held key and button is released.
    FocusLost,
}

/// A key change for the engine (canonical names).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum GameKey {
    Down(String),
    Up(String),
}

/// One frame of routed input.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct FrameInput {
    pub ui: UiInput,
    /// Key changes for the engine, in order.
    pub game: Vec<GameKey>,
    /// Canonical keys that went down this frame (panel hotkeys).
    pub game_pressed: Vec<String>,
    /// Accept (Enter, Space, Interact, gamepad A) went down / up this frame (minigame hold).
    pub accept_pressed: bool,
    pub accept_released: bool,
    /// Alt+Enter or F11.
    pub toggle_fullscreen: bool,
    pub focus_lost: bool,
}

const STICK_PRESS: f32 = 0.5;
const STICK_RELEASE: f32 = 0.35;
const REPEAT_DELAY: f64 = 0.4;
const REPEAT_EVERY: f64 = 0.12;
/// Lines per second the right stick scrolls at full tilt.
const STICK_SCROLL: f32 = 14.0;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
enum Direction {
    Up,
    Down,
    Left,
    Right,
}

impl Direction {
    fn nav(self) -> NavAction {
        match self {
            Direction::Up => NavAction::Up,
            Direction::Down => NavAction::Down,
            Direction::Left => NavAction::Left,
            Direction::Right => NavAction::Right,
        }
    }

    fn action(self) -> BindAction {
        match self {
            Direction::Up => BindAction::MoveUp,
            Direction::Down => BindAction::MoveDown,
            Direction::Left => BindAction::MoveLeft,
            Direction::Right => BindAction::MoveRight,
        }
    }

    fn of_action(action: BindAction) -> Option<Direction> {
        Some(match action {
            BindAction::MoveUp => Direction::Up,
            BindAction::MoveDown => Direction::Down,
            BindAction::MoveLeft => Direction::Left,
            BindAction::MoveRight => Direction::Right,
            _ => return None,
        })
    }
}

/// Turns host events into [`FrameInput`]s. Keep one per player.
#[derive(Debug, Clone, Default)]
pub struct InputRouter {
    /// Physical keys held → the canonical key they pressed (`None`: swallowed).
    keys: BTreeMap<String, Option<String>>,
    /// Canonical keys held → how many sources hold them.
    held: BTreeMap<String, u32>,
    buttons: BTreeSet<GamepadButton>,
    /// Canonical keys held by gamepad buttons.
    button_keys: BTreeMap<GamepadButton, String>,
    /// Actions held by on-screen controls.
    actions: BTreeSet<BindAction>,
    axes: BTreeMap<GamepadAxis, f32>,
    /// Stick directions currently pressed (with hysteresis).
    stick: BTreeSet<Direction>,
    /// The direction repeating in menus and when it fires next.
    repeat: Option<(Direction, f64)>,
    pointer: Option<(f32, f32)>,
    pointer_down: bool,
    device: InputDevice,
    accept_held: u32,
    time: f64,
}

fn key_nav(key: &str, shift: bool) -> Option<NavAction> {
    Some(match key {
        "arrowup" => NavAction::Up,
        "arrowdown" => NavAction::Down,
        "arrowleft" => NavAction::Left,
        "arrowright" => NavAction::Right,
        "enter" | " " => NavAction::Accept,
        "escape" => NavAction::Back,
        "tab" if shift => NavAction::TabPrev,
        "tab" => NavAction::TabNext,
        _ => return None,
    })
}

fn action_nav(action: BindAction) -> Option<NavAction> {
    Some(match action {
        BindAction::MoveUp => NavAction::Up,
        BindAction::MoveDown => NavAction::Down,
        BindAction::MoveLeft => NavAction::Left,
        BindAction::MoveRight => NavAction::Right,
        BindAction::Interact => NavAction::Accept,
        BindAction::Menu => NavAction::Back,
        _ => return None,
    })
}

fn is_accept_key(key: &str) -> bool {
    matches!(key, "enter" | " ")
}

impl InputRouter {
    pub fn new() -> Self {
        Self::default()
    }

    /// The device used last.
    pub fn device(&self) -> InputDevice {
        self.device
    }

    fn press(&mut self, canonical: &str, out: &mut FrameInput) {
        let count = self.held.entry(canonical.to_owned()).or_insert(0);
        *count += 1;
        out.game.push(GameKey::Down(canonical.to_owned()));
        out.game_pressed.push(canonical.to_owned());
    }

    fn release(&mut self, canonical: &str, out: &mut FrameInput) {
        if let Some(count) = self.held.get_mut(canonical) {
            *count = count.saturating_sub(1);
            if *count == 0 {
                self.held.remove(canonical);
                out.game.push(GameKey::Up(canonical.to_owned()));
            }
        }
    }

    fn accept(&mut self, down: bool, out: &mut FrameInput) {
        if down {
            if self.accept_held == 0 {
                out.accept_pressed = true;
            }
            self.accept_held += 1;
        } else if self.accept_held > 0 {
            self.accept_held -= 1;
            if self.accept_held == 0 {
                out.accept_released = true;
            }
        }
    }

    fn modifier(&self, key: &str) -> bool {
        self.keys.contains_key(key)
    }

    /// Routes one frame of events. `capture` means the settings screen waits for a key to bind:
    /// keys then only arrive as raw presses (no navigation, nothing for the game).
    pub fn frame(&mut self, events: &[InputEvent], dt: f64, bindings: &Bindings, capture: bool) -> FrameInput {
        let dt = if dt.is_finite() { dt.max(0.0) } else { 0.0 };
        self.time += dt;
        let mut out = FrameInput::default();
        for event in events {
            match event {
                InputEvent::KeyDown { key, repeat } => {
                    let key = key.to_lowercase();
                    self.device = InputDevice::Keyboard;
                    let alt = self.modifier("alt");
                    if (key == "enter" && alt) || key == "f11" {
                        if !repeat {
                            out.toggle_fullscreen = true;
                        }
                        continue;
                    }
                    if !repeat {
                        out.ui.keys_pressed.push(key.clone());
                    }
                    if capture {
                        continue;
                    }
                    let route = bindings.route(&key);
                    let mut navs = Vec::new();
                    if let KeyRoute::Action(action) = route {
                        navs.extend(action_nav(action));
                    }
                    if let Some(nav) = key_nav(&key, self.modifier("shift")) {
                        if !navs.contains(&nav) {
                            navs.push(nav);
                        }
                    }
                    out.ui.nav.extend(navs);
                    let accepts = is_accept_key(&key) || route == KeyRoute::Action(BindAction::Interact);
                    let canonical = match route {
                        KeyRoute::Action(action) => Some(action.canonical_key().to_owned()),
                        KeyRoute::Pass => Some(key.clone()),
                        KeyRoute::Drop => None,
                    };
                    if *repeat {
                        // Auto-repeat re-presses what is held (hosts' key repeat, like the web).
                        if let Some(Some(canonical)) = self.keys.get(&key) {
                            out.game.push(GameKey::Down(canonical.clone()));
                            out.game_pressed.push(canonical.clone());
                        }
                        continue;
                    }
                    if self.keys.contains_key(&key) {
                        continue;
                    }
                    if accepts {
                        self.accept(true, &mut out);
                    }
                    if let Some(canonical) = &canonical {
                        self.press(canonical, &mut out);
                    }
                    self.keys.insert(key, canonical);
                }
                InputEvent::KeyUp { key } => {
                    let key = key.to_lowercase();
                    if let Some(canonical) = self.keys.remove(&key) {
                        if is_accept_key(&key) || canonical.as_deref() == Some(BindAction::Interact.canonical_key()) {
                            self.accept(false, &mut out);
                        }
                        if let Some(canonical) = canonical {
                            self.release(&canonical, &mut out);
                        }
                    }
                }
                InputEvent::Text { .. } => {}
                InputEvent::PointerMove { x, y } => {
                    self.pointer = Some((*x, *y));
                    self.device = InputDevice::Mouse;
                }
                InputEvent::PointerDown { x, y, button } => {
                    self.pointer = Some((*x, *y));
                    self.device = InputDevice::Mouse;
                    if *button == PointerButton::Primary {
                        self.pointer_down = true;
                        out.ui.pointer_pressed = true;
                    }
                }
                InputEvent::PointerUp { x, y, button } => {
                    self.pointer = Some((*x, *y));
                    if *button == PointerButton::Primary && self.pointer_down {
                        self.pointer_down = false;
                        out.ui.pointer_released = true;
                    }
                }
                InputEvent::PointerLeft => {
                    self.pointer = None;
                }
                InputEvent::Wheel { dy, .. } => {
                    out.ui.wheel += dy;
                    self.device = InputDevice::Mouse;
                }
                InputEvent::GamepadButton { button, pressed } => {
                    self.device = InputDevice::Gamepad;
                    self.gamepad_button(*button, *pressed, bindings, capture, &mut out);
                }
                InputEvent::GamepadAxis { axis, value } => {
                    let value = if value.is_finite() { value.clamp(-1.0, 1.0) } else { 0.0 };
                    if value.abs() > STICK_PRESS {
                        self.device = InputDevice::Gamepad;
                    }
                    self.axes.insert(*axis, value);
                }
                InputEvent::Action { action, pressed } => {
                    // Touch controls sit on a pointer screen: hints follow the pointer.
                    self.device = InputDevice::Mouse;
                    self.action(*action, *pressed, capture, &mut out);
                }
                InputEvent::FocusLost => {
                    self.release_all(&mut out);
                    out.focus_lost = true;
                }
            }
        }
        if !capture {
            self.update_stick(bindings, &mut out);
            self.repeat_navigation(&mut out);
            let scroll = self.axes.get(&GamepadAxis::RightY).copied().unwrap_or(0.0);
            if scroll.abs() > STICK_RELEASE {
                out.ui.wheel -= scroll * STICK_SCROLL * dt as f32;
            }
        }
        out.ui.pointer = self.pointer;
        out.ui.pointer_down = self.pointer_down;
        out.ui.accept_held = self.accept_held > 0;
        out.ui.device = self.device;
        out
    }

    fn gamepad_button(
        &mut self,
        button: GamepadButton,
        pressed: bool,
        bindings: &Bindings,
        capture: bool,
        out: &mut FrameInput,
    ) {
        if pressed == self.buttons.contains(&button) {
            return;
        }
        if pressed {
            self.buttons.insert(button);
        } else {
            self.buttons.remove(&button);
        }
        if capture {
            return;
        }
        let direction = match button {
            GamepadButton::DpadUp => Some(Direction::Up),
            GamepadButton::DpadDown => Some(Direction::Down),
            GamepadButton::DpadLeft => Some(Direction::Left),
            GamepadButton::DpadRight => Some(Direction::Right),
            _ => None,
        };
        if pressed {
            match button {
                GamepadButton::South => {
                    out.ui.nav.push(NavAction::Accept);
                    self.accept(true, out);
                }
                GamepadButton::East => out.ui.nav.push(NavAction::Back),
                GamepadButton::LeftShoulder => out.ui.nav.push(NavAction::TabPrev),
                GamepadButton::RightShoulder => out.ui.nav.push(NavAction::TabNext),
                GamepadButton::Start => out.ui.nav.push(NavAction::Back),
                _ => {}
            }
            if let Some(direction) = direction {
                out.ui.nav.push(direction.nav());
                self.repeat = Some((direction, self.time + REPEAT_DELAY));
            }
        } else {
            if button == GamepadButton::South {
                self.accept(false, out);
            }
            if direction.is_some() && self.repeat.is_some_and(|(held, _)| Some(held) == direction) {
                self.repeat = None;
            }
        }
        // The game: B always closes; the rest follows the bindings.
        let canonical = if button == GamepadButton::East {
            Some(BindAction::Menu.canonical_key().to_owned())
        } else {
            bindings.gamepad_action(button).map(|action| action.canonical_key().to_owned())
        };
        if let Some(canonical) = canonical {
            if pressed {
                self.press(&canonical, out);
                self.button_keys.insert(button, canonical);
            } else if let Some(held) = self.button_keys.remove(&button) {
                self.release(&held, out);
            }
        }
    }

    /// An on-screen control: like a key bound to `action` (navigation, accept, the canonical
    /// key for the game), held until it is let go.
    fn action(&mut self, action: BindAction, pressed: bool, capture: bool, out: &mut FrameInput) {
        if pressed == self.actions.contains(&action) {
            return;
        }
        if pressed {
            self.actions.insert(action);
        } else {
            self.actions.remove(&action);
        }
        if capture {
            return;
        }
        let direction = Direction::of_action(action);
        let canonical = action.canonical_key();
        if pressed {
            out.ui.nav.extend(action_nav(action));
            if action == BindAction::Interact {
                self.accept(true, out);
            }
            if let Some(direction) = direction {
                self.repeat = Some((direction, self.time + REPEAT_DELAY));
            }
            self.press(canonical, out);
        } else {
            if action == BindAction::Interact {
                self.accept(false, out);
            }
            if direction.is_some() && self.repeat.is_some_and(|(held, _)| Some(held) == direction) {
                self.repeat = None;
            }
            self.release(canonical, out);
        }
    }

    fn update_stick(&mut self, bindings: &Bindings, out: &mut FrameInput) {
        let x = self.axes.get(&GamepadAxis::LeftX).copied().unwrap_or(0.0);
        let y = self.axes.get(&GamepadAxis::LeftY).copied().unwrap_or(0.0);
        for (direction, value) in
            [(Direction::Left, -x), (Direction::Right, x), (Direction::Up, -y), (Direction::Down, y)]
        {
            let held = self.stick.contains(&direction);
            let canonical = direction.action().canonical_key();
            if !held && value > STICK_PRESS {
                self.stick.insert(direction);
                self.press(canonical, out);
                out.ui.nav.push(direction.nav());
                self.repeat = Some((direction, self.time + REPEAT_DELAY));
            } else if held && value < STICK_RELEASE {
                self.stick.remove(&direction);
                self.release(canonical, out);
                if self.repeat.is_some_and(|(repeating, _)| repeating == direction) {
                    self.repeat = None;
                }
            }
        }
        let _ = bindings;
    }

    fn repeat_navigation(&mut self, out: &mut FrameInput) {
        if let Some((direction, next)) = self.repeat {
            if self.time >= next {
                out.ui.nav.push(direction.nav());
                self.repeat = Some((direction, next.max(self.time - REPEAT_EVERY) + REPEAT_EVERY));
            }
        }
    }

    /// Releases every held key and button (focus loss).
    pub fn release_all(&mut self, out: &mut FrameInput) {
        for canonical in self.held.keys() {
            out.game.push(GameKey::Up(canonical.clone()));
        }
        if self.accept_held > 0 {
            out.accept_released = true;
        }
        self.keys.clear();
        self.held.clear();
        self.buttons.clear();
        self.button_keys.clear();
        self.actions.clear();
        self.axes.clear();
        self.stick.clear();
        self.repeat = None;
        self.accept_held = 0;
        self.pointer_down = false;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn down(key: &str) -> InputEvent {
        InputEvent::KeyDown { key: key.into(), repeat: false }
    }

    fn up(key: &str) -> InputEvent {
        InputEvent::KeyUp { key: key.into() }
    }

    #[test]
    fn keys_reach_the_engine_as_canonical_keys() {
        let mut router = InputRouter::new();
        let mut bindings = Bindings::default();
        let frame = router.frame(&[down("arrowup"), down("w")], 0.016, &bindings, false);
        assert_eq!(frame.game, vec![GameKey::Down("w".into()), GameKey::Down("w".into())]);
        // Up only when the last source lets go.
        let frame = router.frame(&[up("arrowup")], 0.016, &bindings, false);
        assert!(frame.game.is_empty());
        let frame = router.frame(&[up("w")], 0.016, &bindings, false);
        assert_eq!(frame.game, vec![GameKey::Up("w".into())]);
        bindings.rebind(BindAction::Water, "k");
        let frame = router.frame(&[down("k"), down("q"), down("1")], 0.016, &bindings, false);
        assert_eq!(frame.game, vec![GameKey::Down("q".into()), GameKey::Down("1".into())]);
        assert_eq!(frame.ui.keys_pressed, ["k", "q", "1"]);
    }

    #[test]
    fn keyboard_navigation_and_accept_edges() {
        let mut router = InputRouter::new();
        let bindings = Bindings::default();
        let frame = router.frame(&[down("arrowdown"), down("enter")], 0.016, &bindings, false);
        assert_eq!(frame.ui.nav, [NavAction::Down, NavAction::Accept]);
        assert!(frame.accept_pressed && frame.ui.accept_held);
        let frame = router.frame(&[down("shift"), down("tab"), up("enter")], 0.016, &bindings, false);
        assert_eq!(frame.ui.nav, [NavAction::TabPrev]);
        assert!(frame.accept_released);
        let frame = router.frame(&[down("alt"), down("enter")], 0.016, &bindings, false);
        assert!(frame.toggle_fullscreen);
        assert!(frame.ui.nav.is_empty());
    }

    #[test]
    fn capture_mode_only_reports_raw_keys() {
        let mut router = InputRouter::new();
        let frame = router.frame(&[down("arrowdown")], 0.016, &Bindings::default(), true);
        assert!(frame.ui.nav.is_empty() && frame.game.is_empty());
        assert_eq!(frame.ui.keys_pressed, ["arrowdown"]);
    }

    #[test]
    fn gamepad_moves_navigates_and_repeats() {
        let mut router = InputRouter::new();
        let bindings = Bindings::default();
        let frame =
            router.frame(&[InputEvent::GamepadAxis { axis: GamepadAxis::LeftY, value: 0.9 }], 0.016, &bindings, false);
        assert_eq!(frame.game, vec![GameKey::Down("s".into())]);
        assert_eq!(frame.ui.nav, [NavAction::Down]);
        assert_eq!(frame.ui.device, InputDevice::Gamepad);
        // Held: repeats after the delay.
        let frame = router.frame(&[], 0.3, &bindings, false);
        assert!(frame.ui.nav.is_empty());
        let frame = router.frame(&[], 0.2, &bindings, false);
        assert_eq!(frame.ui.nav, [NavAction::Down]);
        let frame =
            router.frame(&[InputEvent::GamepadAxis { axis: GamepadAxis::LeftY, value: 0.1 }], 0.016, &bindings, false);
        assert_eq!(frame.game, vec![GameKey::Up("s".into())]);
        let pressed = |button| InputEvent::GamepadButton { button, pressed: true };
        let frame = router.frame(
            &[pressed(GamepadButton::South), pressed(GamepadButton::East), pressed(GamepadButton::West)],
            0.016,
            &bindings,
            false,
        );
        assert_eq!(frame.ui.nav, [NavAction::Accept, NavAction::Back]);
        assert_eq!(
            frame.game,
            vec![GameKey::Down("e".into()), GameKey::Down("escape".into()), GameKey::Down("q".into())]
        );
        let frame = router.frame(&[InputEvent::FocusLost], 0.016, &bindings, false);
        assert!(frame.focus_lost);
        assert_eq!(frame.game.len(), 3);
    }

    #[test]
    fn on_screen_actions_hold_the_action_whatever_its_keys() {
        let mut router = InputRouter::new();
        let mut bindings = Bindings::default();
        // Rebinding keys never changes what a touch button does.
        bindings.rebind(BindAction::Interact, "k");
        let action = |action, pressed| InputEvent::Action { action, pressed };
        let json: InputEvent = serde_json::from_str(r#"{"type":"action","action":"move-up","pressed":true}"#).unwrap();
        assert_eq!(json, action(BindAction::MoveUp, true));

        let frame = router.frame(&[json, action(BindAction::Interact, true)], 0.016, &bindings, false);
        assert_eq!(frame.game, vec![GameKey::Down("w".into()), GameKey::Down("e".into())]);
        assert_eq!(frame.ui.nav, [NavAction::Up, NavAction::Accept]);
        assert!(frame.accept_pressed && frame.ui.accept_held);
        assert_eq!(frame.ui.device, InputDevice::Mouse);
        // A second press of a held control changes nothing; held moves repeat in menus.
        let frame = router.frame(&[action(BindAction::MoveUp, true)], 0.5, &bindings, false);
        assert!(frame.game.is_empty());
        assert_eq!(frame.ui.nav, [NavAction::Up]);

        let frame = router.frame(
            &[action(BindAction::MoveUp, false), action(BindAction::Interact, false), action(BindAction::Sleep, true)],
            0.016,
            &bindings,
            false,
        );
        assert_eq!(frame.game, vec![GameKey::Up("w".into()), GameKey::Up("e".into()), GameKey::Down("z".into())]);
        assert!(frame.accept_released);
        assert_eq!(frame.game_pressed, ["z"]);
        let frame = router.frame(&[action(BindAction::Menu, true)], 0.016, &bindings, false);
        assert_eq!(frame.ui.nav, [NavAction::Back]);
        assert_eq!(frame.game, vec![GameKey::Down("escape".into())]);
        // Focus loss lets go of everything held on screen.
        let frame = router.frame(&[InputEvent::FocusLost], 0.016, &bindings, false);
        assert_eq!(frame.game.len(), 2);
        let frame = router.frame(&[action(BindAction::Sleep, false)], 0.016, &bindings, false);
        assert!(frame.game.is_empty());
    }
}
