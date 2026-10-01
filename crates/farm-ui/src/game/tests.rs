//! Play screen tests: every button runs the right engine command, disabled states follow the
//! engine's numbers, keys and gamepad navigation reach the same buttons as the mouse.

use super::test_support::Fixture;
use super::{GameAction, Panel, ShopTab, ToastKind};
use crate::input::NavAction;
use crate::ui::WidgetId;
use farm_runtime::host::MinigameInput;
use farm_sim::schema::{
    DialogueState, GamePanel, GamePanelEntry, InventorySlot, MinigameSession, ShopSession, TileMachine,
};
use farm_sim::Command;

fn command(actions: &[GameAction]) -> Option<&Command> {
    actions.iter().find_map(|action| match action {
        GameAction::Command(command) => Some(command),
        _ => None,
    })
}

fn with_dialogue(fixture: &mut Fixture, npc: &str, dialogue: &str) {
    fixture.state.dialogue = Some(DialogueState { npc_id: npc.into(), dialogue_id: dialogue.into() });
    fixture.idle();
}

#[test]
fn dialogue_options_pick_by_click_digit_and_navigation() {
    let mut fixture = Fixture::starter();
    with_dialogue(&mut fixture, "npc-merchant", "dialogue-merchant-greeting");
    let second = WidgetId::new("dialogue-option").with("dialogue-merchant-greeting").with(1usize);
    assert_eq!(command(&fixture.click(second)), Some(&Command::ChooseDialogueOption { index: 1 }));
    assert_eq!(command(&fixture.keys(&["1"])), Some(&Command::ChooseDialogueOption { index: 0 }));
    // A digit past the visible options does nothing.
    assert_eq!(command(&fixture.keys(&["9"])), None);
    fixture.nav(&[NavAction::Down]);
    assert_eq!(command(&fixture.nav(&[NavAction::Accept])), Some(&Command::ChooseDialogueOption { index: 1 }));
    let texts = fixture.texts();
    assert!(texts.iter().any(|text| text == "Merchant Mia"), "{texts:?}");
}

#[test]
fn the_dialogue_portrait_shows_the_npcs_art() {
    let mut fixture = Fixture::starter();
    let images = |fixture: &Fixture| {
        fixture.last.commands.iter().filter(|command| matches!(command, farm_render::DrawCmd::Image { .. })).count()
    };
    fixture.idle();
    let without = images(&fixture);
    // The merchant has no art of its own: the built-in art for its appearance stands in.
    with_dialogue(&mut fixture, "npc-merchant", "dialogue-merchant-greeting");
    assert_eq!(images(&fixture), without + 1, "the portrait is drawn from the NPC's art");
    let portrait = fixture.last.commands.iter().rev().find_map(|command| match command {
        farm_render::DrawCmd::Image { dst, .. } if dst.width <= 48.0 && dst.height <= 48.0 => Some(*dst),
        _ => None,
    });
    assert!(portrait.is_some_and(|dst| dst.width >= 16.0), "{portrait:?}");
}

#[test]
fn a_dialogue_without_options_says_goodbye() {
    let mut fixture = Fixture::starter();
    // The farmer's crop talk has one option; hide it behind a flag the player lacks.
    let npc = fixture.ctx.content.npcs.iter_mut().find(|npc| npc.id == "npc-farmer").unwrap();
    npc.dialogue[1].options[0].requires_flag = Some("never-set".into());
    with_dialogue(&mut fixture, "npc-farmer", "dialogue-farmer-crops");
    let actions = fixture.click(WidgetId::new("dialogue-goodbye"));
    assert_eq!(command(&actions), Some(&Command::CloseDialogue));
}

