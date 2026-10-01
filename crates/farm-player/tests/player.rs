//! The graphical player driven headlessly with a fixed frame time: the game shell's flows, saves,
//! settings, the pause menu, embedded mode, plugins, minigames and gamepads.

mod common;

use common::{click, hold, idle, key_down, key_up, press, Stores, FRAME, SIZE};
use farm_cart::save_file;
use farm_player::{
    DebugAction, GamepadAxis, GamepadButton, InputEvent, Player, PlayerError, PlayerMode, PlayerRequest, ScreenKind,
};
use farm_sim::{hash_state, Command};
use farm_ui::game::{Panel, ToastKind};
use farm_ui::WidgetId;

fn new_game(stores: &Stores) -> Player {
    let mut player = stores.standalone(&common::starter());
    idle(&mut player, 1);
    press(&mut player, "enter");
    assert_eq!(player.screen(), ScreenKind::Playing);
    player
}

fn tick(player: &Player) -> u64 {
    player.state().unwrap().clock.tick
}

fn saved_state(stores: &Stores, player: &Player, slot: u32) -> farm_sim::GameState {
    let bytes = farm_player::SaveStore::read(&stores.saves, slot).unwrap();
    let target = save_file::SaveTarget::for_project(&common::starter(), player.session().unwrap().content());
    let loaded = save_file::load_save_bytes(&bytes, &target, player.session().unwrap().content());
    assert!(loaded.ok, "{:?}", loaded.errors);
    loaded.state.unwrap()
}

#[test]
fn new_game_walk_sleep_autosave_quit_and_continue() {
    let stores = Stores::new();
    let mut player = stores.standalone(&common::starter());
    assert_eq!(player.screen(), ScreenKind::Title);
    idle(&mut player, 1);
    // No saves yet: New Game has focus; Enter starts in the first free slot.
    press(&mut player, "enter");
    assert_eq!(player.screen(), ScreenKind::Playing);
    assert_eq!(player.current_slot(), Some(1));

    let start = player.state().unwrap().player.x;
    hold(&mut player, "d", 30);
    idle(&mut player, 3);
    assert!(player.state().unwrap().player.x > start, "walked right");
    assert_eq!(player.state().unwrap().player.move_intent.dx, 0);

    press(&mut player, "z");
    assert_eq!(player.state().unwrap().clock.day, 2);
    assert_eq!(stores.saves.filled(), [1], "autosaved on the new day");
    assert!(player
        .toast_history()
        .iter()
        .any(|(text, kind)| text == "Autosaved (slot 1)" && *kind == ToastKind::Success));
    let saved = saved_state(&stores, &player, 1);
    assert_eq!(saved.clock.day, 2);
    let preview = player.slot_previews()[0].clone().unwrap();
    assert_eq!(preview.farm_name, "My Farming Game");
    assert_eq!(preview.day, 2.0);
    assert!(preview.play_seconds >= 0.0 && preview.saved_at > 0);
    assert!(preview.thumbnail_png.starts_with(b"\x89PNG"), "a thumbnail of the scene");

    // Play on a little, then quit to title without saving.
    idle(&mut player, 30);
    press(&mut player, "escape");
    assert_eq!(player.screen(), ScreenKind::Pause);
    click(&mut player, WidgetId::new("pause").with("Quit to title"));
    assert_eq!(player.screen(), ScreenKind::Confirm);
    // Cancel has focus; Right, then Enter confirms.
    press(&mut player, "arrowright");
    press(&mut player, "enter");
    assert_eq!(player.screen(), ScreenKind::Title);
    assert!(player.state().is_none());

    // Continue (focused now that a save exists) restores the saved game exactly.
    idle(&mut player, 1);
    player.step(FRAME, &[key_down("enter")], SIZE.0, SIZE.1).unwrap();
    assert_eq!(player.screen(), ScreenKind::Playing);
    assert_eq!(hash_state(player.state().unwrap()), hash_state(&saved));
    player.step(FRAME, &[key_up("enter")], SIZE.0, SIZE.1).unwrap();
    assert_eq!(player.current_slot(), Some(1));
}

