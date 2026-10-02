//! Shops and money (port of `Economy.cs` / economy.ts).
//!
//! Economy & shops (M2). Shops are content; buying and selling are explicit commands
//! (harvesting no longer auto-sells). Any NPC can be a merchant via a dialogue option with
//! `openShopId`.

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::inventory;
use crate::messages;
use crate::quests;
use crate::schema::{crop_qualities, DialogueOption, GameState, Item, ShopDefinition, ShopSession};
use crate::world::world_movement;
use crate::{content_builtin, units};

/// The shop definition `shop_id`.
pub fn find_shop<'a>(ctx: &'a EngineContext, shop_id: &str) -> Option<&'a ShopDefinition> {
    ctx.shop(shop_id)
}

/// The `openShop` command: open `shop_id` (closing any dialogue).
pub fn handle_open_shop(ctx: &EngineContext, state: &mut GameState, shop_id: &str) -> Effects {
    let shop = find_shop(ctx, shop_id);
    if shop.is_none() {
        return vec![Effect::say(message_levels::ERROR, &messages::SHOP_MISSING)];
    }
    state.shop = Some(ShopSession { shop_id: shop_id.to_owned() });
    state.dialogue = None;
    Vec::new()
}

/// The `openShop` command under [`crate::CommandRules::Player`]: the player must face an NPC
/// whose dialogue opens that shop (`None` when they do). Dialogue options open shops on their
/// own; the command is for hosts and scripts that skip the conversation.
pub fn open_shop_refusal(ctx: &EngineContext, state: &GameState, shop_id: &str) -> Option<Effects> {
    let facing = world_movement::facing_target(state);
    let opens_shop = |options: &[DialogueOption]| options.iter().any(|o| o.open_shop_id.as_deref() == Some(shop_id));
    let is_merchant = |npc_id: &str| {
        ctx.content
            .npcs
            .iter()
            .filter(|def| def.id == npc_id)
            .any(|def| def.dialogue.iter().any(|d| opens_shop(&d.options)))
            || ctx.content.dialogues.iter().any(|d| d.npc_id == npc_id && opens_shop(&d.options))
    };
    let merchant_faced = state.npcs.iter().any(|(npc_id, npc)| {
        npc.scene_id == state.player.scene_id
            && npc.x == units::tiles(facing.x)
            && npc.y == units::tiles(facing.y)
            && is_merchant(npc_id)
    });
    if merchant_faced {
        None
    } else {
        Some(vec![Effect::say(message_levels::INFO, &messages::TALK_TO_SHOPKEEPER)])
    }
}

/// The `closeShop` command.
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

/// What `shop` pays for one normal-quality `item`: `floor(value × sellPriceMultiplier)` (the
/// multiplier in thousandths).
pub fn sell_unit_price(item: &Item, shop: &ShopDefinition) -> i64 {
    sell_unit_price_with_quality(item, None, shop)
}

/// The value of one `item` at a crop `quality` (`None`: normal): `floor(value × quality
/// multiplier)`, the same multipliers the harvest message uses.
pub fn quality_value(item: &Item, quality: Option<&str>) -> i64 {
    let multiplier = quality.and_then(content_builtin::quality_multiplier).unwrap_or(units::MILLI_ONE);
    if multiplier == units::MILLI_ONE {
        return item.value;
    }
    item.value.saturating_mul(i64::from(multiplier)).div_euclid(i64::from(units::MILLI_ONE))
}

/// What `shop` pays for one `item` at a crop `quality`: `floor(quality value ×
/// sellPriceMultiplier)`.
pub fn sell_unit_price_with_quality(item: &Item, quality: Option<&str>, shop: &ShopDefinition) -> i64 {
    quality_value(item, quality)
        .saturating_mul(i64::from(shop.sell_price_multiplier))
        .div_euclid(i64::from(units::MILLI_ONE))
}

/// What `shop` charges to restore `missing` durability points: `ceil(missing ×
/// repairCostPerPoint)` (the cost per point in thousandths of a gold).
pub fn repair_cost(missing: i64, shop: &ShopDefinition) -> i64 {
    units::div_ceil(missing.saturating_mul(i64::from(shop.repair_cost_per_point)), i64::from(units::MILLI_ONE))
}

