//! Widgets drawn in the theme: panels, buttons, toggles, sliders, steppers, progress bars,
//! keycaps and the modal card scaffold (web Card: header, scrolling body, optional footer).

use crate::icons::{self, Icon};
use crate::layout::{Align, RectExt};
use crate::theme::{fade, mix};
use crate::ui::{HitKind, Interaction, Ui, WidgetId};
use farm_render::{Color, DrawCmd, FontId, Rect};

/// How a button looks.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ButtonKind {
    /// Gold: the main action of a row or screen.
    Primary,
    /// Outlined surface button (toolbar, secondary actions).
    #[default]
    Secondary,
    /// Transparent until hovered (close buttons).
    Subtle,
    /// Full-width, left-aligned choice (dialogue options).
    Option,
    /// A tab; `selected` draws it as the current one.
    Tab { selected: bool },
    /// Large menu entry (title screen, pause menu).
    Menu,
}

/// A button's content and options.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Button<'a> {
    pub label: &'a str,
    pub kind: ButtonKind,
    pub enabled: bool,
    pub icon: Option<Icon>,
    /// A keycap drawn before the label (dialogue option numbers).
    pub keycap: Option<&'a str>,
    /// Font size (logical, before the text-size setting).
    pub size: f32,
    /// Takes keyboard/gamepad focus.
    pub focusable: bool,
    /// Preferred focus when its screen opens.
    pub default_focus: bool,
}

impl<'a> Button<'a> {
    pub fn new(label: &'a str) -> Self {
        Self {
            label,
            kind: ButtonKind::Secondary,
            enabled: true,
            icon: None,
            keycap: None,
            size: 13.0,
            focusable: true,
            default_focus: false,
        }
    }

    pub fn kind(mut self, kind: ButtonKind) -> Self {
        self.kind = kind;
        self
    }

    pub fn primary(self) -> Self {
        self.kind(ButtonKind::Primary)
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.enabled = enabled;
        self
    }

    pub fn icon(mut self, icon: Icon) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn keycap(mut self, keycap: &'a str) -> Self {
        self.keycap = Some(keycap);
        self
    }

    pub fn size(mut self, size: f32) -> Self {
        self.size = size;
        self
    }

    pub fn focusable(mut self, focusable: bool) -> Self {
        self.focusable = focusable;
        self
    }

    pub fn default_focus(mut self) -> Self {
        self.default_focus = true;
        self
    }
}

/// The areas of an open modal card.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Modal {
    /// The whole card.
    pub card: Rect,
    /// The scrolling body's visible area.
    pub body: Rect,
    /// Where body content starts (scrolled); pass the content's bottom to [`Ui::end_modal`].
    pub top: f32,
    /// The footer strip, when one was asked for.
    pub footer: Option<Rect>,
    /// The header's close button was activated.
    pub close: bool,
}

/// What a modal card shows around its body.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ModalSpec<'a> {
    pub id: WidgetId,
    pub icon: Icon,
    pub title: &'a str,
    pub subtitle: Option<&'a str>,
    /// Card width (logical); narrower screens shrink it.
    pub width: f32,
    pub max_height: f32,
    /// Footer height (0 = none).
    pub footer: f32,
    /// Draw the dimming backdrop.
    pub backdrop: bool,
}

/// Horizontal padding of buttons.
const BUTTON_PAD_X: f32 = 12.0;

impl Ui {
    // ── Surfaces ───────────────────────────────────────────────────────

    /// A rounded panel with a border and an optional soft shadow.
    pub fn panel(&mut self, rect: Rect, fill: Color, border: Color, radius: f32, shadow: bool) {
        if shadow && !rect.is_empty() {
            let color = self.theme().colors.shadow;
            self.list_mut().push(DrawCmd::BlurRect { rect: rect.offset(0.0, 6.0).inset(4.0), color, sigma: 10.0 });
        }
        self.bordered(rect, radius, fill, border);
    }

