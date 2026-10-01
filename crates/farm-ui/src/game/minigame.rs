//! The minigame overlay (web MinigameOverlay, C# `MinigameOverlay`): the prompt, then per kind a
//! timing bar with its moving marker and target zone, a simple battle's status, log and choices,
//! or the fallback confirm button. Presses and releases go to `PlaySession::minigame_input`;
//! the score enters the command log there, exactly once.

use super::{GameAction, GameView};
use crate::format::capitalize;
use crate::icons::Icon;
use crate::input::InputDevice;
use crate::layout::{Align, RectExt};
use crate::theme::fade;
use crate::ui::{Ui, WidgetId};
use crate::widgets::{Button, ButtonKind, ModalSpec};
use farm_render::{FontId, Rect, TextAlign};
use farm_runtime::host::MinigameInput;
use farm_sim::Command;

const BAR_WIDTH: f32 = 320.0;
const BAR_HEIGHT: f32 = 26.0;

pub(crate) fn draw(ui: &mut Ui, view: &GameView<'_>, actions: &mut Vec<GameAction>) {
    let (Some(active), Some(game)) = (view.state.minigame.as_ref(), view.minigame) else { return };
    let colors = ui.theme().colors;
    let definition = view.content.minigames.iter().find(|def| def.id == active.minigame_id);
    let title = definition.map_or(active.minigame_id.as_str(), |def| def.name.as_str());
    let footer = ui.button_height(13.0) + 20.0;
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("minigame"),
        icon: Icon::Star,
        title,
        subtitle: definition.map(|def| def.kind.as_str()),
        width: 440.0,
        max_height: 560.0,
        footer,
        backdrop: true,
    });
    if modal.close {
        actions.push(GameAction::Command(Command::CancelMinigame));
    }
    let area = modal.body;
    let mut y = modal.top;
    y +=
        ui.paragraph(area.x, y, area.width, &game.prompt, 14.0, FontId::Regular, colors.text, TextAlign::Center) + 14.0;

    let battle = !game.choices.is_empty();
    if game.kind == "timing-bar" {
        let track = Rect::new(area.x + (area.width - BAR_WIDTH) / 2.0, y, BAR_WIDTH, BAR_HEIGHT);
        ui.list_mut().fill_round_rect(track, 6.0, colors.track);
        let zone = Rect::new(
            track.x + game.target_left as f32 * BAR_WIDTH,
            track.y,
            game.target_size as f32 * BAR_WIDTH,
            BAR_HEIGHT,
        );
        ui.push_clip(track);
        ui.list_mut().fill_rect(zone, fade(colors.primary, 0.35));
        ui.list_mut().stroke_rect(zone.inset(0.5), colors.primary, 1.0);
        let marker_x = track.x + (game.position as f32).clamp(0.0, 1.0) * (BAR_WIDTH - 3.0);
        ui.list_mut().fill_rect(Rect::new(marker_x, track.y, 3.0, BAR_HEIGHT), colors.accent);
        ui.pop_clip();
        y += BAR_HEIGHT + 14.0;
    }
    if battle {
        y +=
            ui.paragraph(area.x, y, area.width, &game.status, 13.5, FontId::Bold, colors.text, TextAlign::Center) + 6.0;
        y += ui.paragraph(area.x, y, area.width, &game.log, 12.5, FontId::Regular, colors.muted, TextAlign::Center)
            + 12.0;
        let buttons: Vec<(String, &String)> = game.choices.iter().map(|choice| (capitalize(choice), choice)).collect();
        let widths: Vec<f32> =
            buttons.iter().map(|(label, _)| ui.button_width(&Button::new(label)).max(84.0)).collect();
        let total = widths.iter().sum::<f32>() + 8.0 * (widths.len() - 1) as f32;
        let mut x = area.x + (area.width - total) / 2.0;
        let height = ui.button_height(13.0);
        for (index, ((label, choice), width)) in buttons.iter().zip(&widths).enumerate() {
            let mut button = Button::new(label).enabled(!game.is_done);
            if index == 0 {
                button = button.default_focus();
            }
            if ui.button(
                WidgetId::new("minigame-choice").with(choice.as_str()),
                Rect::new(x, y, *width, height),
                button,
            ) {
                actions.push(GameAction::Minigame(MinigameInput::Act { choice: (*choice).clone() }));
            }
            x += width + 8.0;
        }
        y += height + 8.0;
    } else {
        // Press/release semantics (hold-to-catch needs both).
        let button = Button::new(&game.button_text).primary().size(15.0).default_focus();
        let width = ui.button_width(&button).max(180.0);
        let height = ui.button_height(15.0) + 8.0;
        let rect = Rect::new(area.x + (area.width - width) / 2.0, y, width, height);
        let state = ui.hold_button(WidgetId::new("minigame-primary"), rect, button);
        if state.pressed {
            actions.push(GameAction::Minigame(MinigameInput::Press));
        }
        if state.released {
            actions.push(GameAction::Minigame(MinigameInput::Release));
        }
        y += height + 6.0;
        // Gamepad A, or the touch controls' A button.
        let hint =
            if matches!(ui.device(), InputDevice::Gamepad | InputDevice::Touch) { "A" } else { ui.tr("minigame.keys") };
        ui.label(
            Rect::new(area.x, y, area.width, ui.line_height(12.0)),
            hint,
            12.0,
            FontId::Regular,
            colors.muted,
            Align::Center,
        );
        y += ui.line_height(12.0);
    }
    ui.end_modal_body(y);
    if let Some(mut footer) = modal.footer {
        let give_up = Button::new(ui.tr("minigame.giveUp")).kind(ButtonKind::Secondary);
        let width = ui.button_width(&give_up);
        let rect = footer.cut_right(width).centered(width, ui.button_height(13.0));
        if ui.button(WidgetId::new("minigame-give-up"), rect, give_up.focusable(battle)) {
            actions.push(GameAction::Command(Command::CancelMinigame));
        }
    }
    ui.close_modal();
}
