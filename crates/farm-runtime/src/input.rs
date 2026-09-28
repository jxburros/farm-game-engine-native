//! Keyboard input and play-mode bindings (port of `Input.cs` / engine-runtime `input.ts` and
//! `src/lib/reserved-keys.ts`).
//!
//! [`InputManager`] is a frame-polled key tracker: the host feeds it key-down/key-up events and
//! the game loop reads held keys and one-shot presses, then calls [`InputManager::end_frame`].
//! [`bindings`] turns a frame's presses into engine [`Command`]s and UI toggles.
//!
//! Keys are normalized TS `KeyboardEvent.key.toLowerCase()` values (`"w"`, `"arrowup"`, `" "`,
//! `"enter"`, `"escape"`, …) so bindings, creator hotkeys and saved settings are interchangeable
//! with the web version. Hosts translate their native key events with [`key_names`]
//! ([`key_names::from_avalonia`], [`key_names::from_dom_code`]) or the
//! [`InputManager::key_down_avalonia`] convenience methods.

use farm_sim::schema::directions;
use farm_sim::Command;
use indexmap::IndexSet;

/// Held movement vector, per axis −1/0/1 (C# `MoveVector`).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MoveVector {
    pub dx: f64,
    pub dy: f64,
}

impl MoveVector {
    pub const fn new(dx: f64, dy: f64) -> Self {
        Self { dx, dy }
    }
}

/// Default gameplay keys whose browser/host default action is suppressed
/// (C# `InputManager.DefaultPreventDefaultKeys`).
pub const DEFAULT_PREVENT_DEFAULT_KEYS: &[&str] = &[
    "arrowup",
    "arrowdown",
    "arrowleft",
    "arrowright",
    "w",
    "a",
    "s",
    "d",
    "e",
    "q",
    "t",
    "r",
    "f",
    "c",
    "x",
    "z",
    "i",
    "j",
    " ",
    "enter",
];

/// Frame-polled keyboard input manager (C# `InputManager`). Tracks held keys and one-shot
/// presses; the game loop drains it into engine commands.
#[derive(Debug, Clone)]
pub struct InputManager {
    prevent_default_keys: IndexSet<String>,
    keys_down: IndexSet<String>,
    keys_just_pressed: IndexSet<String>,
}

impl Default for InputManager {
    fn default() -> Self {
        Self::new()
    }
}

impl InputManager {
    /// A manager that suppresses [`DEFAULT_PREVENT_DEFAULT_KEYS`].
    pub fn new() -> Self {
        Self::with_prevent_default_keys(DEFAULT_PREVENT_DEFAULT_KEYS.iter().copied())
    }

    /// A manager with a custom set of keys whose default action is suppressed (C#
    /// `new InputManager(preventDefaultKeys)`). Keys are matched as given (already normalized).
    pub fn with_prevent_default_keys<K: Into<String>>(keys: impl IntoIterator<Item = K>) -> Self {
        Self {
            prevent_default_keys: keys.into_iter().map(Into::into).collect(),
            keys_down: IndexSet::new(),
            keys_just_pressed: IndexSet::new(),
        }
    }

    /// Keys currently held (normalized), in first-press order.
    pub fn keys_down(&self) -> &IndexSet<String> {
        &self.keys_down
    }

    /// Keys pressed since the last [`end_frame`](Self::end_frame).
    pub fn keys_just_pressed(&self) -> &IndexSet<String> {
        &self.keys_just_pressed
    }

    /// Feed a key-down. `key` is a `KeyboardEvent.key` value (any case). Keystrokes aimed at
    /// editable elements (text fields, selects, contenteditable — `editable_target`) belong to
    /// those elements, not the game: they are ignored so gameplay-bound letters can be typed
    /// into editor fields. Returns true when the host should suppress the key's default action
    /// (TS `preventDefault`): gameplay keys without Ctrl/Meta/Alt modifiers.
    pub fn key_down(&mut self, key: &str, modifiers: Modifiers) -> bool {
        if modifiers.editable_target {
            return false;
        }
        let normalized = key.to_lowercase();
        let prevent =
            self.prevent_default_keys.contains(&normalized) && !modifiers.ctrl && !modifiers.meta && !modifiers.alt;
        self.keys_down.insert(normalized.clone());
        self.keys_just_pressed.insert(normalized);
        prevent
    }

