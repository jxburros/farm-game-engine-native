//! Where a player keeps save slots and settings.
//!
//! [`SaveStore`] holds the binary saves of the numbered slots (`schemas/save.fbs`) and
//! [`SettingsStore`] the settings (TOML), separately. [`FsSaveStore`] and [`FsSettingsStore`]
//! write files atomically (temporary file, then rename); the memory stores serve tests and hosts
//! without persistence (the editor's Play Mode).

use farm_ui::Settings;
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

/// Save slots offered by the game shell.
pub const SLOT_COUNT: u32 = 3;

/// Save slot storage. Implementations must not panic; a failing store reports an error.
pub trait SaveStore: Send {
    /// The bytes of a slot, if it holds a save.
    fn read(&self, slot: u32) -> Option<Vec<u8>>;
    fn write(&mut self, slot: u32, bytes: &[u8]) -> Result<(), String>;
    fn delete(&mut self, slot: u32) -> Result<(), String>;
    /// The folder saves live in (crash logs go next to them), when on disk.
    fn folder(&self) -> Option<PathBuf> {
        None
    }
}

/// Settings storage (one text document).
pub trait SettingsStore: Send {
    fn load(&self) -> Option<String>;
    fn save(&mut self, text: &str) -> Result<(), String>;
}

/// Saves in memory. Clones share the same slots, so a test (or host) can keep a handle.
#[derive(Debug, Clone, Default)]
pub struct MemorySaveStore {
    slots: Arc<Mutex<BTreeMap<u32, Vec<u8>>>>,
}

impl MemorySaveStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// The slots that hold a save.
    pub fn filled(&self) -> Vec<u32> {
        self.slots.lock().map(|slots| slots.keys().copied().collect()).unwrap_or_default()
    }
}

impl SaveStore for MemorySaveStore {
    fn read(&self, slot: u32) -> Option<Vec<u8>> {
        self.slots.lock().ok()?.get(&slot).cloned()
    }

    fn write(&mut self, slot: u32, bytes: &[u8]) -> Result<(), String> {
        self.slots.lock().map_err(|_| "save store poisoned".to_owned())?.insert(slot, bytes.to_vec());
        Ok(())
    }

    fn delete(&mut self, slot: u32) -> Result<(), String> {
        self.slots.lock().map_err(|_| "save store poisoned".to_owned())?.remove(&slot);
        Ok(())
    }
}

/// Settings in memory; clones share the text.
#[derive(Debug, Clone, Default)]
pub struct MemorySettingsStore {
    text: Arc<Mutex<Option<String>>>,
}

impl MemorySettingsStore {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn text(&self) -> Option<String> {
        self.text.lock().ok()?.clone()
    }
}

impl SettingsStore for MemorySettingsStore {
    fn load(&self) -> Option<String> {
        self.text()
    }

    fn save(&mut self, text: &str) -> Result<(), String> {
        *self.text.lock().map_err(|_| "settings store poisoned".to_owned())? = Some(text.to_owned());
        Ok(())
    }
}

/// Writes `bytes` to `path` through a temporary file in the same folder, then renames it, so a
/// crash mid-write never leaves a half-written save.
pub fn write_atomic(path: &Path, bytes: &[u8]) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| format!("Create {}: {error}", parent.display()))?;
    }
    let temporary = path.with_extension("tmp");
    fs::write(&temporary, bytes).map_err(|error| format!("Write {}: {error}", temporary.display()))?;
    fs::rename(&temporary, path).map_err(|error| format!("Replace {}: {error}", path.display()))
}

/// Save slots as files `slot<N>.sav` in a folder.
#[derive(Debug, Clone)]
pub struct FsSaveStore {
    folder: PathBuf,
}

impl FsSaveStore {
    pub fn new(folder: impl Into<PathBuf>) -> Self {
        Self { folder: folder.into() }
    }

    pub fn path(&self, slot: u32) -> PathBuf {
        self.folder.join(format!("slot{slot}.sav"))
    }
}

impl SaveStore for FsSaveStore {
    fn read(&self, slot: u32) -> Option<Vec<u8>> {
        fs::read(self.path(slot)).ok()
    }

    fn write(&mut self, slot: u32, bytes: &[u8]) -> Result<(), String> {
        write_atomic(&self.path(slot), bytes)
    }

    fn delete(&mut self, slot: u32) -> Result<(), String> {
        match fs::remove_file(self.path(slot)) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(format!("Delete {}: {error}", self.path(slot).display())),
        }
    }

    fn folder(&self) -> Option<PathBuf> {
        Some(self.folder.clone())
    }
}

/// Settings in a TOML file.
#[derive(Debug, Clone)]
pub struct FsSettingsStore {
    path: PathBuf,
}

impl FsSettingsStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }
}

impl SettingsStore for FsSettingsStore {
    fn load(&self) -> Option<String> {
        fs::read_to_string(&self.path).ok()
    }

    fn save(&mut self, text: &str) -> Result<(), String> {
        write_atomic(&self.path, text.as_bytes())
    }
}

/// Settings as TOML.
pub fn settings_to_toml(settings: &Settings) -> String {
    toml::to_string_pretty(settings).unwrap_or_default()
}

/// Settings from TOML; missing fields keep their defaults. An unreadable file is an error (the
/// player then starts from the defaults).
pub fn settings_from_toml(text: &str) -> Result<Settings, String> {
    toml::from_str(text).map_err(|error| error.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use farm_ui::settings::BindAction;

    #[test]
    fn settings_round_trip_through_toml() {
        let mut settings = Settings::default();
        settings.audio.master = 0.25;
        settings.display.integer_scaling = false;
        settings.accessibility.text_size = 1.2;
        settings.controls.rebind(BindAction::Water, "k");
        let text = settings_to_toml(&settings);
        assert!(text.contains("[audio]") && text.contains("master = 0.25"), "{text}");
        assert_eq!(settings_from_toml(&text).unwrap(), settings);
        assert_eq!(settings_from_toml("").unwrap(), Settings::default());
        assert!(settings_from_toml("[audio]\nmuted = true\n").unwrap().audio.muted);
        assert!(settings_from_toml("[audio\n").is_err());
    }

    #[test]
    fn file_stores_write_atomically_and_delete() {
        let folder = std::env::temp_dir().join(format!("farm-player-saves-{}", std::process::id()));
        let mut store = FsSaveStore::new(&folder);
        assert!(store.read(1).is_none());
        store.write(1, b"save one").unwrap();
        assert_eq!(store.read(1).unwrap(), b"save one");
        assert!(!store.path(1).with_extension("tmp").exists());
        store.delete(1).unwrap();
        store.delete(1).unwrap();
        assert!(store.read(1).is_none());
        let mut settings = FsSettingsStore::new(folder.join("settings.toml"));
        settings.save("x = 1").unwrap();
        assert_eq!(settings.load().as_deref(), Some("x = 1"));
        fs::remove_dir_all(folder).unwrap();
    }

    #[test]
    fn memory_stores_share_state_between_clones() {
        let store = MemorySaveStore::new();
        let mut writer = store.clone();
        writer.write(2, b"x").unwrap();
        assert_eq!(store.filled(), [2]);
        let settings = MemorySettingsStore::new();
        let mut writer = settings.clone();
        writer.save("a").unwrap();
        assert_eq!(settings.text().as_deref(), Some("a"));
    }
}