#[test]
fn long_dialogue_text_wraps_inside_the_card() {
    let mut fixture = Fixture::starter();
    let npc = fixture.ctx.content.npcs.iter_mut().find(|npc| npc.id == "npc-farmer").unwrap();
    npc.dialogue[0].text = "word ".repeat(200);
    with_dialogue(&mut fixture, "npc-farmer", "dialogue-farmer-greeting");
    let lines = fixture.texts().iter().filter(|text| text.starts_with("word")).count();
    assert!(lines > 5, "{lines} lines");
    let width = |text: &str| farm_render::text::measure(farm_render::FontId::Regular, 15.0, text);
    assert!(fixture.texts().iter().filter(|text| text.starts_with("word")).all(|line| width(line) <= 680.0));
}

#[test]
fn shop_buys_sells_and_repairs_through_commands() {
    let mut fixture = Fixture::starter();
    fixture.state.shop = Some(ShopSession { shop_id: "shop-general".into() });
    fixture.idle();
    let buy = WidgetId::new("shop-buy").with("seed-wheat");
    assert_eq!(command(&fixture.click(buy)), Some(&Command::BuyItem { item_id: "seed-wheat".into(), quantity: 1 }));
    assert_eq!(
        command(&fixture.click(buy.with("5"))),
        Some(&Command::BuyItem { item_id: "seed-wheat".into(), quantity: 5 })
    );
    // Without money the buttons are disabled: a click does nothing.
    fixture.state.player.money = 0;
    fixture.idle();
    assert!(fixture.ui.last_rect(buy).is_none(), "disabled buttons take no input");
    let rect = fixture.ui.last_rect(buy.with("5"));
    assert!(rect.is_none());

    // Tab switches to Sell (keyboard Tab / gamepad RB).
    fixture.nav(&[NavAction::TabNext]);
    assert_eq!(fixture.game_ui.shop_tab, ShopTab::Sell);
    fixture.idle();
    let sell = WidgetId::new("shop-sell").with("seed-wheat");
    assert_eq!(
        command(&fixture.click(sell)),
        Some(&Command::SellItem { item_id: "seed-wheat".into(), quantity: 1, quality: Some("normal".into()) })
    );
    assert_eq!(
        command(&fixture.click(sell.with("all"))),
        Some(&Command::SellItem { item_id: "seed-wheat".into(), quantity: 10, quality: Some("normal".into()) })
    );

    // Repair a worn hoe.
    let hoe = fixture.state.player.inventory.iter_mut().find(|slot| slot.item.id == "tool-hoe").unwrap();
    hoe.item.durability = Some(40);
    fixture.state.player.money = 1000;
    fixture.click(WidgetId::new("shop-tab").with("Repair"));
    assert_eq!(fixture.game_ui.shop_tab, ShopTab::Repair);
    fixture.idle();
    let actions = fixture.click(WidgetId::new("shop-repair").with("tool-hoe"));
    assert_eq!(command(&actions), Some(&Command::RepairTool { item_id: "tool-hoe".into() }));
    assert_eq!(command(&fixture.click(WidgetId::new("shop").with("close"))), Some(&Command::CloseShop));
}

#[test]
fn daily_limits_disable_purchases_past_what_is_left() {
    let mut fixture = Fixture::starter();
    fixture.state.shop = Some(ShopSession { shop_id: "shop-general".into() });
    fixture.state.player.money = 10_000;
    let limited =
        fixture.ctx.content.shops[0].stock.iter().find(|entry| entry.daily_limit.is_some()).unwrap().item_id.clone();
    fixture.idle();
    fixture.idle();
    let buy = WidgetId::new("shop-buy").with(&limited);
    assert!(fixture.ui.last_rect(buy).is_some());
    // Buy out today's stock: the engine's remaining count disables the button.
    let limit =
        fixture.ctx.content.shops[0].stock.iter().find(|entry| entry.item_id == limited).unwrap().daily_limit.unwrap();
    for _ in 0..limit as usize {
        let effects = farm_sim::apply_command(
            &fixture.ctx,
            &mut fixture.state,
            &Command::BuyItem { item_id: limited.clone(), quantity: 1 },
        );
        let _ = effects;
    }
    fixture.idle();
    assert!(fixture.ui.last_rect(buy).is_none(), "sold out today");
    assert!(fixture.texts().iter().any(|text| text.contains("0/")), "{:?}", fixture.texts());
}

