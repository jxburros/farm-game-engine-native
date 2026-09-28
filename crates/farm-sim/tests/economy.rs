//! The economy cases of the retired C# `M2SystemsTests.cs` (m2-systems.test.ts)
//! and the buy-progresses-quests case of `M3SystemsTests.cs`, driven through the `economy` /
//! `dialogue_system` handlers directly (the engine dispatch is ported separately). States come
//! from the golden starter farm (100 money, 10 seed-wheat, a hoe, spring day 1).

mod fixture_project;

use farm_sim::economy::{
    handle_buy_item, handle_close_shop, handle_open_shop, handle_repair_tool, handle_sell_item, remaining_daily_stock,
};
use farm_sim::effects::Effect;
use farm_sim::schema::{
    DialogueOption, DialogueState, GameProject, GameState, Quest, QuestObjective, QuestRewards, ShopDefinition,
    ShopSession, ShopStockEntry,
};
use farm_sim::EngineContext;
use fixture_project::{has_message, make_engine, message_texts, quantity};
use indexmap::IndexMap;

/// The M2 test shop: `shop-test` with a plain entry, a summer-only entry, a daily-limited entry
/// and a price override.
fn test_shop() -> ShopDefinition {
    ShopDefinition {
        id: "shop-test".to_owned(),
        name: "Test Shop".to_owned(),
        stock: vec![
            ShopStockEntry { item_id: "seed-wheat".to_owned(), ..ShopStockEntry::default() },
            ShopStockEntry {
                item_id: "seed-tomato".to_owned(),
                seasons: Some(vec!["summer".to_owned()]),
                ..ShopStockEntry::default()
            },
            ShopStockEntry {
                item_id: "fertilizer-quality".to_owned(),
                daily_limit: Some(2.0),
                ..ShopStockEntry::default()
            },
            ShopStockEntry { item_id: "seed-carrot".to_owned(), price: Some(3.0), ..ShopStockEntry::default() },
        ],
        sell_price_multiplier: 1.0,
        buys_items: true,
        repairs_tools: true,
        repair_cost_per_point: 0.5,
        ..ShopDefinition::default()
    }
}

fn make_m2_engine(mutate: impl FnOnce(&mut GameProject)) -> (EngineContext, GameState) {
    make_engine("m2", |project| {
        project.shops = vec![test_shop()];
        mutate(project);
    })
}

fn open_shop(ctx: &EngineContext, state: &mut GameState) {
    assert!(handle_open_shop(ctx, state, "shop-test").is_empty());
    assert_eq!(state.shop, Some(ShopSession { shop_id: "shop-test".to_owned() }));
}

// --- economy (M2) ---

#[test]
fn buys_stock_charging_money() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    open_shop(&ctx, &mut state);
    let effects = handle_buy_item(&ctx, &mut state, "seed-wheat", 2.0);
    assert_eq!(state.player.money, 100.0 - 20.0);
    assert_eq!(quantity(&state, "seed-wheat"), Some(12.0)); // 10 starting + 2
    assert_eq!(message_texts(&effects)[0], "Bought 2x Wheat Seeds for $20");
}

#[test]
fn honors_price_overrides() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    open_shop(&ctx, &mut state);
    handle_buy_item(&ctx, &mut state, "seed-carrot", 1.0);
    assert_eq!(state.player.money, 97.0);
}

#[test]
fn rejects_purchases_without_enough_money() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    open_shop(&ctx, &mut state);
    state.player.money = 5.0;
    let before = state.clone();
    let effects = handle_buy_item(&ctx, &mut state, "seed-wheat", 1.0);
    assert!(has_message(&effects, |t| t == "Not enough money!"));
    assert_eq!(state.player.money, 5.0);
    assert_eq!(state, before);
}

#[test]
fn enforces_seasonal_stock() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    open_shop(&ctx, &mut state);
    let effects = handle_buy_item(&ctx, &mut state, "seed-tomato", 1.0);
    assert!(has_message(&effects, |t| t.contains("Not available in spring")));
    assert_eq!(effects, vec![Effect::message("error", "Not available in spring.")]);
}

