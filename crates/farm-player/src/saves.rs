//! Where a player keeps save slots and settings.
//!
//! [`SaveStore`] holds the binary saves of the numbered slots (`schemas/save.fbs`) and
//! [`SettingsStore`] the settings (TOML), separately. [`FsSaveStore`] and [`FsSettingsStore`]
//! write files durably: a temporary file of this process, flushed to disk, then renamed over the
//! old file, and the folder flushed too (Unix). Before a slot is replaced its previous save
//! becomes the slot's backup (`slot<N>.bak`), which the player loads when the main file turns out
//! damaged. The memory stores serve tests and hosts without persistence (the editor's Play
//! Mode).

use farm_ui::Settings;
use std::collections::BTreeMap;
use std::fs;
use std::io::Write as _;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

/// Save slots offered by the game shell.
pub const SLOT_COUNT: u32 = 3;

/// Save slot storage. Implementations must not panic; a failing store reports an error.
pub trait SaveStore: Send {
    /// The bytes of a slot, if it holds a save.
    fn read(&self, slot: u32) -> Option<Vec<u8>>;
    fn write(&mut self, slot: u32, bytes: &[u8]) -> Result<(), String>;
    /// Removes a slot's save and its backup.
    fn delete(&mut self, slot: u32) -> Result<(), String>;
    /// The save a slot held before its last write, if the store keeps one. The player falls
    /// back to it when the slot's save is missing or does not load.
    fn read_backup(&self, slot: u32) -> Option<Vec<u8>> {
        let _ = slot;
        None
    }
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

/// Saves in memory, with one backup per slot like [`FsSaveStore`]. Clones share the same
/// slots, so a test (or host) can keep a handle.
#[derive(Debug, Clone, Default)]
pub struct MemorySaveStore {
    slots: Arc<Mutex<BTreeMap<u32, Vec<u8>>>>,
    backups: Arc<Mutex<BTreeMap<u32, Vec<u8>>>>,
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
        let previous = self.slots.lock().map_err(|_| "save store poisoned".to_owned())?.insert(slot, bytes.to_vec());
        if let Some(previous) = previous {
            self.backups.lock().map_err(|_| "save store poisoned".to_owned())?.insert(slot, previous);
        }
        Ok(())
    }

    fn delete(&mut self, slot: u32) -> Result<(), String> {
        self.slots.lock().map_err(|_| "save store poisoned".to_owned())?.remove(&slot);
        self.backups.lock().map_err(|_| "save store poisoned".to_owned())?.remove(&slot);
        Ok(())
    }

    fn read_backup(&self, slot: u32) -> Option<Vec<u8>> {
        self.backups.lock().ok()?.get(&slot).cloned()
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

/// Extension of the temporary files [`write_atomic`] writes (`<name>.<pid>-<n>.tmp`).
const TEMPORARY_EXTENSION: &str = "tmp";

/// A temporary file name next to `path` that no other process or thread uses: two copies of a
/// game running at once never write the same temporary file.
fn temporary_path(path: &Path) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let serial = COUNTER.fetch_add(1, Ordering::Relaxed);
    let name = path.file_name().map_or_else(|| "file".into(), |name| name.to_string_lossy().into_owned());
    path.with_file_name(format!("{name}.{}-{serial}.{TEMPORARY_EXTENSION}", std::process::id()))
}

/// Writes `bytes` to a new temporary file next to `path` and flushes it to the disk. Returns the
/// temporary file; on failure nothing is left behind.
fn write_temporary(path: &Path, bytes: &[u8]) -> Result<PathBuf, String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| format!("Create {}: {error}", parent.display()))?;
    }
    let temporary = temporary_path(path);
    let written = fs::OpenOptions::new().write(true).create_new(true).open(&temporary).and_then(|mut file| {
        file.write_all(bytes)?;
        // The data must be on the disk before the rename makes it the file: otherwise a power loss
        // can keep the rename and lose the data (XFS, NTFS without write-through).
        file.sync_all()
    });
    if let Err(error) = written {
        let _ = fs::remove_file(&temporary);
        return Err(format!("Write {}: {error}", temporary.display()));
    }
    Ok(temporary)
}

