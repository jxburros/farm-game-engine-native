//! The play screens (ports of the editor's Play Mode overlays, `PlayModeView.cs`,
//! `PlayOverlays.cs`, `MinigameOverlay.cs`, `ToastHost.cs`): the HUD with its toolbar and
//! controls hints, creator game panels, dialogue, shop, crafting, inventory, quest log,
//! minigames and toasts.
//!
//! [`GameUi::draw`] reads a [`GameView`] (the live state plus rule queries from
//! `farm_sim::overlay`) and returns [`GameAction`]s; every gameplay action is an engine command.

mod crafting;
mod dialogue;
mod hud;
mod inventory;
mod minigame;
mod quests;
mod shop;
mod toasts;

pub use toasts::{Toast, ToastKind, Toasts, MAX_VISIBLE as MAX_TOASTS, TOAST_LIFETIME};

use crate::icons::{self, Icon};
use crate::layout::RectExt;
use crate::settings::Bindings;
use crate::ui::Ui;
use farm_render::graphics::ArtAsset;
use farm_render::{resolve_visual, BuiltinArt, DrawCmd, ImageStore, Rect, Sampling, SnapshotSprite};
use farm_runtime::host::{CalendarView, MinigameInput, MinigameView};
use farm_runtime::panels::PanelView;
use farm_sim::overlay::OverlayView;
use farm_sim::schema::{GameContent, GameState, Item};
use farm_sim::Command;
use std::sync::Arc;

/// A host panel over the game (web `showInventory` / `showQuests` / `showCrafting`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Panel {
    Inventory,
    Quests,
    Crafting,
}

/// The shop dialog's tab.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ShopTab {
    #[default]
    Buy,
    Sell,
    Repair,
}

/// What the player asked for this frame.
#[derive(Debug, Clone, PartialEq)]
pub enum GameAction {
    /// Run an engine command.
    Command(Command),
    /// Open the panel, or close it when it is open.
    TogglePanel(Panel),
    ClosePanel,
    /// The toolbar's Menu button (the pause menu).
    OpenMenu,
    /// Input for the running minigame.
    Minigame(MinigameInput),
}

/// What item art is drawn from: the creator's assets, else the built-in pack.
#[derive(Debug, Clone, Copy)]
pub struct ItemArt<'a> {
    pub assets: &'a [ArtAsset],
    /// `graphics.pixelArt`: nearest-neighbour scaling.
    pub pixel_art: bool,
    pub builtin: Option<&'a BuiltinArt>,
}

/// Everything the play screens read in one frame.
#[derive(Debug, Clone, Copy)]
pub struct GameView<'a> {
    pub state: &'a GameState,
    pub content: &'a GameContent,
    pub overlay: &'a OverlayView<'a>,
    pub calendar: &'a CalendarView,
    /// Creator game panels (`farm_runtime::panels::render`).
    pub panels: &'a [PanelView],
    pub minigame: Option<&'a MinigameView>,
    pub art: ItemArt<'a>,
    pub bindings: &'a Bindings,
    /// Show "Made with Farming RPG Maker".
    pub show_made_with: bool,
    /// The game UI owns keyboard and gamepad this frame (digits pick dialogue options, tabs
    /// switch shop tabs). False while a shell screen (pause, settings) is on top.
    pub keys_active: bool,
}

/// The play screens' own state: which panel is open, the shop tab, toasts.
#[derive(Debug, Clone, Default)]
pub struct GameUi {
    pub panel: Option<Panel>,
    pub shop_tab: ShopTab,
    pub toasts: Toasts,
}

impl GameUi {
    pub fn new() -> Self {
        Self::default()
    }

    /// Draws the HUD and every open overlay; returns what the player did.
    pub fn draw(&mut self, ui: &mut Ui, view: &GameView<'_>, images: &mut ImageStore) -> Vec<GameAction> {
        let mut actions = Vec::new();
        if view.state.shop.is_none() {
            self.shop_tab = ShopTab::Buy;
        }
        let hud_bottom = hud::draw(ui, view, &mut actions);
        self.toasts.top = hud_bottom + 12.0;
        match self.panel {
            Some(Panel::Inventory) => inventory::draw(ui, view, images, &mut actions),
            Some(Panel::Quests) => quests::draw(ui, view, &mut actions),
            Some(Panel::Crafting) => crafting::draw(ui, view, &mut actions),
            None => {}
        }
        shop::draw(ui, view, &mut self.shop_tab, &mut actions);
        dialogue::draw(ui, view, &mut actions);
        minigame::draw(ui, view, &mut actions);
        actions
    }

    /// Toggles a host panel (the same panel closes it).
    pub fn toggle(&mut self, panel: Panel) {
        self.panel = if self.panel == Some(panel) { None } else { Some(panel) };
    }

