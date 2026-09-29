//! Port of `Runtime/CreatorRuntimeTests.cs` (itself a port of creator-runtime.test.ts: jsdom
//! widgets → host-agnostic session / panel models). The plugin half of the TS file lives with
//! the plugin sandbox (`farm-plugins`).

#[path = "../../farm-sim/tests/common/mod.rs"]
mod common;

use farm_runtime::minigames::custom_game::SimpleBattleSession;
use farm_runtime::minigames::{self, MinigameConfig, MinigameMountOptions};
use farm_runtime::panels::{self, PanelState};
use farm_sim::schema::{GamePanel, GamePanelEntry, InventorySlot, ShopSession};
use farm_sim::{content_builtin, state, text, units, GameState};
use indexmap::IndexMap;
use serde_json::Value;
use std::sync::{Arc, Mutex};

fn recorder() -> (Arc<Mutex<Vec<f64>>>, impl FnMut(f64) + Send + 'static) {
    let complete = Arc::new(Mutex::new(Vec::new()));
    let sink = Arc::clone(&complete);
    (complete, move |score| sink.lock().unwrap().push(score))
}

fn config(key: &str, value: f64) -> MinigameConfig {
    [(key.to_owned(), units::value(value))].into_iter().collect()
}

#[test]
fn registers_fishing_and_combat_in_the_same_registry_used_by_both_hosts() {
    let registry = minigames::create_default_minigame_registry();
    assert!(registry.get("hold-to-catch").is_some());
    assert!(registry.get("simple-battle").is_some());
}

#[test]
fn battle_resolves_once_and_cleans_up_its_ui() {
    let (complete, on_complete) = recorder();
    let mut session = minigames::create_default_minigame_registry()
        .get("simple-battle")
        .unwrap()
        .mount(MinigameMountOptions::new(config("enemyHealth", 10.0), on_complete, || {}));
    let battle = session.as_any_mut().downcast_mut::<SimpleBattleSession>().expect("a SimpleBattleSession");
    battle.act("magic");
    battle.act("magic");
    assert_eq!(*complete.lock().unwrap(), [1.0]);
    session.dispose();
    assert!(session.is_done());
}

#[test]
fn fishing_keyboard_hold_reports_score_and_cannot_resolve_twice() {
    let (complete, on_complete) = recorder();
    let mut session = minigames::create_default_minigame_registry()
        .get("hold-to-catch")
        .unwrap()
        .mount(MinigameMountOptions::new(config("holdMs", 1200.0), on_complete, || {}));
    assert_eq!(session.prompt(), "Hold for 1.2 seconds, then release to reel in.");
    session.press();
    assert_eq!(session.button_text(), "Reeling… release!");
    session.update(1.2);
    session.release();
    session.release();
    assert_eq!(*complete.lock().unwrap(), [1.0]);
    session.dispose();
    assert!(session.is_done());
}

fn entry(kind: &str, label: &str, value: &str) -> GamePanelEntry {
    GamePanelEntry { kind: kind.to_owned(), label: label.to_owned(), value: value.to_owned() }
}

#[test]
fn game_panels_update_counters_respect_flags_and_block_actions_during_modal_interactions() {
    let ran = Arc::new(Mutex::new(Vec::<String>::new()));
    let flags: IndexMap<String, Value> = [("known".to_owned(), Value::Bool(false))].into_iter().collect();
    let state = Arc::new(Mutex::new(PanelState {
        money: 3.0,
        energy: 100.0,
        day: 1.0,
        flags,
        inventory: Vec::new(),
        blocked: false,
    }));
    let panels = vec![GamePanel {
        id: "p".to_owned(),
        title: "Journal".to_owned(),
        visible_flag: Some("known".to_owned()),
        entries: vec![entry("money", "Gold", ""), entry("action", "Cast", "rain")],
    }];
    let (read, sink) = (Arc::clone(&state), Arc::clone(&ran));
    let mut handle =
        panels::mount(panels, move || read.lock().unwrap().clone(), move |id| sink.lock().unwrap().push(id.to_owned()));
    handle.update();
    assert!(handle.views()[0].hidden);
    assert_eq!(handle.views().len(), 1);

    {
        let mut state = state.lock().unwrap();
        state.flags.insert("known".to_owned(), Value::Bool(true));
        state.money = 9.0;
    }
    handle.update();
    assert!(!handle.views()[0].hidden);
    assert!(handle.views()[0].entries.iter().any(|e| e.text == "Gold: 9"));
    assert!(handle.click("rain"));
    assert_eq!(*ran.lock().unwrap(), ["rain"]);

    state.lock().unwrap().blocked = true;
    handle.update();
    let actions: Vec<_> = handle.views()[0].entries.iter().filter(|e| e.kind == "action").collect();
    assert_eq!(actions.len(), 1);
    assert!(!actions[0].enabled);
    assert!(!handle.click("rain"));
    assert_eq!(ran.lock().unwrap().len(), 1);

    handle.dispose();
    assert!(handle.views().is_empty());
}

#[test]
fn panel_entries_render_flags_items_and_text() {
    let items = content_builtin::create_default_items();
    let wheat = items.iter().find(|i| i.id == "seed-wheat").unwrap().clone();
    let state = PanelState {
        money: 0.0,
        energy: 42.5,
        day: 3.0,
        flags: [("met".to_owned(), Value::from("yes"))].into_iter().collect(),
        inventory: vec![InventorySlot { item: wheat.clone(), quantity: 4 }, InventorySlot { item: wheat, quantity: 2 }],
        blocked: false,
    };
    let panel = GamePanel {
        id: "p".to_owned(),
        title: "T".to_owned(),
        visible_flag: None,
        entries: vec![
            entry("flag", "Met", "met"),
            entry("flag", "Other", "nope"),
            entry("item", "Seeds", "seed-wheat"),
            entry("energy", "", ""),
            entry("day", "Day", ""),
            entry("text", "Tip", "Water daily"),
        ],
    };
    let views = panels::render([&panel], &state);
    assert_eq!(views.len(), 1);
    let texts: Vec<&str> = views[0].entries.iter().map(|e| e.text.as_str()).collect();
    assert_eq!(texts, ["Met: Yes", "Other: No", "Seeds: 6", "42.5", "Day: 3", "Tip: Water daily"]);
}

#[test]
fn panel_state_projects_the_running_game_and_the_synced_project() {
    let mut project = common::make_project();
    project.event_flags = [("met".to_owned(), true)].into_iter().collect();
    let game = state::create_game_state(&project, Some("panels"));
    let shop_open = GameState { shop: Some(ShopSession { shop_id: "s".to_owned() }), ..game.clone() };
    let live = PanelState::from_game_state(&shop_open, false);
    assert_eq!((live.money, live.day, live.blocked), (100.0, 1.0, true));
    assert!(!PanelState::from_game_state(&game, false).blocked);
    assert!(PanelState::from_game_state(&game, true).blocked);

    let synced = PanelState::from_project(&project, false);
    assert_eq!((synced.money, synced.energy, synced.day), (100.0, 0.0, 1.0));
    assert!(text::truthy(synced.flags.get("met")));
}
