//! Save slots and settings in memory, for a browser to persist.
//!
//! The browser has no file system, so a web player keeps its three save slots (binary `FGSV`
//! saves) and its settings (TOML) in a [`WebStorage`] and the page persists them wherever it
//! likes (IndexedDB, `localStorage`). They cross as one JSON document:
//!
//! ```json
//! {"version":1,"settings":"[audio]\nmaster = 0.8\n…","slots":{"1":"RkdTVg…"}}
//! ```
//!
//! `settings` is the settings TOML (or `null`), `slots` maps slot numbers to base64 saves.
//! Every change bumps a revision, so the page can tell when to write storage back.

use base64::engine::general_purpose::STANDARD as BASE64;
use base64::Engine as _;
use farm_player::{SaveStore, SettingsStore, SLOT_COUNT};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::sync::{Arc, Mutex, MutexGuard};

/// Version of the storage document.
pub const STORAGE_VERSION: u32 = 1;

#[derive(Debug, Default)]
struct StorageData {
    slots: BTreeMap<u32, Vec<u8>>,
    settings: Option<String>,
    revision: u64,
}

/// Save slots and settings shared between a player and its host (clones share the data).
#[derive(Debug, Clone, Default)]
pub struct WebStorage {
    data: Arc<Mutex<StorageData>>,
}

#[derive(Debug, Serialize, Deserialize)]
struct StorageDocument {
    #[serde(default = "version_one")]
    version: u32,
    #[serde(default)]
    settings: Option<String>,
    #[serde(default)]
    slots: BTreeMap<String, String>,
}

fn version_one() -> u32 {
    STORAGE_VERSION
}

impl WebStorage {
    pub fn new() -> Self {
        Self::default()
    }

    fn lock(&self) -> MutexGuard<'_, StorageData> {
        // Single-threaded on the web; a poisoned lock still holds consistent bytes.
        self.data.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// Increases whenever a slot or the settings change.
    pub fn revision(&self) -> u64 {
        self.lock().revision
    }

    /// The storage document (see the module docs).
    pub fn export_json(&self) -> String {
        let data = self.lock();
        let document = StorageDocument {
            version: STORAGE_VERSION,
            settings: data.settings.clone(),
            slots: data.slots.iter().map(|(slot, bytes)| (slot.to_string(), BASE64.encode(bytes))).collect(),
        };
        serde_json::to_string(&document).unwrap_or_default()
    }

    /// Replaces everything with a storage document. A malformed document changes nothing.
    /// Returns the settings TOML it carried, if any.
    pub fn import_json(&self, text: &str) -> Result<Option<String>, String> {
        let document = StoredDocument::parse(text)?;
        let settings = document.settings.clone();
        self.commit(document);
        Ok(settings)
    }

    /// Replaces everything with a parsed document (bumps the revision).
    pub fn commit(&self, document: StoredDocument) {
        let mut data = self.lock();
        data.slots = document.slots;
        data.settings = document.settings;
        data.revision += 1;
    }
}

/// A storage document that parsed: slots checked and decoded, nothing stored yet. Validate the
/// settings, then [`WebStorage::commit`] it, so a bad document changes nothing.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StoredDocument {
    slots: BTreeMap<u32, Vec<u8>>,
    /// The settings TOML, not yet parsed.
    pub settings: Option<String>,
}

impl StoredDocument {
    /// Parses a storage document (see the module docs).
    pub fn parse(text: &str) -> Result<Self, String> {
        let document: StorageDocument = serde_json::from_str(text).map_err(|e| format!("storage JSON: {e}"))?;
        if document.version > STORAGE_VERSION {
            return Err(format!(
                "This storage was written by a newer player (version {}, this one reads {STORAGE_VERSION}).",
                document.version
            ));
        }
        let mut slots = BTreeMap::new();
        for (slot, encoded) in &document.slots {
            let number: u32 = slot.parse().map_err(|_| format!("storage: slot \"{slot}\" is not a number"))?;
            if !(1..=SLOT_COUNT).contains(&number) {
                return Err(format!("storage: slot {number} is not between 1 and {SLOT_COUNT}"));
            }
            let bytes = BASE64.decode(encoded).map_err(|e| format!("storage: slot {number} is not base64: {e}"))?;
            slots.insert(number, bytes);
        }
        Ok(Self { slots, settings: document.settings })
    }
}

impl SaveStore for WebStorage {
    fn read(&self, slot: u32) -> Option<Vec<u8>> {
        self.lock().slots.get(&slot).cloned()
    }

    fn write(&mut self, slot: u32, bytes: &[u8]) -> Result<(), String> {
        let mut data = self.lock();
        data.slots.insert(slot, bytes.to_vec());
        data.revision += 1;
        Ok(())
    }

    fn delete(&mut self, slot: u32) -> Result<(), String> {
        let mut data = self.lock();
        if data.slots.remove(&slot).is_some() {
            data.revision += 1;
        }
        Ok(())
    }
}

impl SettingsStore for WebStorage {
    fn load(&self) -> Option<String> {
        self.lock().settings.clone()
    }

    fn save(&mut self, text: &str) -> Result<(), String> {
        let mut data = self.lock();
        // The player stores settings whenever it applies them; only real changes count.
        if data.settings.as_deref() != Some(text) {
            data.settings = Some(text.to_owned());
            data.revision += 1;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn storage_round_trips_through_json_and_counts_changes() {
        let storage = WebStorage::new();
        let mut writer = storage.clone();
        assert_eq!(storage.export_json(), r#"{"version":1,"settings":null,"slots":{}}"#);
        writer.write(2, b"FGSV\0\x01").unwrap();
        SettingsStore::save(&mut writer, "[audio]\nmuted = true\n").unwrap();
        let revision = storage.revision();
        SettingsStore::save(&mut writer, "[audio]\nmuted = true\n").unwrap();
        assert_eq!(storage.revision(), revision, "unchanged settings are not a change");
        writer.delete(3).unwrap();
        assert_eq!(storage.revision(), revision, "deleting an empty slot is not a change");

        let json = storage.export_json();
        assert_eq!(json, r#"{"version":1,"settings":"[audio]\nmuted = true\n","slots":{"2":"RkdTVgAB"}}"#);
        let other = WebStorage::new();
        assert_eq!(other.import_json(&json).unwrap().as_deref(), Some("[audio]\nmuted = true\n"));
        assert_eq!(other.read(2).unwrap(), b"FGSV\0\x01");
        assert_eq!(other.export_json(), json);
    }

    #[test]
    fn bad_documents_change_nothing() {
        let storage = WebStorage::new();
        storage.import_json(r#"{"slots":{"1":"AAAA"}}"#).unwrap();
        for bad in [
            "{oops",
            r#"{"slots":{"x":"AAAA"}}"#,
            r#"{"slots":{"9":"AAAA"}}"#,
            r#"{"slots":{"1":"!!"}}"#,
            r#"{"version":2}"#,
        ] {
            assert!(storage.import_json(bad).is_err(), "{bad}");
        }
        assert_eq!(storage.read(1).unwrap(), [0, 0, 0]);
    }
}
