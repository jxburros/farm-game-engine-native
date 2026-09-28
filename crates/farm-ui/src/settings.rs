//! Player settings: display, audio, controls and accessibility. They are plain data with serde
//! (the desktop player stores them as TOML in the config folder, apart from the saves); every
//! field has a default, so older or partial files load.
//!
//! Controls are data too. [`Bindings`] maps each [`BindAction`] to keyboard keys (rebindable in
//! the settings screen) and gamepad buttons to actions. The game engine only knows the default
//! keys (farm-runtime's bindings), so the player translates a pressed key into the action's
//! *canonical* key ([`BindAction::canonical_key`]) before the engine sees it.

use crate::input::GamepadButton;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

/// Everything the settings screen edits.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "kebab-case")]
pub struct Settings {
    pub display: DisplaySettings,
    pub audio: AudioSettings,
    pub controls: Bindings,
    pub accessibility: AccessibilitySettings,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "kebab-case")]
pub struct DisplaySettings {
    /// Borderless fullscreen instead of a window.
    pub fullscreen: bool,
    /// Scale the pixel art by whole numbers only (crisp pixels, possible borders).
    pub integer_scaling: bool,
    /// Size of the interface (1 = 100 %).
    pub ui_scale: f32,
}

impl Default for DisplaySettings {
    fn default() -> Self {
        Self { fullscreen: false, integer_scaling: true, ui_scale: 1.0 }
    }
}

/// Volumes in 0..=1 (defaults from farm-runtime's audio model).
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "kebab-case")]
pub struct AudioSettings {
    pub master: f32,
    pub music: f32,
    pub effects: f32,
    pub muted: bool,
}

impl Default for AudioSettings {
    fn default() -> Self {
        let defaults = farm_runtime::audio::DEFAULT_SETTINGS;
        Self {
            master: defaults.master as f32,
            music: defaults.music as f32,
            effects: defaults.sfx as f32,
            muted: defaults.muted,
        }
    }
}

impl AudioSettings {
    /// The gain of sound effects (0 when muted).
    pub fn effects_gain(&self) -> f32 {
        if self.muted {
            0.0
        } else {
            (self.master.clamp(0.0, 1.0) * self.effects.clamp(0.0, 1.0)).clamp(0.0, 1.0)
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "kebab-case")]
pub struct AccessibilitySettings {
    /// Multiplies every font size (1 = default).
    pub text_size: f32,
    /// No floating pops, fades or screen flashes.
    pub reduced_motion: bool,
}

impl Default for AccessibilitySettings {
    fn default() -> Self {
        Self { text_size: 1.0, reduced_motion: false }
    }
}

/// Text sizes offered by the settings screen.
pub const TEXT_SIZES: [(&str, f32); 4] = [("Small", 0.9), ("Default", 1.0), ("Large", 1.2), ("Largest", 1.4)];
/// Interface scales offered by the settings screen.
pub const UI_SCALES: [(&str, f32); 6] =
    [("75%", 0.75), ("90%", 0.9), ("100%", 1.0), ("110%", 1.1), ("125%", 1.25), ("150%", 1.5)];

/// The option index closest to `value`.
pub fn nearest_option(options: &[(&str, f32)], value: f32) -> usize {
    options
        .iter()
        .enumerate()
        .min_by(|(_, a), (_, b)| (a.1 - value).abs().total_cmp(&(b.1 - value).abs()))
        .map_or(0, |(index, _)| index)
}

/// A rebindable game action.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum BindAction {
    MoveUp,
    MoveDown,
    MoveLeft,
    MoveRight,
    Interact,
    Water,
    Till,
    Axe,
    Pickaxe,
    Scythe,
    Sleep,
    Inventory,
    Quests,
    Craft,
    Menu,
}

impl BindAction {
    pub const ALL: [BindAction; 15] = [
        BindAction::MoveUp,
        BindAction::MoveDown,
        BindAction::MoveLeft,
        BindAction::MoveRight,
        BindAction::Interact,
        BindAction::Water,
        BindAction::Till,
        BindAction::Axe,
        BindAction::Pickaxe,
        BindAction::Scythe,
        BindAction::Sleep,
        BindAction::Inventory,
        BindAction::Quests,
        BindAction::Craft,
        BindAction::Menu,
    ];

