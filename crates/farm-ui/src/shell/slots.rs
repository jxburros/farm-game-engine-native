//! Save slots: a card per slot with its thumbnail, farm name, date, money, play time and when it
//! was saved; Load, Save (or Start for a new game) and Delete.

use super::ShellAction;
use crate::format::{money, num, play_time, saved_ago, season_name};
use crate::i18n::Lang;
use crate::icons::{self, Icon};
use crate::layout::{Align, RectExt};
use crate::theme::fade;
use crate::ui::{Ui, WidgetId};
use crate::widgets::{Button, ModalSpec};
use farm_render::{DrawCmd, FontId, ImageId, Rect, Sampling};

/// Why the slots are shown.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SlotsMode {
    Load,
    Save,
    /// Pick a slot for a new game.
    NewGame,
}

/// A filled slot's preview.
#[derive(Debug, Clone, PartialEq)]
pub struct SlotPreviewView {
    pub farm_name: String,
    pub day: f64,
    pub season: String,
    pub year: f64,
    pub money: f64,
    pub play_seconds: f64,
    /// Unix seconds (0 unknown).
    pub saved_at: i64,
    /// The save's thumbnail, decoded into the UI's image store, with its size.
    pub thumbnail: Option<(ImageId, f32, f32)>,
    /// The day of the season (calendar-aware, from the host).
    pub day_of_season: Option<f64>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct SlotView {
    pub slot: u32,
    pub preview: Option<SlotPreviewView>,
    /// The slot the running game saves to.
    pub current: bool,
    /// The save exists but could not be read.
    pub unreadable: bool,
}

#[derive(Debug, Clone, PartialEq)]
pub struct SlotsView {
    pub mode: SlotsMode,
    pub slots: Vec<SlotView>,
    /// Unix seconds now, for "saved 5 min ago".
    pub now: i64,
}

/// The date line of a preview: "Day 3 of Spring, Year 1".
pub fn date_line(preview: &SlotPreviewView, lang: Lang) -> String {
    let day = preview.day_of_season.unwrap_or(preview.day);
    lang.format("slots.date", &[&num(day), &season_name(&preview.season, lang), &num(preview.year)])
}

pub fn slots(ui: &mut Ui, view: &SlotsView) -> Option<ShellAction> {
    let colors = ui.theme().colors;
    let lang = ui.lang();
    let (title, icon) = match view.mode {
        SlotsMode::Load => (lang.tr("slots.loadTitle"), Icon::Folder),
        SlotsMode::Save => (lang.tr("slots.saveTitle"), Icon::Save),
        SlotsMode::NewGame => (lang.tr("slots.newTitle"), Icon::Play),
    };
    let subtitle = lang.tr(match view.mode {
        SlotsMode::Load => "slots.loadSubtitle",
        SlotsMode::Save => "slots.saveSubtitle",
        SlotsMode::NewGame => "slots.newSubtitle",
    });
    let footer = ui.button_height(13.0) + 20.0;
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("slots"),
        icon,
        title,
        subtitle: Some(subtitle),
        width: 720.0,
        max_height: 640.0,
        footer,
        backdrop: true,
    });
    let mut action = modal.close.then_some(ShellAction::Back);
    if ui.back_pressed() {
        action = Some(ShellAction::Back);
    }
    let area = modal.body;
    let mut y = modal.top;
    let thumb_width = 160.0f32.min(area.width * 0.3);
    let thumb_height = (thumb_width * 0.625 + 0.5).floor();
    let height = thumb_height + 20.0;
    let button_height = ui.button_height(13.0);
    for (index, slot) in view.slots.iter().enumerate() {
        let rect = Rect::new(area.x, y, area.width, height);
        ui.row_background(rect);
        if slot.current {
            let radius = ui.theme().small_radius;
            ui.list_mut().stroke_round_rect(rect.inset(0.5), radius, fade(colors.accent, 0.7), 1.5);
        }
        let mut inner = rect.inset(10.0);
        let thumb = inner.cut_left(thumb_width);
        ui.list_mut().fill_round_rect(thumb, 6.0, colors.panel_header);
        match slot.preview.as_ref().and_then(|preview| preview.thumbnail) {
            Some((image, width, height)) => {
                ui.push_clip(thumb);
                ui.list_mut().push(DrawCmd::Image {
                    image,
                    src: Rect::new(0.0, 0.0, width, height),
                    dst: thumb,
                    opacity: 1.0,
                    sampling: Sampling::Smooth,
                    tint: None,
                });
                ui.pop_clip();
            }
            None => icons::draw(
                ui.list_mut(),
                if slot.preview.is_some() { Icon::Save } else { Icon::Folder },
                thumb.centered(32.0, 32.0),
                fade(colors.muted, 0.5),
                colors.panel_header,
            ),
        }
        ui.list_mut().stroke_round_rect(thumb.inset(0.5), 6.0, colors.row_border, 1.0);
        inner.cut_left(14.0);

        // Buttons.
        let id = WidgetId::new("slot").with(slot.slot);
        let (primary_label, primary_action, primary_enabled) = match view.mode {
            SlotsMode::Load => ("slots.load", ShellAction::LoadSlot(slot.slot), slot.preview.is_some()),
            SlotsMode::Save => ("slots.save", ShellAction::SaveSlot(slot.slot), true),
            SlotsMode::NewGame => ("slots.start", ShellAction::NewGameInSlot(slot.slot), true),
        };
        let primary = Button::new(lang.tr(primary_label)).primary().enabled(primary_enabled);
        let delete = Button::new(lang.tr("slots.delete")).enabled(slot.preview.is_some() || slot.unreadable);
        let primary_width = ui.button_width(&primary).max(80.0);
        let delete_width = ui.button_width(&delete).max(80.0);
        let buttons = inner.cut_right(primary_width.max(delete_width));
        inner.cut_right(10.0);
        let stack = buttons.centered(buttons.width, button_height * 2.0 + 8.0);
        let mut primary_button = primary;
        if index == 0 {
            primary_button = primary_button.default_focus();
        }
        if ui.button(id.with("primary"), Rect::new(stack.x, stack.y, stack.width, button_height), primary_button) {
            action = Some(primary_action);
        }
        if ui.button(
            id.with("delete"),
            Rect::new(stack.x, stack.y + button_height + 8.0, stack.width, button_height),
            delete,
        ) {
            action = Some(ShellAction::DeleteSlot(slot.slot));
        }

        // Details.
        let line = |ui: &Ui, size: f32| ui.line_height(size);
        match &slot.preview {
            Some(preview) => {
                let lines = [
                    (lang.format("slots.slotFarm", &[&slot.slot, &preview.farm_name]), 15.0, FontId::Bold, colors.text),
                    (date_line(preview, lang), 13.0, FontId::Regular, colors.text),
                    (
                        lang.format("slots.played", &[&money(preview.money as i64), &play_time(preview.play_seconds)]),
                        12.5,
                        FontId::Regular,
                        colors.muted,
                    ),
                    (saved_ago(preview.saved_at, view.now, lang), 12.0, FontId::Regular, colors.muted),
                ];
                let total: f32 = lines.iter().map(|(_, size, _, _)| line(ui, *size)).sum();
                let mut ly = inner.y + (inner.height - total) / 2.0;
                for (text, size, font, color) in lines {
                    let rect = Rect::new(inner.x, ly, inner.width, line(ui, size));
                    ui.label(rect, &text, size, font, color, Align::Start);
                    ly += rect.height;
                }
            }
            None => {
                let text = lang.tr(if slot.unreadable { "slots.unreadable" } else { "slots.empty" });
                let heading =
                    Rect::new(inner.x, inner.y + inner.height / 2.0 - line(ui, 15.0), inner.width, line(ui, 15.0));
                let name = lang.format("slots.slot", &[&slot.slot]);
                ui.label(heading, &name, 15.0, FontId::Bold, colors.text, Align::Start);
                let detail = Rect::new(inner.x, heading.bottom(), inner.width, line(ui, 13.0));
                ui.label(
                    detail,
                    text,
                    13.0,
                    FontId::Regular,
                    if slot.unreadable { colors.error } else { colors.muted },
                    Align::Start,
                );
            }
        }
        y += height + 10.0;
    }
    ui.end_modal_body(y);
    if let Some(mut footer) = modal.footer {
        let back = Button::new(lang.tr("common.back"));
        let width = ui.button_width(&back).max(90.0);
        let rect = footer.cut_right(width).centered(width, button_height);
        if ui.button(WidgetId::new("slots-back"), rect, back) {
            action = Some(ShellAction::Back);
        }
    }
    ui.close_modal();
    action
}
