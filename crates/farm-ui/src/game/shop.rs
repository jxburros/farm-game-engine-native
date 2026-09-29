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
use farm_sim::schema::item_types;
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
            let sellable: Vec<_> =
                player.inventory.iter().filter(|slot| slot.item.r#type != item_types::QUEST).collect();
            if sellable.is_empty() {
                y += ui.empty_state(
                    area,
                    y,
                    Icon::Package,
                    lang.tr("shop.nothingToSell"),
                    lang.tr("shop.nothingToSellDetail"),
                );
            }
            for slot in sellable {
                let unit = (slot.item.value * shop.sell_price_multiplier).floor();
                let sell_one = Button::new(lang.tr("shop.sellOne")).primary().size(12.5);
                let sell_all = Button::new(lang.tr("shop.sellAll")).size(12.5);
                let mut widths = vec![ui.button_width(&sell_one)];
                if slot.quantity > 1.0 {
                    widths.push(ui.button_width(&sell_all));
                }
                let actions_width = widths.iter().sum::<f32>() + 6.0 * (widths.len() - 1) as f32;
                let (content, buttons) = row(ui, area, y, row_height, actions_width);
                let quantity = slot.item.stackable.then(|| format!("\u{00d7}{}", num(slot.quantity)));
                let each = lang.format("shop.each", &[&money(unit)]);
                two_lines(ui, content, &slot.item.name, quantity.as_deref(), &each, false);
                let rects = right_aligned(buttons, &widths, small, 6.0);
                let id = WidgetId::new("shop-sell").with(&slot.item.id);
                if ui.button(id, rects[0], sell_one) {
                    actions
                        .push(GameAction::Command(Command::SellItem { item_id: slot.item.id.clone(), quantity: 1.0 }));
                }
                if slot.quantity > 1.0 && ui.button(id.with("all"), rects[1], sell_all) {
                    actions.push(GameAction::Command(Command::SellItem {
                        item_id: slot.item.id.clone(),
                        quantity: slot.quantity,
                    }));
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
                let (durability, max) = (slot.item.durability.unwrap_or(0.0), slot.item.max_durability.unwrap_or(0.0));
                let cost = ((max - durability) * shop.repair_cost_per_point).ceil();
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
                    detail.push_str(&lang.format("shop.leftToday", &[&num(remaining.max(0.0)), &num(limit)]));
                }
                let price_text = money(price);
                let price_width = ui.measure(&price_text, 14.0, farm_render::FontId::Bold).ceil();
                let buy = Button::new(lang.tr("shop.buy"))
                    .primary()
                    .size(12.5)
                    .enabled(player.money >= price && remaining >= 1.0);
                let five = Button::new("\u{00d7}5").size(12.5).enabled(player.money >= price * 5.0 && remaining >= 5.0);
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
                    actions
                        .push(GameAction::Command(Command::BuyItem { item_id: entry.item_id.clone(), quantity: 1.0 }));
                }
                if item.stackable && ui.button(id.with("5"), rects[2], five) {
                    actions
                        .push(GameAction::Command(Command::BuyItem { item_id: entry.item_id.clone(), quantity: 5.0 }));
                }
                y += row_height + ROW_GAP;
            }
        }
    }
    ui.end_modal(y);
}
