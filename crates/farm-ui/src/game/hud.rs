//! The HUD over the world (C# `PlayModeView` HUD bar, toolbar, game panels and controls help):
//! money, season, day, year, weather, time and energy; toolbar buttons; creator game panels;
//! the controls hint row; the "Made with" credit.

use super::{GameAction, GameView, Panel};
use crate::format::{capitalize, money, num};
use crate::icons::Icon;
use crate::input::{GamepadButton, InputDevice};
use crate::layout::{Align, Flow, RectExt};
use crate::settings::BindAction;
use crate::theme::fade;
use crate::ui::{Ui, WidgetId};
use crate::widgets::Button;
use farm_render::{FontId, Rect};
use farm_sim::Command;

/// Height of one HUD row.
const ROW: f32 = 30.0;
/// Space between two stats.
const STAT_GAP: f32 = 16.0;
/// Width of the energy bar.
const ENERGY_BAR: f32 = 96.0;

enum StatValue {
    Chip(String),
    Primary(String),
    Secondary(String),
    Plain(String),
    Energy { ratio: f32, text: String },
}

struct Stat {
    label: &'static str,
    value: StatValue,
}

fn stat_width(ui: &Ui, stat: &Stat) -> f32 {
    let label = if stat.label.is_empty() { 0.0 } else { ui.measure(stat.label, 13.0, FontId::Regular) + 6.0 };
    let value = match &stat.value {
        StatValue::Chip(text) => ui.measure(text, 15.0, FontId::Bold) + 14.0,
        StatValue::Primary(text) | StatValue::Secondary(text) | StatValue::Plain(text) => {
            ui.measure(text, 13.0, FontId::Bold)
        }
        StatValue::Energy { text, .. } => {
            ENERGY_BAR + if text.is_empty() { 0.0 } else { 8.0 + ui.measure(text, 12.0, FontId::Regular) }
        }
    };
    (label + value).ceil()
}

fn draw_stat(ui: &mut Ui, rect: Rect, stat: &Stat) {
    let colors = ui.theme().colors;
    let mut area = rect;
    if !stat.label.is_empty() {
        let label_width = ui.measure(stat.label, 13.0, FontId::Regular);
        let label = area.cut_left(label_width + 6.0);
        ui.label(label, stat.label, 13.0, FontId::Regular, colors.muted, Align::Start);
    }
    let (cy, x) = (rect.y + rect.height / 2.0, area.x);
    match &stat.value {
        StatValue::Chip(text) => {
            ui.chip(x, cy, text, 15.0, colors.accent, colors.on_accent);
        }
        StatValue::Primary(text) => {
            ui.label(area, text, 13.0, FontId::Bold, colors.primary, Align::Start);
        }
        StatValue::Secondary(text) => {
            ui.label(area, text, 13.0, FontId::Bold, colors.secondary, Align::Start);
        }
        StatValue::Plain(text) => {
            ui.label(area, text, 13.0, FontId::Bold, colors.text, Align::Start);
        }
        StatValue::Energy { ratio, text } => {
            let bar = Rect::new(x, cy - 5.0, ENERGY_BAR, 10.0);
            let color = if *ratio <= 0.2 { colors.error } else { colors.primary };
            ui.progress(bar, *ratio, color);
            let rest = Rect::new(bar.right() + 8.0, rect.y, area.right() - bar.right() - 8.0, rect.height);
            ui.label(rest, text, 12.0, FontId::Regular, colors.muted, Align::Start);
        }
    }
}

