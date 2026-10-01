//! The web demo's surfaces (`HostView`): a phone in portrait with touch controls at density 3,
//! and a 1366×768 laptop. The interface keeps its size in CSS pixels, nothing it draws at the
//! bottom hides under the touch controls, and touch prompts name no keys.

mod common;

use common::{press, Stores, FRAME};
use farm_player::{HostView, Player};
use farm_sim::schema::DialogueState;
use farm_ui::WidgetId;

/// A 390×844 CSS-pixel phone at devicePixelRatio 3, with 150 CSS pixels of touch controls.
const PHONE: (u32, u32) = (1170, 2532);
const PHONE_VIEW: HostView = HostView { density: 3.0, touch_controls: true, inset_bottom: 450.0 };

fn playing(view: HostView, size: (u32, u32)) -> Player {
    let stores = Stores::new();
    let mut player = stores.standalone(&common::starter());
    common::idle(&mut player, 1);
    press(&mut player, "enter");
    player.set_host_view(view);
    for _ in 0..3 {
        player.step(FRAME, &[], size.0, size.1).unwrap();
    }
    player
}

fn rect(player: &Player, id: WidgetId) -> farm_render::Rect {
    player.widget_rect(id).unwrap_or_else(|| panic!("widget {id:?} was not drawn"))
}

#[test]
fn a_phone_lays_out_in_css_pixels_above_the_touch_controls() {
    let mut player = playing(PHONE_VIEW, PHONE);
    let menu = rect(&player, WidgetId::new("hud").with("menu"));
    // Toolbar buttons are a finger's size in CSS pixels, not 1280-wide-desktop small.
    assert!(menu.height / 3.0 >= 20.0, "menu button {menu:?}");
    assert!(menu.x + menu.width <= PHONE.0 as f32, "the toolbar fits the screen: {menu:?}");

    // A dialogue sits above the controls.
    let mut state = player.state().unwrap().clone();
    state.dialogue =
        Some(DialogueState { npc_id: "npc-merchant".into(), dialogue_id: "dialogue-merchant-greeting".into() });
    player.replace_state(state).unwrap();
    for _ in 0..3 {
        player.step(FRAME, &[], PHONE.0, PHONE.1).unwrap();
    }
    let option = player
        .widget_rect(WidgetId::new("dialogue-option").with("dialogue-merchant-greeting").with(0usize))
        .or_else(|| player.widget_rect(WidgetId::new("dialogue-goodbye")))
        .expect("a dialogue option was drawn");
    assert!(
        option.y + option.height <= PHONE.1 as f32 - PHONE_VIEW.inset_bottom,
        "dialogue under the controls: {option:?}"
    );

    // Without the inset the same box sits lower, over where the controls would be.
    player.set_host_view(HostView { inset_bottom: 0.0, ..PHONE_VIEW });
    for _ in 0..2 {
        player.step(FRAME, &[], PHONE.0, PHONE.1).unwrap();
    }
    let lower = player
        .widget_rect(WidgetId::new("dialogue-option").with("dialogue-merchant-greeting").with(0usize))
        .or_else(|| player.widget_rect(WidgetId::new("dialogue-goodbye")))
        .unwrap();
    assert!(lower.y > option.y, "{lower:?} vs {option:?}");
}

#[test]
fn touch_controls_drop_the_key_hints() {
    // Wide enough for the toolbar's full labels on one row.
    let size = (2560, 800);
    let keyboard = playing(HostView::default(), size);
    let touch = playing(HostView { touch_controls: true, ..HostView::default() }, size);
    // The toolbar labels lose their key hints ("Quests (Q)" becomes "Quests").
    let width = |player: &Player| rect(player, WidgetId::new("hud").with("quests")).width;
    assert!(width(&touch) < width(&keyboard), "{} vs {}", width(&touch), width(&keyboard));
}

#[test]
fn density_one_keeps_the_desktop_layout() {
    // The desktop never sets a host view: the default is density 1, no controls.
    let size = (1366, 768);
    let mut desktop = playing(HostView::default(), size);
    let before = rect(&desktop, WidgetId::new("hud").with("menu"));
    desktop.set_host_view(HostView { density: 1.0, touch_controls: false, inset_bottom: 0.0 });
    desktop.step(FRAME, &[], size.0, size.1).unwrap();
    assert_eq!(rect(&desktop, WidgetId::new("hud").with("menu")), before);
    // A dense 1366×768 CSS-pixel laptop (2732×1536 frame pixels) lays out the same, twice as big.
    let mut retina = playing(HostView { density: 2.0, ..HostView::default() }, (2732, 1536));
    retina.step(FRAME, &[], 2732, 1536).unwrap();
    let dense = rect(&retina, WidgetId::new("hud").with("menu"));
    assert!(
        (dense.width - before.width * 2.0).abs() < 1.0 && (dense.x - before.x * 2.0).abs() < 1.0,
        "{dense:?} vs {before:?}"
    );
}
