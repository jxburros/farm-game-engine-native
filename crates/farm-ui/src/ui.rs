//! The immediate-mode core: widget ids, hit testing, focus navigation, layers, clipping and
//! scrolling, drawing into a [`DrawList`].
//!
//! A frame is `begin_frame` → widgets → `end_frame`. Pointer and navigation input is resolved at
//! `begin_frame` against the widgets of the *previous* frame (what the player saw when they
//! clicked or pressed a key), so every widget knows during the frame whether it was clicked.
//! Widgets are identified by stable [`WidgetId`]s, never by position in the frame.
//!
//! Layers stack modals: [`Ui::push_layer`] starts a layer above everything drawn so far. Only the
//! top layer of the previous frame takes focus navigation, and the topmost widget under the
//! pointer takes the click, so a modal's backdrop ([`Ui::blocker`]) shields what is under it.
//!
//! Coordinates are logical units: the host passes a scale (UI scale × display density) and the
//! draw list starts with that scale, so text stays crisp at any size.

use crate::i18n::Lang;
use crate::input::{InputDevice, NavAction, UiInput};
use crate::layout::RectExt;
use crate::theme::Theme;
use farm_render::text;
use farm_render::{Color, DrawCmd, DrawList, FontId, Rect, TextAlign};
use std::collections::BTreeMap;

/// A stable widget identity: a hash of names and keys (`WidgetId::new("buy").with(item_id)`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct WidgetId(pub u64);

const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

fn fnv(mut hash: u64, bytes: &[u8]) -> u64 {
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    hash
}

impl WidgetId {
    pub fn new(name: &str) -> Self {
        Self(fnv(FNV_OFFSET, name.as_bytes()))
    }

    /// A child id: this id combined with a key (an item id, an index).
    pub fn with(self, part: impl IdPart) -> Self {
        Self(part.mix(fnv(self.0, b"/")))
    }
}

/// Something that can extend a [`WidgetId`].
pub trait IdPart {
    fn mix(&self, hash: u64) -> u64;
}

impl IdPart for &str {
    fn mix(&self, hash: u64) -> u64 {
        fnv(hash, self.as_bytes())
    }
}

impl IdPart for &String {
    fn mix(&self, hash: u64) -> u64 {
        fnv(hash, self.as_bytes())
    }
}

impl IdPart for String {
    fn mix(&self, hash: u64) -> u64 {
        fnv(hash, self.as_bytes())
    }
}

impl IdPart for usize {
    fn mix(&self, hash: u64) -> u64 {
        fnv(hash, &(*self as u64).to_le_bytes())
    }
}

impl IdPart for u32 {
    fn mix(&self, hash: u64) -> u64 {
        fnv(hash, &u64::from(*self).to_le_bytes())
    }
}

/// How a registered rectangle takes input.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HitKind {
    /// Clickable and (optionally) focusable.
    Button,
    /// Left/right adjust it while focused; the pointer drags it.
    Slider,
    /// Absorbs clicks (modal backdrops and cards); never focused.
    Blocker,
    /// Receives the wheel.
    Scroll,
}

/// A widget of one frame, as hit testing and navigation see it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Hit {
    pub id: WidgetId,
    /// Visible part (clipped), in logical units.
    pub rect: Rect,
    /// Full rectangle, possibly scrolled out of view.
    pub full: Rect,
    pub layer: u32,
    pub kind: HitKind,
    pub focusable: bool,
    /// Preferred focus when its layer opens.
    pub default_focus: bool,
    /// The scroll area it sits in.
    pub scroll: Option<WidgetId>,
    /// That area's scroll offset when it was laid out.
    pub scroll_offset: f32,
}

/// What happened to a widget this frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Interaction {
    pub hovered: bool,
    /// The pointer went down on it this frame.
    pub pressed: bool,
    /// The pointer is held on it.
    pub active: bool,
    /// Activated: pointer released on it, or Accept while focused.
    pub clicked: bool,
    /// The pointer that went down on it was released this frame (anywhere).
    pub released: bool,
    pub focused: bool,
    /// Focus is shown (keyboard or gamepad navigation).
    pub focus_visible: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Default)]
struct ScrollState {
    offset: f32,
    content: f32,
    view: Rect,
    /// Drawn this frame / last frame. Metrics outlive a closed modal, so it reopens at its size;
    /// the offset starts over.
    seen: bool,
    seen_before: bool,
}

#[derive(Debug, Clone, Copy)]
struct OpenScroll {
    id: WidgetId,
    view: Rect,
    top: f32,
    offset: f32,
}

/// Pixels scrolled per wheel line.
const WHEEL_STEP: f32 = 48.0;
/// Room kept around a focused widget scrolled into view.
const REVEAL_MARGIN: f32 = 8.0;

