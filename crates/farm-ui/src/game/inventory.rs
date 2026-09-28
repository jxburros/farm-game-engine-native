//! The inventory (web PlayerInventory, C# `PlayOverlays.Inventory`): every slot with its art,
//! quantity, value and durability; Use runs the item's bound action, Gift gives it to the NPC the
//! player faces; the footer totals the inventory's value.

use super::{draw_item_art, right_aligned, GameAction, GameView};
use crate::format::{money, num};
use crate::icons::Icon;
use crate::layout::{Align, RectExt};
use crate::theme::fade;
use crate::ui::{Ui, WidgetId};
use crate::widgets::{Button, ModalSpec};
use farm_render::{FontId, ImageStore, Rect};
use farm_sim::schema::item_types;
use farm_sim::Command;

pub(crate) fn draw(ui: &mut Ui, view: &GameView<'_>, images: &mut ImageStore, actions: &mut Vec<GameAction>) {
    let colors = ui.theme().colors;
    let player = &view.state.player;
    let subtitle = format!("{} / {} slots used", player.inventory.len(), num(player.max_inventory_size));
    let footer_height = ui.button_height(13.0) + 20.0;
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("inventory"),
        icon: Icon::Package,
        title: "Inventory",
        subtitle: Some(&subtitle),
        width: 680.0,
        max_height: 640.0,
        footer: footer_height,
        backdrop: true,
    });
    if modal.close {
        actions.push(GameAction::ClosePanel);
    }
    let area = modal.body;
    let mut y = modal.top;
    if player.inventory.is_empty() {
        y += ui.empty_state(area, y, Icon::Package, "Your inventory is empty", "Explore the world to find items!");
    } else {
        let columns = if area.width >= 560.0 { 2 } else { 1 };
        let gap = 8.0;
        let column_width = (area.width - gap * (columns - 1) as f32) / columns as f32;
        let small = ui.button_height(12.0);
        let height = (ui.line_height(14.0) + ui.line_height(12.5) + ui.line_height(12.0) + 18.0).max(62.0);
        for (index, slot) in player.inventory.iter().enumerate() {
            let item = &slot.item;
            let column = index % columns;
            let rect = Rect::new(area.x + column as f32 * (column_width + gap), y, column_width, height);
            ui.row_background(rect);
            let mut inner = rect.inset_xy(10.0, 8.0);

            // Buttons: Use (bound action), Gift (to the faced NPC; not tools).
            let use_button = Button::new("Use").size(12.0);
            let gift_button = Button::new("Gift").size(12.0);
            let can_use = item.use_action_id.as_deref().is_some_and(|id| !id.is_empty());
            let can_gift = item.r#type != item_types::TOOL;
            let mut widths = Vec::new();
            if can_use {
                widths.push(ui.button_width(&use_button));
            }
            if can_gift {
                widths.push(ui.button_width(&gift_button));
            }
            let buttons_width = widths.iter().sum::<f32>() + 4.0 * widths.len().saturating_sub(1) as f32;
            let buttons = inner.cut_right(buttons_width);
            let rects = right_aligned(buttons, &widths, small, 4.0);
            let id = WidgetId::new("inventory").with(index).with(&item.id);
            let mut next = 0;
            if can_use {
                if ui.button(id.with("use"), rects[next], use_button) {
                    actions.push(GameAction::ClosePanel);
                    actions.push(GameAction::Command(Command::UseItem { item_id: item.id.clone() }));
                }
                next += 1;
            }
            if can_gift && ui.button(id.with("gift"), rects[next], gift_button) {
                actions.push(GameAction::Command(Command::GiveGift { item_id: item.id.clone() }));
            }

            // Art tile.
            let tile = inner.cut_left(44.0).centered(44.0, 44.0);
            ui.list_mut().fill_round_rect(tile, 8.0, fade(colors.accent, 0.18));
            ui.list_mut().stroke_round_rect(tile.inset(0.5), 8.0, colors.row_border, 1.0);
            draw_item_art(ui, images, &view.art, view.content, item, tile.centered(32.0, 32.0));
            inner.cut_left(10.0);
            inner.cut_right(6.0);

            // Name, description, meta.
            let (title, description, meta) = (ui.line_height(14.0), ui.line_height(12.5), ui.line_height(12.0));
            let top = inner.y + (inner.height - title - description - meta) / 2.0;
            ui.label(
                Rect::new(inner.x, top, inner.width, title),
                &item.name,
                14.0,
                FontId::Bold,
                colors.text,
                Align::Start,
            );
            ui.label(
                Rect::new(inner.x, top + title, inner.width, description),
                &item.description,
                12.5,
                FontId::Regular,
                colors.muted,
                Align::Start,
            );
            let mut meta_row = Rect::new(inner.x, top + title + description, inner.width, meta);
            let cy = meta_row.y + meta_row.height / 2.0;
            if item.stackable {
                let width = ui.chip(
                    meta_row.x,
                    cy,
                    &format!("\u{00d7}{}", num(slot.quantity)),
                    11.0,
                    fade(colors.primary, 0.18),
                    colors.primary,
                );
                meta_row.cut_left(width + 8.0);
            }
            let mut meta_text = money(item.value);
            if item.r#type == item_types::TOOL {
                if let (Some(durability), Some(max)) = (item.durability, item.max_durability) {
                    meta_text.push_str(&format!("  {}/{}", num(durability), num(max)));
                }
            }
            ui.label(meta_row, &meta_text, 12.0, FontId::Regular, colors.muted, Align::Start);
            if column == columns - 1 || index == player.inventory.len() - 1 {
                y += height + gap;
            }
        }
    }
    ui.end_modal_body(y);
    if let Some(footer) = modal.footer {
        let total: f64 = player.inventory.iter().map(|slot| slot.item.value * slot.quantity).sum();
        let mut footer = footer;
        let close = Button::new("Close").primary();
        let width = ui.button_width(&close);
        let close_rect = footer.cut_right(width).centered(width, ui.button_height(13.0));
        ui.label(footer, &format!("Total value: {}", money(total)), 13.5, FontId::Regular, colors.muted, Align::Start);
        if ui.button(WidgetId::new("inventory-close"), close_rect, close) {
            actions.push(GameAction::ClosePanel);
        }
    }
    ui.close_modal();
}