#[test]
fn closing_the_window_during_a_game_asks_first() {
    let stores = Stores::new();
    let mut title = stores.standalone(&common::starter());
    assert!(title.request_close(), "nothing to lose on the title screen");

    let mut player = new_game(&stores);
    assert!(!player.request_close());
    idle(&mut player, 2);
    assert_eq!(player.screen(), ScreenKind::Confirm);
    // Cancel (focused) goes back to the paused game.
    press(&mut player, "enter");
    assert_eq!(player.screen(), ScreenKind::Pause);
    // Asking again and confirming quits.
    assert!(!player.request_close());
    idle(&mut player, 2);
    let rect = player.widget_rect(WidgetId::new("confirm-yes")).unwrap();
    let (x, y) = (rect.x + rect.width / 2.0, rect.y + rect.height / 2.0);
    let mut requests = Vec::new();
    for events in [
        vec![InputEvent::PointerMove { x, y }, InputEvent::PointerDown { x, y, button: Default::default() }],
        vec![InputEvent::PointerUp { x, y, button: Default::default() }],
        vec![],
    ] {
        requests.extend(player.step(FRAME, &events, SIZE.0, SIZE.1).unwrap().requests);
    }
    assert!(requests.contains(&PlayerRequest::Quit), "confirming sends Quit: {requests:?}");
    // Closing twice while the question is up closes at once.
    let mut other = new_game(&stores);
    assert!(!other.request_close());
    assert!(other.request_close());
}

#[test]
fn a_damaged_save_falls_back_to_the_one_before() {
    let stores = Stores::new();
    let mut player = new_game(&stores);
    press(&mut player, "z");
    let saved = saved_state(&stores, &player, 1);
    assert_eq!(saved.clock.day, 2);
    // The next save goes bad (a power loss mid-write on a careless file system).
    let mut saves = stores.saves.clone();
    farm_player::SaveStore::write(&mut saves, 1, b"FGSV\0garbage").unwrap();

    // The slot still shows, and Continue loads the save before the damaged one, saying so.
    let mut player = stores.standalone(&common::starter());
    assert_eq!(player.slot_previews()[0].as_ref().map(|preview| preview.day), Some(2.0));
    idle(&mut player, 1);
    player.step(FRAME, &[key_down("enter")], SIZE.0, SIZE.1).unwrap();
    assert_eq!(player.screen(), ScreenKind::Playing);
    assert_eq!(hash_state(player.state().unwrap()), hash_state(&saved));
    assert!(player.toast_history().iter().any(|(text, kind)| text
        == "Slot 1 was damaged, so its previous save was loaded."
        && *kind == ToastKind::Error));

    // Without a backup the slot shows as unreadable, as before.
    let empty = Stores::new();
    let mut saves = empty.saves.clone();
    farm_player::SaveStore::write(&mut saves, 1, b"garbage").unwrap();
    let player = empty.standalone(&common::starter());
    assert!(player.slot_previews()[0].is_none());
}

