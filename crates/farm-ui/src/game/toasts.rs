//! Stacked toasts (web sonner, C# `ToastHost`): success, error and info messages in the top-right
//! corner, newest on top, at most four. A repeat of the newest message collapses into it with a
//! "×N" counter.
//!
//! A toast stays long enough to read: at least [`TOAST_LIFETIME`], longer for long text (about
//! fifteen characters a second), and errors twice as long. Pointing at a toast holds it; clicking
//! it dismisses it.

use crate::icons::{self, Icon};
use crate::layout::{Align, RectExt};
use crate::theme::fade;
use crate::ui::{HitKind, Ui, WidgetId};
use farm_render::{FontId, Rect, TextAlign};

/// Most toasts shown at once; older ones drop off.
pub const MAX_VISIBLE: usize = 4;
/// Seconds a short toast stays.
pub const TOAST_LIFETIME: f64 = 3.5;
/// Longest a toast stays without being pointed at (errors get twice this).
pub const MAX_TOAST_LIFETIME: f64 = 15.0;
/// Reading speed the lifetime allows for, in characters per second.
const READ_CHARS_PER_SECOND: f64 = 15.0;
const FADE_IN: f64 = 0.15;
const FADE_OUT: f64 = 0.4;

/// Seconds a toast of `text` stays: long enough to read it, errors twice as long.
pub fn toast_lifetime(text: &str, kind: ToastKind) -> f64 {
    let reading = 1.5 + text.chars().count() as f64 / READ_CHARS_PER_SECOND;
    let lifetime = reading.clamp(TOAST_LIFETIME, MAX_TOAST_LIFETIME);
    match kind {
        ToastKind::Error => (lifetime * 2.0).max(8.0),
        ToastKind::Info | ToastKind::Success => lifetime,
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ToastKind {
    #[default]
    Info,
    Success,
    Error,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Toast {
    pub text: String,
    pub kind: ToastKind,
    /// How many times it was shown in a row.
    pub count: u32,
    /// Seconds since it was (last) shown.
    pub age: f64,
    /// Seconds it stays ([`toast_lifetime`]).
    pub lifetime: f64,
    /// Stable id for hit testing.
    pub id: u64,
    /// The pointer is on it: it does not age.
    pub held: bool,
}

/// The toast stack.
#[derive(Debug, Clone, Default)]
pub struct Toasts {
    /// Newest first.
    items: Vec<Toast>,
    /// Every toast shown (tests, diagnostics); bounded.
    history: Vec<(String, ToastKind)>,
    /// Where the stack starts (below the HUD).
    pub top: f32,
    next_id: u64,
}

impl Toasts {
    pub fn push(&mut self, text: impl Into<String>, kind: ToastKind) {
        let text = text.into();
        if self.history.len() >= 256 {
            self.history.remove(0);
        }
        self.history.push((text.clone(), kind));
        if let Some(newest) = self.items.first_mut() {
            if newest.text == text && newest.kind == kind {
                newest.count += 1;
                newest.age = FADE_IN;
                return;
            }
        }
        let lifetime = toast_lifetime(&text, kind);
        self.next_id += 1;
        self.items.insert(0, Toast { text, kind, count: 1, age: 0.0, lifetime, id: self.next_id, held: false });
        self.items.truncate(MAX_VISIBLE);
    }

    /// Ages the toasts by `seconds` (except one the pointer holds) and drops expired ones.
    pub fn tick(&mut self, seconds: f64) {
        let seconds = if seconds.is_finite() { seconds.max(0.0) } else { 0.0 };
        for toast in &mut self.items {
            if toast.held {
                // Fully shown again while held.
                toast.age = toast.age.min(toast.lifetime - FADE_OUT).max(FADE_IN);
            } else {
                toast.age += seconds;
            }
        }
        self.items.retain(|toast| toast.age < toast.lifetime);
    }

    /// Removes a toast (clicked away).
    pub fn dismiss(&mut self, id: u64) {
        self.items.retain(|toast| toast.id != id);
    }

    pub fn visible(&self) -> &[Toast] {
        &self.items
    }

    pub fn history(&self) -> &[(String, ToastKind)] {
        &self.history
    }

    pub fn clear(&mut self) {
        self.items.clear();
    }

    /// Draws the stack at the top-right corner, from [`Toasts::top`]. A toast under the pointer
    /// is held; a clicked one is dismissed.
    pub fn draw(&mut self, ui: &mut Ui) {
        let colors = ui.theme().colors;
        let screen = ui.screen();
        let max_width = 420.0f32.min(screen.width - 32.0);
        let mut y = self.top.max(12.0);
        let mut dismissed = None;
        for toast in &mut self.items {
            let opacity = if ui.reduced_motion() {
                1.0
            } else {
                let fade_in = (toast.age / FADE_IN).min(1.0);
                let fade_out = ((toast.lifetime - toast.age) / FADE_OUT).min(1.0);
                fade_in.min(fade_out).max(0.0) as f32
            };
            let counter = (toast.count > 1).then(|| format!("\u{00d7}{}", toast.count));
            let counter_width = counter.as_ref().map_or(0.0, |text| ui.measure(text, 12.5, FontId::Regular) + 8.0);
            let text_width = max_width - 24.0 - 26.0 - counter_width;
            let lines = ui.wrap(&toast.text, 13.5, FontId::Regular, text_width);
            let natural = lines.iter().map(|line| ui.measure(line, 13.5, FontId::Regular)).fold(0.0f32, f32::max);
            let width = (natural + 24.0 + 26.0 + counter_width).clamp(240.0f32.min(max_width), max_width);
            let height = (lines.len() as f32 * ui.line_height(13.5)).max(20.0) + 16.0;
            let rect = Rect::new(screen.right() - 16.0 - width, y, width, height);
            let interaction = ui.interact(WidgetId::new("toast").with(toast.id as usize), rect, HitKind::Button, false);
            toast.held = interaction.hovered;
            if interaction.clicked {
                dismissed = Some(toast.id);
            }
            let (fill, border, icon, icon_color) = match toast.kind {
                ToastKind::Success => (colors.success_tint, colors.primary, Icon::Check, colors.primary),
                ToastKind::Error => (colors.error_tint, colors.error, Icon::Alert, colors.error),
                ToastKind::Info => (colors.panel, colors.panel_border, Icon::Info, colors.muted),
            };
            if opacity >= 0.99 {
                ui.panel(rect, fill, fade(border, 0.8), 8.0, true);
            } else {
                ui.panel(rect, fade(fill, opacity), fade(border, 0.8 * opacity), 8.0, false);
            }
            let mut inner = rect.inset_xy(12.0, 8.0);
            let icon_rect = inner.cut_left(16.0);
            let icon_top = inner.y + (ui.line_height(13.5) - 16.0) / 2.0;
            icons::draw(
                ui.list_mut(),
                icon,
                Rect::new(icon_rect.x, icon_top, 16.0, 16.0),
                fade(icon_color, opacity),
                fill,
            );
            inner.cut_left(10.0);
            if let Some(counter) = &counter {
                let counter_rect = inner.cut_right(counter_width);
                let line = Rect::new(counter_rect.x, inner.y, counter_rect.width, ui.line_height(13.5));
                ui.label(line, counter, 12.5, FontId::Regular, fade(colors.muted, opacity), Align::End);
            }
            ui.paragraph(
                inner.x,
                inner.y,
                inner.width,
                &toast.text,
                13.5,
                FontId::Regular,
                fade(colors.text, opacity),
                TextAlign::Left,
            );
            y += height + 8.0;
        }
        if let Some(id) = dismissed {
            self.dismiss(id);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn repeats_collapse_and_old_toasts_expire() {
        let mut toasts = Toasts::default();
        toasts.push("Watered!", ToastKind::Success);
        toasts.push("Watered!", ToastKind::Success);
        assert_eq!(toasts.visible().len(), 1);
        assert_eq!(toasts.visible()[0].count, 2);
        for index in 0..6 {
            toasts.push(format!("Message {index}"), ToastKind::Info);
        }
        assert_eq!(toasts.visible().len(), MAX_VISIBLE);
        assert_eq!(toasts.visible()[0].text, "Message 5");
        toasts.tick(TOAST_LIFETIME + 0.1);
        assert!(toasts.visible().is_empty());
        assert_eq!(toasts.history().len(), 8);
    }

    #[test]
    fn long_and_error_toasts_stay_longer() {
        assert_eq!(toast_lifetime("Watered!", ToastKind::Success), TOAST_LIFETIME);
        let long = "Quarantined items: ".to_owned() + &"item-".repeat(30);
        let reading = toast_lifetime(&long, ToastKind::Info);
        assert!(reading > 10.0 && reading <= MAX_TOAST_LIFETIME, "{reading}");
        assert_eq!(toast_lifetime(&"x".repeat(10_000), ToastKind::Info), MAX_TOAST_LIFETIME);
        assert_eq!(toast_lifetime("Oops", ToastKind::Error), 8.0);
        assert_eq!(toast_lifetime(&long, ToastKind::Error), 2.0 * reading);

        let mut toasts = Toasts::default();
        toasts.push(long.clone(), ToastKind::Info);
        toasts.push("Short", ToastKind::Info);
        toasts.tick(TOAST_LIFETIME + 0.1);
        assert_eq!(toasts.visible().len(), 1, "the short one went, the long one stays");
        assert_eq!(toasts.visible()[0].text, long);
    }

    #[test]
    fn held_toasts_wait_and_dismissed_ones_go() {
        let mut toasts = Toasts::default();
        toasts.push("Hold me", ToastKind::Info);
        toasts.push("Dismiss me", ToastKind::Info);
        toasts.items[1].held = true;
        toasts.tick(60.0);
        assert_eq!(toasts.visible().len(), 1);
        assert_eq!(toasts.visible()[0].text, "Hold me");
        let id = toasts.visible()[0].id;
        toasts.dismiss(id);
        assert!(toasts.visible().is_empty());
    }
}