    /// Feed a key-up (`KeyboardEvent.key` value, any case).
    pub fn key_up(&mut self, key: &str) {
        // `shift_remove` keeps the remaining keys in press order (C# `HashSet.Remove`).
        self.keys_down.shift_remove(&key.to_lowercase());
    }

    /// [`key_down`](Self::key_down) for an Avalonia `Key` enum name (`key.ToString()`).
    /// Unmapped keys are ignored (returns false).
    pub fn key_down_avalonia(&mut self, avalonia_key_name: &str, modifiers: Modifiers) -> bool {
        match key_names::from_avalonia(avalonia_key_name) {
            Some(key) => self.key_down(&key, modifiers),
            None => false,
        }
    }

    /// [`key_up`](Self::key_up) for an Avalonia `Key` enum name.
    pub fn key_up_avalonia(&mut self, avalonia_key_name: &str) {
        if let Some(key) = key_names::from_avalonia(avalonia_key_name) {
            self.key_up(&key);
        }
    }

    /// Held OR tapped since the last frame (so sub-frame taps still register).
    pub fn pressed(&self, key: &str) -> bool {
        self.keys_down.contains(key) || self.keys_just_pressed.contains(key)
    }

    pub fn just_pressed(&self, key: &str) -> bool {
        self.keys_just_pressed.contains(key)
    }

    /// Held movement vector from WASD/arrows, per axis −1/0/1 (free movement: both axes may be
    /// active at once; opposing keys cancel).
    pub fn move_vector(&self) -> MoveVector {
        let mut dx = 0.0;
        let mut dy = 0.0;
        if self.pressed("arrowleft") || self.pressed("a") {
            dx -= 1.0;
        }
        if self.pressed("arrowright") || self.pressed("d") {
            dx += 1.0;
        }
        if self.pressed("arrowup") || self.pressed("w") {
            dy -= 1.0;
        }
        if self.pressed("arrowdown") || self.pressed("s") {
            dy += 1.0;
        }
        MoveVector { dx, dy }
    }

    /// Current movement direction from WASD/arrows, if any (one of [`directions`]).
    pub fn direction(&self) -> Option<&'static str> {
        if self.pressed("arrowup") || self.pressed("w") {
            Some(directions::UP)
        } else if self.pressed("arrowdown") || self.pressed("s") {
            Some(directions::DOWN)
        } else if self.pressed("arrowleft") || self.pressed("a") {
            Some(directions::LEFT)
        } else if self.pressed("arrowright") || self.pressed("d") {
            Some(directions::RIGHT)
        } else {
            None
        }
    }

    /// Call at the end of each frame.
    pub fn end_frame(&mut self) {
        self.keys_just_pressed.clear();
    }

    /// Forget everything (focus loss, leaving play mode).
    pub fn clear(&mut self) {
        self.keys_down.clear();
        self.keys_just_pressed.clear();
    }
}

/// The optional arguments of C# `InputManager.KeyDown` (`ctrl`, `meta`, `alt`,
/// `editableTarget`); `Modifiers::default()` is a plain key press.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Modifiers {
    pub ctrl: bool,
    pub meta: bool,
    pub alt: bool,
    /// The key event targets an editable element (text field, select, contenteditable).
    pub editable_target: bool,
}

impl Modifiers {
    /// No modifiers, not aimed at an editable element.
    pub const NONE: Self = Self { ctrl: false, meta: false, alt: false, editable_target: false };
    pub const CTRL: Self = Self { ctrl: true, ..Self::NONE };
    pub const META: Self = Self { meta: true, ..Self::NONE };
    pub const ALT: Self = Self { alt: true, ..Self::NONE };
    /// Aimed at an editable element: the game ignores the key.
    pub const EDITABLE: Self = Self { editable_target: true, ..Self::NONE };
}

