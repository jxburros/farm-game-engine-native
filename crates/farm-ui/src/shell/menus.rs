//! The pause menu, the credits and the confirmation dialog.

use super::ShellAction;
use crate::icons::Icon;
use crate::layout::RectExt;
use crate::ui::{Ui, WidgetId};
use crate::widgets::{Button, ButtonKind, ModalSpec};
use farm_render::{FontId, Rect, TextAlign};

/// What the pause menu offers.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PauseView {
    /// The game's title (subtitle of the card).
    pub title: String,
    /// Save slots, loading and quitting (standalone games; the editor's Play Mode has none).
    pub full: bool,
    /// A Quit (to desktop) button.
    pub can_quit: bool,
}

pub fn pause(ui: &mut Ui, view: &PauseView) -> Option<ShellAction> {
    let mut entries: Vec<(&str, Icon, ShellAction)> = vec![("Resume", Icon::Play, ShellAction::Resume)];
    if view.full {
        entries.push(("Save", Icon::Save, ShellAction::OpenSave));
        entries.push(("Load", Icon::Folder, ShellAction::OpenLoad));
    }
    entries.push(("Settings", Icon::Gear, ShellAction::OpenSettings));
    if view.full {
        entries.push(("Quit to title", Icon::Exit, ShellAction::QuitToTitle));
        if view.can_quit {
            entries.push(("Quit game", Icon::Close, ShellAction::Quit));
        }
    }
    let button_height = ui.button_height(15.0).max(40.0);
    let gap = 8.0;
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("pause"),
        icon: Icon::Menu,
        title: "Paused",
        subtitle: Some(&view.title),
        width: 360.0,
        max_height: 640.0,
        footer: 0.0,
        backdrop: true,
    });
    let mut action = (modal.close || ui.back_pressed()).then_some(ShellAction::Resume);
    let mut y = modal.top;
    for (index, (label, icon, entry)) in entries.into_iter().enumerate() {
        let mut button = Button::new(label).kind(ButtonKind::Menu).icon(icon).size(15.0);
        if index == 0 {
            button = button.default_focus();
        }
        if ui.button(
            WidgetId::new("pause").with(label),
            Rect::new(modal.body.x, y, modal.body.width, button_height),
            button,
        ) {
            action = Some(entry);
        }
        y += button_height + gap;
    }
    ui.end_modal(y - gap);
    action
}

/// The credits of the game.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CreditsView {
    pub title: String,
    pub version: String,
    pub author: Option<String>,
    pub company: Option<String>,
    /// The creator's credits text (paragraphs separated by newlines).
    pub credits: Option<String>,
}

pub fn credits(ui: &mut Ui, view: &CreditsView) -> Option<ShellAction> {
    let colors = ui.theme().colors;
    let footer = ui.button_height(13.0) + 20.0;
    let subtitle = format!("Version {}", view.version);
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("credits"),
        icon: Icon::Heart,
        title: "Credits",
        subtitle: Some(&subtitle),
        width: 600.0,
        max_height: 640.0,
        footer,
        backdrop: true,
    });
    let mut action = (modal.close || ui.back_pressed()).then_some(ShellAction::Back);
    let area = modal.body;
    let mut y = modal.top;
    y += ui.paragraph(area.x, y, area.width, &view.title, 22.0, FontId::Bold, colors.accent_text, TextAlign::Center)
        + 4.0;
    let by: Vec<&str> =
        [view.author.as_deref(), view.company.as_deref()].into_iter().flatten().filter(|s| !s.is_empty()).collect();
    if !by.is_empty() {
        let line = format!("by {}", by.join(" \u{00b7} "));
        y += ui.paragraph(area.x, y, area.width, &line, 14.0, FontId::Regular, colors.text, TextAlign::Center) + 8.0;
    }
    if let Some(text) = view.credits.as_deref().filter(|text| !text.trim().is_empty()) {
        y += 8.0;
        for paragraph in text.split('\n') {
            if paragraph.trim().is_empty() {
                y += ui.line_height(13.5) / 2.0;
                continue;
            }
            y += ui.paragraph(area.x, y, area.width, paragraph, 13.5, FontId::Regular, colors.text, TextAlign::Center);
        }
        y += 8.0;
    }
    y += 12.0;
    ui.list_mut().line((area.x + area.width * 0.3, y), (area.right() - area.width * 0.3, y), colors.panel_border, 1.0);
    y += 14.0;
    y += ui.paragraph(
        area.x,
        y,
        area.width,
        "Made with Farming RPG Maker",
        14.0,
        FontId::Bold,
        colors.text,
        TextAlign::Center,
    ) + 4.0;
    y += ui.paragraph(
        area.x,
        y,
        area.width,
        "Third-party licenses are in licenses/THIRD-PARTY.txt next to the game.",
        12.5,
        FontId::Regular,
        colors.muted,
        TextAlign::Center,
    );
    ui.end_modal_body(y);
    if let Some(mut footer) = modal.footer {
        let back = Button::new("Back").default_focus();
        let width = ui.button_width(&back).max(90.0);
        let rect = footer.cut_right(width).centered(width, ui.button_height(13.0));
        if ui.button(WidgetId::new("credits-back"), rect, back) {
            action = Some(ShellAction::Back);
        }
    }
    ui.close_modal();
    action
}

/// A yes/no question.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ConfirmView {
    pub title: String,
    pub message: String,
    pub confirm: String,
}

pub fn confirm(ui: &mut Ui, view: &ConfirmView) -> Option<ShellAction> {
    let colors = ui.theme().colors;
    let footer = ui.button_height(13.0) + 20.0;
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("confirm"),
        icon: Icon::Alert,
        title: &view.title,
        subtitle: None,
        width: 440.0,
        max_height: 400.0,
        footer,
        backdrop: true,
    });
    let mut action = (modal.close || ui.back_pressed()).then_some(ShellAction::Cancel);
    let area = modal.body;
    let y = modal.top
        + ui.paragraph(
            area.x,
            modal.top,
            area.width,
            &view.message,
            14.0,
            FontId::Regular,
            colors.text,
            TextAlign::Left,
        );
    ui.end_modal_body(y);
    if let Some(mut footer) = modal.footer {
        let yes = Button::new(&view.confirm).primary();
        let no = Button::new("Cancel").default_focus();
        let height = ui.button_height(13.0);
        let yes_width = ui.button_width(&yes).max(90.0);
        let no_width = ui.button_width(&no).max(90.0);
        let yes_rect = footer.cut_right(yes_width).centered(yes_width, height);
        footer.cut_right(8.0);
        let no_rect = footer.cut_right(no_width).centered(no_width, height);
        if ui.button(WidgetId::new("confirm-no"), no_rect, no) {
            action = Some(ShellAction::Cancel);
        }
        if ui.button(WidgetId::new("confirm-yes"), yes_rect, yes) {
            action = Some(ShellAction::Confirm);
        }
    }
    ui.close_modal();
    action
}
