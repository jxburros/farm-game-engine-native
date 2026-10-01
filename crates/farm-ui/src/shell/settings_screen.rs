//! The settings screen: Display, Audio, Controls and Accessibility (with the language and the
//! readable font) tabs. It edits [`Settings`] in
//! place and reports [`ShellAction::SettingsChanged`]; the player applies and stores them.
//! Rebinding captures the next raw key press (Escape cancels).

use super::ShellAction;
use crate::i18n::Lang;
use crate::icons::Icon;
use crate::input::GamepadButton;
use crate::layout::{Align, RectExt};
use crate::settings::{key_label_in, nearest_option, BindAction, Bindings, Settings, TEXT_SIZES, UI_SCALES};
use crate::ui::{Ui, WidgetId};
use crate::widgets::{Button, ButtonKind, ModalSpec};
use farm_render::{FontId, Rect};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SettingsTab {
    #[default]
    Display,
    Audio,
    Controls,
    Accessibility,
}

impl SettingsTab {
    pub const ALL: [SettingsTab; 4] =
        [SettingsTab::Display, SettingsTab::Audio, SettingsTab::Controls, SettingsTab::Accessibility];

    /// The tab's stable name (widget ids, tests).
    pub fn name(self) -> &'static str {
        match self {
            SettingsTab::Display => "Display",
            SettingsTab::Audio => "Audio",
            SettingsTab::Controls => "Controls",
            SettingsTab::Accessibility => "Accessibility",
        }
    }

    /// The tab's title in `lang`.
    pub fn label(self, lang: Lang) -> &'static str {
        lang.tr(match self {
            SettingsTab::Display => "settings.tabDisplay",
            SettingsTab::Audio => "settings.tabAudio",
            SettingsTab::Controls => "settings.tabControls",
            SettingsTab::Accessibility => "settings.tabAccessibility",
        })
    }
}

/// The settings screen's own state (kept by the player between frames).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SettingsScreen {
    pub tab: SettingsTab,
    /// Waiting for a key to bind to this action.
    pub capture: Option<BindAction>,
}

fn keys_text(bindings: &Bindings, action: BindAction, lang: Lang) -> String {
    let keys = bindings.keys(action);
    if keys.is_empty() {
        lang.tr("settings.notBound").to_owned()
    } else {
        keys.iter().map(|key| key_label_in(key, lang)).collect::<Vec<_>>().join(" / ")
    }
}

/// The fixed gamepad buttons and what they do (i18n keys).
const GAMEPAD_LAYOUT: [(GamepadButton, &str); 5] = [
    (GamepadButton::DpadUp, "settings.padMove"),
    (GamepadButton::South, "settings.padInteract"),
    (GamepadButton::East, "settings.padBack"),
    (GamepadButton::LeftShoulder, "settings.padPreviousTab"),
    (GamepadButton::RightShoulder, "settings.padNextTab"),
];