/// Host key-name translation into the normalized `KeyboardEvent.key` lowercase vocabulary
/// [`InputManager`] uses (C# `KeyNames`).
pub mod key_names {
    pub const ARROW_UP: &str = "arrowup";
    pub const ARROW_DOWN: &str = "arrowdown";
    pub const ARROW_LEFT: &str = "arrowleft";
    pub const ARROW_RIGHT: &str = "arrowright";
    pub const SPACE: &str = " ";
    pub const ENTER: &str = "enter";
    pub const ESCAPE: &str = "escape";

    const AVALONIA: &[(&str, &str)] = &[
        ("Up", ARROW_UP),
        ("Down", ARROW_DOWN),
        ("Left", ARROW_LEFT),
        ("Right", ARROW_RIGHT),
        ("Space", SPACE),
        ("Enter", ENTER),
        ("Return", ENTER),
        ("Escape", ESCAPE),
        ("Tab", "tab"),
        ("Back", "backspace"),
        ("Delete", "delete"),
        ("Insert", "insert"),
        ("Home", "home"),
        ("End", "end"),
        ("PageUp", "pageup"),
        ("Prior", "pageup"),
        ("PageDown", "pagedown"),
        ("Next", "pagedown"),
        ("LeftShift", "shift"),
        ("RightShift", "shift"),
        ("LeftCtrl", "control"),
        ("RightCtrl", "control"),
        ("LeftAlt", "alt"),
        ("RightAlt", "alt"),
        ("LWin", "meta"),
        ("RWin", "meta"),
        ("OemComma", ","),
        ("OemPeriod", "."),
        ("OemMinus", "-"),
        ("OemPlus", "="),
        ("OemQuestion", "/"),
        ("Oem2", "/"),
        ("OemSemicolon", ";"),
        ("Oem1", ";"),
        ("OemQuotes", "'"),
        ("Oem7", "'"),
        ("OemOpenBrackets", "["),
        ("Oem4", "["),
        ("OemCloseBrackets", "]"),
        ("Oem6", "]"),
        ("OemPipe", "\\"),
        ("Oem5", "\\"),
        ("OemBackslash", "\\"),
        ("OemTilde", "`"),
        ("Oem3", "`"),
        ("Multiply", "*"),
        ("Add", "+"),
        ("Subtract", "-"),
        ("Divide", "/"),
        ("Decimal", "."),
    ];

    const DOM_CODES: &[(&str, &str)] = &[
        ("ArrowUp", ARROW_UP),
        ("ArrowDown", ARROW_DOWN),
        ("ArrowLeft", ARROW_LEFT),
        ("ArrowRight", ARROW_RIGHT),
        ("Space", SPACE),
        ("Enter", ENTER),
        ("NumpadEnter", ENTER),
        ("Escape", ESCAPE),
        ("Tab", "tab"),
        ("Backspace", "backspace"),
        ("Delete", "delete"),
        ("Insert", "insert"),
        ("Home", "home"),
        ("End", "end"),
        ("PageUp", "pageup"),
        ("PageDown", "pagedown"),
        ("ShiftLeft", "shift"),
        ("ShiftRight", "shift"),
        ("ControlLeft", "control"),
        ("ControlRight", "control"),
        ("AltLeft", "alt"),
        ("AltRight", "alt"),
        ("MetaLeft", "meta"),
        ("MetaRight", "meta"),
        ("Comma", ","),
        ("Period", "."),
        ("Minus", "-"),
        ("Equal", "="),
        ("Slash", "/"),
        ("Semicolon", ";"),
        ("Quote", "'"),
        ("BracketLeft", "["),
        ("BracketRight", "]"),
        ("Backslash", "\\"),
        ("Backquote", "`"),
        ("NumpadMultiply", "*"),
        ("NumpadAdd", "+"),
        ("NumpadSubtract", "-"),
        ("NumpadDivide", "/"),
        ("NumpadDecimal", "."),
    ];