#[test]
fn crafting_crafts_loads_machines_and_places_them() {
    let mut fixture = Fixture::starter();
    fixture.game_ui.panel = Some(Panel::Crafting);
    let item = |id: &str, quantity: u32| {
        let item = fixture_item(id);
        InventorySlot::new(item, quantity)
    };
    fn fixture_item(id: &str) -> farm_sim::schema::Item {
        let project = super::test_support::starter_project();
        let content = farm_sim::create_content_from_project(&project);
        content.items.iter().find(|item| item.id == id).unwrap_or_else(|| panic!("{id}")).clone()
    }
    fixture.state.player.inventory.push(item("material-fiber", 3));
    fixture.idle();
    fixture.idle();
    let hay = WidgetId::new("craft").with("recipe-craft-hay");
    fixture.scroll_to(hay);
    assert_eq!(command(&fixture.click(hay)), Some(&Command::Craft { recipe_id: "recipe-craft-hay".into() }));
    // Without the stone and copper, the furnace can't be crafted and says why.
    assert!(fixture.ui.last_rect(WidgetId::new("craft").with("recipe-craft-furnace")).is_none());

    // Place a machine the player holds on the faced tile.
    fixture.state.player.inventory.push(item("machine-furnace", 1));
    fixture.idle();
    let place = WidgetId::new("craft-place").with("machine-furnace");
    fixture.scroll_to(place);
    assert_eq!(
        command(&fixture.click(place)),
        Some(&Command::PlaceMachine { machine_type_id: "machine-furnace".into() })
    );

    // Facing a furnace: load it.
    let facing = farm_sim::world::world_movement::facing_target(&fixture.state);
    let scene = fixture.state.world.scenes.iter_mut().find(|scene| scene.id == fixture.state.player.scene_id).unwrap();
    scene.tiles[facing.y as usize][facing.x as usize].machine =
        Some(TileMachine { type_id: "machine-furnace".into(), ..TileMachine::default() });
    fixture.state.player.inventory.push(item("ore-copper", 3));
    fixture.state.player.inventory.push(item("material-wood", 1));
    fixture.idle();
    fixture.idle();
    let load = WidgetId::new("craft-load").with("recipe-smelt-copper");
    fixture.scroll_to(load);
    assert_eq!(command(&fixture.click(load)), Some(&Command::MachineLoad { recipe_id: "recipe-smelt-copper".into() }));
    assert!(fixture.ui.last_rect(WidgetId::new("craft-load").with("recipe-smelt-iron")).is_none(), "no iron ore");
    // An idle machine can be picked back up.
    let pick_up = WidgetId::new("craft-pick-up");
    fixture.scroll_to(pick_up);
    assert_eq!(command(&fixture.click(pick_up)), Some(&Command::PickUpMachine));
    assert_eq!(fixture.click(WidgetId::new("crafting").with("close")), [GameAction::ClosePanel]);
}

#[test]
fn inventory_uses_and_gifts_items() {
    let mut fixture = Fixture::starter();
    fixture.game_ui.panel = Some(Panel::Inventory);
    fixture.idle();
    fixture.idle();
    let index = fixture.state.player.inventory.iter().position(|slot| slot.item.id == "snack-trail-mix").unwrap();
    let snack = WidgetId::new("inventory").with(index).with("snack-trail-mix");
    assert_eq!(
        fixture.click(snack.with("use")),
        [GameAction::ClosePanel, GameAction::Command(Command::UseItem { item_id: "snack-trail-mix".into() })]
    );
    let gift = fixture.click(snack.with("gift"));
    assert_eq!(command(&gift), Some(&Command::GiveGift { item_id: "snack-trail-mix".into() }));
    // Tools can't be gifted; items without an action can't be used.
    let hoe_index = fixture.state.player.inventory.iter().position(|slot| slot.item.id == "tool-hoe").unwrap();
    assert!(fixture.ui.last_rect(WidgetId::new("inventory").with(hoe_index).with("tool-hoe").with("gift")).is_none());
    assert!(fixture.texts().iter().any(|text| text.starts_with("Total value: $")));
    assert_eq!(fixture.click(WidgetId::new("inventory-close")), [GameAction::ClosePanel]);
}