#[test]
fn enforces_and_resets_daily_limits() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    open_shop(&ctx, &mut state);
    handle_buy_item(&ctx, &mut state, "fertilizer-quality", 2.0);
    let blocked = handle_buy_item(&ctx, &mut state, "fertilizer-quality", 1.0);
    assert!(has_message(&blocked, |t| t.contains("Sold out for today")));

    // Next day the limit resets.
    farm_sim::game_time::perform_sleep(&ctx, &mut state, farm_sim::game_time::SleepOptions::default());
    open_shop(&ctx, &mut state);
    let again = handle_buy_item(&ctx, &mut state, "fertilizer-quality", 1.0);
    assert!(has_message(&again, |t| t.starts_with("Bought")));
}

#[test]
fn sells_items_for_their_value() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    open_shop(&ctx, &mut state);
    let effects = handle_sell_item(&ctx, &mut state, "seed-wheat", 10.0);
    assert_eq!(state.player.money, 100.0 + 100.0);
    assert!(!state.player.inventory.iter().any(|s| s.item.id == "seed-wheat"));
    assert_eq!(effects, vec![Effect::message("success", "Sold 10x Wheat Seeds for $100")]);
}

#[test]
fn repairs_damaged_tools_for_a_fee() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    open_shop(&ctx, &mut state);
    for slot in &mut state.player.inventory {
        if slot.item.id == "tool-hoe" {
            slot.item.durability = Some(0.0);
        }
    }
    let effects = handle_repair_tool(&ctx, &mut state, "tool-hoe");
    let hoe = state.player.inventory.iter().find(|s| s.item.id == "tool-hoe").expect("hoe stays in the inventory");
    assert_eq!(hoe.item.durability, Some(100.0));
    assert_eq!(state.player.money, 100.0 - 50.0); // 100 points * 0.5
    assert_eq!(effects, vec![Effect::message("success", "Repaired Hoe for $50")]);
}

#[test]
fn opens_a_shop_from_a_dialogue_option() {
    let (ctx, mut state) = make_m2_engine(|project| {
        let option = DialogueOption {
            text: "Trade".to_owned(),
            open_shop_id: Some("shop-test".to_owned()),
            ..DialogueOption::default()
        };
        project.npcs[0].dialogue[0].options.push(option.clone());
        project.dialogues[0].options.push(option);
    });
    state.dialogue =
        Some(DialogueState { npc_id: "npc-farmer".to_owned(), dialogue_id: "dialogue-farmer-greeting".to_owned() });
    let effects = farm_sim::dialogue_system::handle_choose_dialogue_option(&ctx, &mut state, 2.0);
    assert!(effects.is_empty());
    assert_eq!(state.shop, Some(ShopSession { shop_id: "shop-test".to_owned() }));
    assert_eq!(state.dialogue, None);
}

// --- quest-giver binding & collect objectives (M3) ---

#[test]
fn buying_items_progresses_collect_objectives() {
    let (ctx, mut state) = make_engine("m3", |project| {
        project.quests.push(Quest {
            id: "quest-buy".to_owned(),
            name: "Buy".to_owned(),
            description: "Buy carrots seeds".to_owned(),
            status: "active".to_owned(),
            objectives: vec![QuestObjective {
                id: "o".to_owned(),
                r#type: "collect".to_owned(),
                description: "x".to_owned(),
                target_item_id: Some("seed-carrot".to_owned()),
                target_item_quantity: Some(2.0),
                ..QuestObjective::default()
            }],
            rewards: QuestRewards { money: Some(5.0), ..QuestRewards::default() },
            ..Quest::default()
        });
        project.player.active_quests.push("quest-buy".to_owned());
        project.shops = vec![ShopDefinition {
            id: "shop-s".to_owned(),
            name: "S".to_owned(),
            stock: vec![ShopStockEntry { item_id: "seed-carrot".to_owned(), ..ShopStockEntry::default() }],
            sell_price_multiplier: 1.0,
            buys_items: true,
            repairs_tools: false,
            repair_cost_per_point: 0.5,
            ..ShopDefinition::default()
        }];
    });
    handle_open_shop(&ctx, &mut state, "shop-s");
    handle_buy_item(&ctx, &mut state, "seed-carrot", 2.0);
    assert!(state.player.completed_quests.iter().any(|id| id == "quest-buy"));
}

// --- the remaining economy.ts branches (no TS test covers them) ---