    /// A rounded rectangle with a 1-unit border. Opaque ones are two fills (border, then the
    /// inside), which rasterize much faster than a stroke.
    pub fn bordered(&mut self, rect: Rect, radius: f32, fill: Color, border: Color) {
        if fill.a == 255 && border.a == 255 && rect.width > 2.0 && rect.height > 2.0 {
            self.list_mut().fill_round_rect(rect, radius, border);
            self.list_mut().fill_round_rect(rect.inset(1.0), (radius - 1.0).max(0.0), fill);
            return;
        }
        if fill.a > 0 {
            self.list_mut().fill_round_rect(rect, radius, fill);
        }
        if border.a > 0 {
            self.list_mut().stroke_round_rect(rect.inset(0.5), radius, border, 1.0);
        }
    }

    /// A list row background (web `Border.row`).
    pub fn row_background(&mut self, rect: Rect) {
        let colors = self.theme().colors;
        let radius = self.theme().small_radius;
        self.panel(rect, colors.row, colors.row_border, radius, false);
    }

    /// A vertical gradient over `rect` from `top` to `bottom` (drawn as bands).
    pub fn vertical_gradient(&mut self, rect: Rect, top: Color, bottom: Color) {
        let bands = 16;
        let band = rect.height / bands as f32;
        for index in 0..bands {
            let t = (index as f32 + 0.5) / bands as f32;
            let color = mix(top, bottom, t);
            let y = rect.y + index as f32 * band;
            // Overlap by a pixel so bands never show seams.
            self.list_mut().push(DrawCmd::FillRect {
                rect: Rect::new(rect.x, y.floor(), rect.width, band.ceil() + 1.0),
                color,
                anti_alias: false,
            });
        }
    }

    /// A full-screen dimming layer.
    ///
    /// It dims the world (see [`Ui::tint_world`]); the HUD above the world stays as it is.
    pub fn backdrop(&mut self) {
        let color = self.theme().colors.backdrop;
        self.tint_world(color, color);
    }

    // ── Buttons ────────────────────────────────────────────────────────

    /// Width a button needs for its content.
    pub fn button_width(&self, button: &Button<'_>) -> f32 {
        let mut width = self.measure(button.label, button.size, FontId::Bold) + BUTTON_PAD_X * 2.0;
        if button.icon.is_some() {
            width += self.font_size(button.size) + if button.label.is_empty() { 0.0 } else { 6.0 };
        }
        if let Some(key) = button.keycap {
            width += self.keycap_width(key) + 8.0;
        }
        width.ceil()
    }

    /// Standard button height for a font size.
    pub fn button_height(&self, size: f32) -> f32 {
        (self.font_size(size) * 2.15).max(26.0).ceil()
    }

    /// Draws a button; true when it was activated this frame.
    pub fn button(&mut self, id: WidgetId, rect: Rect, button: Button<'_>) -> bool {
        let state = if button.enabled {
            if button.default_focus {
                self.interact_default(id, rect, HitKind::Button)
            } else {
                self.interact(id, rect, HitKind::Button, button.focusable)
            }
        } else {
            Interaction::default()
        };
        self.draw_button(rect, &button, &state);
        button.enabled && state.clicked
    }

    /// A button that reports press and release (hold-to-act minigames).
    pub fn hold_button(&mut self, id: WidgetId, rect: Rect, button: Button<'_>) -> Interaction {
        let state = self.interact(id, rect, HitKind::Button, button.focusable);
        let shown = Interaction { active: state.active || self.input().accept_held && state.focused, ..state };
        self.draw_button(rect, &button, &shown);
        state
    }