#[test]
fn quest_log_lists_active_quests_with_progress() {
    let mut fixture = Fixture::starter();
    farm_sim::quests::auto_start_quests(&fixture.ctx, &mut fixture.state);
    fixture.game_ui.panel = Some(Panel::Quests);
    fixture.idle();
    fixture.idle();
    let texts = fixture.texts();
    assert!(texts.iter().any(|text| text == "ACTIVE QUESTS"), "{texts:?}");
    assert!(texts.iter().any(|text| text.starts_with("Rewards: ")), "{texts:?}");
    assert_eq!(fixture.click(WidgetId::new("quests").with("close")), [GameAction::ClosePanel]);
}

#[test]
fn hud_toolbar_and_creator_panels_run_their_actions() {
    let mut fixture = Fixture::starter();
    fixture.panels = vec![GamePanel {
        id: "panel-1".into(),
        title: "Farm".into(),
        entries: vec![
            GamePanelEntry { kind: "money".into(), label: "Gold".into(), ..GamePanelEntry::default() },
            GamePanelEntry { kind: "action".into(), label: "Bless".into(), value: "action-growth-blessing".into() },
        ],
        ..GamePanel::default()
    }];
    fixture.idle();
    assert_eq!(command(&fixture.click(WidgetId::new("hud").with("sleep"))), Some(&Command::Sleep));
    assert_eq!(fixture.click(WidgetId::new("hud").with("quests")), [GameAction::TogglePanel(Panel::Quests)]);
    assert_eq!(fixture.click(WidgetId::new("hud").with("menu")), [GameAction::OpenMenu]);
    let bless = WidgetId::new("panel").with("panel-1").with(1usize);
    assert_eq!(
        command(&fixture.click(bless)),
        Some(&Command::PerformAction { action_id: "action-growth-blessing".into() })
    );
    assert!(fixture.texts().iter().any(|text| text == "Gold: 100"), "{:?}", fixture.texts());
    assert!(fixture.texts().iter().any(|text| text == "Made with Farming RPG Maker"));
    // While a panel is open, creator actions are disabled (and the HUD is under its backdrop).
    fixture.game_ui.panel = Some(Panel::Inventory);
    fixture.idle();
    assert!(fixture.ui.last_rect(bless).is_none());
    // The HUD takes no keyboard focus: navigation stays in the panel.
    fixture.nav(&[NavAction::Up, NavAction::Up]);
    assert!(fixture.ui.focused().is_some_and(|focus| focus != WidgetId::new("hud").with("sleep")));
}

#[test]
fn minigames_press_release_choose_and_give_up() {
    let mut fixture = Fixture::starter();
    fixture.state.minigame = Some(MinigameSession { minigame_id: "fishing".into(), ..MinigameSession::default() });
    fixture.mount_minigame();
    fixture.idle();
    fixture.idle();
    let primary = WidgetId::new("minigame-primary");
    let actions = fixture.click(primary);
    assert_eq!(actions, [GameAction::Minigame(MinigameInput::Press), GameAction::Minigame(MinigameInput::Release)]);
    assert_eq!(command(&fixture.click(WidgetId::new("minigame-give-up"))), Some(&Command::CancelMinigame));
    // Keyboard accept on the hold button is the host's job (it forwards Space/Enter itself).
    assert!(fixture.nav(&[NavAction::Accept]).is_empty());

    // A battle offers its choices as buttons.
    fixture.ctx.content.minigames.push(farm_sim::schema::MinigameDef {
        id: "duel".into(),
        name: "Duel".into(),
        kind: "simple-battle".into(),
        ..Default::default()
    });
    fixture.state.minigame = Some(MinigameSession { minigame_id: "duel".into(), ..MinigameSession::default() });
    fixture.mount_minigame();
    fixture.idle();
    fixture.idle();
    let guard = WidgetId::new("minigame-choice").with("guard");
    assert_eq!(fixture.click(guard), [GameAction::Minigame(MinigameInput::Act { choice: "guard".into() })]);
    // Focus starts on the first choice; accept picks it.
    let actions = fixture.nav(&[NavAction::Accept]);
    assert!(matches!(actions.as_slice(), [GameAction::Minigame(MinigameInput::Act { .. })]), "{actions:?}");
}