    /// The key the engine's bindings (farm-runtime `input::bindings`) know this action by.
    pub fn canonical_key(self) -> &'static str {
        match self {
            BindAction::MoveUp => "w",
            BindAction::MoveDown => "s",
            BindAction::MoveLeft => "a",
            BindAction::MoveRight => "d",
            BindAction::Interact => "e",
            BindAction::Water => "q",
            BindAction::Till => "t",
            BindAction::Axe => "r",
            BindAction::Pickaxe => "f",
            BindAction::Scythe => "c",
            BindAction::Sleep => "z",
            BindAction::Inventory => "i",
            BindAction::Quests => "j",
            BindAction::Craft => "x",
            BindAction::Menu => "escape",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            BindAction::MoveUp => "Move up",
            BindAction::MoveDown => "Move down",
            BindAction::MoveLeft => "Move left",
            BindAction::MoveRight => "Move right",
            BindAction::Interact => "Interact",
            BindAction::Water => "Watering can",
            BindAction::Till => "Hoe",
            BindAction::Axe => "Axe",
            BindAction::Pickaxe => "Pickaxe",
            BindAction::Scythe => "Scythe",
            BindAction::Sleep => "Sleep",
            BindAction::Inventory => "Inventory",
            BindAction::Quests => "Quests",
            BindAction::Craft => "Craft",
            BindAction::Menu => "Menu / close",
        }
    }

    /// Short label for the controls hint row.
    pub fn hint(self) -> &'static str {
        match self {
            BindAction::MoveUp | BindAction::MoveDown | BindAction::MoveLeft | BindAction::MoveRight => "Move",
            BindAction::Interact => "Interact",
            BindAction::Water => "Water",
            BindAction::Till => "Till",
            BindAction::Axe => "Axe",
            BindAction::Pickaxe => "Pickaxe",
            BindAction::Scythe => "Scythe",
            BindAction::Sleep => "Sleep",
            BindAction::Inventory => "Inventory",
            BindAction::Quests => "Quests",
            BindAction::Craft => "Craft",
            BindAction::Menu => "Menu",
        }
    }

    fn default_keys(self) -> &'static [&'static str] {
        match self {
            BindAction::MoveUp => &["w", "arrowup"],
            BindAction::MoveDown => &["s", "arrowdown"],
            BindAction::MoveLeft => &["a", "arrowleft"],
            BindAction::MoveRight => &["d", "arrowright"],
            BindAction::Interact => &["e", " ", "enter"],
            BindAction::Menu => &["escape"],
            BindAction::Water => &["q"],
            BindAction::Till => &["t"],
            BindAction::Axe => &["r"],
            BindAction::Pickaxe => &["f"],
            BindAction::Scythe => &["c"],
            BindAction::Sleep => &["z"],
            BindAction::Inventory => &["i"],
            BindAction::Quests => &["j"],
            BindAction::Craft => &["x"],
        }
    }

    fn default_button(self) -> Option<GamepadButton> {
        Some(match self {
            BindAction::Interact => GamepadButton::South,
            BindAction::Menu => GamepadButton::Start,
            BindAction::Water => GamepadButton::West,
            BindAction::Till => GamepadButton::North,
            BindAction::Axe => GamepadButton::LeftShoulder,
            BindAction::Pickaxe => GamepadButton::RightShoulder,
            BindAction::Scythe => GamepadButton::LeftTrigger,
            BindAction::Craft => GamepadButton::RightTrigger,
            BindAction::Inventory => GamepadButton::Select,
            BindAction::Quests => GamepadButton::RightStick,
            BindAction::Sleep => GamepadButton::LeftStick,
            BindAction::MoveUp => GamepadButton::DpadUp,
            BindAction::MoveDown => GamepadButton::DpadDown,
            BindAction::MoveLeft => GamepadButton::DpadLeft,
            BindAction::MoveRight => GamepadButton::DpadRight,
        })
    }
}

/// What the player does with a pressed key before the engine sees it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeyRoute {
    /// A bound action: the engine gets the action's canonical key.
    Action(BindAction),
    /// A default game key that was rebound elsewhere: swallowed so it no longer acts.
    Drop,
    /// Anything else (digits, creator hotkeys): passed through unchanged.
    Pass,
}

/// Keyboard and gamepad bindings.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "kebab-case")]
pub struct Bindings {
    /// Keys per action (farm-runtime key names); the first is the one shown in prompts.
    pub keyboard: BTreeMap<BindAction, Vec<String>>,
    /// The action of each gamepad button. The B button always closes and goes back.
    pub gamepad: BTreeMap<GamepadButton, BindAction>,
}

impl Default for Bindings {
    fn default() -> Self {
        Self {
            keyboard: BindAction::ALL
                .iter()
                .map(|action| (*action, action.default_keys().iter().map(|key| (*key).to_owned()).collect()))
                .collect(),
            gamepad: BindAction::ALL
                .iter()
                .filter_map(|action| action.default_button().map(|button| (button, *action)))
                .collect(),
        }
    }
}

impl Bindings {
    /// The keys bound to `action` (none when the player unbound them all).
    pub fn keys(&self, action: BindAction) -> &[String] {
        self.keyboard.get(&action).map_or(&[], Vec::as_slice)
    }

    /// The action a key triggers.
    pub fn action_for_key(&self, key: &str) -> Option<BindAction> {
        self.keyboard.iter().find(|(_, keys)| keys.iter().any(|bound| bound == key)).map(|(action, _)| *action)
    }