#[test]
fn save_slots_hold_previews_and_load_them() {
    let stores = Stores::new();
    let mut player = new_game(&stores);
    player.run_command(&Command::Sleep).unwrap();
    press(&mut player, "escape");
    click(&mut player, WidgetId::new("pause").with("Save"));
    assert_eq!(player.screen(), ScreenKind::SaveSlots);
    click(&mut player, WidgetId::new("slot").with(2u32).with("primary"));
    assert_eq!(player.screen(), ScreenKind::Pause);
    assert_eq!(stores.saves.filled(), [1, 2]);
    assert_eq!(player.current_slot(), Some(2));
    let previews = player.slot_previews();
    assert!(previews[2].is_none());
    let (first, second) = (previews[0].clone().unwrap(), previews[1].clone().unwrap());
    assert!(second.saved_at > first.saved_at);

    // Saving over another game's slot asks first.
    click(&mut player, WidgetId::new("pause").with("Save"));
    click(&mut player, WidgetId::new("slot").with(1u32).with("primary"));
    assert_eq!(player.screen(), ScreenKind::Confirm);
    click(&mut player, WidgetId::new("confirm-no"));
    assert_eq!(player.screen(), ScreenKind::SaveSlots);
    press(&mut player, "escape");
    assert_eq!(player.screen(), ScreenKind::Pause);

    // Load the first slot from the pause menu.
    let first_state = saved_state(&stores, &player, 1);
    click(&mut player, WidgetId::new("pause").with("Load"));
    assert_eq!(player.screen(), ScreenKind::LoadSlots);
    click(&mut player, WidgetId::new("slot").with(1u32).with("primary"));
    assert_eq!(player.screen(), ScreenKind::Playing);
    assert_eq!(hash_state(player.state().unwrap()), hash_state(&first_state));
    assert_eq!(player.current_slot(), Some(1));

    // Delete a slot (confirmed).
    press(&mut player, "escape");
    click(&mut player, WidgetId::new("pause").with("Load"));
    click(&mut player, WidgetId::new("slot").with(2u32).with("delete"));
    click(&mut player, WidgetId::new("confirm-yes"));
    assert_eq!(stores.saves.filled(), [1]);
}

#[test]
fn settings_persist_between_runs() {
    let stores = Stores::new();
    let mut player = stores.standalone(&common::starter());
    idle(&mut player, 1);
    press(&mut player, "arrowdown");
    press(&mut player, "enter");
    assert_eq!(player.screen(), ScreenKind::Settings);
    // Audio tab: lower the master volume with the keyboard.
    press(&mut player, "tab");
    idle(&mut player, 1);
    press(&mut player, "arrowdown");
    press(&mut player, "arrowleft");
    press(&mut player, "arrowleft");
    let master = player.settings().audio.master;
    assert!((master - 0.6).abs() < 1e-4, "{master}");
    // Controls: rebind the watering can to K.
    click(&mut player, WidgetId::new("settings-tab").with("Controls"));
    click(&mut player, WidgetId::new("rebind").with("q"));
    press(&mut player, "k");
    assert_eq!(player.settings().controls.keys(farm_ui::BindAction::Water)[0], "k");
    // Accessibility: text size up.
    click(&mut player, WidgetId::new("settings-tab").with("Accessibility"));
    idle(&mut player, 1);
    press(&mut player, "arrowdown");
    press(&mut player, "arrowright");
    assert_eq!(player.settings().accessibility.text_size, 1.2);
    press(&mut player, "escape");
    assert_eq!(player.screen(), ScreenKind::Title);

    let text = stores.settings.text().unwrap();
    assert!(text.contains("[audio]") && text.contains("text-size = 1.2"), "{text}");
    let again = stores.standalone(&common::starter());
    assert_eq!(again.settings(), player.settings());

    // The new key waters in game.
    let mut game = new_game(&stores);
    press(&mut game, "k");
    let commands = game.session().unwrap().recent_commands();
    assert!(commands.iter().any(|command| command.contains("watering-can")), "{commands:?}");
}

#[test]
fn the_pause_menu_stops_time_and_fullscreen_toggles() {
    let stores = Stores::new();
    let mut player = new_game(&stores);
    idle(&mut player, 5);
    press(&mut player, "escape");
    assert_eq!(player.screen(), ScreenKind::Pause);
    let paused = tick(&player);
    idle(&mut player, 30);
    assert_eq!(tick(&player), paused, "time stands still");
    press(&mut player, "escape");
    assert_eq!(player.screen(), ScreenKind::Playing);
    idle(&mut player, 30);
    assert!(tick(&player) > paused);

    let output = player.step(FRAME, &[key_down("f11")], SIZE.0, SIZE.1).unwrap();
    assert_eq!(output.requests, [PlayerRequest::SetFullscreen(true)]);
    assert!(player.settings().display.fullscreen);
    assert!(stores.settings.text().unwrap().contains("fullscreen = true"));
    // Quit from the pause menu asks, then asks the host to close.
    player.step(FRAME, &[key_up("f11")], SIZE.0, SIZE.1).unwrap();
    press(&mut player, "escape");
    click(&mut player, WidgetId::new("pause").with("Quit game"));
    let rect = player.widget_rect(WidgetId::new("confirm-yes")).unwrap();
    let (x, y) = (rect.x + 4.0, rect.y + 4.0);
    player
        .step(
            FRAME,
            &[InputEvent::PointerMove { x, y }, InputEvent::PointerDown { x, y, button: Default::default() }],
            SIZE.0,
            SIZE.1,
        )
        .unwrap();
    let output =
        player.step(FRAME, &[InputEvent::PointerUp { x, y, button: Default::default() }], SIZE.0, SIZE.1).unwrap();
    assert!(output.requests.contains(&PlayerRequest::Quit));
}

