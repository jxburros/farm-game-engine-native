//! Port of `Runtime/InputTests.cs` (itself a port of input.test.ts: DOM events → the
//! host-agnostic `key_down`/`key_up` feed).

#[path = "../../farm-sim/tests/common/mod.rs"]
mod common;

use farm_runtime::input::{bindings, key_names, InputManager, Modifiers, MoveVector};
use farm_sim::schema::{ActionDef, ShopSession};
use farm_sim::{state, Command, GameContent, GameProject, GameState};

#[test]
fn tracks_held_and_just_pressed_keys_from_window_events() {
    let mut input = InputManager::new();
    input.key_down("w", Modifiers::NONE);
    assert!(input.pressed("w"));
    assert!(input.just_pressed("w"));
    assert_eq!(input.direction(), Some("up"));

    input.end_frame();
    assert!(!input.just_pressed("w"));
    assert!(input.pressed("w")); // still held

    input.key_up("w");
    assert!(!input.pressed("w"));
}

#[test]
fn prevents_default_for_gameplay_keys_on_non_editable_targets() {
    let mut input = InputManager::new();
    assert!(input.key_down("e", Modifiers::NONE));
}

#[test]
fn ignores_keystrokes_aimed_at_text_inputs_editor_typing_regression() {
    let mut input = InputManager::new();
    // Every gameplay binding must pass through untouched so users can type words like
    // "sara ward" into editor fields.
    for key in ["w", "a", "s", "d", "e", "q", "t", "x", "z", "i", "j", " ", "Enter"] {
        assert!(!input.key_down(key, Modifiers::EDITABLE), "{key:?}");
        assert!(!input.pressed(&key.to_lowercase()), "{key:?}");
    }
}

#[test]
fn ignores_keystrokes_aimed_at_textareas_and_contenteditable_elements() {
    let mut input = InputManager::new();
    assert!(!input.key_down("w", Modifiers::EDITABLE));
    assert!(!input.pressed("w"));
    assert!(!input.key_down("a", Modifiers::EDITABLE));
    assert!(!input.pressed("a"));
}

#[test]
fn leaves_modified_shortcuts_ctrl_cmd_alt_for_the_browser_and_app() {
    let mut input = InputManager::new();
    assert!(!input.key_down("z", Modifiers::CTRL));
    assert!(!input.key_down("s", Modifiers::META));
    assert!(!input.key_down("d", Modifiers::ALT));
}

// --- C# additions: key-name translation, movement vector, play bindings ---

/// One `#[test]` per C# `[InlineData]` row: `name: input => expected`.
macro_rules! key_name_cases {
    ($map:path; $($name:ident: $input:expr => $expected:expr),* $(,)?) => {
        $(
            #[test]
            fn $name() {
                assert_eq!($map($input).as_deref(), Some($expected));
            }
        )*
    };
}

/// C# theory `MapsAvaloniaKeyNames`.
mod maps_avalonia_key_names {
    use farm_runtime::input::key_names::from_avalonia;

    key_name_cases! { from_avalonia;
        w: "W" => "w",
        up: "Up" => "arrowup",
        down: "Down" => "arrowdown",
        left: "Left" => "arrowleft",
        right: "Right" => "arrowright",
        space: "Space" => " ",
        enter: "Enter" => "enter",
        return_key: "Return" => "enter",
        escape: "Escape" => "escape",
        e: "E" => "e",
        d1: "D1" => "1",
        num_pad7: "NumPad7" => "7",
        f5: "F5" => "f5",
        oem_comma: "OemComma" => ",",
    }
}

/// C# theory `MapsDomCodes`.
mod maps_dom_codes {
    use farm_runtime::input::key_names::from_dom_code;

    key_name_cases! { from_dom_code;
        key_w: "KeyW" => "w",
        arrow_up: "ArrowUp" => "arrowup",
        space: "Space" => " ",
        enter: "Enter" => "enter",
        digit3: "Digit3" => "3",
        escape: "Escape" => "escape",
    }
}

