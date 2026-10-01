//! The shop (web ShopDialog, C# `PlayOverlays.Shop`): buy the season's stock within daily limits,
//! sell inventory, repair tools. Buttons are enabled from the engine's own numbers (stock left
//! from `farm_sim::overlay`); the purchase itself is an engine command that checks again.

use super::{right_aligned, row, GameAction, GameView, ShopTab};
use crate::format::{money, num};
use crate::icons::Icon;
use crate::layout::{Align, RectExt};
use crate::ui::{Ui, WidgetId};
use crate::widgets::{Button, ButtonKind, ModalSpec};
use farm_render::{FontId, Rect};
use farm_sim::economy;
use farm_sim::schema::{crop_qualities, item_types, InventorySlot};
use farm_sim::{tools, Command};

const ROW_GAP: f32 = 8.0;

/// The tab's stable name (widget ids) and its label key.
fn tab_names(tab: ShopTab) -> (&'static str, &'static str) {
    match tab {
        ShopTab::Buy => ("Buy", "shop.buy"),
        ShopTab::Sell => ("Sell", "shop.sell"),
        ShopTab::Repair => ("Repair", "shop.repair"),
    }
}

/// Title and detail lines in a row's content area.
fn two_lines(ui: &mut Ui, area: Rect, title: &str, trailing: Option<&str>, detail: &str, detail_error: bool) {
    let colors = ui.theme().colors;
    let title_height = ui.line_height(14.0);
    let detail_height = ui.line_height(12.5);
    let top = area.y + (area.height - title_height - detail_height) / 2.0;
    let mut title_rect = Rect::new(area.x, top, area.width, title_height);
    let title_width = ui.label(title_rect, title, 14.0, FontId::Bold, colors.text, Align::Start);
    if let Some(trailing) = trailing {
        title_rect.cut_left(title_width + 6.0);
        ui.label(title_rect, trailing, 12.5, FontId::Regular, colors.muted, Align::Start);
    }
    let detail_rect = Rect::new(area.x, top + title_height, area.width, detail_height);
    ui.label(
        detail_rect,
        detail,
        12.5,
        FontId::Regular,
        if detail_error { colors.error } else { colors.muted },
        Align::Start,
    );
}