#[test]
fn panels_open_from_the_toolbar_and_close_with_their_key() {
    let stores = Stores::new();
    let mut player = new_game(&stores);
    click(&mut player, WidgetId::new("hud").with("inventory"));
    assert_eq!(player.panel(), Some(Panel::Inventory));
    press(&mut player, "i");
    assert_eq!(player.panel(), None);
    press(&mut player, "j");
    assert_eq!(player.panel(), Some(Panel::Quests));
    // Escape closes the panel before it opens the menu.
    press(&mut player, "escape");
    assert_eq!(player.panel(), None);
    assert_eq!(player.screen(), ScreenKind::Playing);
    // The toolbar's Sleep runs the engine command.
    click(&mut player, WidgetId::new("hud").with("sleep"));
    assert_eq!(player.state().unwrap().clock.day, 2);
}

#[test]
fn embedded_mode_starts_in_game_and_serves_the_editor() {
    let stores = Stores::new();
    let mut player = Player::from_project(common::starter(), stores.options(PlayerMode::Embedded)).unwrap();
    assert_eq!(player.screen(), ScreenKind::Playing);
    let output = player.step(FRAME, &[], SIZE.0, SIZE.1).unwrap();
    assert_eq!(output.requests, [PlayerRequest::SetTitle("My Farming Game".into())]);
    player.debug(&DebugAction::AddMoney { amount: 250.0 }).unwrap();
    let money = player.state().unwrap().player.money;
    assert_eq!(player.synced_project().unwrap().player.money, money, "keep changes writes the state back");
    let mut state = player.state().unwrap().clone();
    state.clock.day = 9;
    player.replace_state(state).unwrap();
    assert_eq!(player.state().unwrap().clock.day, 9);
    // Sleeping never autosaves in the editor.
    player.run_command(&Command::Sleep).unwrap();
    assert!(stores.saves.filled().is_empty());
    // The pause menu has no save slots or quitting.
    press(&mut player, "escape");
    assert_eq!(player.screen(), ScreenKind::Pause);
    assert!(player.widget_rect(WidgetId::new("pause").with("Resume")).is_some());
    assert!(player.widget_rect(WidgetId::new("pause").with("Save")).is_none());
    assert!(player.widget_rect(WidgetId::new("pause").with("Quit to title")).is_none());
    assert!(player.plugin_errors().is_empty());

    // From a cartridge too.
    let cart = Player::from_cartridge_bytes(&common::cartridge(), stores.options(PlayerMode::Embedded)).unwrap();
    assert_eq!(cart.screen(), ScreenKind::Playing);
    assert!(cart.synced_project().is_none(), "a cartridge has no project to write back to");
    // A title screen has no game for the debug drawer.
    let mut title = stores.standalone(&common::starter());
    assert_eq!(title.debug(&DebugAction::FullEnergy), Err(PlayerError::NoGame));
    assert!(Player::from_cartridge_bytes(b"not a cartridge", stores.options(PlayerMode::Standalone)).is_err());
}

