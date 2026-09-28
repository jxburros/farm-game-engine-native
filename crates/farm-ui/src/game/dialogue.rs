//! The dialogue box (web DialogueBox, C# `PlayOverlays.Dialogue`): a card at the bottom with the
//! NPC's portrait, name and text, and the *visible* options (friendship, item and flag gates are
//! the engine's; `chooseDialogueOption` indexes the same list). Keys 1–9 pick an option;
//! "Goodbye" closes a dialogue without options.

use super::{GameAction, GameView};
use crate::icons::{self, Icon};
use crate::layout::{Align, RectExt};
use crate::ui::{HitKind, Ui, WidgetId};
use crate::widgets::{Button, ButtonKind};
use farm_render::{FontId, Rect, TextAlign};
use farm_sim::Command;

const TEXT_SIZE: f32 = 15.0;
const OPTION_SIZE: f32 = 14.0;

pub(crate) fn draw(ui: &mut Ui, view: &GameView<'_>, actions: &mut Vec<GameAction>) {
    let Some(active) = view.state.dialogue.as_ref() else { return };
    let Some(dialogue) = view.overlay.dialogue else { return };
    let colors = ui.theme().colors;
    let npc_name = view
        .content
        .npcs
        .iter()
        .find(|npc| npc.id == active.npc_id)
        .map_or(active.npc_id.as_str(), |npc| npc.name.as_str());
    let options = &view.overlay.visible_dialogue_options;

    // Number keys pick options (1 = first visible option).
    if view.keys_active {
        for digit in ui.input().digits() {
            let index = usize::from(digit) - 1;
            if index < options.len() {
                actions.push(GameAction::Command(Command::ChooseDialogueOption { index: index as f64 }));
                return;
            }
        }
    }

    ui.push_layer();
    let screen = ui.screen();
    let dim = crate::theme::fade(colors.backdrop, 0.55);
    ui.tint_world(dim, dim);
    ui.blocker(screen);

    let width = 680.0f32.min(screen.width - 32.0);
    let text_x = 20.0 + 56.0 + 16.0;
    let text_width = width - text_x - 20.0;
    let name_height = ui.line_height(16.5);
    let text_height = ui.paragraph_height(&dialogue.text, TEXT_SIZE, FontId::Regular, text_width);
    let header_height = (name_height + 4.0 + text_height).max(56.0);
    let option_width = width - 40.0;
    let option_heights: Vec<f32> = if options.is_empty() {
        vec![ui.button_height(OPTION_SIZE) + 6.0]
    } else {
        options
            .iter()
            .enumerate()
            .map(|(index, option)| {
                let key_width = ui.keycap_width(&(index + 1).to_string()) + 10.0;
                let lines =
                    ui.paragraph_height(&option.text, OPTION_SIZE, FontId::Regular, option_width - 28.0 - key_width);
                (lines + 18.0).max(ui.button_height(OPTION_SIZE) + 6.0)
            })
            .collect()
    };
    let options_height: f32 = option_heights.iter().sum::<f32>() + 8.0 * (option_heights.len() - 1) as f32;
    let height = 20.0 + header_height + 18.0 + options_height + 20.0;
    let bottom_margin = 24.0 + (screen.height * 0.04).min(40.0);
    let card =
        Rect::new((screen.width - width) / 2.0, (screen.bottom() - bottom_margin - height).max(12.0), width, height);
    ui.panel(card, colors.panel, colors.panel_border, 12.0, true);
    ui.blocker(card);

    // Portrait and text.
    let portrait = Rect::new(card.x + 20.0, card.y + 20.0, 56.0, 56.0);
    ui.list_mut().fill_circle(portrait.x + 28.0, portrait.y + 28.0, 28.0, colors.secondary);
    icons::draw(
        ui.list_mut(),
        Icon::Person,
        portrait.centered(30.0, 30.0),
        farm_render::Color::WHITE,
        colors.secondary,
    );
    let name_rect = Rect::new(card.x + text_x, card.y + 20.0, text_width, name_height);
    ui.label(name_rect, npc_name, 16.5, FontId::Bold, colors.text, Align::Start);
    ui.paragraph(
        card.x + text_x,
        name_rect.bottom() + 4.0,
        text_width,
        &dialogue.text,
        TEXT_SIZE,
        FontId::Regular,
        colors.text,
        TextAlign::Left,
    );

    // Options.
    let mut y = card.y + 20.0 + header_height + 18.0;
    let x = card.x + 20.0;
    if options.is_empty() {
        let rect = Rect::new(x, y, option_width, option_heights[0]);
        let button = Button::new("Goodbye").kind(ButtonKind::Option).keycap("Esc").size(OPTION_SIZE).default_focus();
        if ui.button(WidgetId::new("dialogue-goodbye"), rect, button) {
            actions.push(GameAction::Command(Command::CloseDialogue));
        }
    }
    for (index, (option, height)) in options.iter().zip(&option_heights).enumerate() {
        let rect = Rect::new(x, y, option_width, *height);
        let id = WidgetId::new("dialogue-option").with(&dialogue.id).with(index);
        let state = if index == 0 {
            ui.interact_default(id, rect, HitKind::Button)
        } else {
            ui.interact(id, rect, HitKind::Button, true)
        };
        let hovered = state.hovered || state.focus_visible;
        let fill = if state.active {
            colors.button_pressed
        } else if hovered {
            colors.button_hover
        } else {
            colors.button
        };
        let radius = ui.theme().small_radius;
        ui.list_mut().fill_round_rect(rect, radius, fill);
        ui.list_mut().stroke_round_rect(
            rect.inset(0.5),
            radius,
            if hovered { colors.accent } else { colors.button_border },
            1.0,
        );
        if state.focus_visible {
            ui.focus_ring(rect, radius);
        }
        let key = (index + 1).to_string();
        let text_top = rect.y + 9.0;
        let key_width = ui.keycap(rect.x + 14.0, text_top + ui.line_height(OPTION_SIZE) / 2.0, &key, true);
        let text_left = rect.x + 14.0 + key_width + 10.0;
        ui.paragraph(
            text_left,
            text_top,
            rect.right() - 14.0 - text_left,
            &option.text,
            OPTION_SIZE,
            FontId::Regular,
            colors.text,
            TextAlign::Left,
        );
        if state.clicked {
            actions.push(GameAction::Command(Command::ChooseDialogueOption { index: index as f64 }));
        }
        y += height + 8.0;
    }
    ui.pop_layer();
}