/// The `buyItem` command: buy `quantity` of `item_id` from the open shop (stock, daily limits,
/// money and inventory space permitting).
pub fn handle_buy_item(ctx: &EngineContext, state: &mut GameState, item_id: &str, quantity: u32) -> Effects {
    let Some(session) = &state.shop else {
        return vec![Effect::say(message_levels::ERROR, &messages::NO_SHOP_OPEN)];
    };
    if quantity == 0 {
        return Vec::new();
    }

    let Some(shop) = find_shop(ctx, &session.shop_id) else {
        return vec![Effect::say(message_levels::ERROR, &messages::NO_SHOP_OPEN)];
    };

    let Some(entry) = shop.stock.iter().find(|stock_entry| stock_entry.item_id == item_id) else {
        return vec![Effect::say(message_levels::ERROR, &messages::NOT_SOLD_HERE)];
    };

    if let Some(seasons) = entry.seasons.as_ref().filter(|seasons| !seasons.is_empty()) {
        if !seasons.contains(&state.clock.season) {
            return vec![Effect::say(
                message_levels::ERROR,
                messages::NOT_AVAILABLE_IN.with_args(vec![messages::season_noun(&state.clock.season)]),
            )];
        }
    }

    if let Some(remaining) = remaining_daily_stock(state, &shop.id, item_id, entry.daily_limit) {
        if quantity > remaining {
            let text =
                if remaining == 0 { messages::SOLD_OUT.with(&[]) } else { messages::ONLY_LEFT.with(&[&remaining]) };
            return vec![Effect::say(message_levels::ERROR, text)];
        }
    }

    let Some(item) = ctx.item(item_id) else {
        return vec![Effect::say(message_levels::ERROR, &messages::UNKNOWN_ITEM)];
    };

    let price = entry.price.unwrap_or(item.value);
    let total = price.saturating_mul(i64::from(quantity));
    if state.player.money < total {
        return vec![Effect::say(message_levels::ERROR, &messages::NOT_ENOUGH_MONEY)];
    }

    let result = inventory::add_item(&state.player.inventory, item, quantity, state.player.max_inventory_size, None);
    if !result.added {
        return vec![Effect::say(message_levels::ERROR, &messages::INVENTORY_FULL)];
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
        vec![Effect::say(message_levels::SUCCESS, messages::BOUGHT.with(&[&quantity, &item.name, &total]))];
    effects.extend(quest_effects);
    effects
}

/// Sell `quantity` units of `item_id`, drawn from every slot holding them: at one crop `quality`
/// (`normal` included), or, without one, at any quality, lowest first, each unit at its own
/// quality's price.
pub fn handle_sell_item(
    ctx: &EngineContext,
    state: &mut GameState,
    item_id: &str,
    quantity: u32,
    quality: Option<&str>,
) -> Effects {
    let Some(session) = &state.shop else {
        return vec![Effect::say(message_levels::ERROR, &messages::NO_SHOP_OPEN)];
    };
    if quantity == 0 {
        return Vec::new();
    }

    let Some(shop) = find_shop(ctx, &session.shop_id) else {
        return vec![Effect::say(message_levels::ERROR, &messages::NO_SHOP_OPEN)];
    };
    if !shop.buys_items {
        return vec![Effect::say(message_levels::ERROR, messages::SHOP_DOESNT_BUY.with(&[&shop.name]))];
    }

    // Items routinely span several slots (stack caps, qualities), so count across all of them.
    let tiers: Vec<Option<String>> = match quality {
        Some(quality) => vec![inventory::slot_quality(Some(quality))],
        None => crop_qualities::ALL.iter().map(|tier| inventory::slot_quality(Some(tier))).collect(),
    };
    let held: u64 = tiers
        .iter()
        .map(|tier| inventory::count_item_with_quality(&state.player.inventory, item_id, tier.as_deref()))
        .sum();
    let first = state.player.inventory.iter().find(|s| s.item.id == item_id);
    let Some(first) = first.filter(|_| held >= u64::from(quantity)) else {
        return vec![Effect::say(message_levels::ERROR, &messages::NOT_THAT_MANY)];
    };
    let item_name = match tiers.as_slice() {
        [Some(quality)] => format!("{} ({quality})", first.item.name),
        _ => first.item.name.clone(),
    };

    let mut inventory = state.player.inventory.clone();
    let mut remaining = quantity;
    let mut total: i64 = 0;
    for tier in &tiers {
        let have = inventory::count_item_with_quality(&inventory, item_id, tier.as_deref());
        let take = u32::try_from(have).unwrap_or(u32::MAX).min(remaining);
        let Some(slot) = inventory.iter().find(|s| s.item.id == item_id && s.quality == *tier).filter(|_| take > 0)
        else {
            continue;
        };
        let unit_price = sell_unit_price_with_quality(&slot.item, tier.as_deref(), shop);
        total = total.saturating_add(unit_price.saturating_mul(i64::from(take)));
        inventory = inventory::remove_item_with_quality(&inventory, item_id, tier.as_deref(), take);
        remaining -= take;
    }

    state.player.inventory = inventory;
    state.player.money = state.player.money.saturating_add(total);
    vec![Effect::say(message_levels::SUCCESS, messages::SOLD.with(&[&quantity, &item_name, &total]))]
}

/// The `repairTool` command: repair the held tool `item_id` at the open shop, if it repairs tools.
pub fn handle_repair_tool(ctx: &EngineContext, state: &mut GameState, item_id: &str) -> Effects {
    let Some(session) = &state.shop else {
        return vec![Effect::say(message_levels::ERROR, &messages::NO_SHOP_OPEN)];
    };
    let Some(shop) = find_shop(ctx, &session.shop_id) else {
        return vec![Effect::say(message_levels::ERROR, &messages::NO_SHOP_OPEN)];
    };
    if !shop.repairs_tools {
        return vec![Effect::say(message_levels::ERROR, messages::SHOP_DOESNT_REPAIR.with(&[&shop.name]))];
    }

    let slot = state.player.inventory.iter().find(|s| s.item.id == item_id);
    let Some((slot, durability, max_durability)) =
        slot.and_then(|slot| Some((slot, slot.item.durability?, slot.item.max_durability?)))
    else {
        return vec![Effect::say(message_levels::ERROR, &messages::CANT_REPAIR)];
    };
    let missing = i64::from(max_durability) - i64::from(durability);
    if missing <= 0 {
        return vec![Effect::say(message_levels::INFO, messages::PERFECT_SHAPE.with(&[&slot.item.name]))];
    }

    let cost = repair_cost(missing, shop);
    if state.player.money < cost {
        return vec![Effect::say(message_levels::ERROR, messages::REPAIR_TOO_EXPENSIVE.with(&[&cost]))];
    }

    let repaired = crate::schema::Item { durability: Some(max_durability), ..slot.item.clone() };
    let item_name = slot.item.name.clone();
    state.player.inventory = inventory::replace_item(&state.player.inventory, item_id, &repaired);
    state.player.money = state.player.money.saturating_sub(cost);
    vec![Effect::say(message_levels::SUCCESS, messages::REPAIRED.with(&[&item_name, &cost]))]
}