#[test]
fn pack_plugins_run_in_the_player() {
    let fixture: serde_json::Value = serde_json::from_str(
        &std::fs::read_to_string(common::root().join("fixtures/golden/replays/content-packs-and-plugins.json"))
            .unwrap(),
    )
    .unwrap();
    let project: farm_sim::GameProject = serde_json::from_value(fixture["project"].clone()).unwrap();
    let stores = Stores::new();
    let mut player = Player::from_project(project, stores.options(PlayerMode::Embedded)).unwrap();
    assert!(player.session().unwrap().has_plugins());
    player.run_command(&Command::Sleep).unwrap();
    idle(&mut player, 2);
    assert!(player.plugin_errors().is_empty(), "{:?}", player.plugin_errors());
    assert!(
        player.toast_history().iter().any(|(text, _)| text.contains("glowshrooms hum")),
        "{:?}",
        player.toast_history()
    );
}

#[test]
fn a_minigame_plays_through_the_overlay() {
    let stores = Stores::new();
    let mut player = new_game(&stores);
    player.run_command(&Command::StartMinigame { minigame_id: "fishing".into() }).unwrap();
    idle(&mut player, 2);
    assert!(player.session().unwrap().minigame_view().is_some());
    assert!(player.widget_rect(WidgetId::new("minigame-primary")).is_some());
    // Space presses and releases the timing bar's button: the score enters the command log once.
    press(&mut player, " ");
    assert!(player.state().unwrap().minigame.is_none());
    let resolved = player.session().unwrap().recent_commands().iter().filter(|c| c.contains("resolveMinigame")).count();
    assert_eq!(resolved, 1);

    // Give up from the overlay.
    player.run_command(&Command::StartMinigame { minigame_id: "fishing".into() }).unwrap();
    idle(&mut player, 2);
    click(&mut player, WidgetId::new("minigame-give-up"));
    assert!(player.state().unwrap().minigame.is_none());
}

#[test]
fn a_gamepad_plays_the_whole_game() {
    let stores = Stores::new();
    let mut player = stores.standalone(&common::starter());
    idle(&mut player, 1);
    let button = |button, pressed| InputEvent::GamepadButton { button, pressed };
    player.step(FRAME, &[button(GamepadButton::South, true)], SIZE.0, SIZE.1).unwrap();
    player.step(FRAME, &[button(GamepadButton::South, false)], SIZE.0, SIZE.1).unwrap();
    assert_eq!(player.screen(), ScreenKind::Playing);
    let start = player.state().unwrap().player.x;
    player.step(FRAME, &[InputEvent::GamepadAxis { axis: GamepadAxis::LeftX, value: 1.0 }], SIZE.0, SIZE.1).unwrap();
    idle(&mut player, 20);
    player.step(FRAME, &[InputEvent::GamepadAxis { axis: GamepadAxis::LeftX, value: 0.0 }], SIZE.0, SIZE.1).unwrap();
    assert!(player.state().unwrap().player.x > start);
    // Select opens the inventory, B closes it; Start pauses, B resumes.
    player.step(FRAME, &[button(GamepadButton::Select, true)], SIZE.0, SIZE.1).unwrap();
    player.step(FRAME, &[button(GamepadButton::Select, false)], SIZE.0, SIZE.1).unwrap();
    assert_eq!(player.panel(), Some(Panel::Inventory));
    player.step(FRAME, &[button(GamepadButton::East, true)], SIZE.0, SIZE.1).unwrap();
    player.step(FRAME, &[button(GamepadButton::East, false)], SIZE.0, SIZE.1).unwrap();
    assert_eq!(player.panel(), None);
    player.step(FRAME, &[button(GamepadButton::Start, true)], SIZE.0, SIZE.1).unwrap();
    player.step(FRAME, &[button(GamepadButton::Start, false)], SIZE.0, SIZE.1).unwrap();
    assert_eq!(player.screen(), ScreenKind::Pause);
    idle(&mut player, 1);
    player.step(FRAME, &[button(GamepadButton::East, true)], SIZE.0, SIZE.1).unwrap();
    player.step(FRAME, &[button(GamepadButton::East, false)], SIZE.0, SIZE.1).unwrap();
    assert_eq!(player.screen(), ScreenKind::Playing);
}