/// The prompt for an action on the current device.
pub(crate) fn prompt(view: &GameView<'_>, device: InputDevice, action: BindAction) -> String {
    match device {
        InputDevice::Gamepad => match action {
            BindAction::MoveUp | BindAction::MoveDown | BindAction::MoveLeft | BindAction::MoveRight => "LS".to_owned(),
            BindAction::Menu => view.bindings.button_for(action).unwrap_or(GamepadButton::Start).label().to_owned(),
            _ => view.bindings.button_for(action).map_or_else(|| "—".to_owned(), |button| button.label().to_owned()),
        },
        _ => match action {
            BindAction::MoveUp | BindAction::MoveDown | BindAction::MoveLeft | BindAction::MoveRight => {
                let keys: Vec<String> =
                    [BindAction::MoveUp, BindAction::MoveLeft, BindAction::MoveDown, BindAction::MoveRight]
                        .iter()
                        .map(|action| view.bindings.key_label(*action))
                        .collect();
                if keys.iter().all(|key| key.chars().count() == 1) {
                    keys.concat()
                } else {
                    keys.join(" ")
                }
            }
            other => view.bindings.key_label(other),
        },
    }
}

/// The toolbar buttons (id, icon), left to right.
const TOOLS: [(&str, Icon); 5] = [
    ("inventory", Icon::Package),
    ("quests", Icon::Star),
    ("craft", Icon::Hammer),
    ("sleep", Icon::Moon),
    ("menu", Icon::Menu),
];

fn toolbar_labels(view: &GameView<'_>, device: InputDevice, compact: bool) -> Vec<String> {
    let player = &view.state.player;
    let inventory = format!("Inventory {}/{}", player.inventory.len(), num(player.max_inventory_size));
    let with_key = |label: &str, action: BindAction| {
        if compact {
            label.to_owned()
        } else {
            format!("{label} ({})", prompt(view, device, action))
        }
    };
    vec![
        inventory,
        with_key("Quests", BindAction::Quests),
        with_key("Craft", BindAction::Craft),
        with_key("Sleep", BindAction::Sleep),
        with_key("Menu", BindAction::Menu),
    ]
}

fn stats(view: &GameView<'_>, compact: bool) -> Vec<Stat> {
    let state = view.state;
    let content = view.content;
    let clock = &state.clock;
    let calendar = view.calendar;
    let mut stats = vec![
        Stat { label: if compact { "" } else { "Money:" }, value: StatValue::Chip(money(state.player.money)) },
        Stat {
            label: "Season:",
            value: StatValue::Primary(calendar.season_name.clone().unwrap_or_else(|| capitalize(&clock.season))),
        },
        Stat {
            label: "Day:",
            value: StatValue::Secondary(format!("{} / {}", num(calendar.day_of_season), num(calendar.season_days))),
        },
        Stat { label: "Year:", value: StatValue::Secondary(num(clock.year)) },
        Stat {
            label: "Weather:",
            value: StatValue::Plain(
                content
                    .weather
                    .types
                    .iter()
                    .find(|weather| weather.id == clock.weather_id)
                    .map_or_else(|| "Sunny".to_owned(), |weather| weather.name.clone()),
            ),
        },
        Stat { label: "Time:", value: StatValue::Plain(calendar.time_text.clone()) },
    ];
    if content.settings.energy_enabled {
        let max = if state.player.max_energy > 0.0 { state.player.max_energy } else { content.settings.max_energy };
        let ratio = if max > 0.0 { (state.player.energy / max) as f32 } else { 0.0 };
        let text = if compact { String::new() } else { format!("{} / {}", num(state.player.energy.floor()), num(max)) };
        stats.push(Stat { label: "Energy:", value: StatValue::Energy { ratio, text } });
    }
    stats
}