#[test]
fn timing_bar_draws_its_marker_inside_the_track() {
    let mut fixture = Fixture::starter();
    fixture.state.minigame = Some(MinigameSession { minigame_id: "fishing".into(), ..MinigameSession::default() });
    fixture.mount_minigame();
    fixture.idle();
    let marker = fixture.last.commands.iter().any(|command| {
        matches!(command, farm_render::DrawCmd::FillRect { rect, .. } if rect.width == 3.0 && rect.height == 26.0)
    });
    assert!(marker, "the marker is drawn");
}

#[test]
fn toasts_collapse_and_draw_under_the_hud() {
    let mut fixture = Fixture::starter();
    fixture.game_ui.toasts.push("Watered!", ToastKind::Success);
    fixture.game_ui.toasts.push("Watered!", ToastKind::Success);
    fixture.idle();
    let texts = fixture.texts();
    assert!(texts.iter().any(|text| text == "Watered!") && texts.iter().any(|text| text == "\u{00d7}2"), "{texts:?}");
    assert!(fixture.game_ui.toasts.top > 40.0);
}

#[test]
fn ui_scale_and_text_size_scale_hit_areas() {
    let mut fixture = Fixture::starter();
    fixture.size = (2560.0, 1600.0);
    fixture.scale = 2.0;
    fixture.idle();
    let sleep = WidgetId::new("hud").with("sleep");
    let logical = fixture.ui.last_rect(sleep).unwrap();
    // The click helper converts logical centers to physical pixels at scale 2.
    assert_eq!(command(&fixture.click(sleep)), Some(&Command::Sleep));
    fixture.text_scale = 1.4;
    fixture.idle();
    fixture.idle();
    let larger = fixture.ui.last_rect(sleep).unwrap();
    assert!(larger.width > logical.width * 1.2, "{logical:?} → {larger:?}");
}

#[test]
fn narrow_screens_wrap_the_hud_and_hints_without_overlap() {
    let mut fixture = Fixture::starter();
    fixture.size = (800.0, 600.0);
    fixture.idle();
    fixture.idle();
    let tools: Vec<_> = ["inventory", "quests", "craft", "sleep", "menu"]
        .iter()
        .map(|key| fixture.ui.last_rect(WidgetId::new("hud").with(*key)).unwrap())
        .collect();
    for (a, b) in tools.iter().zip(tools.iter().skip(1)) {
        assert!(a.right() <= b.x || a.bottom() <= b.y, "{a:?} overlaps {b:?}");
    }
    assert!(tools.iter().all(|rect| rect.right() <= 800.0), "{tools:?}");
}

#[test]
fn spanish_translates_the_hud_toolbar_and_panels() {
    let mut fixture = Fixture::starter();
    fixture.ui.set_lang(crate::i18n::Lang::Es);
    // Wide enough for the full labels (longer in Spanish) on one row.
    fixture.size = (1920.0, 1080.0);
    fixture.idle();
    fixture.idle();
    let texts = fixture.texts();
    // Season and weather names come from the game's content, not the tables.
    for expected in
        ["Dinero:", "Estaci\u{f3}n:", "D\u{ed}a:", "A\u{f1}o:", "Hora:", "Misiones (J)", "Regar", "Guada\u{f1}a"]
    {
        assert!(texts.iter().any(|text| text == expected), "{expected} missing from {texts:?}");
    }
    assert!(!texts.iter().any(|text| text == "Money:" || text == "Quests (J)"), "{texts:?}");
    // Widget ids don't depend on the language: the same buttons run the same actions.
    assert_eq!(command(&fixture.click(WidgetId::new("hud").with("sleep"))), Some(&Command::Sleep));
    fixture.game_ui.panel = Some(Panel::Inventory);
    fixture.idle();
    fixture.idle();
    let texts = fixture.texts();
    assert!(texts.iter().any(|text| text == "Inventario"), "{texts:?}");
    assert!(texts.iter().any(|text| text.starts_with("Valor total: $")), "{texts:?}");
    assert_eq!(fixture.click(WidgetId::new("inventory-close")), [GameAction::ClosePanel]);
}

