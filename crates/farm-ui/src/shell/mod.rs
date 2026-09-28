//! The game shell of a standalone game (docs/EXPORT.md "What the player must include"): title
//! screen, save slots, pause menu, settings, credits and confirmations. Each screen draws on its
//! own layer and returns a [`ShellAction`]; `farm-player` owns the state machine, the saves and
//! the settings file.

mod menus;
mod settings_screen;
mod slots;
mod title;

pub use menus::{confirm, credits, pause, ConfirmView, CreditsView, PauseView};
pub use settings_screen::{settings, SettingsScreen, SettingsTab};
pub use slots::{date_line, slots, SlotPreviewView, SlotView, SlotsMode, SlotsView};
pub use title::{title, TitleView};

use crate::settings::BindAction;

/// What the player chose on a shell screen.
#[derive(Debug, Clone, PartialEq)]
pub enum ShellAction {
    // Title screen.
    NewGame,
    Continue,
    OpenLoad,
    OpenSettings,
    OpenCredits,
    Quit,
    // Pause menu.
    Resume,
    OpenSave,
    QuitToTitle,
    /// Leave the current screen.
    Back,
    // Save slots.
    LoadSlot(u32),
    SaveSlot(u32),
    NewGameInSlot(u32),
    DeleteSlot(u32),
    // Confirmation.
    Confirm,
    Cancel,
    // Settings (edited in place).
    SettingsChanged,
    StartCapture(BindAction),
    CancelCapture,
}