pub(crate) fn draw(ui: &mut Ui, view: &GameView<'_>, tab: &mut ShopTab, actions: &mut Vec<GameAction>) {
    if view.state.shop.is_none() {
        return;
    }
    let Some(shop) = view.overlay.shop else { return };
    let state = view.state;
    let player = &state.player;
    let colors = ui.theme().colors;
    let lang = ui.lang();

    let mut tabs = vec![ShopTab::Buy];
    if shop.buys_items {
        tabs.push(ShopTab::Sell);
    }
    if shop.repairs_tools {
        tabs.push(ShopTab::Repair);
    }
    if !tabs.contains(tab) {
        *tab = ShopTab::Buy;
    }
    if view.keys_active && ui.tab_delta() != 0 {
        let current = tabs.iter().position(|t| t == tab).unwrap_or(0) as i32;
        let next = (current + ui.tab_delta()).rem_euclid(tabs.len() as i32) as usize;
        *tab = tabs[next];
    }

    let subtitle = lang.format("shop.yourMoney", &[&money(player.money)]);
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("shop"),
        icon: Icon::Store,
        title: &shop.name,
        subtitle: Some(&subtitle),
        width: 600.0,
        max_height: 620.0,
        footer: 0.0,
        backdrop: true,
    });
    if modal.close {
        actions.push(GameAction::Command(Command::CloseShop));
    }
    let area = modal.body;
    let mut y = modal.top;

    // Tabs.
    let tab_height = ui.button_height(13.0);
    let mut x = area.x;
    for candidate in &tabs {
        let (name, label) = tab_names(*candidate);
        let button = Button::new(lang.tr(label)).kind(ButtonKind::Tab { selected: candidate == tab });
        let width = ui.button_width(&button).max(72.0);
        let id = WidgetId::new("shop-tab").with(name);
        if ui.button(id, Rect::new(x, y, width, tab_height), button) {
            *tab = *candidate;
        }
        x += width + 6.0;
    }
    y += tab_height + 12.0;

    let small = ui.button_height(12.5);
    let row_height = (ui.line_height(14.0) + ui.line_height(12.5) + 16.0).max(small + 16.0);
    match *tab {
        ShopTab::Sell => {
            // One row per item and quality, totalled across the slots holding it.
            let mut sellable: Vec<(&InventorySlot, u32)> = Vec::new();
            for slot in player.inventory.iter().filter(|slot| slot.item.r#type != item_types::QUEST) {
                match sellable
                    .iter_mut()
                    .find(|(first, _)| first.item.id == slot.item.id && first.quality == slot.quality)
                {
                    Some((_, total)) => *total = total.saturating_add(slot.quantity),
                    None => sellable.push((slot, slot.quantity)),
                }
            }
            if sellable.is_empty() {
                y += ui.empty_state(
                    area,
                    y,
                    Icon::Package,
                    lang.tr("shop.nothingToSell"),
                    lang.tr("shop.nothingToSellDetail"),
                );
            }
            for (slot, held) in sellable {
                let unit = economy::sell_unit_price_with_quality(&slot.item, slot.quality.as_deref(), shop);
                let sell_one = Button::new(lang.tr("shop.sellOne")).primary().size(12.5);
                let sell_all = Button::new(lang.tr("shop.sellAll")).size(12.5);
                let mut widths = vec![ui.button_width(&sell_one)];
                if held > 1 {
                    widths.push(ui.button_width(&sell_all));
                }
                let actions_width = widths.iter().sum::<f32>() + 6.0 * (widths.len() - 1) as f32;
                let (content, buttons) = row(ui, area, y, row_height, actions_width);
                let quantity = slot.item.stackable.then(|| format!("\u{00d7}{}", num(held)));
                let each = lang.format("shop.each", &[&money(unit)]);
                let name = super::slot_name(&slot.item.name, slot.quality.as_deref());
                two_lines(ui, content, &name, quantity.as_deref(), &each, false);
                let rects = right_aligned(buttons, &widths, small, 6.0);
                let mut id = WidgetId::new("shop-sell").with(&slot.item.id);
                if let Some(quality) = &slot.quality {
                    id = id.with(quality);
                }
                // The row's own quality (normal included), never another quality's units.
                let quality = slot.quality.clone().unwrap_or_else(|| crop_qualities::NORMAL.to_owned());
                let sell = |quantity| {
                    GameAction::Command(Command::SellItem {
                        item_id: slot.item.id.clone(),
                        quantity,
                        quality: Some(quality.clone()),
                    })
                };
                if ui.button(id, rects[0], sell_one) {
                    actions.push(sell(1));
                }
                if held > 1 && ui.button(id.with("all"), rects[1], sell_all) {
                    actions.push(sell(held));
                }
                y += row_height + ROW_GAP;
            }
        }
        ShopTab::Repair => {
            let damaged: Vec<_> = player
                .inventory
                .iter()
                .filter(|slot| slot.item.r#type == item_types::TOOL)
                .filter(|slot| matches!((slot.item.durability, slot.item.max_durability), (Some(d), Some(m)) if d < m))
                .collect();
            if damaged.is_empty() {
                y += ui.empty_state(area, y, Icon::Hammer, lang.tr("shop.toolsFine"), lang.tr("shop.toolsFineDetail"));
            }
            for slot in damaged {
                let (durability, max) = (slot.item.durability.unwrap_or(0), slot.item.max_durability.unwrap_or(0));
                let cost = economy::repair_cost(i64::from(max) - i64::from(durability), shop);
                let broken = tools::is_tool_broken(&slot.item);
                let label = lang.format("shop.repairFor", &[&money(cost)]);
                let button = Button::new(&label).primary().size(12.5).enabled(player.money >= cost);
                let width = ui.button_width(&button);
                let (content, buttons) = row(ui, area, y, row_height, width);
                let mut detail = lang.format("shop.durability", &[&num(durability), &num(max)]);
                if broken {
                    detail.push_str(" \u{2014} ");
                    detail.push_str(lang.tr("shop.broken"));
                }
                two_lines(ui, content, &slot.item.name, None, &detail, broken);
                let rect = right_aligned(buttons, &[width], small, 6.0)[0];
                if ui.button(WidgetId::new("shop-repair").with(&slot.item.id), rect, button) {
                    actions.push(GameAction::Command(Command::RepairTool { item_id: slot.item.id.clone() }));
                }
                y += row_height + ROW_GAP;
            }
        }
        ShopTab::Buy => {
            let season = &state.clock.season;
            let stock: Vec<_> = shop
                .stock
                .iter()
                .filter_map(|entry| Some((entry, view.content.items.iter().find(|item| item.id == entry.item_id)?)))
                .filter(|(entry, _)| match &entry.seasons {
                    Some(seasons) if !seasons.is_empty() => seasons.contains(season),
                    _ => true,
                })
                .collect();
            if stock.is_empty() {
                y += ui.empty_state(area, y, Icon::Store, lang.tr("shop.noStock"), lang.tr("shop.noStockDetail"));
            }
            for (entry, item) in stock {
                let price = entry.price.unwrap_or(item.value);
                let remaining = view.overlay.remaining(&entry.item_id);
                let mut detail = item.description.clone();
                if let Some(limit) = entry.daily_limit {
                    if !detail.is_empty() {
                        detail.push_str(" \u{00b7} ");
                    }
                    let left = remaining.map_or_else(|| "Infinity".to_owned(), num);
                    detail.push_str(&lang.format("shop.leftToday", &[&left, &num(limit)]));
                }
                let price_text = money(price);
                let price_width = ui.measure(&price_text, 14.0, farm_render::FontId::Bold).ceil();
                let buy = Button::new(lang.tr("shop.buy"))
                    .primary()
                    .size(12.5)
                    .enabled(player.money >= price && remaining.is_none_or(|left| left >= 1));
                let five = Button::new("\u{00d7}5")
                    .size(12.5)
                    .enabled(player.money >= price.saturating_mul(5) && remaining.is_none_or(|left| left >= 5));
                let mut widths = vec![price_width, ui.button_width(&buy).max(52.0)];
                if item.stackable {
                    widths.push(ui.button_width(&five).max(40.0));
                }
                let actions_width = widths.iter().sum::<f32>() + 6.0 * (widths.len() - 1) as f32;
                let (content, buttons) = row(ui, area, y, row_height, actions_width);
                two_lines(ui, content, &item.name, None, &detail, false);
                let rects = right_aligned(buttons, &widths, small, 6.0);
                ui.label(rects[0], &price_text, 14.0, FontId::Bold, colors.text, Align::End);
                let id = WidgetId::new("shop-buy").with(&entry.item_id);
                if ui.button(id, rects[1], buy) {
                    actions.push(GameAction::Command(Command::BuyItem { item_id: entry.item_id.clone(), quantity: 1 }));
                }
                if item.stackable && ui.button(id.with("5"), rects[2], five) {
                    actions.push(GameAction::Command(Command::BuyItem { item_id: entry.item_id.clone(), quantity: 5 }));
                }
                y += row_height + ROW_GAP;
            }
        }
    }
    ui.end_modal(y);
}
