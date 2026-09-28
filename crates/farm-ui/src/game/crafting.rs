//! Crafting (web CraftingDialog, C# `PlayOverlays.Crafting`): load recipes into the machine the
//! player faces, hand recipes by category with the engine's availability and blocked reasons,
//! and machine placement on the faced tile.

use super::{held, item_name, right_aligned, row, GameAction, GameView};
use crate::format::num;
use crate::icons::Icon;
use crate::layout::{Align, RectExt};
use crate::ui::{Ui, WidgetId};
use crate::widgets::{Button, ModalSpec};
use farm_render::{css_color, FontId, Rect, TextAlign};
use farm_sim::crafting::craft_block_reasons;
use farm_sim::schema::RecipeDefinition;
use farm_sim::Command;

const ROW_GAP: f32 = 8.0;

fn ingredient_line(view: &GameView<'_>, recipe: &RecipeDefinition) -> String {
    recipe
        .inputs
        .iter()
        .map(|input| {
            format!(
                "{}x {} ({})",
                num(input.quantity),
                item_name(view.content, &input.item_id),
                num(held(view.state, &input.item_id))
            )
        })
        .collect::<Vec<_>>()
        .join(", ")
}

pub(crate) fn draw(ui: &mut Ui, view: &GameView<'_>, actions: &mut Vec<GameAction>) {
    let colors = ui.theme().colors;
    let content = view.content;
    let state = view.state;
    let facing_machine = view.overlay.facing_tile(state).and_then(|tile| tile.machine.as_ref());
    let facing_type =
        facing_machine.and_then(|machine| content.machine_types.iter().find(|kind| kind.id == machine.type_id));
    let subtitle = match facing_type {
        Some(kind) => format!("Facing: {}", kind.name),
        None => "Hand crafting & machine placement".to_owned(),
    };
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("crafting"),
        icon: Icon::Hammer,
        title: "Crafting",
        subtitle: Some(&subtitle),
        width: 560.0,
        max_height: 620.0,
        footer: 0.0,
        backdrop: true,
    });
    if modal.close {
        actions.push(GameAction::ClosePanel);
    }
    let area = modal.body;
    let mut y = modal.top;
    let small = ui.button_height(12.5);
    let line = |ui: &Ui, size: f32| ui.line_height(size);

    // ── Load into the faced machine ──
    if let Some(machine) = facing_machine {
        let name = facing_type.map_or("machine", |kind| kind.name.as_str());
        y += ui.section(area, y, &format!("Load into {name}"), None);
        let recipes: Vec<_> = content
            .recipes
            .iter()
            .filter(|recipe| recipe.machine_type_id.as_deref() == Some(machine.type_id.as_str()))
            .collect();
        if machine.processing.is_some() {
            let rect = Rect::new(area.x, y, area.width, line(ui, 12.5));
            ui.label(rect, "Working\u{2026} come back later.", 12.5, FontId::Regular, colors.muted, Align::Start);
            y += rect.height + ROW_GAP;
        } else if recipes.is_empty() {
            let rect = Rect::new(area.x, y, area.width, line(ui, 12.5));
            ui.label(rect, "No recipes for this machine.", 12.5, FontId::Regular, colors.muted, Align::Start);
            y += rect.height + ROW_GAP;
        } else {
            for recipe in recipes {
                let button = Button::new("Load").primary().size(12.5).enabled(view.overlay.ingredients(&recipe.id));
                let width = ui.button_width(&button);
                let detail =
                    format!("{} \u{00b7} {} min", ingredient_line(view, recipe), num(recipe.processing_minutes));
                let text_width = area.width - 20.0 - width - 10.0;
                let detail_height = ui.paragraph_height(&detail, 12.5, FontId::Regular, text_width);
                let height = (line(ui, 14.0) + detail_height + 16.0).max(small + 16.0);
                let (content_rect, buttons) = row(ui, area, y, height, width);
                ui.label(
                    Rect::new(content_rect.x, content_rect.y, content_rect.width, line(ui, 14.0)),
                    &recipe.name,
                    14.0,
                    FontId::Bold,
                    colors.text,
                    Align::Start,
                );
                ui.paragraph(
                    content_rect.x,
                    content_rect.y + line(ui, 14.0),
                    content_rect.width,
                    &detail,
                    12.5,
                    FontId::Regular,
                    colors.muted,
                    TextAlign::Left,
                );
                let rect = right_aligned(buttons, &[width], small, 6.0)[0];
                if ui.button(WidgetId::new("craft-load").with(&recipe.id), rect, button) {
                    actions.push(GameAction::Command(Command::MachineLoad { recipe_id: recipe.id.clone() }));
                }
                y += height + ROW_GAP;
            }
        }
        y += 6.0;
    }

    // ── Hand crafting by category ──
    y += ui.section(area, y, "Hand crafting", None);
    let hand: Vec<_> =
        content.recipes.iter().filter(|recipe| recipe.machine_type_id.as_deref().is_none_or(str::is_empty)).collect();
    if hand.is_empty() {
        let rect = Rect::new(area.x, y, area.width, line(ui, 12.5));
        ui.label(rect, "No hand recipes known.", 12.5, FontId::Regular, colors.muted, Align::Start);
        y += rect.height + ROW_GAP;
    }
    let mut categories: Vec<&str> = hand.iter().map(|recipe| recipe.category.as_str()).collect();
    categories.sort();
    categories.dedup();
    for category in categories {
        y += ui.section(area, y + 2.0, category, Some(colors.primary)) + 2.0;
        for recipe in hand.iter().filter(|recipe| recipe.category == category) {
            let status = view.overlay.status(&recipe.id);
            let button = Button::new("Craft").primary().size(12.5).enabled(status.craftable);
            let width = ui.button_width(&button);
            let text_width = area.width - 20.0 - width - 10.0;
            let ingredients = ingredient_line(view, recipe);
            let ingredients_height = ui.paragraph_height(&ingredients, 12.5, FontId::Regular, text_width);
            let blocked = (!status.craftable).then_some(status.message.as_deref()).flatten();
            let blocked_height =
                blocked.map_or(0.0, |message| ui.paragraph_height(message, 12.5, FontId::Regular, text_width));
            let height = (line(ui, 14.0) + ingredients_height + blocked_height + 16.0).max(small + 16.0);
            let (content_rect, buttons) = row(ui, area, y, height, width);
            let mut text_y = content_rect.y;
            ui.label(
                Rect::new(content_rect.x, text_y, content_rect.width, line(ui, 14.0)),
                &recipe.name,
                14.0,
                FontId::Bold,
                colors.text,
                Align::Start,
            );
            text_y += line(ui, 14.0);
            text_y += ui.paragraph(
                content_rect.x,
                text_y,
                content_rect.width,
                &ingredients,
                12.5,
                FontId::Regular,
                colors.muted,
                TextAlign::Left,
            );
            if let Some(message) = blocked {
                let color = if status.reason.as_deref() == Some(craft_block_reasons::STATION) {
                    colors.error
                } else {
                    colors.muted
                };
                ui.paragraph(
                    content_rect.x,
                    text_y,
                    content_rect.width,
                    message,
                    12.5,
                    FontId::Regular,
                    color,
                    TextAlign::Left,
                );
            }
            let rect = right_aligned(buttons, &[width], small, 6.0)[0];
            if ui.button(WidgetId::new("craft").with(&recipe.id), rect, button) {
                actions.push(GameAction::Command(Command::Craft { recipe_id: recipe.id.clone() }));
            }
            y += height + ROW_GAP;
        }
    }

    // ── Machine placement ──
    let placeable: Vec<_> = content
        .machine_types
        .iter()
        .filter(|kind| {
            kind.item_id.as_deref().is_some_and(|item_id| {
                !item_id.is_empty() && state.player.inventory.iter().any(|slot| slot.item.id == item_id)
            })
        })
        .collect();
    if !placeable.is_empty() {
        y += 6.0 + ui.section(area, y + 6.0, "Place machine (on the tile you face)", None);
        for kind in placeable {
            let button = Button::new("Place").primary().size(12.5);
            let width = ui.button_width(&button);
            let height = (line(ui, 14.0) + line(ui, 12.5) + 16.0).max(small + 16.0);
            let (mut content_rect, buttons) = row(ui, area, y, height, width);
            let swatch = content_rect.cut_left(18.0).centered(18.0, 18.0);
            ui.list_mut().fill_round_rect(swatch, 4.0, css_color::parse(Some(&kind.color)));
            ui.list_mut().stroke_round_rect(swatch.inset(0.5), 4.0, colors.muted, 1.0);
            content_rect.cut_left(10.0);
            let top = content_rect.y + (content_rect.height - line(ui, 14.0) - line(ui, 12.5)) / 2.0;
            ui.label(
                Rect::new(content_rect.x, top, content_rect.width, line(ui, 14.0)),
                &kind.name,
                14.0,
                FontId::Bold,
                colors.text,
                Align::Start,
            );
            ui.label(
                Rect::new(content_rect.x, top + line(ui, 14.0), content_rect.width, line(ui, 12.5)),
                &kind.description,
                12.5,
                FontId::Regular,
                colors.muted,
                Align::Start,
            );
            let rect = right_aligned(buttons, &[width], small, 6.0)[0];
            if ui.button(WidgetId::new("craft-place").with(&kind.id), rect, button) {
                actions.push(GameAction::Command(Command::PlaceMachine { machine_type_id: kind.id.clone() }));
            }
            y += height + ROW_GAP;
        }
    }
    ui.end_modal(y);
}