/// The immediate-mode UI context. Keep one per surface and reuse it every frame.
#[derive(Debug)]
pub struct Ui {
    theme: Theme,
    scale: f32,
    text_scale: f32,
    size: (f32, f32),
    input: UiInput,
    list: DrawList,
    hovered: Option<WidgetId>,
    pressed: Option<WidgetId>,
    active: Option<WidgetId>,
    clicked: Option<WidgetId>,
    released: Option<WidgetId>,
    focus: Option<WidgetId>,
    focus_visible: bool,
    slider_steps: Vec<(WidgetId, i32)>,
    back: bool,
    tab: i32,
    hits: Vec<Hit>,
    prev_hits: Vec<Hit>,
    prev_top_layer: u32,
    layer: u32,
    layer_stack: Vec<u32>,
    max_layer: u32,
    clip_stack: Vec<Rect>,
    scroll_stack: Vec<OpenScroll>,
    scrolls: BTreeMap<WidgetId, ScrollState>,
    reduced_motion: bool,
    lang: Lang,
    readable_font: bool,
    time: f64,
    tints: Vec<(Color, Color)>,
    /// Physical pixels at the bottom the host covers (on-screen touch controls).
    bottom_inset: f32,
}

impl Default for Ui {
    fn default() -> Self {
        Self::new(Theme::default())
    }
}

impl Ui {
    pub fn new(theme: Theme) -> Self {
        Self {
            theme,
            scale: 1.0,
            text_scale: 1.0,
            size: (0.0, 0.0),
            input: UiInput::default(),
            list: DrawList::new(),
            hovered: None,
            pressed: None,
            active: None,
            clicked: None,
            released: None,
            focus: None,
            focus_visible: false,
            slider_steps: Vec::new(),
            back: false,
            tab: 0,
            hits: Vec::new(),
            prev_hits: Vec::new(),
            prev_top_layer: 0,
            layer: 0,
            layer_stack: Vec::new(),
            max_layer: 0,
            clip_stack: Vec::new(),
            scroll_stack: Vec::new(),
            scrolls: BTreeMap::new(),
            reduced_motion: false,
            lang: Lang::En,
            readable_font: false,
            time: 0.0,
            tints: Vec::new(),
            bottom_inset: 0.0,
        }
    }

    pub fn theme(&self) -> &Theme {
        &self.theme
    }

    pub fn set_theme(&mut self, theme: Theme) {
        self.theme = theme;
    }

    /// Starts a frame of `physical` pixels. `scale` maps logical units to pixels (UI scale ×
    /// density); `text_scale` multiplies font sizes (the text-size setting); `time` is seconds
    /// since start (animations).
    pub fn begin_frame(&mut self, input: UiInput, physical: (f32, f32), scale: f32, text_scale: f32, time: f64) {
        self.scale = if scale.is_finite() && scale > 0.05 { scale } else { 1.0 };
        self.text_scale = if text_scale.is_finite() && text_scale > 0.1 { text_scale } else { 1.0 };
        self.size = (physical.0.max(1.0) / self.scale, physical.1.max(1.0) / self.scale);
        self.time = time;
        let mut input = input;
        input.pointer = input.pointer.map(|(x, y)| (x / self.scale, y / self.scale));
        self.input = input;
        self.list = DrawList::new();
        self.list.save();
        self.list.scale(self.scale, self.scale);
        self.prev_hits = std::mem::take(&mut self.hits);
        self.prev_top_layer = self.prev_hits.iter().map(|hit| hit.layer).max().unwrap_or(0);
        self.layer = 0;
        self.layer_stack.clear();
        self.max_layer = 0;
        self.clip_stack.clear();
        self.scroll_stack.clear();
        self.clicked = None;
        self.pressed = None;
        self.released = None;
        self.slider_steps.clear();
        self.back = false;
        self.tab = 0;
        self.tints.clear();
        for scroll in self.scrolls.values_mut() {
            scroll.seen_before = scroll.seen;
            scroll.seen = false;
        }
        self.resolve_pointer();
        self.resolve_navigation();
    }

    /// Ends the frame and returns what to draw (UI coordinates already scaled).
    pub fn end_frame(&mut self) -> DrawList {
        while !self.clip_stack.is_empty() {
            self.pop_clip();
        }
        self.list.restore();
        if self.scrolls.len() > 64 {
            self.scrolls.retain(|_, scroll| scroll.seen);
        }
        cull(std::mem::take(&mut self.list), self.screen())
    }

    fn topmost_at(&self, x: f32, y: f32, kinds: &[HitKind]) -> Option<&Hit> {
        self.prev_hits
            .iter()
            .enumerate()
            .filter(|(_, hit)| kinds.contains(&hit.kind) && hit.rect.contains(x, y))
            .max_by_key(|(index, hit)| (hit.layer, *index))
            .map(|(_, hit)| hit)
    }

    fn resolve_pointer(&mut self) {
        let hovered = self
            .input
            .pointer
            .and_then(|(x, y)| self.topmost_at(x, y, &[HitKind::Button, HitKind::Slider, HitKind::Blocker]).copied());
        self.hovered = hovered.filter(|hit| hit.kind != HitKind::Blocker).map(|hit| hit.id);
        if self.input.pointer_pressed {
            self.active = self.hovered;
            self.pressed = self.hovered;
            if let Some(hit) = hovered.filter(|hit| hit.focusable) {
                self.focus = Some(hit.id);
            }
            self.focus_visible = false;
        }
        if self.input.pointer_released {
            if let Some(active) = self.active.take() {
                self.released = Some(active);
                if self.hovered == Some(active) {
                    self.clicked = Some(active);
                }
            }
        } else if !self.input.pointer_down && !self.input.pointer_pressed {
            self.active = None;
        }
        if self.input.wheel != 0.0 {
            if let Some((x, y)) = self.input.pointer {
                let target = self.topmost_at(x, y, &[HitKind::Scroll]).filter(|hit| hit.layer >= self.prev_top_layer);
                if let Some(id) = target.map(|hit| hit.id) {
                    self.scroll_by(id, -self.input.wheel * WHEEL_STEP);
                }
            }
        }
    }

