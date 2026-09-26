//! Shops and money (port of `Economy.cs` / economy.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{GameState, ShopDefinition};

pub fn find_shop<'a>(ctx: &'a EngineContext, shop_id: &str) -> Option<&'a ShopDefinition> {
    let _ = (ctx, shop_id);
    todo!("port Economy.FindShop")
}

pub fn handle_open_shop(ctx: &EngineContext, state: &mut GameState, shop_id: &str) -> Effects {
    let _ = (ctx, state, shop_id);
    todo!("port Economy.HandleOpenShop")
}

pub fn handle_close_shop(state: &mut GameState) -> Effects {
    let _ = state;
    todo!("port Economy.HandleCloseShop")
}

pub fn remaining_daily_stock(state: &GameState, shop_id: &str, item_id: &str, daily_limit: Option<f64>) -> f64 {
    let _ = (state, shop_id, item_id, daily_limit);
    todo!("port Economy.RemainingDailyStock")
}

pub fn handle_buy_item(ctx: &EngineContext, state: &mut GameState, item_id: &str, quantity: f64) -> Effects {
    let _ = (ctx, state, item_id, quantity);
    todo!("port Economy.HandleBuyItem")
}

pub fn handle_sell_item(ctx: &EngineContext, state: &mut GameState, item_id: &str, quantity: f64) -> Effects {
    let _ = (ctx, state, item_id, quantity);
    todo!("port Economy.HandleSellItem")
}

pub fn handle_repair_tool(ctx: &EngineContext, state: &mut GameState, item_id: &str) -> Effects {
    let _ = (ctx, state, item_id);
    todo!("port Economy.HandleRepairTool")
}