    fn lookup(table: &[(&str, &str)], name: &str) -> Option<String> {
        table.iter().find(|(from, _)| *from == name).map(|(_, to)| (*to).to_owned())
    }

    /// `name` is `prefix` followed by exactly one ASCII character matching `accept`; returns
    /// that character. (Byte lengths equal the C# UTF-16 lengths whenever this matches.)
    fn prefixed_char(name: &str, prefix: &str, accept: fn(&u8) -> bool) -> Option<char> {
        let rest = name.strip_prefix(prefix)?.as_bytes();
        (rest.len() == 1 && accept(&rest[0])).then(|| char::from(rest[0]))
    }

    /// Map an Avalonia `Key` enum name (`"W"`, `"Up"`, `"Space"`, `"Enter"`, `"D1"`,
    /// `"NumPad1"`, `"F5"`, …) to the normalized `KeyboardEvent.key` value (unshifted), or
    /// `None` when unmapped.
    pub fn from_avalonia(avalonia_key_name: &str) -> Option<String> {
        if avalonia_key_name.is_empty() {
            return None;
        }
        if let Some(letter) = prefixed_char(avalonia_key_name, "", u8::is_ascii_alphabetic) {
            return Some(letter.to_ascii_lowercase().to_string());
        }
        if let Some(digit) = prefixed_char(avalonia_key_name, "D", u8::is_ascii_digit) {
            return Some(digit.to_string());
        }
        if let Some(digit) = prefixed_char(avalonia_key_name, "NumPad", u8::is_ascii_digit) {
            return Some(digit.to_string());
        }
        if is_function_key(avalonia_key_name) {
            return Some(avalonia_key_name.to_lowercase());
        }
        lookup(AVALONIA, avalonia_key_name)
    }

    /// Map a DOM `KeyboardEvent.code` (`"KeyW"`, `"ArrowUp"`, `"Space"`, `"Digit1"`,
    /// `"Numpad1"`, `"F5"`, …) to the normalized `KeyboardEvent.key` value of a US layout, or
    /// `None` when unmapped.
    pub fn from_dom_code(code: &str) -> Option<String> {
        if code.is_empty() {
            return None;
        }
        if let Some(letter) = prefixed_char(code, "Key", u8::is_ascii_alphabetic) {
            return Some(letter.to_ascii_lowercase().to_string());
        }
        if let Some(digit) = prefixed_char(code, "Digit", u8::is_ascii_digit) {
            return Some(digit.to_string());
        }
        if let Some(digit) = prefixed_char(code, "Numpad", u8::is_ascii_digit) {
            return Some(digit.to_string());
        }
        if is_function_key(code) {
            return Some(code.to_lowercase());
        }
        lookup(DOM_CODES, code)
    }

    /// Normalize a `KeyboardEvent.key` value (TS `e.key.toLowerCase()`).
    pub fn from_dom_key(key: &str) -> String {
        key.to_lowercase()
    }

    /// C# `IsFunctionKey`: `F` + an `int.TryParse`-able 1..24 in a 2- or 3-character name.
    fn is_function_key(name: &str) -> bool {
        // Only ASCII names can parse, so the byte length is the C# (UTF-16) length.
        if !(name.len() == 2 || name.len() == 3) || !name.is_ascii() {
            return false;
        }
        name.strip_prefix('F').and_then(parse_dotnet_int).is_some_and(|n| (1..=24).contains(&n))
    }