/// Presses and releases a gamepad button, then lets two frames pass.
fn pad(player: &mut Player, button: GamepadButton) {
    player.step(FRAME, &[InputEvent::GamepadButton { button, pressed: true }], SIZE.0, SIZE.1).unwrap();
    player.step(FRAME, &[InputEvent::GamepadButton { button, pressed: false }], SIZE.0, SIZE.1).unwrap();
    idle(player, 2);
}

/// Moves focus with a D-pad direction until `target` has it.
fn pad_to(player: &mut Player, direction: GamepadButton, target: WidgetId) {
    for _ in 0..16 {
        if player.focused_widget() == Some(target) {
            return;
        }
        pad(player, direction);
    }
    panic!("the D-pad never reached {target:?}");
}

#[test]
fn a_gamepad_enters_and_leaves_the_rebind_prompt() {
    let stores = Stores::new();
    let mut player = stores.standalone(&common::starter());
    idle(&mut player, 1);
    pad_to(&mut player, GamepadButton::DpadDown, WidgetId::new("title").with("Settings"));
    pad(&mut player, GamepadButton::South);
    assert_eq!(player.screen(), ScreenKind::Settings);
    // RB twice: Display → Audio → Controls; then down to Reset controls and right into the
    // column of key buttons.
    pad(&mut player, GamepadButton::RightShoulder);
    pad(&mut player, GamepadButton::RightShoulder);
    pad_to(&mut player, GamepadButton::DpadDown, WidgetId::new("rebind-reset"));
    pad(&mut player, GamepadButton::DpadRight);
    let focused = player.focused_widget();
    assert!(
        farm_ui::BindAction::ALL.iter().any(|bind| focused == Some(WidgetId::new("rebind").with(bind.canonical_key()))),
        "{focused:?}"
    );
    let before = player.settings().controls.clone();
    let prompt = "Press a key\u{2026} (Esc or B cancels)";

    // A starts the capture; the D-pad and A are swallowed while it waits; B cancels it.
    pad(&mut player, GamepadButton::South);
    assert!(shows(&player, prompt), "{:?}", ui_texts(&player));
    pad(&mut player, GamepadButton::DpadDown);
    pad(&mut player, GamepadButton::South);
    assert!(shows(&player, prompt));
    pad(&mut player, GamepadButton::East);
    assert!(!shows(&player, prompt));
    assert_eq!(player.screen(), ScreenKind::Settings, "B only cancelled the capture");
    // Start cancels too, and nothing was rebound.
    pad(&mut player, GamepadButton::South);
    assert!(shows(&player, prompt));
    pad(&mut player, GamepadButton::Start);
    assert!(!shows(&player, prompt));
    assert_eq!(player.settings().controls, before);
    // B again leaves the settings.
    pad(&mut player, GamepadButton::East);
    assert_eq!(player.screen(), ScreenKind::Title);
}

#[test]
fn a_player_runs_on_another_thread() {
    let stores = Stores::new();
    let player = new_game(&stores);
    let handle = std::thread::spawn(move || {
        let mut player = player;
        let output = player.frame(FRAME, &[], 320, 200).unwrap();
        (output.pixels.width(), player)
    });
    let (width, player) = handle.join().unwrap();
    assert_eq!(width, 320);
    assert!(player.crash_report().contains("Recent commands"));
}

/// The text of every text command of the last UI frame, with its font.
fn ui_texts(player: &Player) -> Vec<(String, farm_render::FontId)> {
    player
        .ui_draw_list()
        .commands
        .iter()
        .filter_map(|command| match command {
            farm_render::DrawCmd::Text { text, font, .. } => Some((text.clone(), *font)),
            _ => None,
        })
        .collect()
}

fn shows(player: &Player, text: &str) -> bool {
    ui_texts(player).iter().any(|(shown, _)| shown == text)
}