/// Renames a flushed temporary file over `path` and flushes the folder (on Unix the rename itself
/// is only durable then). A failed rename removes the temporary file.
fn commit(temporary: &Path, path: &Path) -> Result<(), String> {
    if let Err(error) = fs::rename(temporary, path) {
        let _ = fs::remove_file(temporary);
        return Err(format!("Replace {}: {error}", path.display()));
    }
    sync_folder(path);
    Ok(())
}

/// Flushes the folder holding `path` (Unix; Windows has no folder handle to flush, and NTFS
/// journals the rename with the file's metadata).
fn sync_folder(path: &Path) {
    #[cfg(unix)]
    if let Some(parent) = path.parent() {
        // Best effort: some file systems refuse to sync folders.
        let _ = fs::File::open(parent).and_then(|folder| folder.sync_all());
    }
    #[cfg(not(unix))]
    let _ = path;
}

/// Writes `bytes` to `path` durably: a temporary file of this process in the same folder,
/// flushed, then renamed over `path`, then the folder flushed. A crash or power loss mid-write
/// leaves either the old file or the new one, never a half-written or empty one.
pub fn write_atomic(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let temporary = write_temporary(path, bytes)?;
    commit(&temporary, path)
}

/// Removes temporary files of interrupted writes in `folder` (a crash or power loss between the
/// write and the rename). Files younger than a minute may belong to a write in progress in
/// another copy of the game and stay.
pub fn remove_stale_temporaries(folder: &Path) {
    // File ages decide what to clean up here; they never reach the simulation.
    #[allow(clippy::disallowed_types, clippy::disallowed_methods)]
    fn is_stale(entry: &fs::DirEntry) -> bool {
        let modified = entry.metadata().and_then(|metadata| metadata.modified());
        let age = modified.ok().and_then(|modified| std::time::SystemTime::now().duration_since(modified).ok());
        age.is_some_and(|age| age.as_secs() >= 60)
    }
    let Ok(entries) = fs::read_dir(folder) else { return };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().is_some_and(|extension| extension == TEMPORARY_EXTENSION) && is_stale(&entry) {
            let _ = fs::remove_file(path);
        }
    }
}

/// Save slots as files `slot<N>.sav` in a folder, each with the save before it as
/// `slot<N>.bak`.
#[derive(Debug, Clone)]
pub struct FsSaveStore {
    folder: PathBuf,
}

fn remove_if_present(path: &Path) -> Result<(), String> {
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(format!("Delete {}: {error}", path.display())),
    }
}

impl FsSaveStore {
    /// Saves in `folder`; temporary files that interrupted writes left there are removed.
    pub fn new(folder: impl Into<PathBuf>) -> Self {
        let folder = folder.into();
        remove_stale_temporaries(&folder);
        Self { folder }
    }

    pub fn path(&self, slot: u32) -> PathBuf {
        self.folder.join(format!("slot{slot}.sav"))
    }

    /// The previous save of a slot.
    pub fn backup_path(&self, slot: u32) -> PathBuf {
        self.folder.join(format!("slot{slot}.bak"))
    }
}

impl SaveStore for FsSaveStore {
    fn read(&self, slot: u32) -> Option<Vec<u8>> {
        fs::read(self.path(slot)).ok()
    }

    fn write(&mut self, slot: u32, bytes: &[u8]) -> Result<(), String> {
        let path = self.path(slot);
        let temporary = write_temporary(&path, bytes)?;
        // The save being replaced becomes the backup. Between the two renames only the backup
        // exists, and the player loads it.
        if path.exists() {
            let backup = self.backup_path(slot);
            if let Err(error) = fs::rename(&path, &backup) {
                let _ = fs::remove_file(&temporary);
                return Err(format!("Keep {} as {}: {error}", path.display(), backup.display()));
            }
        }
        commit(&temporary, &path)
    }