    /// .NET `int.TryParse` with `NumberStyles.Integer` for the short strings used here: optional
    /// surrounding ASCII white space, an optional sign, then ASCII digits.
    fn parse_dotnet_int(text: &str) -> Option<i64> {
        let is_white = |c: char| matches!(c, '\t' | '\n' | '\u{b}' | '\u{c}' | '\r' | ' ');
        let trimmed = text.trim_matches(is_white);
        let (negative, digits) = match trimmed.as_bytes().first() {
            Some(b'+') => (false, &trimmed[1..]),
            Some(b'-') => (true, &trimmed[1..]),
            _ => (false, trimmed),
        };
        if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        let value: i64 = digits.parse().ok()?;
        Some(if negative { -value } else { value })
    }
}

/// What a play-mode frame's one-shot input asks the host to do (C# `PlayFrameInput`).
#[derive(Debug, Clone, PartialEq, Default, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayFrameInput {
    /// Engine commands to run, in order.
    pub commands: Vec<Command>,
    /// I — inventory panel.
    pub toggle_inventory: bool,
    /// J — quest journal.
    pub toggle_quests: bool,
    /// X — crafting panel.
    pub toggle_crafting: bool,
    /// Escape with no engine modal open (host menu / close its own modal).
    pub escape: bool,
}

/// The play-mode key → action bindings shared by the editor's play mode (App.tsx) and the
/// exported game shell (game-shell/main.ts), plus `RESERVED_ACTION_KEYS`
/// (src/lib/reserved-keys.ts) (C# `InputBindings`).
pub mod bindings {
    use super::{InputManager, MoveVector, PlayFrameInput};
    use farm_sim::{Command, GameContent, GameState};

    /// Gameplay keys that creator-defined action hotkeys may never claim. Shared between the
    /// play-mode input handler and the Actions editor, which warns when an author picks a
    /// colliding hotkey.
    pub const RESERVED_ACTION_KEYS: &[&str] = &[
        "w",
        "a",
        "s",
        "d",
        "e",
        "q",
        "t",
        "r",
        "f",
        "c",
        "z",
        "i",
        "j",
        "x",
        " ",
        "enter",
        "escape",
        "arrowup",
        "arrowdown",
        "arrowleft",
        "arrowright",
    ];

    /// Keys that interact (talk, harvest, open, …).
    pub const INTERACT_KEYS: &[&str] = &["e", " ", "enter"];

    /// Tool hotkeys → `useTool` tool type, in firing order.
    pub const TOOL_KEYS: &[(&str, &str)] =
        &[("q", "watering-can"), ("t", "hoe"), ("r", "axe"), ("f", "pickaxe"), ("c", "scythe")];

    pub const SLEEP_KEY: &str = "z";
    pub const INVENTORY_KEY: &str = "i";
    pub const QUESTS_KEY: &str = "j";
    pub const CRAFTING_KEY: &str = "x";
    pub const ESCAPE_KEY: &str = "escape";

    /// C# `Math.Max(-1, Math.Min(1, v))` (NaN passes through, as in .NET).
    fn clamp_axis(value: f64) -> f64 {
        if value.is_nan() {
            value
        } else {
            value.clamp(-1.0, 1.0)
        }
    }

    fn modal_open(state: &GameState) -> bool {
        state.dialogue.is_some() || state.shop.is_some() || state.minigame.is_some()
    }

    /// The movement intent for this frame: keyboard vector (plus any `extra` touch/D-pad
    /// vector), clamped per axis, and zero while dialogue, a shop or a minigame is open
    /// (App.tsx semantics).
    pub fn move_intent(input: &InputManager, state: &GameState, extra: Option<MoveVector>) -> MoveVector {
        if modal_open(state) {
            return MoveVector::new(0.0, 0.0);
        }
        let key = input.move_vector();
        let add = extra.unwrap_or_default();
        MoveVector::new(clamp_axis(key.dx + add.dx), clamp_axis(key.dy + add.dy))
    }

