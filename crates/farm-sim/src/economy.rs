//! Shops and money (port of `Economy.cs` / economy.ts).
//!
//! Economy & shops (M2). Shops are content; buying and selling are explicit commands
//! (harvesting no longer auto-sells). Any NPC can be a merchant via a dialogue option with
//! `openShopId`.

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::inventory;
use crate::quests;
use crate::schema::{GameState, ShopDefinition, ShopSession};
use crate::units;

pub fn find_shop<'a>(ctx: &'a EngineContext, shop_id: &str) -> Option<&'a ShopDefinition> {
    ctx.content.shops.iter().find(|shop| shop.id == shop_id)
}

pub fn handle_open_shop(ctx: &EngineContext, state: &mut GameState, shop_id: &str) -> Effects {
    let shop = find_shop(ctx, shop_id);
    if shop.is_none() {
        return vec![Effect::message(message_levels::ERROR, "That shop does not exist.")];
    }
    state.shop = Some(ShopSession { shop_id: shop_id.to_owned() });
    state.dialogue = None;
    Vec::new()
}

pub fn handle_close_shop(state: &mut GameState) -> Effects {
    if state.shop.is_none() {
        return Vec::new();
    }
    state.shop = None;
    Vec::new()
}

/// JS `!!n` for an optional number: `undefined` and `0` are falsy.
fn truthy_number(value: Option<u32>) -> Option<u32> {
    value.filter(|n| *n != 0)
}

/// Units of an item still purchasable today under a stock entry's daily limit; `None` when the
/// stock is unlimited (`!dailyLimit`: undefined and 0 mean unlimited).
pub fn remaining_daily_stock(state: &GameState, shop_id: &str, item_id: &str, daily_limit: Option<u32>) -> Option<u32> {
    let limit = truthy_number(daily_limit)?;
    let bought = purchased_today(state, shop_id, item_id).unwrap_or(0);
    Some(limit.saturating_sub(bought))
}

/// TS `state.shopPurchasesToday[shopId]?.[itemId]`.
fn purchased_today(state: &GameState, shop_id: &str, item_id: &str) -> Option<u32> {
    state.shop_purchases_today.get(shop_id).and_then(|per_item| per_item.get(item_id)).copied()
}

pub fn handle_buy_item(ctx: &EngineContext, state: &mut GameState, item_id: &str, quantity: u32) -> Effects {
    let Some(session) = &state.shop else {
        return vec![Effect::message(message_levels::ERROR, "No shop is open.")];
    };
    if quantity == 0 {
        return Vec::new();
    }

    let Some(shop) = find_shop(ctx, &session.shop_id) else {
        return vec![Effect::message(message_levels::ERROR, "No shop is open.")];
    };

    let Some(entry) = shop.stock.iter().find(|stock_entry| stock_entry.item_id == item_id) else {
        return vec![Effect::message(message_levels::ERROR, "Not sold here.")];
    };

    if let Some(seasons) = entry.seasons.as_ref().filter(|seasons| !seasons.is_empty()) {
        if !seasons.contains(&state.clock.season) {
            return vec![Effect::message(message_levels::ERROR, format!("Not available in {}.", state.clock.season))];
        }
    }

    if let Some(remaining) = remaining_daily_stock(state, &shop.id, item_id, entry.daily_limit) {
        if quantity > remaining {
            let text =
                if remaining == 0 { "Sold out for today!".to_owned() } else { format!("Only {remaining} left today.") };
            return vec![Effect::message(message_levels::ERROR, text)];
        }
    }

    let Some(item) = ctx.content.items.iter().find(|i| i.id == item_id) else {
        return vec![Effect::message(message_levels::ERROR, "Unknown item.")];
    };

    let price = entry.price.unwrap_or(item.value);
    let total = price.saturating_mul(i64::from(quantity));
    if state.player.money < total {
        return vec![Effect::message(message_levels::ERROR, "Not enough money!")];
    }

    let result = inventory::add_item(&state.player.inventory, item, quantity, state.player.max_inventory_size, None);
    if !result.added {
        return vec![Effect::message(message_levels::ERROR, "Inventory is full!")];
    }

    if truthy_number(entry.daily_limit).is_some() {
        let already = purchased_today(state, &shop.id, item_id).unwrap_or(0);
        state
            .shop_purchases_today
            .entry(shop.id.clone())
            .or_default()
            .insert(item_id.to_owned(), already.saturating_add(quantity));
    }

    state.player.inventory = result.inventory;
    state.player.money = state.player.money.saturating_sub(total);

    // Buying counts toward collect objectives (M3).
    let quest_effects = quests::progress_quests(ctx, state, "collect", item_id, quantity);

    let mut effects =
        vec![Effect::message(message_levels::SUCCESS, format!("Bought {quantity}x {} for ${total}", item.name))];
    effects.extend(quest_effects);
    effects
}

