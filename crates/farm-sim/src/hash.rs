//! Stable state hashing for replay/determinism tests (port of hash.ts / `Hash.cs`).

use crate::stable_json;
use serde::Serialize;

/// JSON with sorted object keys so hashing is order-independent.
pub fn stable_stringify<T: Serialize>(value: &T) -> String {
    stable_json::stringify(value)
}

/// FNV-1a 64-bit (as two 32-bit lanes) over the stable JSON encoding of `value`.
pub fn hash_state<T: Serialize>(value: &T) -> String {
    hash_text(&stable_json::stringify(value))
}

/// FNV-1a over the UTF-16 code units of `text`, as two 32-bit lanes rendered in hex.
pub fn hash_text(text: &str) -> String {
    let mut h1: u32 = 0x811c9dc5;
    let mut h2: u32 = 0xcbf29ce4;
    for unit in text.encode_utf16() {
        let c = u32::from(unit);
        h1 = (h1 ^ c).wrapping_mul(0x01000193);
        h2 = (h2 ^ ((c << 1) | 1)).wrapping_mul(0x01000193);
    }
    format!("{h1:08x}{h2:08x}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hashes_like_the_typescript_engine() {
        assert_eq!(hash_text("{}"), "5465b8257807bf56");
        assert_eq!(hash_text("[]"), "741638a538a6be56");
        assert_eq!(hash_text("null"), "77074ba4d9fff516");
    }
}