    fn scroll_by(&mut self, id: WidgetId, delta: f32) {
        if let Some(scroll) = self.scrolls.get_mut(&id) {
            let max = (scroll.content - scroll.view.height).max(0.0);
            scroll.offset = (scroll.offset + delta).clamp(0.0, max);
        }
    }

    /// Scrolls a scroll area programmatically (right stick, page keys).
    pub fn scroll_area_by(&mut self, id: WidgetId, delta: f32) {
        self.scroll_by(id, delta);
    }

    fn focusables(&self) -> Vec<Hit> {
        self.prev_hits.iter().filter(|hit| hit.focusable && hit.layer == self.prev_top_layer).copied().collect()
    }

    fn resolve_navigation(&mut self) {
        let focusables = self.focusables();
        let valid = self.focus.is_some_and(|focus| focusables.iter().any(|hit| hit.id == focus));
        if !valid {
            self.focus =
                focusables.iter().find(|hit| hit.default_focus).or_else(|| focusables.first()).map(|hit| hit.id);
        }
        let nav = std::mem::take(&mut self.input.nav);
        for action in &nav {
            if !matches!(action, NavAction::Back | NavAction::TabNext | NavAction::TabPrev) {
                self.focus_visible = true;
            }
            match action {
                NavAction::Accept => {
                    if self.focus.is_some() {
                        self.clicked = self.focus;
                    }
                }
                NavAction::Back => self.back = true,
                NavAction::TabNext => self.tab += 1,
                NavAction::TabPrev => self.tab -= 1,
                NavAction::Left | NavAction::Right => {
                    let step = if *action == NavAction::Left { -1 } else { 1 };
                    let current = self.focus.and_then(|focus| focusables.iter().find(|hit| hit.id == focus));
                    match current {
                        Some(hit) if hit.kind == HitKind::Slider => self.slider_steps.push((hit.id, step)),
                        _ => self.move_focus(&focusables, *action),
                    }
                }
                NavAction::Up | NavAction::Down => self.move_focus(&focusables, *action),
            }
        }
        self.input.nav = nav;
    }

    fn move_focus(&mut self, focusables: &[Hit], action: NavAction) {
        if focusables.is_empty() {
            // Nothing to focus (a read-only list): up and down scroll the top scroll area.
            let step = match action {
                NavAction::Up => -WHEEL_STEP,
                NavAction::Down => WHEEL_STEP,
                _ => return,
            };
            let area = self
                .prev_hits
                .iter()
                .rev()
                .find(|hit| hit.kind == HitKind::Scroll && hit.layer == self.prev_top_layer)
                .map(|hit| hit.id);
            if let Some(id) = area {
                self.scroll_by(id, step);
            }
            return;
        }
        let Some(current) = self.focus.and_then(|focus| focusables.iter().find(|hit| hit.id == focus)).copied() else {
            self.focus = focusables.first().map(|hit| hit.id);
            return;
        };
        let (cx, cy) = current.full.center();
        let (dir_x, dir_y) = match action {
            NavAction::Up => (0.0, -1.0),
            NavAction::Down => (0.0, 1.0),
            NavAction::Left => (-1.0, 0.0),
            _ => (1.0, 0.0),
        };
        // Candidates ahead in the direction, scored by distance with a penalty for being off
        // the axis (so a column keeps its column).
        let score = |hit: &Hit| {
            let (x, y) = hit.full.center();
            let (dx, dy) = (x - cx, y - cy);
            let along = dx * dir_x + dy * dir_y;
            let across = (dx * dir_y - dy * dir_x).abs();
            (along, across)
        };
        let ahead =
            focusables.iter().filter(|hit| hit.id != current.id).filter(|hit| score(hit).0 > 1.0).min_by(|a, b| {
                let (sa, sb) = (score(a), score(b));
                (sa.0 + sa.1 * 2.5).total_cmp(&(sb.0 + sb.1 * 2.5))
            });
        let target = match ahead {
            Some(hit) => Some(*hit),
            // Wrap around: the farthest one the other way, best aligned.
            None => focusables.iter().filter(|hit| hit.id != current.id).copied().min_by(|a, b| {
                let (sa, sb) = (score(a), score(b));
                (sa.0 - sa.1 * 2.5).total_cmp(&(sb.0 - sb.1 * 2.5))
            }),
        };
        if let Some(target) = target {
            self.focus = Some(target.id);
            self.reveal(&target);
        }
    }

