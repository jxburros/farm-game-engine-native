//! Shared helpers of the player tests: fixtures, players with in-memory stores, input shortcuts.
#![allow(dead_code)]

use farm_player::{InputEvent, MemorySaveStore, MemorySettingsStore, Player, PlayerMode, PlayerOptions};
use farm_sim::GameProject;
use std::path::PathBuf;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Arc;

pub const FRAME: f64 = 1.0 / 60.0;
/// Layout size of stepped (unrendered) frames.
pub const SIZE: (u32, u32) = (1280, 800);

pub fn root() -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "..", ".."].iter().collect()
}

pub fn project(name: &str) -> GameProject {
    let path = root().join("fixtures").join("golden").join("content").join(format!("{name}.json"));
    let fixture: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
    serde_json::from_value(fixture["project"].clone()).unwrap()
}

pub fn starter() -> GameProject {
    project("starter-farm")
}

pub fn cartridge() -> Vec<u8> {
    std::fs::read(root().join("fixtures").join("golden").join("cartridges").join("project-v8.cart")).unwrap()
}

/// Stores a test keeps handles to.
#[derive(Clone, Default)]
pub struct Stores {
    pub saves: MemorySaveStore,
    pub settings: MemorySettingsStore,
    pub clock: Arc<AtomicI64>,
}

impl Stores {
    pub fn new() -> Self {
        let stores = Self::default();
        stores.clock.store(1_790_000_000, Ordering::SeqCst);
        stores
    }

    pub fn options(&self, mode: PlayerMode) -> PlayerOptions {
        let clock = Arc::clone(&self.clock);
        PlayerOptions {
            mode,
            seed: Some("player-test".into()),
            saves: Box::new(self.saves.clone()),
            settings: Box::new(self.settings.clone()),
            // Every read moves a minute on, so saves are ordered.
            clock: Box::new(move || clock.fetch_add(60, Ordering::SeqCst)),
            can_quit: mode == PlayerMode::Standalone,
            ..PlayerOptions::standalone()
        }
    }

    pub fn standalone(&self, project: &GameProject) -> Player {
        Player::from_project(project.clone(), self.options(PlayerMode::Standalone)).unwrap()
    }
}

pub fn key_down(key: &str) -> InputEvent {
    InputEvent::KeyDown { key: key.into(), repeat: false }
}

pub fn key_up(key: &str) -> InputEvent {
    InputEvent::KeyUp { key: key.into() }
}

/// Steps `count` frames without input.
pub fn idle(player: &mut Player, count: usize) {
    for _ in 0..count {
        player.step(FRAME, &[], SIZE.0, SIZE.1).unwrap();
    }
}

/// Presses and releases a key over two frames, then lets two frames pass.
pub fn press(player: &mut Player, key: &str) {
    player.step(FRAME, &[key_down(key)], SIZE.0, SIZE.1).unwrap();
    player.step(FRAME, &[key_up(key)], SIZE.0, SIZE.1).unwrap();
    idle(player, 2);
}

/// Holds a key for `frames` frames.
pub fn hold(player: &mut Player, key: &str, frames: usize) {
    player.step(FRAME, &[key_down(key)], SIZE.0, SIZE.1).unwrap();
    idle(player, frames);
    player.step(FRAME, &[key_up(key)], SIZE.0, SIZE.1).unwrap();
}

/// Clicks the center of a widget drawn last frame, then lets two frames pass (a modal that
/// just opened settles to its content's size on its second frame).
pub fn click(player: &mut Player, id: farm_ui::WidgetId) {
    let rect = player.widget_rect(id).unwrap_or_else(|| panic!("widget {id:?} was not drawn"));
    let (x, y) = (rect.x + rect.width / 2.0, rect.y + rect.height / 2.0);
    player
        .step(
            FRAME,
            &[InputEvent::PointerMove { x, y }, InputEvent::PointerDown { x, y, button: Default::default() }],
            SIZE.0,
            SIZE.1,
        )
        .unwrap();
    player.step(FRAME, &[InputEvent::PointerUp { x, y, button: Default::default() }], SIZE.0, SIZE.1).unwrap();
    idle(player, 2);
}