    fn delete(&mut self, slot: u32) -> Result<(), String> {
        remove_if_present(&self.path(slot))?;
        remove_if_present(&self.backup_path(slot))
    }

    fn read_backup(&self, slot: u32) -> Option<Vec<u8>> {
        fs::read(self.backup_path(slot)).ok()
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

    fn files_in(folder: &Path) -> Vec<String> {
        let mut names: Vec<String> =
            fs::read_dir(folder).unwrap().map(|entry| entry.unwrap().file_name().to_string_lossy().into()).collect();
        names.sort();
        names
    }

    #[test]
    fn file_stores_write_atomically_keep_a_backup_and_delete() {
        let folder = std::env::temp_dir().join(format!("farm-player-saves-{}", std::process::id()));
        let mut store = FsSaveStore::new(&folder);
        assert!(store.read(1).is_none());
        store.write(1, b"save one").unwrap();
        assert_eq!(store.read(1).unwrap(), b"save one");
        assert!(store.read_backup(1).is_none());
        store.write(1, b"save two").unwrap();
        assert_eq!(store.read(1).unwrap(), b"save two");
        assert_eq!(store.read_backup(1).unwrap(), b"save one", "the replaced save is the backup");
        assert_eq!(files_in(&folder), ["slot1.bak", "slot1.sav"], "no temporary file is left");
        store.delete(1).unwrap();
        store.delete(1).unwrap();
        assert!(store.read(1).is_none() && store.read_backup(1).is_none());
        let mut settings = FsSettingsStore::new(folder.join("settings.toml"));
        settings.save("x = 1").unwrap();
        settings.save("x = 2").unwrap();
        assert_eq!(settings.load().as_deref(), Some("x = 2"));
        assert_eq!(files_in(&folder), ["settings.toml"]);
        fs::remove_dir_all(folder).unwrap();
    }

    #[test]
    fn temporary_names_are_unique_and_stale_ones_are_cleaned_up() {
        let folder = std::env::temp_dir().join(format!("farm-player-temporaries-{}", std::process::id()));
        fs::create_dir_all(&folder).unwrap();
        let path = folder.join("slot1.sav");
        let (first, second) = (temporary_path(&path), temporary_path(&path));
        assert_ne!(first, second);
        let name = first.file_name().unwrap().to_string_lossy().into_owned();
        assert!(name.starts_with(&format!("slot1.sav.{}-", std::process::id())) && name.ends_with(".tmp"), "{name}");
        // A fresh temporary file may be another copy's write in progress: it stays.
        fs::write(&first, b"partial").unwrap();
        fs::write(folder.join("slot2.sav"), b"keep").unwrap();
        remove_stale_temporaries(&folder);
        assert!(first.exists());
        // An old one is left over from a crash.
        let old = fs::File::options().write(true).open(&first).unwrap();
        #[allow(clippy::disallowed_types, clippy::disallowed_methods)]
        let long_ago = std::time::SystemTime::now() - std::time::Duration::from_secs(3600);
        old.set_modified(long_ago).unwrap();
        drop(old);
        let _store = FsSaveStore::new(&folder);
        assert_eq!(files_in(&folder), ["slot2.sav"]);
        fs::remove_dir_all(folder).unwrap();
    }

    #[test]
    fn memory_stores_keep_a_backup_too() {
        let mut store = MemorySaveStore::new();
        store.write(1, b"one").unwrap();
        assert!(store.read_backup(1).is_none());
        store.write(1, b"two").unwrap();
        assert_eq!(store.read_backup(1).unwrap(), b"one");
        store.delete(1).unwrap();
        assert!(store.read_backup(1).is_none());
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