    fn draw_button(&mut self, rect: Rect, button: &Button<'_>, state: &Interaction) {
        let colors = self.theme().colors;
        let radius = self.theme().small_radius;
        let (mut fill, mut border, mut text) = match button.kind {
            ButtonKind::Primary => (colors.accent, mix(colors.accent, Color::BLACK, 0.25), colors.on_accent),
            ButtonKind::Subtle => (Color::TRANSPARENT, Color::TRANSPARENT, colors.muted),
            ButtonKind::Tab { selected: true } => (colors.success_tint, colors.primary, colors.text),
            ButtonKind::Menu => (fade(colors.button, 0.92), colors.button_border, colors.text),
            _ => (colors.button, colors.button_border, colors.text),
        };
        if state.active {
            fill = match button.kind {
                ButtonKind::Primary => mix(colors.accent, Color::BLACK, 0.18),
                _ => colors.button_pressed,
            };
        } else if state.hovered || state.focus_visible {
            fill = match button.kind {
                ButtonKind::Primary => mix(colors.accent, Color::WHITE, 0.2),
                ButtonKind::Subtle => fade(colors.button_hover, 0.8),
                _ => colors.button_hover,
            };
            if matches!(button.kind, ButtonKind::Option | ButtonKind::Menu) {
                border = colors.accent;
            }
            if button.kind == ButtonKind::Subtle {
                text = colors.text;
            }
        }
        if !button.enabled {
            // Disabled buttons turn neutral, whatever their kind.
            fill = if button.kind == ButtonKind::Subtle { Color::TRANSPARENT } else { fade(colors.button, 0.55) };
            border = fade(colors.button_border, 0.5);
            text = fade(colors.muted, 0.75);
        }
        self.bordered(rect, radius, fill, border);
        if state.focus_visible {
            self.focus_ring(rect, radius);
        }
        let px = self.font_size(button.size);
        let mut content = rect.inset_xy(BUTTON_PAD_X, 0.0);
        let align = match button.kind {
            ButtonKind::Option => Align::Start,
            _ => Align::Center,
        };
        // Center icon + keycap + label as a group unless left-aligned.
        let icon_width = if button.icon.is_some() { px + if button.label.is_empty() { 0.0 } else { 6.0 } } else { 0.0 };
        let key_width = button.keycap.map_or(0.0, |key| self.keycap_width(key) + 8.0);
        let label_width = self.measure(button.label, button.size, FontId::Bold);
        let group = (icon_width + key_width + label_width).min(content.width);
        if align == Align::Center {
            content = content.aligned(group, Align::Center);
        }
        if let Some(icon) = button.icon {
            let icon_rect = content.cut_left(icon_width);
            let square = Rect::new(icon_rect.x, rect.y + (rect.height - px) / 2.0, px, px);
            icons::draw(self.list_mut(), icon, square, text, fill);
        }
        if let Some(key) = button.keycap {
            let key_rect = content.cut_left(key_width);
            self.keycap(key_rect.x, rect.y + rect.height / 2.0, key, button.enabled);
        }
        self.label(content, button.label, button.size, FontId::Bold, text, Align::Start);
    }

    /// The focus outline around a widget.
    pub fn focus_ring(&mut self, rect: Rect, radius: f32) {
        let color = self.theme().colors.focus;
        let ring = Rect::new(rect.x - 2.5, rect.y - 2.5, rect.width + 5.0, rect.height + 5.0);
        self.list_mut().stroke_round_rect(ring, radius + 2.5, color, 2.0);
    }

    // ── Keycaps, chips, progress ────────────────────────────────────────

    pub fn keycap_width(&self, key: &str) -> f32 {
        (self.measure(key, 11.0, FontId::Bold) + 10.0).max(self.font_size(11.0) + 9.0).ceil()
    }

    /// A small key label centered vertically on `center_y`; returns its width.
    pub fn keycap(&mut self, x: f32, center_y: f32, key: &str, enabled: bool) -> f32 {
        let colors = self.theme().colors;
        let width = self.keycap_width(key);
        let height = (self.font_size(11.0) + 8.0).ceil();
        let rect = Rect::new(x, center_y - height / 2.0, width, height);
        let alpha = if enabled { 1.0 } else { 0.5 };
        self.list_mut().fill_round_rect(rect.offset(0.0, 1.5), 4.0, fade(colors.panel_border, alpha));
        self.list_mut().fill_round_rect(rect, 4.0, fade(colors.row, alpha));
        self.list_mut().stroke_round_rect(rect.inset(0.5), 4.0, fade(colors.button_border, alpha), 1.0);
        self.label(rect, key, 11.0, FontId::Bold, fade(colors.text, alpha), Align::Center);
        width
    }

    /// A rounded chip with text (money, quantities); returns its width.
    #[allow(clippy::too_many_arguments)]
    pub fn chip(&mut self, x: f32, center_y: f32, text: &str, size: f32, fill: Color, color: Color) -> f32 {
        let width = (self.measure(text, size, FontId::Bold) + 14.0).ceil();
        let height = (self.font_size(size) + 8.0).ceil();
        let rect = Rect::new(x, center_y - height / 2.0, width, height);
        self.list_mut().fill_round_rect(rect, 5.0, fill);
        self.label(rect, text, size, FontId::Bold, color, Align::Center);
        width
    }

