//! `farm-ui`: the in-game interface of Farming RPG Maker games, drawn onto `farm-render` draw
//! lists (phase 6 of docs/LANGUAGES.md).
//!
//! - [`Ui`] is a small immediate-mode toolkit: stable [`WidgetId`]s, hit testing, focus
//!   navigation that works with keyboard, gamepad and mouse, layers for modals, clipping and
//!   scrolling. [`layout`] has the rectangle-cutting helpers, [`widgets`] the themed widgets,
//!   [`theme`] the colors (cozy dark panels, gold accents, Inter), [`i18n`] the string tables
//!   of the interface (English and Spanish).
//! - [`game`] draws the play screens: HUD, dialogue, shop, crafting, inventory, quests, creator
//!   panels, minigames and toasts. Screens read the game and return [`game::GameAction`]s;
//!   every gameplay action is an engine command. Rules (visible options, stock left, what can
//!   be crafted) come from `farm_sim::overlay`, never from the UI.
//! - [`shell`] draws the game shell of a standalone game: title screen, save slots, pause
//!   menu, settings, credits and confirmations. It returns [`shell::ShellAction`]s; the player
//!   (`farm-player`) runs the state machine.
//!
//! Nothing here touches the OS, a clock or the simulation state.
#![forbid(unsafe_code)]

pub mod format;
pub mod game;
pub mod i18n;
pub mod icons;
pub mod input;
pub mod layout;
pub mod settings;
pub mod shell;
pub mod theme;
pub mod ui;
pub mod widgets;

pub use i18n::Lang;
pub use icons::Icon;
pub use input::{GamepadButton, InputDevice, NavAction, UiInput};
pub use layout::{Align, Flow, RectExt};
pub use settings::{BindAction, Bindings, KeyRoute, Settings};
pub use theme::Theme;
pub use ui::{HitKind, Interaction, Ui, WidgetId};
pub use widgets::{Button, ButtonKind, Modal, ModalSpec};