#[test]
fn remaining_daily_stock_treats_missing_zero_and_nan_limits_as_unlimited() {
    let (_, mut state) = make_m2_engine(|_| {});
    assert_eq!(remaining_daily_stock(&state, "shop-test", "x", None), f64::INFINITY);
    assert_eq!(remaining_daily_stock(&state, "shop-test", "x", Some(0.0)), f64::INFINITY);
    assert_eq!(remaining_daily_stock(&state, "shop-test", "x", Some(f64::NAN)), f64::INFINITY);
    assert_eq!(remaining_daily_stock(&state, "shop-test", "x", Some(3.0)), 3.0);
    let mut per_item = IndexMap::new();
    per_item.insert("x".to_owned(), 5.0);
    state.shop_purchases_today.insert("shop-test".to_owned(), per_item);
    assert_eq!(remaining_daily_stock(&state, "shop-test", "x", Some(3.0)), 0.0);
    assert_eq!(remaining_daily_stock(&state, "shop-test", "x", Some(7.0)), 2.0);
    assert_eq!(remaining_daily_stock(&state, "shop-other", "x", Some(7.0)), 7.0);
}

#[test]
fn open_shop_rejects_unknown_shops_and_closes_an_open_dialogue() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    state.dialogue =
        Some(DialogueState { npc_id: "npc-farmer".to_owned(), dialogue_id: "dialogue-farmer-greeting".to_owned() });
    let missing = handle_open_shop(&ctx, &mut state, "shop-nope");
    assert_eq!(missing, vec![Effect::message("error", "That shop does not exist.")]);
    assert_eq!(state.shop, None);
    assert!(state.dialogue.is_some());

    open_shop(&ctx, &mut state);
    assert_eq!(state.dialogue, None);
}

#[test]
fn close_shop_only_changes_state_while_a_session_is_open() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    let before = state.clone();
    assert!(handle_close_shop(&mut state).is_empty());
    assert_eq!(state, before);
    open_shop(&ctx, &mut state);
    assert!(handle_close_shop(&mut state).is_empty());
    assert_eq!(state.shop, None);
}

#[test]
fn buy_rejects_without_a_session_for_unsold_and_unknown_items_and_for_non_positive_quantities() {
    let (ctx, mut state) = make_m2_engine(|project| {
        project.shops[0].stock.push(ShopStockEntry { item_id: "ghost".to_owned(), ..ShopStockEntry::default() });
    });
    assert_eq!(
        handle_buy_item(&ctx, &mut state, "seed-wheat", 1.0),
        vec![Effect::message("error", "No shop is open.")]
    );
    open_shop(&ctx, &mut state);
    assert!(handle_buy_item(&ctx, &mut state, "seed-wheat", 0.0).is_empty());
    assert!(handle_buy_item(&ctx, &mut state, "seed-wheat", -2.0).is_empty());
    assert_eq!(handle_buy_item(&ctx, &mut state, "gift-flower", 1.0), vec![Effect::message("error", "Not sold here.")]);
    assert_eq!(handle_buy_item(&ctx, &mut state, "ghost", 1.0), vec![Effect::message("error", "Unknown item.")]);
    assert_eq!(state.player.money, 100.0);

    // A session naming a shop that no longer exists reads as closed.
    state.shop = Some(ShopSession { shop_id: "shop-gone".to_owned() });
    assert_eq!(
        handle_buy_item(&ctx, &mut state, "seed-wheat", 1.0),
        vec![Effect::message("error", "No shop is open.")]
    );
}

#[test]
fn buy_reports_the_remaining_daily_stock_before_selling_out() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    open_shop(&ctx, &mut state);
    let mut per_item = IndexMap::new();
    per_item.insert("fertilizer-quality".to_owned(), 1.0);
    state.shop_purchases_today.insert("shop-test".to_owned(), per_item);
    let some_left = handle_buy_item(&ctx, &mut state, "fertilizer-quality", 2.0);
    assert_eq!(some_left, vec![Effect::message("error", "Only 1 left today.")]);

    state.shop_purchases_today["shop-test"].insert("fertilizer-quality".to_owned(), 2.0);
    let sold_out = handle_buy_item(&ctx, &mut state, "fertilizer-quality", 1.0);
    assert_eq!(sold_out, vec![Effect::message("error", "Sold out for today!")]);
    assert_eq!(state.player.money, 100.0);
}