/// Draws the settings; `embedded` hides the display options the editor controls itself.
pub fn settings(
    ui: &mut Ui,
    screen: &mut SettingsScreen,
    settings: &mut Settings,
    embedded: bool,
) -> Option<ShellAction> {
    let colors = ui.theme().colors;
    let lang = ui.lang();
    let tabs: Vec<SettingsTab> =
        SettingsTab::ALL.iter().copied().filter(|tab| !(embedded && *tab == SettingsTab::Display)).collect();
    if !tabs.contains(&screen.tab) {
        screen.tab = tabs[0];
    }
    let mut action = None;

    // Key capture for rebinding: the next raw key. Escape cancels, and so does Back from a
    // gamepad or the on-screen Menu button (the only navigation the player sends while it waits),
    // so a gamepad-only player is never stuck here.
    if let Some(capturing) = screen.capture {
        if let Some(key) = ui.input().keys_pressed.first().cloned() {
            screen.capture = None;
            if key == "escape" {
                action = Some(ShellAction::CancelCapture);
            } else {
                settings.controls.rebind(capturing, &key);
                action = Some(ShellAction::SettingsChanged);
            }
        } else if ui.back_pressed() {
            screen.capture = None;
            action = Some(ShellAction::CancelCapture);
        }
    } else if ui.tab_delta() != 0 {
        let current = tabs.iter().position(|tab| *tab == screen.tab).unwrap_or(0) as i32;
        screen.tab = tabs[(current + ui.tab_delta()).rem_euclid(tabs.len() as i32) as usize];
    }

    let footer = ui.button_height(13.0) + 20.0;
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("settings"),
        icon: Icon::Gear,
        title: lang.tr("settings.title"),
        subtitle: Some(lang.tr("settings.subtitle")),
        width: 660.0,
        max_height: 660.0,
        footer,
        backdrop: true,
    });
    if (modal.close || ui.back_pressed()) && screen.capture.is_none() && action.is_none() {
        action = Some(ShellAction::Back);
    }
    let area = modal.body;
    let mut y = modal.top;

    // Tabs.
    let tab_height = ui.button_height(13.0);
    let mut x = area.x;
    for tab in &tabs {
        let button = Button::new(tab.label(lang)).kind(ButtonKind::Tab { selected: *tab == screen.tab });
        let width = ui.button_width(&button).max(84.0);
        if ui.button(WidgetId::new("settings-tab").with(tab.name()), Rect::new(x, y, width, tab_height), button) {
            screen.tab = *tab;
            screen.capture = None;
        }
        x += width + 6.0;
    }
    y += tab_height + 14.0;

    let row = ui.button_height(14.0).max(40.0) + 4.0;
    let mut changed = false;
    let id = |name: &str| WidgetId::new("setting").with(name);
    match screen.tab {
        SettingsTab::Display => {
            let display = &mut settings.display;
            let label = lang.tr("settings.fullscreen");
            if let Some(value) =
                ui.toggle(id("fullscreen"), Rect::new(area.x, y, area.width, row), label, display.fullscreen)
            {
                display.fullscreen = value;
                changed = true;
            }
            y += row + 4.0;
            let label = lang.tr("settings.integerScaling");
            if let Some(value) =
                ui.toggle(id("integer"), Rect::new(area.x, y, area.width, row), label, display.integer_scaling)
            {
                display.integer_scaling = value;
                changed = true;
            }
            y += row + 4.0;
            let names: Vec<&str> = UI_SCALES.iter().map(|(name, _)| *name).collect();
            let index = nearest_option(&UI_SCALES, display.ui_scale);
            let label = lang.tr("settings.interfaceSize");
            if let Some(next) = ui.stepper(id("ui-scale"), Rect::new(area.x, y, area.width, row), label, &names, index)
            {
                display.ui_scale = UI_SCALES[next].1;
                changed = true;
            }
            y += row + 4.0;
            let hint = lang.tr("settings.fullscreenHint");
            ui.label(
                Rect::new(area.x + 10.0, y, area.width - 20.0, ui.line_height(12.5)),
                hint,
                12.5,
                FontId::Regular,
                colors.muted,
                Align::Start,
            );
            y += ui.line_height(12.5);
        }
        SettingsTab::Audio => {
            let audio = &mut settings.audio;
            for (name, label, value) in [
                ("master", lang.tr("settings.masterVolume"), &mut audio.master),
                ("music", lang.tr("settings.music"), &mut audio.music),
                ("effects", lang.tr("settings.effects"), &mut audio.effects),
            ] {
                if let Some(next) = ui.slider(id(name), Rect::new(area.x, y, area.width, row), label, *value, 0.1) {
                    *value = next;
                    changed = true;
                }
                y += row + 4.0;
            }
            let label = lang.tr("settings.mute");
            if let Some(value) = ui.toggle(id("mute"), Rect::new(area.x, y, area.width, row), label, audio.muted) {
                audio.muted = value;
                changed = true;
            }
            y += row + 4.0;
        }
        SettingsTab::Controls => {
            y += ui.section(area, y, lang.tr("settings.keyboard"), None);
            let key_row = ui.button_height(13.0) + 8.0;
            for bind in BindAction::ALL {
                let rect = Rect::new(area.x, y, area.width, key_row);
                let mut inner = rect.inset_xy(10.0, 0.0);
                let capturing = screen.capture == Some(bind);
                let text = if capturing {
                    lang.tr("settings.pressKey").to_owned()
                } else {
                    keys_text(&settings.controls, bind, lang)
                };
                let button =
                    Button::new(&text).kind(if capturing { ButtonKind::Primary } else { ButtonKind::Secondary });
                let width = ui.button_width(&button).clamp(150.0, inner.width * 0.55);
                let button_rect = inner.cut_right(width).centered(width, ui.button_height(13.0));
                ui.label(inner, bind.label(lang), 14.0, FontId::Regular, colors.text, Align::Start);
                if ui.button(WidgetId::new("rebind").with(bind.canonical_key()), button_rect, button) && !capturing {
                    screen.capture = Some(bind);
                    action = Some(ShellAction::StartCapture(bind));
                }
                y += key_row;
            }
            let reset = Button::new(lang.tr("settings.resetControls"));
            let width = ui.button_width(&reset);
            y += 6.0;
            if ui.button(
                WidgetId::new("rebind-reset"),
                Rect::new(area.x + 10.0, y, width, ui.button_height(13.0)),
                reset,
            ) {
                settings.controls = Bindings::default();
                screen.capture = None;
                changed = true;
            }
            y += ui.button_height(13.0) + 16.0;
            y += ui.section(area, y, lang.tr("settings.gamepad"), None);
            let line = ui.line_height(13.5) + 8.0;
            let mut layout: Vec<(String, String)> = GAMEPAD_LAYOUT
                .iter()
                .map(|(button, what)| (button.label().to_owned(), lang.tr(what).to_owned()))
                .collect();
            for (button, bind) in &settings.controls.gamepad {
                if matches!(
                    bind,
                    BindAction::MoveUp
                        | BindAction::MoveDown
                        | BindAction::MoveLeft
                        | BindAction::MoveRight
                        | BindAction::Interact
                ) {
                    continue;
                }
                layout.push((button.label().to_owned(), bind.label(lang).to_owned()));
            }
            for (button, what) in layout {
                let mut inner = Rect::new(area.x + 10.0, y, area.width - 20.0, line);
                let key_width = ui.keycap(inner.x, inner.y + line / 2.0, &button, true);
                inner.cut_left(key_width + 12.0);
                ui.label(inner, &what, 13.5, FontId::Regular, colors.text, Align::Start);
                y += line;
            }
        }
        SettingsTab::Accessibility => {
            let access = &mut settings.accessibility;
            let names: Vec<&str> = TEXT_SIZES.iter().map(|(name, _)| lang.tr(name)).collect();
            let index = nearest_option(&TEXT_SIZES, access.text_size);
            let label = lang.tr("settings.textSize");
            if let Some(next) = ui.stepper(id("text-size"), Rect::new(area.x, y, area.width, row), label, &names, index)
            {
                access.text_size = TEXT_SIZES[next].1;
                changed = true;
            }
            y += row + 4.0;
            let label = lang.tr("settings.readableFont");
            if let Some(value) =
                ui.toggle(id("readable-font"), Rect::new(area.x, y, area.width, row), label, access.readable_font)
            {
                access.readable_font = value;
                changed = true;
            }
            y += row + 4.0;
            let label = lang.tr("settings.reducedMotion");
            if let Some(value) =
                ui.toggle(id("reduced-motion"), Rect::new(area.x, y, area.width, row), label, access.reduced_motion)
            {
                access.reduced_motion = value;
                changed = true;
            }
            y += row + 4.0;
            // Language: automatic, then each table by its own name.
            let mut names = vec![lang.tr("settings.languageAuto")];
            names.extend(Lang::ALL.iter().map(|option| option.native_name()));
            let chosen = Lang::from_tag(&settings.language);
            let index = Lang::ALL.iter().position(|option| chosen == Some(*option)).map_or(0, |index| index + 1);
            let label = lang.tr("settings.language");
            if let Some(next) = ui.stepper(id("language"), Rect::new(area.x, y, area.width, row), label, &names, index)
            {
                settings.language =
                    next.checked_sub(1).map_or_else(String::new, |index| Lang::ALL[index].code().to_owned());
                changed = true;
            }
            y += row + 4.0;
        }
    }
    ui.end_modal_body(y);
    if let Some(mut footer) = modal.footer {
        let back = Button::new(lang.tr("common.back")).primary();
        let width = ui.button_width(&back).max(90.0);
        let rect = footer.cut_right(width).centered(width, ui.button_height(13.0));
        if ui.button(WidgetId::new("settings-back"), rect, back) {
            action = Some(ShellAction::Back);
            screen.capture = None;
        }
        let hint = lang.tr("settings.tabsHint");
        ui.label(footer, hint, 12.0, FontId::Regular, colors.muted, Align::Start);
    }
    ui.close_modal();
    if changed && action.is_none() {
        action = Some(ShellAction::SettingsChanged);
    }
    action
}
