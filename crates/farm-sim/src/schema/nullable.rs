//! Fields that TypeScript declares `.nullable().optional()`, where an absent key and an explicit
//! `null` are different values: `JSON.stringify` omits the one and writes the other, so they
//! hash differently. Such a field is an `Option<Option<T>>` with this module as its serde
//! helper: `None` is absent, `Some(None)` is `null`, `Some(Some(v))` is a value.
//!
//! ```text
//! #[serde(default, skip_serializing_if = "Option::is_none", with = "super::nullable")]
//! pub respawn_days: Option<Option<f64>>,
//! ```

use serde::{Deserialize, Deserializer, Serialize, Serializer};

pub fn serialize<T: Serialize, S: Serializer>(value: &Option<Option<T>>, serializer: S) -> Result<S::Ok, S::Error> {
    match value {
        // Absent keys are skipped by `skip_serializing_if`; if a caller forgets it, write null.
        None | Some(None) => serializer.serialize_none(),
        Some(Some(inner)) => inner.serialize(serializer),
    }
}

/// Called only when the key is present, so a present `null` becomes `Some(None)`.
pub fn deserialize<'de, T: Deserialize<'de>, D: Deserializer<'de>>(
    deserializer: D,
) -> Result<Option<Option<T>>, D::Error> {
    Option::<T>::deserialize(deserializer).map(Some)
}