    /// Translate this frame's one-shot presses into commands and UI toggles (game-shell
    /// `update()` semantics). Does NOT call [`InputManager::end_frame`] — the host does, once
    /// per frame.
    ///
    /// While an engine modal (dialogue/shop/minigame) or a host modal (`host_modal_open`) is
    /// open, only Escape is handled: it closes the host modal first (reported via
    /// [`PlayFrameInput::escape`]), else cancels the minigame, else closes the shop, else the
    /// dialogue.
    pub fn poll_play_frame(
        input: &InputManager,
        state: &GameState,
        content: &GameContent,
        host_modal_open: bool,
    ) -> PlayFrameInput {
        let mut commands = Vec::new();
        if host_modal_open || modal_open(state) {
            let mut escape = false;
            if input.just_pressed(ESCAPE_KEY) {
                if host_modal_open {
                    escape = true;
                } else if state.minigame.is_some() {
                    commands.push(Command::CancelMinigame);
                } else if state.shop.is_some() {
                    commands.push(Command::CloseShop);
                } else if state.dialogue.is_some() {
                    commands.push(Command::CloseDialogue);
                }
            }
            return PlayFrameInput { commands, escape, ..PlayFrameInput::default() };
        }

        if INTERACT_KEYS.iter().any(|key| input.just_pressed(key)) {
            commands.push(Command::Interact);
        }
        for (key, tool) in TOOL_KEYS {
            if input.just_pressed(key) {
                commands.push(Command::UseTool { tool: (*tool).to_owned() });
            }
        }
        if input.just_pressed(SLEEP_KEY) {
            commands.push(Command::Sleep);
        }

        // Creator-defined action hotkeys (extensibility layer) — reserved gameplay keys never
        // fire actions.
        for action in &content.actions {
            let Some(key) = action.hotkey.as_deref().map(str::to_lowercase) else { continue };
            if !key.is_empty() && !RESERVED_ACTION_KEYS.contains(&key.as_str()) && input.just_pressed(&key) {
                commands.push(Command::PerformAction { action_id: action.id.clone() });
            }
        }

        PlayFrameInput {
            commands,
            toggle_inventory: input.just_pressed(INVENTORY_KEY),
            toggle_quests: input.just_pressed(QUESTS_KEY),
            toggle_crafting: input.just_pressed(CRAFTING_KEY),
            escape: input.just_pressed(ESCAPE_KEY),
        }
    }
}

// Re-exported so `farm_runtime::input::{move_intent, poll_play_frame}` read like the C#
// `InputBindings.*` calls.
pub use bindings::{move_intent, poll_play_frame};

#[cfg(test)]
mod tests {
    use super::key_names::{from_avalonia, from_dom_code, from_dom_key};

    #[test]
    fn function_keys_follow_dotnet_int_parse() {
        assert_eq!(from_avalonia("F1").as_deref(), Some("f1"));
        assert_eq!(from_avalonia("F24").as_deref(), Some("f24"));
        assert_eq!(from_avalonia("F25"), None);
        assert_eq!(from_avalonia("F0"), None);
        assert_eq!(from_dom_code("F12").as_deref(), Some("f12"));
        // int.TryParse accepts a sign and surrounding white space.
        assert_eq!(from_dom_code("F+5").as_deref(), Some("f+5"));
        assert_eq!(from_dom_code("F 5").as_deref(), Some("f 5"));
        assert_eq!(from_dom_code("F-5"), None);
    }

    #[test]
    fn letter_digit_and_numpad_prefixes_need_exactly_one_ascii_character() {
        assert_eq!(from_avalonia("D").as_deref(), Some("d"));
        assert_eq!(from_avalonia("D12"), None);
        assert_eq!(from_avalonia("NumPad"), None);
        assert_eq!(from_avalonia(""), None);
        assert_eq!(from_dom_code("KeyA").as_deref(), Some("a"));
        assert_eq!(from_dom_code("KeyAB"), None);
        assert_eq!(from_dom_code("Digit"), None);
        assert_eq!(from_dom_code("Numpad0").as_deref(), Some("0"));
        assert_eq!(from_dom_code("NumpadEnter").as_deref(), Some("enter"));
        assert_eq!(from_dom_code(""), None);
        assert_eq!(from_dom_key("ArrowUp"), "arrowup");
    }
}