#[test]
fn readable_font_draws_the_interface_in_atkinson_hyperlegible() {
    use farm_render::{DrawCmd, FontId};
    let fonts = |fixture: &Fixture| -> Vec<FontId> {
        fixture
            .last
            .commands
            .iter()
            .filter_map(|command| match command {
                DrawCmd::Text { font, .. } => Some(*font),
                _ => None,
            })
            .collect()
    };
    let mut fixture = Fixture::starter();
    fixture.idle();
    fixture.idle();
    let inter = fonts(&fixture);
    assert!(inter.iter().all(|font| matches!(font, FontId::Regular | FontId::Bold)), "{inter:?}");
    let default_width = fixture.ui.last_rect(WidgetId::new("hud").with("quests")).unwrap().width;

    fixture.ui.set_readable_font(true);
    fixture.idle();
    fixture.idle();
    let readable = fonts(&fixture);
    assert!(readable.contains(&FontId::ReadableRegular) && readable.contains(&FontId::ReadableBold), "{readable:?}");
    // Text the readable face can't draw stays in Inter.
    assert_eq!(fixture.ui.face(FontId::Bold, "Harvest"), FontId::ReadableBold);
    assert_eq!(fixture.ui.face(FontId::Regular, "\u{7530}"), FontId::Regular);
    // Layout measures the face it draws: the toolbar button follows its label's new width.
    let width = fixture.ui.last_rect(WidgetId::new("hud").with("quests")).unwrap().width;
    assert_ne!(width, default_width);
    assert_eq!(command(&fixture.click(WidgetId::new("hud").with("sleep"))), Some(&Command::Sleep));
}

#[test]
fn inventory_holds_seeds_and_fertilizer_for_planting() {
    let mut fixture = Fixture::starter();
    fixture.game_ui.panel = Some(Panel::Inventory);
    fixture.idle();
    fixture.idle();
    let slot_id = |fixture: &Fixture, id: &str| {
        let index = fixture.state.player.inventory.iter().position(|slot| slot.item.id == id).unwrap();
        WidgetId::new("inventory").with(index).with(id)
    };
    let seed = slot_id(&fixture, "seed-tomato");
    assert_eq!(fixture.click(seed.with("hold")), [GameAction::Hold("seed-tomato".into())]);
    // The host toggles what is held; the button then reads "Held".
    fixture.game_ui.planting.toggle(&fixture.ctx.content, "seed-tomato");
    fixture.game_ui.planting.toggle(&fixture.ctx.content, "fertilizer-basic");
    fixture.idle();
    assert!(fixture.texts().iter().any(|text| text == "Held"));
    assert_eq!(fixture.game_ui.planting.seed.as_deref(), Some("seed-tomato"));
    assert_eq!(fixture.game_ui.planting.fertilizer.as_deref(), Some("fertilizer-basic"));
    // Tools can't be held; toggling again puts the seed away.
    let hoe = slot_id(&fixture, "tool-hoe");
    assert!(fixture.ui.last_rect(hoe.with("hold")).is_none());
    fixture.game_ui.planting.toggle(&fixture.ctx.content, "tool-hoe");
    fixture.game_ui.planting.toggle(&fixture.ctx.content, "seed-tomato");
    assert_eq!(fixture.game_ui.planting.seed, None);
    // Running out puts it away.
    fixture.state.player.inventory.retain(|slot| slot.item.id != "fertilizer-basic");
    fixture.idle();
    assert_eq!(fixture.game_ui.planting.fertilizer, None);
}
