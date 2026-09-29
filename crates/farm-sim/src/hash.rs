//! State hashing (docs/NUMERICS.md "State hash").
//!
//! The state hash is xxh3-64 over the **canonical binary encoding** of a value: a small serde
//! [`Serializer`] that writes
//!
//! - integers little-endian at their own width, booleans as one byte, `char` as a `u32`;
//! - floats (only JSON values such as flags and pass-through keys carry them) as their IEEE-754
//!   bits, little-endian;
//! - strings and byte strings, sequences and maps with a `u32` length prefix; map entries
//!   sorted by their encoded key, so the order a map was built in (which a save, written as
//!   stable JSON, does not keep) does not change the hash;
//! - options as a tag byte (0 = none, 1 = some) before the value;
//! - struct fields in declaration order, each behind a tag byte: 1 = written, 0 = skipped
//!   (`skip_serializing_if`), so an absent optional key and a present value never collide;
//! - enum variants as their `u32` index before the content (internally tagged enums are
//!   structs with the tag as their first field).
//!
//! The encoding is not human-readable ([`Serializer::is_human_readable`] is false), which is
//! how the [`crate::units`] adapters know to write the stored integers instead of authoring
//! numbers. The hash text is 16 lowercase hex digits.
//!
//! [`stable_stringify`] (stable JSON) stays for debug output and the readable goldens;
//! [`hash_text`] is the v8 text hash (FNV-1a over UTF-16), kept for the FFI's `fe_hash_text`.

use crate::stable_json;
use serde::ser::{self, Serialize};
use std::fmt;

/// JSON with sorted object keys (debug output, readable goldens).
pub fn stable_stringify<T: Serialize>(value: &T) -> String {
    stable_json::stringify(value)
}

/// The canonical binary encoding of `value`.
pub fn canonical_bytes<T: Serialize + ?Sized>(value: &T) -> Vec<u8> {
    let mut encoder = Encoder { out: Vec::with_capacity(4096) };
    value.serialize(&mut encoder).expect("engine types always encode");
    encoder.out
}

/// xxh3-64 of the canonical binary encoding of `value`, as 16 hex digits.
pub fn hash_state<T: Serialize + ?Sized>(value: &T) -> String {
    format!("{:016x}", twox_hash::XxHash3_64::oneshot(&canonical_bytes(value)))
}

/// FNV-1a over the UTF-16 code units of `text`, as two 32-bit lanes rendered in hex (the v8
/// state hash over stable JSON; still exported as `fe_hash_text`).
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

/// Encoding failure: only a custom error from a value's own `Serialize` impl.
#[derive(Debug)]
pub struct EncodeError(String);

impl fmt::Display for EncodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for EncodeError {}

impl ser::Error for EncodeError {
    fn custom<T: fmt::Display>(message: T) -> Self {
        Self(message.to_string())
    }
}

struct Encoder {
    out: Vec<u8>,
}

impl Encoder {
    fn length(&mut self, len: usize) {
        self.out.extend_from_slice(&u32::try_from(len).unwrap_or(u32::MAX).to_le_bytes());
    }

    /// Reserves a `u32` length slot to fill in when the sequence ends.
    fn open(&mut self, len: Option<usize>) -> Compound<'_> {
        let slot = self.out.len();
        self.length(len.unwrap_or(0));
        Compound { encoder: self, slot, count: 0 }
    }

    fn variant(&mut self, index: u32) {
        self.out.extend_from_slice(&index.to_le_bytes());
    }
}

/// A sequence or map being written; its length slot is patched on `end`.
struct Compound<'a> {
    encoder: &'a mut Encoder,
    slot: usize,
    count: u32,
}

impl Compound<'_> {
    fn finish(self) {
        self.encoder.out[self.slot..self.slot + 4].copy_from_slice(&self.count.to_le_bytes());
    }
}

/// Struct fields (and tuples): no length, a presence tag per struct field.
struct Fields<'a> {
    encoder: &'a mut Encoder,
}

