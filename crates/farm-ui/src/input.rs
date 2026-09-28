//! What the UI reads from one frame of input. The host (farm-player) turns keyboard, gamepad
//! and pointer events into a [`UiInput`]: pointer positions in physical pixels, navigation
//! actions already mapped from keys and buttons, and the raw keys pressed (dialogue digits,
//! key rebinding).

use serde::{Deserialize, Serialize};

/// A menu navigation action (keyboard, gamepad or both).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum NavAction {
    Up,
    Down,
    Left,
    Right,
    /// Activate the focused widget (Enter, Space, gamepad A).
    Accept,
    /// Leave the current screen (Escape, gamepad B).
    Back,
    /// Next tab (Tab, gamepad RB).
    TabNext,
    /// Previous tab (Shift+Tab, gamepad LB).
    TabPrev,
}

/// The device the player used last; prompts show its glyphs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum InputDevice {
    #[default]
    Keyboard,
    Mouse,
    Gamepad,
}

/// An abstract gamepad button (Xbox names; the host maps its pads onto these).
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum GamepadButton {
    /// Bottom face button (Xbox A, PlayStation ×, Nintendo B).
    South,
    /// Right face button (Xbox B).
    East,
    /// Left face button (Xbox X).
    West,
    /// Top face button (Xbox Y).
    North,
    Start,
    Select,
    LeftShoulder,
    RightShoulder,
    LeftTrigger,
    RightTrigger,
    LeftStick,
    RightStick,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
}

impl GamepadButton {
    pub const ALL: [GamepadButton; 16] = [
        GamepadButton::South,
        GamepadButton::East,
        GamepadButton::West,
        GamepadButton::North,
        GamepadButton::Start,
        GamepadButton::Select,
        GamepadButton::LeftShoulder,
        GamepadButton::RightShoulder,
        GamepadButton::LeftTrigger,
        GamepadButton::RightTrigger,
        GamepadButton::LeftStick,
        GamepadButton::RightStick,
        GamepadButton::DpadUp,
        GamepadButton::DpadDown,
        GamepadButton::DpadLeft,
        GamepadButton::DpadRight,
    ];

    /// The glyph shown in prompts (Xbox / Steam Deck layout).
    pub fn label(self) -> &'static str {
        match self {
            GamepadButton::South => "A",
            GamepadButton::East => "B",
            GamepadButton::West => "X",
            GamepadButton::North => "Y",
            GamepadButton::Start => "Menu",
            GamepadButton::Select => "View",
            GamepadButton::LeftShoulder => "LB",
            GamepadButton::RightShoulder => "RB",
            GamepadButton::LeftTrigger => "LT",
            GamepadButton::RightTrigger => "RT",
            GamepadButton::LeftStick => "L3",
            GamepadButton::RightStick => "R3",
            GamepadButton::DpadUp => "D-pad \u{2191}",
            GamepadButton::DpadDown => "D-pad \u{2193}",
            GamepadButton::DpadLeft => "D-pad \u{2190}",
            GamepadButton::DpadRight => "D-pad \u{2192}",
        }
    }
}

/// One frame of UI input. Pointer coordinates are physical pixels; the UI converts them with
/// its scale.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct UiInput {
    /// Where the pointer is (`None` when it left the window).
    pub pointer: Option<(f32, f32)>,
    /// The primary button went down this frame.
    pub pointer_pressed: bool,
    /// The primary button went up this frame.
    pub pointer_released: bool,
    /// The primary button is held.
    pub pointer_down: bool,
    /// Scroll in lines; positive scrolls content up (wheel away from the user).
    pub wheel: f32,
    /// Navigation in the order it happened.
    pub nav: Vec<NavAction>,
    /// Accept (Enter, Space, gamepad A) is held: hold-to-act buttons.
    pub accept_held: bool,
    /// Raw keys pressed this frame (farm-runtime names, before rebinding): digits pick dialogue
    /// options and the controls screen captures new bindings.
    pub keys_pressed: Vec<String>,
    pub device: InputDevice,
}

impl UiInput {
    /// The 1-based digit keys pressed this frame, in order.
    pub fn digits(&self) -> impl Iterator<Item = u8> + '_ {
        self.keys_pressed.iter().filter_map(|key| match key.as_bytes() {
            [digit @ b'1'..=b'9'] => Some(digit - b'0'),
            _ => None,
        })
    }
}
