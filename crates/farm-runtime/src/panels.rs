//! Creator-defined game panels (port of `GamePanels.cs` / engine-runtime `game-panels.ts`
//! `mountGamePanels`), host-agnostic: [`render`] turns panel definitions + live state into plain
//! view models the host draws; [`mount`] keeps the TS update/dispose lifecycle and routes action
//! clicks.

use farm_sim::js;
use farm_sim::schema::{game_panel_entry_kinds as kinds, GamePanel, GamePanelEntry, InventorySlot};
use farm_sim::{GameProject, GameState};
use indexmap::IndexMap;
use serde_json::Value;
use std::fmt;

/// What creator panels read (TS `PanelState`).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PanelState {
    pub money: f64,
    pub energy: f64,
    pub day: f64,
    /// Values are `boolean | string | number`.
    pub flags: IndexMap<String, Value>,
    pub inventory: Vec<InventorySlot>,
    /// A modal interaction (dialogue/shop/minigame/host menu) is open: actions are disabled.
    pub blocked: bool,
}

impl PanelState {
    /// The exported shell's view of the running game (game-shell main.ts).
    pub fn from_game_state(state: &GameState, host_modal_open: bool) -> Self {
        Self {
            money: state.player.money,
            energy: state.player.energy,
            day: state.clock.day,
            flags: state.flags.clone(),
            inventory: state.player.inventory.clone(),
            blocked: state.dialogue.is_some() || state.minigame.is_some() || state.shop.is_some() || host_modal_open,
        }
    }

    /// The editor play view's projection (src/components/GamePanels.tsx): the synced project.
    pub fn from_project(project: &GameProject, blocked: bool) -> Self {
        Self {
            money: project.player.money,
            energy: project.player.energy.unwrap_or(0.0),
            day: project.current_day,
            flags: project.event_flags.iter().map(|(key, value)| (key.clone(), Value::Bool(*value))).collect(),
            inventory: project.player.inventory.clone(),
            blocked,
        }
    }
}

/// One rendered panel entry. Text is always plain text; game content is never markup.
#[derive(Debug, Clone, PartialEq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PanelEntryView {
    /// One of [`game_panel_entry_kinds`](farm_sim::schema::game_panel_entry_kinds).
    pub kind: String,
    /// Button label for actions; `"{label}: {value}"` (or just the value) otherwise.
    pub text: String,
    /// The action to perform when an action entry is clicked (`None` otherwise).
    pub action_id: Option<String>,
    /// Action buttons are disabled while [`PanelState::blocked`].
    pub enabled: bool,
}

#[derive(Debug, Clone, PartialEq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PanelView {
    pub id: String,
    pub title: String,
    /// The panel's `visibleFlag` is set and currently falsy.
    pub hidden: bool,
    pub entries: Vec<PanelEntryView>,
}

/// The display value of a non-action entry.
pub fn entry_value(entry: &GamePanelEntry, state: &PanelState) -> String {
    match entry.kind.as_str() {
        kinds::TEXT => entry.value.clone(),
        kinds::FLAG => if js::truthy(state.flags.get(&entry.value)) { "Yes" } else { "No" }.to_owned(),
        kinds::ITEM => js::num(
            state.inventory.iter().filter(|slot| slot.item.id == entry.value).fold(0.0, |n, slot| n + slot.quantity),
        ),
        kinds::MONEY => js::num(state.money),
        kinds::ENERGY => js::num(state.energy),
        kinds::DAY => js::num(state.day),
        // TS `state[entry.kind]` for an unknown kind → undefined.
        _ => "undefined".to_owned(),
    }
}

/// Build the panel views for the current state.
pub fn render<'a>(panels: impl IntoIterator<Item = &'a GamePanel>, state: &PanelState) -> Vec<PanelView> {
    panels
        .into_iter()
        .map(|panel| PanelView {
            id: panel.id.clone(),
            title: panel.title.clone(),
            hidden: panel
                .visible_flag
                .as_deref()
                .is_some_and(|flag| !flag.is_empty() && !js::truthy(state.flags.get(flag))),
            entries: panel
                .entries
                .iter()
                .map(|entry| {
                    if entry.kind == kinds::ACTION {
                        PanelEntryView {
                            kind: entry.kind.clone(),
                            text: entry.label.clone(),
                            action_id: Some(entry.value.clone()),
                            enabled: !state.blocked,
                        }
                    } else {
                        let separator = if entry.label.is_empty() { "" } else { ": " };
                        PanelEntryView {
                            kind: entry.kind.clone(),
                            text: format!("{}{separator}{}", entry.label, entry_value(entry, state)),
                            action_id: None,
                            enabled: false,
                        }
                    }
                })
                .collect(),
        })
        .collect()
}

/// TS `mountGamePanels(root, panels, getState, run)` without the DOM.
pub fn mount(
    panels: Vec<GamePanel>,
    get_state: impl Fn() -> PanelState + 'static,
    run: impl FnMut(&str) + 'static,
) -> MountedGamePanels {
    MountedGamePanels { panels, get_state: Box::new(get_state), run: Box::new(run), views: Vec::new() }
}

/// A live set of panels: call [`update`](Self::update) each frame (or on change) and draw
/// [`views`](Self::views).
pub struct MountedGamePanels {
    panels: Vec<GamePanel>,
    get_state: Box<dyn Fn() -> PanelState>,
    run: Box<dyn FnMut(&str)>,
    views: Vec<PanelView>,
}

impl MountedGamePanels {
    /// The latest rendered views (empty until the first [`update`](Self::update), and after
    /// [`dispose`](Self::dispose)).
    pub fn views(&self) -> &[PanelView] {
        &self.views
    }

    pub fn update(&mut self) {
        self.views = render(&self.panels, &(self.get_state)());
    }

    /// A click on an action entry: runs it unless a modal interaction blocks actions right now
    /// (checked live, like the TS button handler). Returns whether the action ran.
    pub fn click(&mut self, action_id: &str) -> bool {
        if (self.get_state)().blocked {
            return false;
        }
        let known = self
            .panels
            .iter()
            .any(|panel| panel.entries.iter().any(|entry| entry.kind == kinds::ACTION && entry.value == action_id));
        if !known {
            return false;
        }
        (self.run)(action_id);
        true
    }

    pub fn dispose(&mut self) {
        self.panels = Vec::new();
        self.views = Vec::new();
    }
}

impl fmt::Debug for MountedGamePanels {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("MountedGamePanels")
            .field("panels", &self.panels)
            .field("views", &self.views)
            .finish_non_exhaustive()
    }
}