    /// Scrolls the scroll area holding `hit` so it is in view.
    fn reveal(&mut self, hit: &Hit) {
        let Some(scroll) = hit.scroll.and_then(|id| self.scrolls.get_mut(&id)) else { return };
        let view = scroll.view;
        let max = (scroll.content - view.height).max(0.0);
        // Content coordinates of the widget (independent of later scrolling).
        let top = hit.full.y - view.y + hit.scroll_offset;
        let bottom = top + hit.full.height;
        if top < scroll.offset {
            scroll.offset = (top - REVEAL_MARGIN).clamp(0.0, max);
        } else if bottom > scroll.offset + view.height {
            scroll.offset = (bottom - view.height + REVEAL_MARGIN).clamp(0.0, max);
        }
    }

    /// Darkens (or tints) the world behind the UI with a vertical gradient over the screen,
    /// from `top` to `bottom`. The host applies it to the world at its native pixel size, which
    /// costs a fraction of a full-screen fill; the UI itself is not tinted.
    pub fn tint_world(&mut self, top: Color, bottom: Color) {
        self.tints.push((top, bottom));
    }

    /// The world tints of the last frame, in order.
    pub fn world_tints(&self) -> &[(Color, Color)] {
        &self.tints
    }

    // ── Frame information ───────────────────────────────────────────────

    /// The screen in logical units.
    pub fn screen(&self) -> Rect {
        Rect::new(0.0, 0.0, self.size.0, self.size.1)
    }

    /// The bottom strip of the frame, in physical pixels, the host draws its own controls over
    /// (the web demo's touch buttons). Kept for later frames; at most half the screen counts.
    pub fn set_bottom_inset(&mut self, physical: f32) {
        self.bottom_inset = if physical.is_finite() { physical.max(0.0) } else { 0.0 };
    }

    /// The screen minus the host's bottom inset, in logical units: where the HUD's bottom row,
    /// panels and the dialogue box sit, so on-screen controls never cover them.
    pub fn safe_area(&self) -> Rect {
        let screen = self.screen();
        let inset = (self.bottom_inset / self.scale).min(screen.height / 2.0);
        Rect::new(screen.x, screen.y, screen.width, screen.height - inset)
    }

    pub fn scale(&self) -> f32 {
        self.scale
    }

    pub fn text_scale(&self) -> f32 {
        self.text_scale
    }

    pub fn time(&self) -> f64 {
        self.time
    }

    pub fn set_reduced_motion(&mut self, reduced: bool) {
        self.reduced_motion = reduced;
    }

    pub fn reduced_motion(&self) -> bool {
        self.reduced_motion
    }

    /// The language of the interface's own strings ([`Ui::tr`]).
    pub fn lang(&self) -> Lang {
        self.lang
    }

    pub fn set_lang(&mut self, lang: Lang) {
        self.lang = lang;
    }