    /// A horizontal progress bar.
    pub fn progress(&mut self, rect: Rect, ratio: f32, color: Color) {
        let colors = self.theme().colors;
        let radius = rect.height / 2.0;
        self.list_mut().fill_round_rect(rect, radius, colors.track);
        let ratio = if ratio.is_finite() { ratio.clamp(0.0, 1.0) } else { 0.0 };
        if ratio > 0.0 {
            let filled = Rect::new(rect.x, rect.y, (rect.width * ratio).max(rect.height), rect.height);
            self.list_mut().fill_round_rect(filled, radius, color);
        }
    }

    // ── Settings controls ───────────────────────────────────────────────

    /// A labeled on/off switch filling `rect`; returns the new value when toggled.
    pub fn toggle(&mut self, id: WidgetId, rect: Rect, label: &str, value: bool) -> Option<bool> {
        let state = self.interact(id, rect, HitKind::Button, true);
        let colors = self.theme().colors;
        if state.hovered || state.focus_visible {
            let radius = self.theme().small_radius;
            self.list_mut().fill_round_rect(rect, radius, fade(colors.button_hover, 0.6));
        }
        if state.focus_visible {
            let radius = self.theme().small_radius;
            self.focus_ring(rect, radius);
        }
        let mut row = rect.inset_xy(10.0, 0.0);
        let switch = row.cut_right(44.0).centered(44.0, 24.0);
        self.label(row, label, 14.0, FontId::Regular, colors.text, Align::Start);
        let track = if value { colors.primary } else { colors.track };
        self.list_mut().fill_round_rect(switch, 12.0, track);
        let knob_x = if value { switch.right() - 12.0 } else { switch.x + 12.0 };
        self.list_mut().fill_circle(knob_x, switch.y + 12.0, 9.0, colors.text);
        state.clicked.then_some(!value)
    }

    /// A labeled slider over 0..=1 filling `rect`; `step` is the keyboard increment. Returns the
    /// new value when changed.
    pub fn slider(&mut self, id: WidgetId, rect: Rect, label: &str, value: f32, step: f32) -> Option<f32> {
        let state = self.interact(id, rect, HitKind::Slider, true);
        let colors = self.theme().colors;
        let radius = self.theme().small_radius;
        if state.hovered || state.focus_visible {
            self.list_mut().fill_round_rect(rect, radius, fade(colors.button_hover, 0.6));
        }
        if state.focus_visible {
            self.focus_ring(rect, radius);
        }
        let mut row = rect.inset_xy(10.0, 0.0);
        let percent = format!("{}%", (value.clamp(0.0, 1.0) * 100.0 + 0.5).floor());
        let value_rect = row.cut_right(52.0);
        let track_area = row.cut_right((row.width * 0.5).min(260.0));
        self.label(row, label, 14.0, FontId::Regular, colors.text, Align::Start);
        self.label(value_rect, &percent, 13.0, FontId::Bold, colors.muted, Align::End);
        let track =
            Rect::new(track_area.x + 8.0, track_area.y + track_area.height / 2.0 - 3.0, track_area.width - 16.0, 6.0);
        self.progress(track, value, colors.accent);
        let knob_x = track.x + track.width * value.clamp(0.0, 1.0);
        self.list_mut().fill_circle(knob_x, track.y + 3.0, 8.0, colors.text);
        let mut next = value;
        let steps = self.slider_steps(id);
        if steps != 0 {
            next = ((value / step + 0.5).floor() + steps as f32) * step;
        }
        if state.active {
            // Dragging from the track (not the label) sets the value under the pointer.
            if let Some((x, _)) = self.pointer().filter(|(x, _)| *x >= track_area.x - 24.0) {
                next = (x - track.x) / track.width.max(1.0);
                next = ((next / step * 5.0 + 0.5).floor() / 5.0) * step;
            }
        }
        let next = next.clamp(0.0, 1.0);
        ((next - value).abs() > 1e-4).then_some(next)
    }