macro_rules! write_le {
    ($($method:ident: $ty:ty),* $(,)?) => {
        $(
            fn $method(self, value: $ty) -> Result<(), EncodeError> {
                self.out.extend_from_slice(&value.to_le_bytes());
                Ok(())
            }
        )*
    };
}

impl<'a> ser::Serializer for &'a mut Encoder {
    type Ok = ();
    type Error = EncodeError;
    type SerializeSeq = Compound<'a>;
    type SerializeTuple = Fields<'a>;
    type SerializeTupleStruct = Fields<'a>;
    type SerializeTupleVariant = Fields<'a>;
    type SerializeMap = SortedMap<'a>;
    type SerializeStruct = Fields<'a>;
    type SerializeStructVariant = Fields<'a>;

    fn is_human_readable(&self) -> bool {
        false
    }

    fn serialize_bool(self, value: bool) -> Result<(), EncodeError> {
        self.out.push(u8::from(value));
        Ok(())
    }

    write_le! {
        serialize_i8: i8, serialize_i16: i16, serialize_i32: i32, serialize_i64: i64, serialize_i128: i128,
        serialize_u8: u8, serialize_u16: u16, serialize_u32: u32, serialize_u64: u64, serialize_u128: u128,
    }

    fn serialize_f32(self, value: f32) -> Result<(), EncodeError> {
        self.out.extend_from_slice(&value.to_bits().to_le_bytes());
        Ok(())
    }

    fn serialize_f64(self, value: f64) -> Result<(), EncodeError> {
        self.out.extend_from_slice(&value.to_bits().to_le_bytes());
        Ok(())
    }

    fn serialize_char(self, value: char) -> Result<(), EncodeError> {
        self.out.extend_from_slice(&u32::from(value).to_le_bytes());
        Ok(())
    }

    fn serialize_str(self, value: &str) -> Result<(), EncodeError> {
        self.length(value.len());
        self.out.extend_from_slice(value.as_bytes());
        Ok(())
    }

    fn serialize_bytes(self, value: &[u8]) -> Result<(), EncodeError> {
        self.length(value.len());
        self.out.extend_from_slice(value);
        Ok(())
    }

    fn serialize_none(self) -> Result<(), EncodeError> {
        self.out.push(0);
        Ok(())
    }

    fn serialize_some<T: Serialize + ?Sized>(self, value: &T) -> Result<(), EncodeError> {
        self.out.push(1);
        value.serialize(self)
    }

    fn serialize_unit(self) -> Result<(), EncodeError> {
        Ok(())
    }

    fn serialize_unit_struct(self, _name: &'static str) -> Result<(), EncodeError> {
        Ok(())
    }

    fn serialize_unit_variant(
        self,
        _name: &'static str,
        index: u32,
        _variant: &'static str,
    ) -> Result<(), EncodeError> {
        self.variant(index);
        Ok(())
    }

    fn serialize_newtype_struct<T: Serialize + ?Sized>(
        self,
        _name: &'static str,
        value: &T,
    ) -> Result<(), EncodeError> {
        value.serialize(self)
    }

    fn serialize_newtype_variant<T: Serialize + ?Sized>(
        self,
        _name: &'static str,
        index: u32,
        _variant: &'static str,
        value: &T,
    ) -> Result<(), EncodeError> {
        self.variant(index);
        value.serialize(self)
    }