    /// What happens to a pressed key (see [`KeyRoute`]).
    pub fn route(&self, key: &str) -> KeyRoute {
        if let Some(action) = self.action_for_key(key) {
            return KeyRoute::Action(action);
        }
        if farm_runtime::input::bindings::RESERVED_ACTION_KEYS.contains(&key) {
            KeyRoute::Drop
        } else {
            KeyRoute::Pass
        }
    }

    /// Binds `key` as the primary key of `action`, taking it away from any other action.
    pub fn rebind(&mut self, action: BindAction, key: &str) {
        for keys in self.keyboard.values_mut() {
            keys.retain(|bound| bound != key);
        }
        let keys = self.keyboard.entry(action).or_default();
        if keys.is_empty() {
            keys.push(key.to_owned());
        } else {
            keys[0] = key.to_owned();
        }
    }

    /// The action of a gamepad button.
    pub fn gamepad_action(&self, button: GamepadButton) -> Option<BindAction> {
        self.gamepad.get(&button).copied()
    }

    /// The first gamepad button bound to `action`.
    pub fn button_for(&self, action: BindAction) -> Option<GamepadButton> {
        self.gamepad.iter().find(|(_, bound)| **bound == action).map(|(button, _)| *button)
    }

    /// The prompt for an action: its first key.
    pub fn key_label(&self, action: BindAction) -> String {
        self.keys(action).first().map_or_else(|| "—".to_owned(), |key| key_label(key))
    }
}

/// How a key name is shown on a keycap.
pub fn key_label(key: &str) -> String {
    match key {
        " " => "Space".to_owned(),
        "arrowup" => "\u{2191}".to_owned(),
        "arrowdown" => "\u{2193}".to_owned(),
        "arrowleft" => "\u{2190}".to_owned(),
        "arrowright" => "\u{2192}".to_owned(),
        "escape" => "Esc".to_owned(),
        "enter" => "Enter".to_owned(),
        "tab" => "Tab".to_owned(),
        "backspace" => "Backspace".to_owned(),
        "delete" => "Delete".to_owned(),
        "shift" => "Shift".to_owned(),
        "control" => "Ctrl".to_owned(),
        "alt" => "Alt".to_owned(),
        "meta" => "Meta".to_owned(),
        "pageup" => "PgUp".to_owned(),
        "pagedown" => "PgDn".to_owned(),
        "home" => "Home".to_owned(),
        "end" => "End".to_owned(),
        other => other.to_uppercase(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_bindings_match_the_engine_keys() {
        let bindings = Bindings::default();
        for action in BindAction::ALL {
            assert_eq!(bindings.keys(action)[0], action.canonical_key(), "{action:?}");
            assert_eq!(bindings.route(action.canonical_key()), KeyRoute::Action(action));
        }
        assert_eq!(bindings.route("arrowup"), KeyRoute::Action(BindAction::MoveUp));
        assert_eq!(bindings.route("1"), KeyRoute::Pass);
        assert_eq!(bindings.gamepad_action(GamepadButton::South), Some(BindAction::Interact));
        assert_eq!(bindings.button_for(BindAction::Menu), Some(GamepadButton::Start));
    }

    #[test]
    fn rebinding_moves_a_key_and_frees_the_old_one() {
        let mut bindings = Bindings::default();
        bindings.rebind(BindAction::Water, "e");
        assert_eq!(bindings.route("e"), KeyRoute::Action(BindAction::Water));
        assert_eq!(bindings.keys(BindAction::Interact), [" ", "enter"]);
        // The old water key is a default game key now bound to nothing: swallowed.
        assert_eq!(bindings.route("q"), KeyRoute::Drop);
        bindings.rebind(BindAction::Inventory, "tab");
        assert_eq!(bindings.route("tab"), KeyRoute::Action(BindAction::Inventory));
        assert_eq!(bindings.route("i"), KeyRoute::Drop);
        assert_eq!(bindings.key_label(BindAction::Inventory), "Tab");
        assert_eq!(bindings.key_label(BindAction::Water), "E");
    }

    #[test]
    fn settings_survive_json_with_missing_fields() {
        let settings: Settings = serde_json::from_str(r#"{"audio":{"master":0.5},"display":{}}"#).unwrap();
        assert_eq!(settings.audio.master, 0.5);
        assert_eq!(settings.audio.effects, AudioSettings::default().effects);
        assert_eq!(settings.controls, Bindings::default());
        let json = serde_json::to_string(&Settings::default()).unwrap();
        assert_eq!(serde_json::from_str::<Settings>(&json).unwrap(), Settings::default());
        assert_eq!(AudioSettings { muted: true, ..AudioSettings::default() }.effects_gain(), 0.0);
        assert_eq!(nearest_option(&TEXT_SIZES, 1.19), 2);
    }
}