pub fn handle_sell_item(ctx: &EngineContext, state: &mut GameState, item_id: &str, quantity: u32) -> Effects {
    let Some(session) = &state.shop else {
        return vec![Effect::message(message_levels::ERROR, "No shop is open.")];
    };
    if quantity == 0 {
        return Vec::new();
    }

    let Some(shop) = find_shop(ctx, &session.shop_id) else {
        return vec![Effect::message(message_levels::ERROR, "No shop is open.")];
    };
    if !shop.buys_items {
        return vec![Effect::message(message_levels::ERROR, format!("{} doesn't buy items.", shop.name))];
    }

    let slot = state.player.inventory.iter().find(|s| s.item.id == item_id);
    let Some(slot) = slot.filter(|slot| slot.quantity >= quantity) else {
        return vec![Effect::message(message_levels::ERROR, "You don't have that many.")];
    };

    // floor(value × multiplier), the multiplier in thousandths.
    let unit_price =
        (slot.item.value.saturating_mul(i64::from(shop.sell_price_multiplier))).div_euclid(i64::from(units::MILLI_ONE));
    let total = unit_price.saturating_mul(i64::from(quantity));
    let item_name = slot.item.name.clone();

    state.player.inventory = inventory::remove_item(&state.player.inventory, item_id, quantity);
    state.player.money = state.player.money.saturating_add(total);
    vec![Effect::message(message_levels::SUCCESS, format!("Sold {quantity}x {item_name} for ${total}"))]
}

pub fn handle_repair_tool(ctx: &EngineContext, state: &mut GameState, item_id: &str) -> Effects {
    let Some(session) = &state.shop else {
        return vec![Effect::message(message_levels::ERROR, "No shop is open.")];
    };
    let Some(shop) = find_shop(ctx, &session.shop_id) else {
        return vec![Effect::message(message_levels::ERROR, "No shop is open.")];
    };
    if !shop.repairs_tools {
        return vec![Effect::message(message_levels::ERROR, format!("{} doesn't repair tools.", shop.name))];
    }

    let slot = state.player.inventory.iter().find(|s| s.item.id == item_id);
    let Some((slot, durability, max_durability)) =
        slot.and_then(|slot| Some((slot, slot.item.durability?, slot.item.max_durability?)))
    else {
        return vec![Effect::message(message_levels::ERROR, "That can't be repaired.")];
    };
    let missing = i64::from(max_durability) - i64::from(durability);
    if missing <= 0 {
        return vec![Effect::message(message_levels::INFO, format!("{} is in perfect shape.", slot.item.name))];
    }

    // ceil(missing × cost per point), the cost per point in thousandths of a gold.
    let cost =
        units::div_ceil(missing.saturating_mul(i64::from(shop.repair_cost_per_point)), i64::from(units::MILLI_ONE));
    if state.player.money < cost {
        return vec![Effect::message(message_levels::ERROR, format!("Repair costs ${cost} — not enough money!"))];
    }

    let repaired = crate::schema::Item { durability: Some(max_durability), ..slot.item.clone() };
    let item_name = slot.item.name.clone();
    state.player.inventory = inventory::replace_item(&state.player.inventory, item_id, &repaired);
    state.player.money = state.player.money.saturating_sub(cost);
    vec![Effect::message(message_levels::SUCCESS, format!("Repaired {item_name} for ${cost}"))]
}