    /// An interface string in the current language (see [`crate::i18n`]).
    pub fn tr(&self, key: &'static str) -> &'static str {
        self.lang.tr(key)
    }

    /// An interface template in the current language with `{0}`, `{1}`… filled in.
    pub fn tr_format(&self, key: &'static str, args: &[&dyn std::fmt::Display]) -> String {
        self.lang.format(key, args)
    }

    /// Draw text in Atkinson Hyperlegible instead of Inter (the "Readable font" setting).
    pub fn set_readable_font(&mut self, readable: bool) {
        self.readable_font = readable;
    }

    pub fn readable_font(&self) -> bool {
        self.readable_font
    }

    /// The face `font` stands for when drawing `content`: its readable face with the readable
    /// font setting, unless that face lacks a character of `content` (then Inter, which has
    /// more). Measuring and drawing go through here, so layouts match what is drawn.
    pub fn face(&self, font: FontId, content: &str) -> FontId {
        if !self.readable_font {
            return font;
        }
        let readable = font.readable();
        if text::font(readable).covers(content) {
            readable
        } else {
            font
        }
    }

    pub fn input(&self) -> &UiInput {
        &self.input
    }

    pub fn device(&self) -> InputDevice {
        self.input.device
    }

    /// Back (Escape, gamepad B) was pressed this frame.
    pub fn back_pressed(&self) -> bool {
        self.back
    }

    /// Tab steps this frame (+1 next, −1 previous).
    pub fn tab_delta(&self) -> i32 {
        self.tab
    }

    pub fn focused(&self) -> Option<WidgetId> {
        self.focus
    }

    /// Moves focus to a widget (for example the first button of a screen that just opened).
    pub fn set_focus(&mut self, id: WidgetId) {
        self.focus = Some(id);
    }

    pub fn focus_visible(&self) -> bool {
        self.focus_visible
    }

    /// The widget the pointer is over (previous frame's layout).
    pub fn hovered(&self) -> Option<WidgetId> {
        self.hovered
    }

    /// Widgets registered so far this frame (tests find buttons by id here).
    pub fn hits(&self) -> &[Hit] {
        &self.hits
    }

    /// The rectangle a widget had in the last completed frame (between frames), or so far in
    /// this one (during a frame).
    pub fn last_rect(&self, id: WidgetId) -> Option<Rect> {
        self.hits.iter().find(|hit| hit.id == id).map(|hit| hit.rect)
    }

    /// Commands drawn so far this frame.
    pub fn list(&self) -> &DrawList {
        &self.list
    }

    pub fn list_mut(&mut self) -> &mut DrawList {
        &mut self.list
    }

    // ── Registration ────────────────────────────────────────────────────

    fn clip(&self) -> Option<Rect> {
        self.clip_stack.last().copied()
    }

    /// Registers an interactive rectangle and reports what happened to it.
    pub fn interact(&mut self, id: WidgetId, rect: Rect, kind: HitKind, focusable: bool) -> Interaction {
        self.register(id, rect, kind, focusable, false)
    }

    /// Like [`Ui::interact`], preferred for focus when its layer opens.
    pub fn interact_default(&mut self, id: WidgetId, rect: Rect, kind: HitKind) -> Interaction {
        self.register(id, rect, kind, true, true)
    }

    fn register(
        &mut self,
        id: WidgetId,
        rect: Rect,
        kind: HitKind,
        focusable: bool,
        default_focus: bool,
    ) -> Interaction {
        let visible = match self.clip() {
            Some(clip) => rect.intersect(&clip),
            None => rect,
        };
        self.hits.push(Hit {
            id,
            rect: visible,
            full: rect,
            layer: self.layer,
            kind,
            focusable,
            default_focus,
            scroll: self.scroll_stack.last().map(|open| open.id),
            scroll_offset: self.scroll_stack.last().map_or(0.0, |open| open.offset),
        });
        let focused = focusable && self.focus == Some(id);
        Interaction {
            hovered: self.hovered == Some(id),
            pressed: self.pressed == Some(id),
            active: self.active == Some(id),
            clicked: self.clicked == Some(id),
            released: self.released == Some(id),
            focused,
            focus_visible: focused && self.focus_visible,
        }
    }

    /// Absorbs clicks over `rect` on the current layer (backdrops, cards).
    pub fn blocker(&mut self, rect: Rect) {
        self.hits.push(Hit {
            id: WidgetId(0),
            rect: match self.clip() {
                Some(clip) => rect.intersect(&clip),
                None => rect,
            },
            full: rect,
            layer: self.layer,
            kind: HitKind::Blocker,
            focusable: false,
            default_focus: false,
            scroll: None,
            scroll_offset: 0.0,
        });
    }

    /// Left/right steps a focused slider received this frame.
    pub fn slider_steps(&self, id: WidgetId) -> i32 {
        self.slider_steps.iter().filter(|(target, _)| *target == id).map(|(_, step)| step).sum()
    }

    /// Pointer position in logical units.
    pub fn pointer(&self) -> Option<(f32, f32)> {
        self.input.pointer
    }

    // ── Layers, clipping, scrolling ────────────────────────────────────

    /// Starts a layer above everything so far (a modal).
    pub fn push_layer(&mut self) {
        self.layer_stack.push(self.layer);
        self.max_layer += 1;
        self.layer = self.max_layer;
    }

    pub fn pop_layer(&mut self) {
        self.layer = self.layer_stack.pop().unwrap_or(0);
    }

    /// Clips drawing (and hit testing) to `rect` until [`Ui::pop_clip`].
    pub fn push_clip(&mut self, rect: Rect) {
        let clip = match self.clip() {
            Some(outer) => rect.intersect(&outer),
            None => rect,
        };
        self.clip_stack.push(clip);
        self.list.save();
        self.list.clip_rect(rect);
    }

    pub fn pop_clip(&mut self) {
        if self.clip_stack.pop().is_some() {
            self.list.restore();
        }
    }

    /// Starts a vertical scroll area over `view`; returns the y where content starts (scrolled).
    /// Lay the content out from there and pass its bottom to [`Ui::end_scroll`].
    pub fn begin_scroll(&mut self, id: WidgetId, view: Rect) -> f32 {
        let hit_layer = self.layer;
        self.hits.push(Hit {
            id,
            rect: view,
            full: view,
            layer: hit_layer,
            kind: HitKind::Scroll,
            focusable: false,
            default_focus: false,
            scroll: None,
            scroll_offset: 0.0,
        });
        let state = self.scrolls.entry(id).or_default();
        if !state.seen_before {
            state.offset = 0.0;
        }
        let max = (state.content - view.height).max(0.0);
        state.offset = state.offset.clamp(0.0, max);
        state.view = view;
        state.seen = true;
        let offset = state.offset;
        let top = view.y - offset;
        self.push_clip(view);
        self.scroll_stack.push(OpenScroll { id, view, top, offset });
        top
    }

    /// Ends the scroll area begun last; `content_bottom` is where its content ended.
    pub fn end_scroll(&mut self, content_bottom: f32) {
        let Some(open) = self.scroll_stack.pop() else { return };
        self.pop_clip();
        let content = (content_bottom - open.top).max(0.0);
        let Some(state) = self.scrolls.get_mut(&open.id) else { return };
        state.content = content;
        let view = open.view;
        if content > view.height + 0.5 {
            let offset = state.offset;
            let track = Rect::new(view.right() - 5.0, view.y + 2.0, 3.0, view.height - 4.0);
            let thumb_height = (view.height / content * track.height).max(24.0).min(track.height);
            let max = content - view.height;
            let thumb_y = track.y + (track.height - thumb_height) * (offset / max).clamp(0.0, 1.0);
            let colors = self.theme.colors;
            self.list.fill_round_rect(track, 1.5, crate::theme::fade(colors.track, 0.6));
            self.list.fill_round_rect(Rect::new(track.x, thumb_y, track.width, thumb_height), 1.5, colors.muted);
        }
    }

    /// Content height and view of a scroll area as of the last frame.
    pub fn scroll_metrics(&self, id: WidgetId) -> Option<(f32, f32, f32)> {
        self.scrolls.get(&id).map(|state| (state.offset, state.content, state.view.height))
    }

    // ── Text ───────────────────────────────────────────────────────────

    /// A font size after the text-size setting.
    pub fn font_size(&self, base: f32) -> f32 {
        base * self.text_scale
    }

    pub fn measure(&self, text: &str, size: f32, font: FontId) -> f32 {
        text::measure(self.face(font, text), self.font_size(size), text)
    }

    /// Baseline-to-baseline distance for text of `size` (before scaling).
    pub fn line_height(&self, size: f32) -> f32 {
        (self.font_size(size) * 1.42).ceil()
    }

    /// Text with its baseline at `y`.
    #[allow(clippy::too_many_arguments)]
    pub fn text(&mut self, x: f32, y: f32, content: &str, size: f32, font: FontId, color: Color, align: TextAlign) {
        if content.is_empty() {
            return;
        }
        self.list.push(DrawCmd::Text {
            text: content.to_owned(),
            x,
            y,
            font: self.face(font, content),
            size: self.font_size(size),
            color,
            stroke: None,
            align,
        });
    }

    /// One line of text vertically centered in `rect` (by cap height), aligned horizontally and
    /// shortened with an ellipsis when it does not fit.
    #[allow(clippy::too_many_arguments)]
    pub fn label(
        &mut self,
        rect: Rect,
        content: &str,
        size: f32,
        font: FontId,
        color: Color,
        align: crate::layout::Align,
    ) -> f32 {
        let px = self.font_size(size);
        let font = self.face(font, content);
        let fitted = text::ellipsize(font, px, content, rect.width + 0.01);
        let baseline = rect.y + rect.height / 2.0 + px * 0.727 / 2.0;
        let (x, text_align) = match align {
            crate::layout::Align::Start => (rect.x, TextAlign::Left),
            crate::layout::Align::Center => (rect.x + rect.width / 2.0, TextAlign::Center),
            crate::layout::Align::End => (rect.right(), TextAlign::Right),
        };
        let width = text::measure(font, px, &fitted);
        if !fitted.is_empty() {
            self.list.push(DrawCmd::Text {
                text: fitted,
                x,
                y: baseline,
                font,
                size: px,
                color,
                stroke: None,
                align: text_align,
            });
        }
        width
    }

    /// Wrapped lines of `content` for `width`.
    pub fn wrap(&self, content: &str, size: f32, font: FontId, width: f32) -> Vec<String> {
        text::wrap(self.face(font, content), self.font_size(size), content, width.max(1.0))
    }

    /// Height a wrapped paragraph takes.
    pub fn paragraph_height(&self, content: &str, size: f32, font: FontId, width: f32) -> f32 {
        self.wrap(content, size, font, width).len() as f32 * self.line_height(size)
    }

    /// A wrapped paragraph from `top`; returns its height.
    #[allow(clippy::too_many_arguments)]
    pub fn paragraph(
        &mut self,
        x: f32,
        top: f32,
        width: f32,
        content: &str,
        size: f32,
        font: FontId,
        color: Color,
        align: TextAlign,
    ) -> f32 {
        let lines = self.wrap(content, size, font, width);
        let line_height = self.line_height(size);
        let px = self.font_size(size);
        let font = self.face(font, content);
        let x = match align {
            TextAlign::Left => x,
            TextAlign::Center => x + width / 2.0,
            TextAlign::Right => x + width,
        };
        for (index, line) in lines.iter().enumerate() {
            let baseline = top + index as f32 * line_height + (line_height + px * 0.727) / 2.0;
            self.list.push(DrawCmd::Text {
                text: line.clone(),
                x,
                y: baseline,
                font,
                size: px,
                color,
                stroke: None,
                align,
            });
        }
        lines.len() as f32 * line_height
    }
}

/// The area a command may paint (logical units), when it is easy to tell.
fn bounds(command: &DrawCmd) -> Option<Rect> {
    Some(match command {
        DrawCmd::FillRect { rect, .. }
        | DrawCmd::FillRoundRect { rect, .. }
        | DrawCmd::FillOval { rect, .. }
        | DrawCmd::ModulateRect { rect, .. } => *rect,
        DrawCmd::StrokeRect { rect, width, .. } | DrawCmd::StrokeRoundRect { rect, width, .. } => rect.inset(-width),
        DrawCmd::BlurRect { rect, sigma, .. } | DrawCmd::BlurOval { rect, sigma, .. } => rect.inset(-3.0 * sigma - 1.0),
        DrawCmd::FillCircle { cx, cy, radius, .. } | DrawCmd::StrokeCircle { cx, cy, radius, .. } => {
            Rect::new(cx - radius - 2.0, cy - radius - 2.0, radius * 2.0 + 4.0, radius * 2.0 + 4.0)
        }
        DrawCmd::Line { x0, y0, x1, y1, width, .. } => Rect::new(
            x0.min(*x1) - width,
            y0.min(*y1) - width,
            (x1 - x0).abs() + width * 2.0,
            (y1 - y0).abs() + width * 2.0,
        ),
        DrawCmd::Image { dst, .. } => *dst,
        DrawCmd::Text { text, x, y, font, size, align, .. } => {
            let width = farm_render::text::measure(*font, *size, text);
            let left = match align {
                TextAlign::Left => *x,
                TextAlign::Center => x - width / 2.0,
                TextAlign::Right => x - width,
            };
            Rect::new(left - size, y - size * 1.2, width + size * 2.0, size * 1.8)
        }
        _ => return None,
    })
}

/// Drops the commands that paint nothing: entirely outside the screen or the clip in force
/// (rows scrolled out of a list). The UI draws in one scaled coordinate space, so bounds are
/// compared in logical units.
fn cull(list: DrawList, screen: Rect) -> DrawList {
    let mut clips = vec![screen];
    let mut commands = Vec::with_capacity(list.commands.len());
    let mut translated = false;
    for command in list.commands {
        match &command {
            DrawCmd::Save => clips.push(*clips.last().unwrap_or(&screen)),
            DrawCmd::Restore => {
                if clips.len() > 1 {
                    clips.pop();
                }
            }
            DrawCmd::ClipRect { rect } => {
                if let Some(clip) = clips.last_mut() {
                    *clip = clip.intersect(rect);
                }
            }
            // Screens don't move the origin; if one does, stop culling rather than guess.
            DrawCmd::Translate { .. } => translated = true,
            _ => {
                let clip = clips.last().copied().unwrap_or(screen);
                if !translated {
                    if let Some(bounds) = bounds(&command) {
                        if bounds.intersect(&clip).is_empty() {
                            continue;
                        }
                    }
                }
            }
        }
        commands.push(command);
    }
    DrawList { commands }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::input::NavAction;

    fn frame(ui: &mut Ui, input: UiInput, build: impl FnOnce(&mut Ui)) {
        ui.begin_frame(input, (800.0, 600.0), 1.0, 1.0, 0.0);
        build(ui);
        ui.end_frame();
    }

    fn buttons(ui: &mut Ui, clicked: &mut Vec<u32>) {
        for index in 0..3u32 {
            let id = WidgetId::new("b").with(index);
            let rect = Rect::new(10.0, 10.0 + index as f32 * 40.0, 100.0, 30.0);
            if ui.interact(id, rect, HitKind::Button, true).clicked {
                clicked.push(index);
            }
        }
    }

    #[test]
    fn ids_are_stable_and_distinct() {
        assert_eq!(WidgetId::new("a").with("x"), WidgetId::new("a").with("x"));
        assert_ne!(WidgetId::new("a").with("x"), WidgetId::new("a").with("y"));
        assert_ne!(WidgetId::new("a").with(1u32), WidgetId::new("a").with(2u32));
    }

    #[test]
    fn clicks_resolve_against_the_previous_frame() {
        let mut ui = Ui::default();
        let mut clicked = Vec::new();
        frame(&mut ui, UiInput::default(), |ui| buttons(ui, &mut clicked));
        let at = |y| UiInput { pointer: Some((50.0, y)), ..UiInput::default() };
        frame(&mut ui, UiInput { pointer_pressed: true, pointer_down: true, ..at(55.0) }, |ui| {
            buttons(ui, &mut clicked)
        });
        assert!(clicked.is_empty(), "a click needs the release");
        frame(&mut ui, UiInput { pointer_released: true, ..at(55.0) }, |ui| buttons(ui, &mut clicked));
        assert_eq!(clicked, [1]);
        // Pressed on one, released on another: nothing.
        frame(&mut ui, UiInput { pointer_pressed: true, pointer_down: true, ..at(15.0) }, |ui| {
            buttons(ui, &mut clicked)
        });
        frame(&mut ui, UiInput { pointer_released: true, ..at(95.0) }, |ui| buttons(ui, &mut clicked));
        assert_eq!(clicked, [1]);
    }

    #[test]
    fn keyboard_navigation_moves_focus_and_accepts() {
        let mut ui = Ui::default();
        let mut clicked = Vec::new();
        frame(&mut ui, UiInput::default(), |ui| buttons(ui, &mut clicked));
        let nav = |actions: &[NavAction]| UiInput { nav: actions.to_vec(), ..UiInput::default() };
        frame(&mut ui, nav(&[NavAction::Down, NavAction::Down]), |ui| buttons(ui, &mut clicked));
        assert_eq!(ui.focused(), Some(WidgetId::new("b").with(2u32)));
        assert!(ui.focus_visible());
        // Down from the last wraps to the first.
        frame(&mut ui, nav(&[NavAction::Down, NavAction::Accept]), |ui| buttons(ui, &mut clicked));
        assert_eq!(clicked, [0]);
        frame(&mut ui, nav(&[NavAction::Up, NavAction::Accept]), |ui| buttons(ui, &mut clicked));
        assert_eq!(clicked, [0, 2]);
        frame(&mut ui, nav(&[NavAction::Back, NavAction::TabNext]), |ui| {
            assert!(ui.back_pressed());
            assert_eq!(ui.tab_delta(), 1);
        });
    }

    #[test]
    fn a_modal_layer_takes_focus_and_blocks_clicks_below() {
        let mut ui = Ui::default();
        let mut clicked = Vec::new();
        let modal = WidgetId::new("modal-ok");
        let build = |ui: &mut Ui, clicked: &mut Vec<u32>| {
            buttons(ui, clicked);
            ui.push_layer();
            ui.blocker(ui.screen());
            if ui.interact(modal, Rect::new(300.0, 300.0, 80.0, 30.0), HitKind::Button, true).clicked {
                clicked.push(99);
            }
            ui.pop_layer();
        };
        frame(&mut ui, UiInput::default(), |ui| build(ui, &mut clicked));
        frame(&mut ui, UiInput::default(), |ui| build(ui, &mut clicked));
        assert_eq!(ui.focused(), Some(modal));
        let at = |pressed: bool| UiInput {
            pointer: Some((50.0, 15.0)),
            pointer_pressed: pressed,
            pointer_down: pressed,
            pointer_released: !pressed,
            ..UiInput::default()
        };
        frame(&mut ui, at(true), |ui| build(ui, &mut clicked));
        frame(&mut ui, at(false), |ui| build(ui, &mut clicked));
        assert!(clicked.is_empty(), "the backdrop absorbed the click");
        frame(&mut ui, UiInput { nav: vec![NavAction::Down, NavAction::Accept], ..UiInput::default() }, |ui| {
            build(ui, &mut clicked)
        });
        assert_eq!(clicked, [99], "navigation stays in the modal");
    }

    #[test]
    fn scroll_areas_scroll_with_the_wheel_and_reveal_focus() {
        let mut ui = Ui::default();
        let area = WidgetId::new("list");
        let build = |ui: &mut Ui| {
            let view = Rect::new(0.0, 0.0, 200.0, 100.0);
            let mut y = ui.begin_scroll(area, view);
            for index in 0..10u32 {
                ui.interact(WidgetId::new("row").with(index), Rect::new(0.0, y, 200.0, 30.0), HitKind::Button, true);
                y += 30.0;
            }
            ui.end_scroll(y);
        };
        frame(&mut ui, UiInput::default(), build);
        frame(&mut ui, UiInput::default(), build);
        assert_eq!(ui.scroll_metrics(area), Some((0.0, 300.0, 100.0)));
        frame(&mut ui, UiInput { pointer: Some((10.0, 10.0)), wheel: -1.0, ..UiInput::default() }, build);
        assert_eq!(ui.scroll_metrics(area).unwrap().0, WHEEL_STEP);
        frame(&mut ui, UiInput { pointer: Some((10.0, 10.0)), wheel: 10.0, ..UiInput::default() }, build);
        assert_eq!(ui.scroll_metrics(area).unwrap().0, 0.0);
        let down = UiInput { nav: vec![NavAction::Down; 5], ..UiInput::default() };
        frame(&mut ui, down, build);
        assert_eq!(ui.focused(), Some(WidgetId::new("row").with(5u32)));
        let (offset, _, _) = ui.scroll_metrics(area).unwrap();
        // Row 5 spans 150..180 in content; the 100-high view must show it.
        assert!((80.0..=150.0).contains(&offset), "{offset}");
    }

    #[test]
    fn commands_outside_the_clip_are_culled() {
        let mut ui = Ui::default();
        ui.begin_frame(UiInput::default(), (800.0, 600.0), 2.0, 1.0, 0.0);
        ui.push_clip(Rect::new(0.0, 0.0, 100.0, 100.0));
        ui.list_mut().fill_rect(Rect::new(10.0, 10.0, 10.0, 10.0), Color::WHITE);
        ui.list_mut().fill_rect(Rect::new(10.0, 200.0, 10.0, 10.0), Color::BLACK);
        ui.text(10.0, 250.0, "hidden", 12.0, FontId::Regular, Color::BLACK, TextAlign::Left);
        ui.pop_clip();
        ui.list_mut().fill_rect(Rect::new(10.0, 200.0, 10.0, 10.0), Color::WHITE);
        ui.list_mut().fill_rect(Rect::new(1000.0, 10.0, 10.0, 10.0), Color::WHITE);
        let list = ui.end_frame();
        let fills: Vec<_> = list
            .commands
            .iter()
            .filter_map(|command| match command {
                DrawCmd::FillRect { rect, color, .. } => Some((rect.y, *color)),
                _ => None,
            })
            .collect();
        assert_eq!(fills, [(10.0, Color::WHITE), (200.0, Color::WHITE)]);
        assert!(!list.commands.iter().any(|command| matches!(command, DrawCmd::Text { .. })));
    }

    #[test]
    fn scaling_converts_pointer_and_screen_to_logical_units() {
        let mut ui = Ui::default();
        ui.begin_frame(
            UiInput { pointer: Some((200.0, 100.0)), ..UiInput::default() },
            (1600.0, 1000.0),
            2.0,
            1.25,
            0.0,
        );
        assert_eq!(ui.screen(), Rect::new(0.0, 0.0, 800.0, 500.0));
        assert_eq!(ui.pointer(), Some((100.0, 50.0)));
        assert_eq!(ui.font_size(10.0), 12.5);
        let list = ui.end_frame();
        assert_eq!(list.commands[1], DrawCmd::Scale { sx: 2.0, sy: 2.0 });
    }
}