    /// A labeled choice cycled with left/right or clicks on its halves; returns the new index.
    pub fn stepper(&mut self, id: WidgetId, rect: Rect, label: &str, options: &[&str], index: usize) -> Option<usize> {
        let state = self.interact(id, rect, HitKind::Slider, true);
        let colors = self.theme().colors;
        let radius = self.theme().small_radius;
        if state.hovered || state.focus_visible {
            self.list_mut().fill_round_rect(rect, radius, fade(colors.button_hover, 0.6));
        }
        if state.focus_visible {
            self.focus_ring(rect, radius);
        }
        let mut row = rect.inset_xy(10.0, 0.0);
        let control = row.cut_right((row.width * 0.5).min(260.0));
        self.label(row, label, 14.0, FontId::Regular, colors.text, Align::Start);
        let current = options.get(index).copied().unwrap_or("");
        let mut inner = control;
        let left = inner.cut_left(24.0);
        let right = inner.cut_right(24.0);
        let can_left = index > 0;
        let can_right = index + 1 < options.len();
        let arrow = |enabled: bool| if enabled { colors.accent_text } else { fade(colors.muted, 0.4) };
        self.label(left, "\u{25C0}", 12.0, FontId::Regular, arrow(can_left), Align::Center);
        self.label(right, "\u{25B6}", 12.0, FontId::Regular, arrow(can_right), Align::Center);
        self.label(inner, current, 14.0, FontId::Bold, colors.text, Align::Center);
        let mut next = index as i64 + i64::from(self.slider_steps(id));
        if state.clicked {
            if let Some((x, _)) = self.pointer() {
                if x < control.x + control.width / 2.0 {
                    next -= 1;
                } else {
                    next += 1;
                }
            } else {
                next += 1;
            }
        }
        let next = next.clamp(0, options.len().saturating_sub(1) as i64) as usize;
        (next != index).then_some(next)
    }

    // ── Modal card ──────────────────────────────────────────────────────

    /// Opens a modal card (backdrop, header with icon, title, subtitle and close button, a
    /// scrolling body and an optional footer) on a new layer. The card grows with the body's
    /// content height up to `max_height`. Close with [`Ui::end_modal`].
    pub fn begin_modal(&mut self, spec: ModalSpec<'_>) -> Modal {
        self.push_layer();
        let screen = self.screen();
        if spec.backdrop {
            self.backdrop();
        }
        self.blocker(screen);
        let colors = self.theme().colors;
        let radius = self.theme().radius;
        let header_height =
            (self.line_height(16.5) + if spec.subtitle.is_some() { self.line_height(12.5) } else { 0.0 }).max(40.0)
                + 24.0;
        let body_id = spec.id.with("body");
        let content = self.scroll_metrics(body_id).map_or(spec.max_height, |(_, content, _)| content);
        let width = spec.width.min(screen.width - 24.0).max(200.0);
        let max_height = spec.max_height.min(screen.height - 24.0);
        // `content` already holds the body's top and bottom padding.
        let height =
            (header_height + content + 4.0 + spec.footer).clamp(header_height + spec.footer + 60.0, max_height);
        let card = screen.centered(width, height);
        self.panel(card, colors.panel, colors.panel_border, radius, true);
        self.blocker(card);

        let mut area = card;
        let mut header = area.cut_top(header_height);
        self.list_mut().line(
            (header.x + 1.0, header.bottom() - 0.5),
            (header.right() - 1.0, header.bottom() - 0.5),
            colors.panel_border,
            1.0,
        );
        header = header.inset_xy(16.0, 12.0);
        let icon_tile = header.cut_left(40.0).centered(40.0, 40.0);
        self.list_mut().fill_round_rect(icon_tile, 8.0, fade(colors.primary, 0.16));
        icons::draw(self.list_mut(), spec.icon, icon_tile.centered(22.0, 22.0), colors.primary, colors.panel);
        header.cut_left(12.0);
        let close_rect = header.cut_right(34.0).centered(34.0, 34.0);
        let close = self.button(
            spec.id.with("close"),
            close_rect,
            Button::new("").kind(ButtonKind::Subtle).icon(Icon::Close).size(14.0).focusable(false),
        );
        let title_height = self.line_height(16.5);
        let sub_height = if spec.subtitle.is_some() { self.line_height(12.5) } else { 0.0 };
        let mut titles = header.centered(header.width, title_height + sub_height);
        let title_rect = titles.cut_top(title_height);
        self.label(title_rect, spec.title, 16.5, FontId::Bold, colors.text, Align::Start);
        if let Some(subtitle) = spec.subtitle {
            self.label(titles, subtitle, 12.5, FontId::Regular, colors.muted, Align::Start);
        }

        let footer = (spec.footer > 0.0).then(|| {
            let footer = area.cut_bottom(spec.footer);
            // A tinted strip with the card's bottom corners.
            self.list_mut().fill_round_rect(footer, radius, colors.panel_header);
            self.list_mut().fill_rect(Rect::new(footer.x, footer.y, footer.width, radius), colors.panel_header);
            self.list_mut().stroke_round_rect(card.inset(0.5), radius, colors.panel_border, 1.0);
            self.list_mut().line(
                (footer.x + 1.0, footer.y + 0.5),
                (footer.right() - 1.0, footer.y + 0.5),
                colors.panel_border,
                1.0,
            );
            footer.inset_xy(16.0, 0.0)
        });
        let body = area.inset_xy(0.0, 2.0);
        let top = self.begin_scroll(body_id, body) + 12.0;
        Modal { card, body: body.inset_xy(16.0, 0.0), top, footer, close }
    }