#[test]
fn the_language_follows_the_setting_then_the_system_then_the_game() {
    // No system locale and an English game: English.
    let stores = Stores::new();
    let mut player = stores.standalone(&common::starter());
    idle(&mut player, 2);
    assert_eq!(player.lang(), farm_ui::Lang::En);
    assert!(shows(&player, "New Game"), "{:?}", ui_texts(&player));

    // The system's language while the setting is automatic.
    let mut options = stores.options(PlayerMode::Standalone);
    options.system_locale = Some("es_ES.UTF-8".into());
    let mut spanish = Player::from_project(common::starter(), options).unwrap();
    idle(&mut spanish, 2);
    assert_eq!(spanish.lang(), farm_ui::Lang::Es);
    assert!(shows(&spanish, "Nueva partida") && shows(&spanish, "Ajustes"), "{:?}", ui_texts(&spanish));
    // Widget ids stay English: the same click opens the settings.
    click(&mut spanish, WidgetId::new("title").with("Settings"));
    assert_eq!(spanish.screen(), ScreenKind::Settings);
    click(&mut spanish, WidgetId::new("settings-tab").with("Accessibility"));
    assert!(shows(&spanish, "Idioma") && shows(&spanish, "Autom\u{e1}tico"), "{:?}", ui_texts(&spanish));

    // The player's choice wins, and is stored.
    let mut settings = spanish.settings().clone();
    settings.language = "en".into();
    spanish.set_settings(settings);
    idle(&mut spanish, 2);
    assert!(shows(&spanish, "Language") && shows(&spanish, "English"), "{:?}", ui_texts(&spanish));
    assert!(stores.settings.text().unwrap().contains("language = \"en\""));

    // A game whose locale has a table picks it when the system gives none.
    let mut project = common::starter();
    project.settings.locale = "es".into();
    let mut game = Player::from_project(project, Stores::new().options(PlayerMode::Standalone)).unwrap();
    idle(&mut game, 2);
    assert_eq!(game.lang(), farm_ui::Lang::Es);
}

#[test]
fn spanish_confirmations_and_toasts() {
    let stores = Stores::new();
    let mut player = new_game(&stores);
    let mut settings = player.settings().clone();
    settings.language = "es".into();
    player.set_settings(settings);
    press(&mut player, "z");
    idle(&mut player, 30);
    let toasts: Vec<&String> = player.toast_history().iter().map(|(text, _)| text).collect();
    assert!(toasts.iter().any(|text| text.starts_with("Guardado autom\u{e1}tico (ranura 1)")), "{toasts:?}");
    press(&mut player, "escape");
    assert!(shows(&player, "Pausa") && shows(&player, "Reanudar"), "{:?}", ui_texts(&player));
    click(&mut player, WidgetId::new("pause").with("Quit to title"));
    assert_eq!(player.screen(), ScreenKind::Confirm);
    assert!(shows(&player, "\u{bf}Volver al t\u{ed}tulo?") && shows(&player, "Cancelar"), "{:?}", ui_texts(&player));
}

#[test]
fn the_readable_font_setting_switches_the_interface_font() {
    use farm_render::FontId;
    let stores = Stores::new();
    let mut player = stores.standalone(&common::starter());
    idle(&mut player, 1);
    click(&mut player, WidgetId::new("title").with("Settings"));
    click(&mut player, WidgetId::new("settings-tab").with("Accessibility"));
    assert!(ui_texts(&player).iter().all(|(_, font)| matches!(font, FontId::Regular | FontId::Bold)));
    click(&mut player, WidgetId::new("setting").with("readable-font"));
    assert!(player.settings().accessibility.readable_font);
    idle(&mut player, 1);
    let fonts: Vec<FontId> = ui_texts(&player).into_iter().map(|(_, font)| font).collect();
    assert!(fonts.contains(&FontId::ReadableRegular) && fonts.contains(&FontId::ReadableBold), "{fonts:?}");
    assert!(stores.settings.text().unwrap().contains("readable-font = true"));
    // It persists, and the rendered frame changes.
    let mut again = stores.standalone(&common::starter());
    idle(&mut again, 1);
    let readable = again.frame(FRAME, &[], 640, 400).unwrap().pixels.clone();
    let mut settings = again.settings().clone();
    settings.accessibility.readable_font = false;
    again.set_settings(settings);
    let inter = again.frame(FRAME, &[], 640, 400).unwrap().pixels.clone();
    assert_ne!(readable.data(), inter.data());
}