    fn serialize_seq(self, len: Option<usize>) -> Result<Compound<'a>, EncodeError> {
        Ok(self.open(len))
    }

    fn serialize_tuple(self, _len: usize) -> Result<Fields<'a>, EncodeError> {
        Ok(Fields { encoder: self })
    }

    fn serialize_tuple_struct(self, _name: &'static str, _len: usize) -> Result<Fields<'a>, EncodeError> {
        Ok(Fields { encoder: self })
    }

    fn serialize_tuple_variant(
        self,
        _name: &'static str,
        index: u32,
        _variant: &'static str,
        _len: usize,
    ) -> Result<Fields<'a>, EncodeError> {
        self.variant(index);
        Ok(Fields { encoder: self })
    }

    fn serialize_map(self, _len: Option<usize>) -> Result<SortedMap<'a>, EncodeError> {
        Ok(SortedMap { encoder: self, entries: Vec::new() })
    }

    fn serialize_struct(self, _name: &'static str, _len: usize) -> Result<Fields<'a>, EncodeError> {
        Ok(Fields { encoder: self })
    }

    fn serialize_struct_variant(
        self,
        _name: &'static str,
        index: u32,
        _variant: &'static str,
        _len: usize,
    ) -> Result<Fields<'a>, EncodeError> {
        self.variant(index);
        Ok(Fields { encoder: self })
    }
}

impl ser::SerializeSeq for Compound<'_> {
    type Ok = ();
    type Error = EncodeError;

    fn serialize_element<T: Serialize + ?Sized>(&mut self, value: &T) -> Result<(), EncodeError> {
        self.count += 1;
        value.serialize(&mut *self.encoder)
    }

    fn end(self) -> Result<(), EncodeError> {
        self.finish();
        Ok(())
    }
}

/// A map being written: entries are encoded apart and sorted by key bytes on `end`.
struct SortedMap<'a> {
    encoder: &'a mut Encoder,
    entries: Vec<(Vec<u8>, Vec<u8>)>,
}

impl ser::SerializeMap for SortedMap<'_> {
    type Ok = ();
    type Error = EncodeError;

    fn serialize_key<T: Serialize + ?Sized>(&mut self, key: &T) -> Result<(), EncodeError> {
        let mut encoder = Encoder { out: Vec::new() };
        key.serialize(&mut encoder)?;
        self.entries.push((encoder.out, Vec::new()));
        Ok(())
    }

    fn serialize_value<T: Serialize + ?Sized>(&mut self, value: &T) -> Result<(), EncodeError> {
        let mut encoder = Encoder { out: Vec::new() };
        value.serialize(&mut encoder)?;
        if let Some(entry) = self.entries.last_mut() {
            entry.1 = encoder.out;
        }
        Ok(())
    }

    fn end(mut self) -> Result<(), EncodeError> {
        // Stable: equal keys (which a map cannot hold) would keep their order.
        self.entries.sort_by(|a, b| a.0.cmp(&b.0));
        self.encoder.length(self.entries.len());
        for (key, value) in self.entries {
            self.encoder.out.extend_from_slice(&key);
            self.encoder.out.extend_from_slice(&value);
        }
        Ok(())
    }
}

impl ser::SerializeTuple for Fields<'_> {
    type Ok = ();
    type Error = EncodeError;

    fn serialize_element<T: Serialize + ?Sized>(&mut self, value: &T) -> Result<(), EncodeError> {
        value.serialize(&mut *self.encoder)
    }

    fn end(self) -> Result<(), EncodeError> {
        Ok(())
    }
}

impl ser::SerializeTupleStruct for Fields<'_> {
    type Ok = ();
    type Error = EncodeError;

    fn serialize_field<T: Serialize + ?Sized>(&mut self, value: &T) -> Result<(), EncodeError> {
        value.serialize(&mut *self.encoder)
    }

    fn end(self) -> Result<(), EncodeError> {
        Ok(())
    }
}

impl ser::SerializeTupleVariant for Fields<'_> {
    type Ok = ();
    type Error = EncodeError;

    fn serialize_field<T: Serialize + ?Sized>(&mut self, value: &T) -> Result<(), EncodeError> {
        value.serialize(&mut *self.encoder)
    }

    fn end(self) -> Result<(), EncodeError> {
        Ok(())
    }
}