#[test]
fn buy_rejects_when_the_inventory_is_full() {
    let (ctx, mut state) = make_m2_engine(|_| {});
    open_shop(&ctx, &mut state);
    state.player.max_inventory_size = state.player.inventory.len() as f64;
    let effects = handle_buy_item(&ctx, &mut state, "fertilizer-quality", 1.0);
    assert_eq!(effects, vec![Effect::message("error", "Inventory is full!")]);
    assert_eq!(state.player.money, 100.0);
    assert!(state.shop_purchases_today.is_empty());
}

#[test]
fn sell_rejects_non_buying_shops_and_missing_quantities() {
    let (ctx, mut state) = make_m2_engine(|project| {
        project.shops.push(ShopDefinition {
            id: "shop-museum".to_owned(),
            name: "Museum".to_owned(),
            buys_items: false,
            ..ShopDefinition::default()
        });
    });
    assert_eq!(
        handle_sell_item(&ctx, &mut state, "seed-wheat", 1.0),
        vec![Effect::message("error", "No shop is open.")]
    );
    open_shop(&ctx, &mut state);
    assert!(handle_sell_item(&ctx, &mut state, "seed-wheat", 0.0).is_empty());
    assert_eq!(
        handle_sell_item(&ctx, &mut state, "seed-wheat", 11.0),
        vec![Effect::message("error", "You don't have that many.")]
    );
    assert_eq!(
        handle_sell_item(&ctx, &mut state, "gift-flower", 1.0),
        vec![Effect::message("error", "You don't have that many.")]
    );
    state.shop = Some(ShopSession { shop_id: "shop-museum".to_owned() });
    assert_eq!(
        handle_sell_item(&ctx, &mut state, "seed-wheat", 1.0),
        vec![Effect::message("error", "Museum doesn't buy items.")]
    );
    assert_eq!(state.player.money, 100.0);
    assert_eq!(quantity(&state, "seed-wheat"), Some(10.0));
}

#[test]
fn sell_floors_the_unit_price_through_the_multiplier() {
    let (ctx, mut state) = make_m2_engine(|project| {
        project.shops[0].sell_price_multiplier = 0.75;
    });
    open_shop(&ctx, &mut state);
    // Wheat seeds are worth 10: floor(7.5) = 7 each.
    let effects = handle_sell_item(&ctx, &mut state, "seed-wheat", 3.0);
    assert_eq!(effects, vec![Effect::message("success", "Sold 3x Wheat Seeds for $21")]);
    assert_eq!(state.player.money, 121.0);
    assert_eq!(quantity(&state, "seed-wheat"), Some(7.0));
}

#[test]
fn repair_rejects_non_tools_perfect_tools_and_reports_the_cost_when_broke() {
    let (ctx, mut state) = make_m2_engine(|project| {
        project.shops.push(ShopDefinition {
            id: "shop-stall".to_owned(),
            name: "Stall".to_owned(),
            repairs_tools: false,
            ..ShopDefinition::default()
        });
    });
    assert_eq!(handle_repair_tool(&ctx, &mut state, "tool-hoe"), vec![Effect::message("error", "No shop is open.")]);
    open_shop(&ctx, &mut state);
    assert_eq!(
        handle_repair_tool(&ctx, &mut state, "seed-wheat"),
        vec![Effect::message("error", "That can't be repaired.")]
    );
    assert_eq!(
        handle_repair_tool(&ctx, &mut state, "tool-axe"),
        vec![Effect::message("error", "That can't be repaired.")]
    );
    assert_eq!(
        handle_repair_tool(&ctx, &mut state, "tool-hoe"),
        vec![Effect::message("info", "Hoe is in perfect shape.")]
    );

    for slot in &mut state.player.inventory {
        if slot.item.id == "tool-hoe" {
            slot.item.durability = Some(37.0);
        }
    }
    state.player.money = 10.0;
    // 63 missing points * 0.5 = 31.5 → ceil → 32.
    assert_eq!(
        handle_repair_tool(&ctx, &mut state, "tool-hoe"),
        vec![Effect::message("error", "Repair costs $32 — not enough money!")]
    );
    assert_eq!(state.player.money, 10.0);

    state.shop = Some(ShopSession { shop_id: "shop-stall".to_owned() });
    assert_eq!(
        handle_repair_tool(&ctx, &mut state, "tool-hoe"),
        vec![Effect::message("error", "Stall doesn't repair tools.")]
    );
}