/// Draws the HUD; returns the bottom of the top bar.
pub(crate) fn draw(ui: &mut Ui, view: &GameView<'_>, actions: &mut Vec<GameAction>) -> f32 {
    let colors = ui.theme().colors;
    let screen = ui.screen();
    let device = ui.device();

    // ── Stats and toolbar ──
    // Full labels when everything fits on one row; else compact labels (no "Money:", no energy
    // numbers, no key hints on the toolbar: the hint row has them); else two rows.
    let bar_outer = Rect::new(12.0, 10.0, screen.width - 24.0, 0.0);
    let inner_width = bar_outer.width - 24.0;
    let button_height = ui.button_height(13.0).min(ROW);
    let mut layout = None;
    for compact in [false, true] {
        let stats = stats(view, compact);
        let stat_sizes: Vec<(f32, f32)> = stats.iter().map(|stat| (stat_width(ui, stat), ROW)).collect();
        let labels = toolbar_labels(view, device, compact);
        let widths: Vec<f32> = labels
            .iter()
            .zip(TOOLS)
            .map(|(label, (_, icon))| ui.button_width(&Button::new(label).icon(icon)))
            .collect();
        let toolbar_width = widths.iter().sum::<f32>() + 6.0 * (widths.len() - 1) as f32;
        let stats_width: f32 =
            stat_sizes.iter().map(|(w, _)| w).sum::<f32>() + STAT_GAP * (stat_sizes.len() - 1) as f32;
        let one_row = stats_width + 24.0 + toolbar_width <= inner_width;
        if one_row || compact {
            layout = Some((stats, stat_sizes, labels, widths, toolbar_width, one_row));
            break;
        }
    }
    let Some((stats, stat_sizes, labels, widths, toolbar_width, one_row)) = layout else { return 0.0 };
    let stats_area_width = if one_row { inner_width - toolbar_width - 24.0 } else { inner_width };
    let flow = Flow::layout(
        Rect::new(bar_outer.x + 12.0, bar_outer.y + 8.0, stats_area_width, 0.0),
        &stat_sizes,
        STAT_GAP,
        2.0,
        Align::Start,
    );
    let sizes: Vec<(f32, f32)> = widths.iter().map(|w| (*w, button_height)).collect();
    // Two rows: stats first, the toolbar under them.
    let wrapped_toolbar = Flow::layout(Rect::new(0.0, 0.0, inner_width, 0.0), &sizes, 6.0, 4.0, Align::End);
    let content_height = if one_row { flow.height.max(ROW) } else { flow.height + 6.0 + wrapped_toolbar.height };
    let bar = Rect::new(bar_outer.x, bar_outer.y, bar_outer.width, content_height + 16.0);
    ui.panel(bar, colors.hud, colors.hud_border, 10.0, true);
    for (stat, rect) in stats.iter().zip(&flow.items) {
        draw_stat(ui, *rect, stat);
    }
    let toolbar_origin = if one_row {
        Rect::new(
            bar.right() - 12.0 - toolbar_width,
            bar.y + 8.0 + (content_height - button_height) / 2.0,
            toolbar_width,
            0.0,
        )
    } else {
        Rect::new(bar.x + 12.0, bar.y + 8.0 + flow.height + 6.0, inner_width, 0.0)
    };
    let toolbar = Flow::layout(toolbar_origin, &sizes, 6.0, 4.0, Align::End);
    for (((key, icon), label), rect) in TOOLS.iter().zip(&labels).zip(&toolbar.items) {
        let button = Button::new(label).icon(*icon).focusable(false);
        if ui.button(WidgetId::new("hud").with(*key), *rect, button) {
            actions.push(match *key {
                "inventory" => GameAction::TogglePanel(Panel::Inventory),
                "quests" => GameAction::TogglePanel(Panel::Quests),
                "craft" => GameAction::TogglePanel(Panel::Crafting),
                "sleep" => GameAction::Command(Command::Sleep),
                _ => GameAction::OpenMenu,
            });
        }
    }

    // ── Controls hint row ──
    let hints: Vec<(String, &str)> = [
        BindAction::MoveUp,
        BindAction::Interact,
        BindAction::Water,
        BindAction::Till,
        BindAction::Axe,
        BindAction::Pickaxe,
        BindAction::Scythe,
        BindAction::Craft,
        BindAction::Sleep,
        BindAction::Inventory,
        BindAction::Quests,
        BindAction::Menu,
    ]
    .iter()
    .map(|action| (prompt(view, device, *action), action.hint()))
    .collect();
    let hint_sizes: Vec<(f32, f32)> = hints
        .iter()
        .map(|(key, label)| (ui.keycap_width(key) + 5.0 + ui.measure(label, 12.0, FontId::Regular), 22.0))
        .collect();
    let max_hint_width = (screen.width - 48.0).min(1100.0);
    let hint_flow =
        Flow::layout(Rect::new(0.0, 0.0, max_hint_width - 24.0, 0.0), &hint_sizes, 14.0, 4.0, Align::Center);
    let pill = Rect::new(
        (screen.width - hint_flow.width - 24.0) / 2.0,
        screen.bottom() - 10.0 - hint_flow.height - 12.0,
        hint_flow.width + 24.0,
        hint_flow.height + 12.0,
    );
    ui.panel(pill, fade(colors.hud, 0.9), colors.hud_border, 8.0, false);
    let origin_x = pill.x + 12.0 - (max_hint_width - 24.0 - hint_flow.width) / 2.0;
    for ((key, label), item) in hints.iter().zip(&hint_flow.items) {
        let item = item.offset(origin_x, pill.y + 6.0);
        let key_width = ui.keycap(item.x, item.y + item.height / 2.0, key, true);
        let text = Rect::new(item.x + key_width + 5.0, item.y, item.width - key_width - 5.0, item.height);
        ui.label(text, label, 12.0, FontId::Regular, colors.muted, Align::Start);
    }
    let mut above_hints = pill.y - 8.0;

    // ── Creator game panels ──
    let visible: Vec<_> = view.panels.iter().filter(|panel| !panel.hidden).collect();
    if !visible.is_empty() {
        let mut cards = Vec::new();
        for panel in &visible {
            let mut width = ui.measure(&panel.title, 13.5, FontId::Bold) + 20.0;
            let mut entries = Vec::new();
            for entry in &panel.entries {
                let entry_width = if entry.action_id.is_some() {
                    ui.button_width(&Button::new(&entry.text).size(12.0))
                } else {
                    ui.measure(&entry.text, 13.0, FontId::Regular)
                };
                entries.push(entry_width);
                width += 12.0 + entry_width;
            }
            cards.push((width, entries));
        }
        let sizes: Vec<(f32, f32)> = cards.iter().map(|(width, _)| (width.min(screen.width - 48.0), 38.0)).collect();
        let flow = Flow::layout(Rect::new(24.0, 0.0, screen.width - 48.0, 0.0), &sizes, 8.0, 8.0, Align::Center);
        let top = above_hints - flow.height;
        for ((panel, (_, entries)), rect) in visible.iter().zip(&cards).zip(&flow.items) {
            let rect = rect.offset(0.0, top);
            ui.panel(rect, colors.panel, colors.panel_border, 8.0, false);
            let mut inner = rect.inset_xy(10.0, 0.0);
            let title_width = ui.measure(&panel.title, 13.5, FontId::Bold);
            let title = inner.cut_left(title_width);
            ui.label(title, &panel.title, 13.5, FontId::Bold, colors.text, Align::Start);
            for (index, (entry, width)) in panel.entries.iter().zip(entries).enumerate() {
                inner.cut_left(12.0);
                let slot = inner.cut_left(*width);
                match &entry.action_id {
                    Some(action_id) => {
                        let height = ui.button_height(12.0).min(slot.height - 8.0);
                        let button_rect = slot.centered(slot.width, height);
                        let id = WidgetId::new("panel").with(&panel.id).with(index);
                        let button = Button::new(&entry.text).size(12.0).enabled(entry.enabled).focusable(false);
                        if ui.button(id, button_rect, button) {
                            actions.push(GameAction::Command(Command::PerformAction { action_id: action_id.clone() }));
                        }
                    }
                    None => {
                        ui.label(slot, &entry.text, 13.0, FontId::Regular, colors.text, Align::Start);
                    }
                }
            }
        }
        above_hints = top - 8.0;
    }

    // ── "Made with" credit ──
    if view.show_made_with {
        let text = "Made with Farming RPG Maker";
        let width = ui.measure(text, 11.0, FontId::Regular) + 16.0;
        let rect = Rect::new(screen.right() - 12.0 - width, above_hints - 18.0, width, 18.0);
        ui.list_mut().fill_round_rect(rect, 5.0, fade(colors.hud, 0.75));
        ui.label(rect, text, 11.0, FontId::Regular, colors.muted, Align::Center);
    }
    bar.bottom()
}