#[test]
fn unknown_host_keys_are_ignored() {
    let mut input = InputManager::new();
    assert_eq!(key_names::from_avalonia("NoName"), None);
    assert!(!input.key_down_avalonia("NoName", Modifiers::NONE));
    assert!(input.keys_down().is_empty());
    assert!(input.key_down_avalonia("Up", Modifiers::NONE));
    assert!(input.pressed("arrowup"));
    input.key_up_avalonia("Up");
    assert!(!input.keys_down().contains("arrowup"));
}

#[test]
fn move_vector_combines_axes_and_cancels_opposing_keys() {
    let mut input = InputManager::new();
    input.key_down("d", Modifiers::NONE);
    input.key_down("ArrowUp", Modifiers::NONE);
    assert_eq!(input.move_vector(), MoveVector::new(1.0, -1.0));
    input.key_down("a", Modifiers::NONE);
    assert_eq!(input.move_vector(), MoveVector::new(0.0, -1.0));
    input.clear();
    assert_eq!(input.move_vector(), MoveVector::new(0.0, 0.0));
    assert_eq!(input.direction(), None);
}

fn play(mutate: impl FnOnce(&mut GameProject)) -> (GameState, GameContent) {
    let mut project = common::make_project();
    mutate(&mut project);
    (state::create_game_state(&project, Some("input")), state::create_content_from_project(&project))
}

#[test]
fn poll_play_frame_maps_gameplay_keys_to_commands() {
    let (state, content) = play(|p| {
        p.actions = vec![
            ActionDef {
                id: "action-ok".to_owned(),
                name: "Ok".to_owned(),
                hotkey: Some("K".to_owned()),
                ..ActionDef::default()
            },
            ActionDef {
                id: "action-reserved".to_owned(),
                name: "Reserved".to_owned(),
                hotkey: Some("e".to_owned()),
                ..ActionDef::default()
            },
        ];
    });
    let mut input = InputManager::new();
    for key in [" ", "q", "t", "r", "f", "c", "z", "i", "j", "x", "k"] {
        input.key_down(key, Modifiers::NONE);
    }

    let frame = bindings::poll_play_frame(&input, &state, &content, false);
    assert_eq!(
        frame.commands.iter().map(Command::type_name).collect::<Vec<_>>(),
        ["interact", "useTool", "useTool", "useTool", "useTool", "useTool", "sleep", "performAction"]
    );
    let tools: Vec<&str> = frame
        .commands
        .iter()
        .filter_map(|c| match c {
            Command::UseTool { tool } => Some(tool.as_str()),
            _ => None,
        })
        .collect();
    assert_eq!(tools, ["watering-can", "hoe", "axe", "pickaxe", "scythe"]);
    let actions: Vec<&str> = frame
        .commands
        .iter()
        .filter_map(|c| match c {
            Command::PerformAction { action_id } => Some(action_id.as_str()),
            _ => None,
        })
        .collect();
    assert_eq!(actions, ["action-ok"]);
    assert!(frame.toggle_inventory && frame.toggle_quests && frame.toggle_crafting);
    assert!(!frame.escape);
}

#[test]
fn poll_play_frame_only_handles_escape_while_a_modal_is_open() {
    let (state, content) = play(|_| {});
    let shop_open = GameState { shop: Some(ShopSession { shop_id: "shop".to_owned() }), ..state };
    let mut input = InputManager::new();
    input.key_down("e", Modifiers::NONE);
    input.key_down("Escape", Modifiers::NONE);
    let frame = bindings::poll_play_frame(&input, &shop_open, &content, false);
    assert_eq!(frame.commands, [Command::CloseShop]);
    assert_eq!(bindings::move_intent(&input, &shop_open, None), MoveVector::new(0.0, 0.0));

    let host_modal = bindings::poll_play_frame(&input, &shop_open, &content, true);
    assert!(host_modal.commands.is_empty());
    assert!(host_modal.escape);
}

#[test]
fn move_intent_adds_touch_vector_and_clamps() {
    let (state, _) = play(|_| {});
    let mut input = InputManager::new();
    input.key_down("d", Modifiers::NONE);
    assert_eq!(bindings::move_intent(&input, &state, Some(MoveVector::new(1.0, 1.0))), MoveVector::new(1.0, 1.0));
}