    /// Ends the modal begun last; `content_bottom` is where the body content ended.
    pub fn end_modal(&mut self, content_bottom: f32) {
        self.end_modal_body(content_bottom);
        self.close_modal();
    }

    /// Ends the modal's scrolling body; draw the footer next, then [`Ui::close_modal`].
    pub fn end_modal_body(&mut self, content_bottom: f32) {
        self.end_scroll(content_bottom + 12.0);
    }

    /// Leaves the modal's layer.
    pub fn close_modal(&mut self) {
        self.pop_layer();
    }

    /// A centered empty-state message (icon, title, detail) from `top`; returns its height.
    pub fn empty_state(&mut self, area: Rect, top: f32, icon: Icon, title: &str, detail: &str) -> f32 {
        let colors = self.theme().colors;
        let icon_rect = Rect::new(area.x + area.width / 2.0 - 22.0, top + 24.0, 44.0, 44.0);
        icons::draw(self.list_mut(), icon, icon_rect, fade(colors.muted, 0.45), colors.panel);
        let title_rect = Rect::new(area.x, icon_rect.bottom() + 10.0, area.width, self.line_height(14.0));
        self.label(title_rect, title, 14.0, FontId::Bold, colors.text, Align::Center);
        let detail_rect = Rect::new(area.x, title_rect.bottom(), area.width, self.line_height(12.5));
        self.label(detail_rect, detail, 12.5, FontId::Regular, colors.muted, Align::Center);
        detail_rect.bottom() + 24.0 - top
    }