    /// Draws the toasts (call last, above every other layer).
    pub fn draw_toasts(&mut self, ui: &mut Ui) {
        self.toasts.draw(ui);
    }
}

/// A list row with content on the left and buttons on the right (web `Ui.Row`): draws the row
/// background and returns (content area, actions area).
pub(crate) fn row(ui: &mut Ui, area: Rect, top: f32, height: f32, actions_width: f32) -> (Rect, Rect) {
    let rect = Rect::new(area.x, top, area.width, height);
    ui.row_background(rect);
    let mut inner = rect.inset_xy(10.0, 8.0);
    let actions = inner.cut_right(actions_width);
    inner.cut_right(10.0);
    (inner, actions)
}

/// Lays buttons of `widths` right to left in `area`, vertically centered at `height`; returns
/// their rectangles left to right.
pub(crate) fn right_aligned(area: Rect, widths: &[f32], height: f32, gap: f32) -> Vec<Rect> {
    let total: f32 = widths.iter().sum::<f32>() + gap * widths.len().saturating_sub(1) as f32;
    let mut x = area.right() - total;
    let y = area.y + (area.height - height) / 2.0;
    widths
        .iter()
        .map(|width| {
            let rect = Rect::new(x, y, *width, height);
            x += width + gap;
            rect
        })
        .collect()
}

/// Draws an item's art into `rect`: the creator's visual (first frame), its custom image, the
/// built-in art for its type, or a package glyph.
pub(crate) fn draw_item_art(
    ui: &mut Ui,
    images: &mut ImageStore,
    art: &ItemArt<'_>,
    content: &GameContent,
    item: &Item,
    rect: Rect,
) {
    let definition = content.items.iter().find(|candidate| candidate.id == item.id).unwrap_or(item);
    let visual = definition.visual.as_ref().or(item.visual.as_ref());
    let sprite = resolve_visual(art.assets, visual, 0.0, "down", true)
        .or_else(|| {
            let url = definition.custom_image.as_deref().or(item.custom_image.as_deref())?;
            Some(SnapshotSprite { image_url: Arc::from(url), ..SnapshotSprite::default() })
        })
        .or_else(|| art.builtin.and_then(|builtin| builtin.item(Some(definition.r#type.as_str()))));
    if let Some(sprite) = sprite {
        if draw_sprite(ui, images, &sprite, rect, art.pixel_art) {
            return;
        }
    }
    let colors = ui.theme().colors;
    icons::draw(ui.list_mut(), Icon::Package, rect.inset(rect.width * 0.18), colors.accent_text, colors.row);
}

/// Draws the first frame of a sprite fitted into `rect`; false when its image is unavailable.
pub(crate) fn draw_sprite(
    ui: &mut Ui,
    images: &mut ImageStore,
    sprite: &SnapshotSprite,
    rect: Rect,
    pixel_art: bool,
) -> bool {
    let Some(id) = images.get_shared(&sprite.image_url) else { return false };
    let Some((width, height)) = images.size(id) else { return false };
    let (width, height) = (f64::from(width), f64::from(height));
    let sw = if sprite.frame_width > 0.0 { sprite.frame_width } else { width };
    let sh = if sprite.frame_height > 0.0 { sprite.frame_height } else { height };
    let sx = sprite.source_x.unwrap_or(sprite.frame * sw);
    let sy = sprite.source_y.unwrap_or(sprite.row * sh);
    if sw <= 0.0 || sh <= 0.0 || sx < 0.0 || sy < 0.0 || sx + sw > width || sy + sh > height {
        return false;
    }
    let scale = (f64::from(rect.width) / sw).min(f64::from(rect.height) / sh);
    // Whole-number scales keep pixel art even.
    let scale = if pixel_art && scale >= 1.0 { scale.floor() } else { scale };
    let (dw, dh) = ((sw * scale) as f32, (sh * scale) as f32);
    let dst = Rect::new(rect.x + (rect.width - dw) / 2.0, rect.y + (rect.height - dh) / 2.0, dw, dh);
    ui.list_mut().push(DrawCmd::Image {
        image: id,
        src: Rect::new(sx as f32, sy as f32, sw as f32, sh as f32),
        dst,
        opacity: 1.0,
        sampling: if pixel_art { Sampling::Nearest } else { Sampling::Smooth },
        tint: None,
    });
    true
}

/// The quantity of an item held across all slots.
pub(crate) fn held(state: &GameState, item_id: &str) -> u64 {
    state.player.inventory.iter().filter(|slot| slot.item.id == item_id).map(|slot| u64::from(slot.quantity)).sum()
}

/// An item's display name (its id when unknown).
pub(crate) fn item_name<'a>(content: &'a GameContent, item_id: &'a str) -> &'a str {
    content.items.iter().find(|item| item.id == item_id).map_or(item_id, |item| item.name.as_str())
}

#[cfg(test)]
pub(crate) mod test_support;
#[cfg(test)]
mod tests;