impl ser::SerializeStruct for Fields<'_> {
    type Ok = ();
    type Error = EncodeError;

    fn serialize_field<T: Serialize + ?Sized>(&mut self, _key: &'static str, value: &T) -> Result<(), EncodeError> {
        self.encoder.out.push(1);
        value.serialize(&mut *self.encoder)
    }

    fn skip_field(&mut self, _key: &'static str) -> Result<(), EncodeError> {
        self.encoder.out.push(0);
        Ok(())
    }

    fn end(self) -> Result<(), EncodeError> {
        Ok(())
    }
}

impl ser::SerializeStructVariant for Fields<'_> {
    type Ok = ();
    type Error = EncodeError;

    fn serialize_field<T: Serialize + ?Sized>(&mut self, _key: &'static str, value: &T) -> Result<(), EncodeError> {
        self.encoder.out.push(1);
        value.serialize(&mut *self.encoder)
    }

    fn skip_field(&mut self, _key: &'static str) -> Result<(), EncodeError> {
        self.encoder.out.push(0);
        Ok(())
    }

    fn end(self) -> Result<(), EncodeError> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use indexmap::IndexMap;
    use serde::Serialize;

    #[test]
    fn hashes_text_like_the_v8_engine() {
        assert_eq!(hash_text("{}"), "5465b8257807bf56");
        assert_eq!(hash_text("[]"), "741638a538a6be56");
        assert_eq!(hash_text("null"), "77074ba4d9fff516");
    }

    #[derive(Serialize)]
    struct Sample {
        flag: bool,
        count: u32,
        #[serde(skip_serializing_if = "Option::is_none")]
        skipped: Option<i64>,
        name: String,
        list: Vec<i8>,
        map: IndexMap<String, u16>,
        maybe: Option<u8>,
    }

    #[test]
    fn encodes_the_documented_layout() {
        let mut map = IndexMap::new();
        map.insert("b".to_owned(), 2);
        map.insert("a".to_owned(), 1);
        let sample = Sample {
            flag: true,
            count: 7,
            skipped: None,
            name: "hé".to_owned(),
            list: vec![-1, 2],
            map,
            maybe: Some(9),
        };
        let bytes = canonical_bytes(&sample);
        let expected: Vec<u8> = [
            &[1, 1][..],                                                      // flag: present, true
            &[1, 7, 0, 0, 0],                                                 // count: present, u32 LE
            &[0],                                                             // skipped
            &[1, 3, 0, 0, 0, b'h', 0xc3, 0xa9],                               // name: UTF-8 with u32 length
            &[1, 2, 0, 0, 0, 0xff, 2],                                        // list
            &[1, 2, 0, 0, 0, 1, 0, 0, 0, b'a', 1, 0, 1, 0, 0, 0, b'b', 2, 0], // map sorted by key
            &[1, 1, 9],                                                       // maybe: present, some, 9
        ]
        .concat();
        assert_eq!(bytes, expected);
        assert_eq!(hash_state(&sample).len(), 16);
        assert!(hash_state(&sample).chars().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
    }

    #[test]
    fn distinguishes_skipped_fields_but_not_map_order() {
        #[derive(Serialize)]
        struct Two {
            #[serde(skip_serializing_if = "Option::is_none")]
            a: Option<u8>,
            #[serde(skip_serializing_if = "Option::is_none")]
            b: Option<u8>,
        }
        assert_ne!(hash_state(&Two { a: Some(1), b: None }), hash_state(&Two { a: None, b: Some(1) }));
        let one: IndexMap<&str, u8> = [("a", 1), ("b", 2)].into_iter().collect();
        let other: IndexMap<&str, u8> = [("b", 2), ("a", 1)].into_iter().collect();
        assert_eq!(hash_state(&one), hash_state(&other));
    }

    #[test]
    fn json_values_encode_with_unknown_lengths() {
        let value = serde_json::json!({"x": [1, 2.5, "s"], "y": null});
        let bytes = canonical_bytes(&value);
        // map of 2 entries, patched after the fact
        assert_eq!(&bytes[..4], &[2, 0, 0, 0]);
        assert_eq!(hash_state(&value), hash_state(&serde_json::json!({"x": [1, 2.5, "s"], "y": null})));
    }
}