    /// A small upper-case section heading from `top`; returns its height.
    pub fn section(&mut self, area: Rect, top: f32, text: &str, color: Option<Color>) -> f32 {
        let colors = self.theme().colors;
        let height = self.line_height(11.5) + 6.0;
        let rect = Rect::new(area.x, top + 4.0, area.width, height - 4.0);
        self.label(rect, &text.to_uppercase(), 11.5, FontId::Bold, color.unwrap_or(colors.muted), Align::Start);
        height
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::input::{NavAction, UiInput};

    fn run(ui: &mut Ui, input: UiInput, build: impl FnOnce(&mut Ui)) -> farm_render::DrawList {
        ui.begin_frame(input, (1000.0, 700.0), 1.0, 1.0, 0.0);
        build(ui);
        ui.end_frame()
    }

    #[test]
    fn disabled_buttons_ignore_clicks_and_focus() {
        let mut ui = Ui::default();
        let id = WidgetId::new("buy");
        let rect = Rect::new(10.0, 10.0, 80.0, 28.0);
        let mut clicked = false;
        for input in [
            UiInput::default(),
            UiInput { pointer: Some((20.0, 20.0)), pointer_pressed: true, pointer_down: true, ..UiInput::default() },
            UiInput { pointer: Some((20.0, 20.0)), pointer_released: true, ..UiInput::default() },
            UiInput { nav: vec![NavAction::Accept], ..UiInput::default() },
        ] {
            run(&mut ui, input, |ui| clicked |= ui.button(id, rect, Button::new("Buy").enabled(false)));
        }
        assert!(!clicked);
        assert_eq!(ui.focused(), None);
    }

    #[test]
    fn button_width_grows_with_text_size() {
        let mut ui = Ui::default();
        ui.begin_frame(UiInput::default(), (800.0, 600.0), 1.0, 1.0, 0.0);
        let normal = ui.button_width(&Button::new("Inventory"));
        ui.end_frame();
        ui.begin_frame(UiInput::default(), (800.0, 600.0), 1.0, 1.4, 0.0);
        let large = ui.button_width(&Button::new("Inventory"));
        ui.end_frame();
        assert!(large > normal * 1.2, "{normal} → {large}");
    }

    #[test]
    fn sliders_step_with_left_and_right_and_toggles_flip() {
        let mut ui = Ui::default();
        let slider = WidgetId::new("volume");
        let toggle = WidgetId::new("mute");
        let mut value = 0.5f32;
        let mut muted = false;
        let build = |ui: &mut Ui, value: &mut f32, muted: &mut bool| {
            if let Some(v) = ui.slider(slider, Rect::new(0.0, 0.0, 400.0, 40.0), "Master", *value, 0.1) {
                *value = v;
            }
            if let Some(m) = ui.toggle(toggle, Rect::new(0.0, 50.0, 400.0, 40.0), "Mute", *muted) {
                *muted = m;
            }
        };
        run(&mut ui, UiInput::default(), |ui| build(ui, &mut value, &mut muted));
        run(&mut ui, UiInput { nav: vec![NavAction::Right, NavAction::Right], ..UiInput::default() }, |ui| {
            build(ui, &mut value, &mut muted)
        });
        assert!((value - 0.7).abs() < 1e-5, "{value}");
        run(&mut ui, UiInput { nav: vec![NavAction::Down, NavAction::Accept], ..UiInput::default() }, |ui| {
            build(ui, &mut value, &mut muted)
        });
        assert!(muted);
    }

    #[test]
    fn steppers_cycle_within_their_options() {
        let mut ui = Ui::default();
        let id = WidgetId::new("size");
        let mut index = 1usize;
        let options = ["Small", "Default", "Large"];
        for nav in [
            vec![],
            vec![NavAction::Right],
            vec![NavAction::Right],
            vec![NavAction::Left, NavAction::Left, NavAction::Left],
        ] {
            run(&mut ui, UiInput { nav, ..UiInput::default() }, |ui| {
                if let Some(next) = ui.stepper(id, Rect::new(0.0, 0.0, 400.0, 40.0), "Text size", &options, index) {
                    index = next;
                }
            });
        }
        assert_eq!(index, 0);
    }

    #[test]
    fn modal_close_button_and_body_scroll() {
        let mut ui = Ui::default();
        let spec = ModalSpec {
            id: WidgetId::new("m"),
            icon: Icon::Package,
            title: "Inventory",
            subtitle: Some("3 / 20 slots used"),
            width: 600.0,
            max_height: 400.0,
            footer: 0.0,
            backdrop: true,
        };
        let mut closed = false;
        let mut card = Rect::default();
        for _ in 0..2 {
            run(&mut ui, UiInput::default(), |ui| {
                let modal = ui.begin_modal(spec);
                card = modal.card;
                ui.end_modal(modal.top + 1000.0);
            });
        }
        assert_eq!(card.height, 400.0, "tall content caps the card");
        let close = ui.last_rect(WidgetId::new("m").with("close")).unwrap();
        let (x, y) = close.center();
        for (pressed, released) in [(true, false), (false, true)] {
            let input = UiInput {
                pointer: Some((x, y)),
                pointer_pressed: pressed,
                pointer_down: pressed,
                pointer_released: released,
                ..UiInput::default()
            };
            run(&mut ui, input, |ui| {
                let modal = ui.begin_modal(spec);
                closed |= modal.close;
                ui.end_modal(modal.top + 1000.0);
            });
        }
        assert!(closed);
    }
}
