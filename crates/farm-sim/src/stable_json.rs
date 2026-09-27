//! Port of `stableStringify` (engine-core/src/hash.ts, `FarmEngine.Json.StableJson`): JSON with
//! object keys sorted by UTF-16 code unit and JavaScript number/string formatting, so a Rust
//! state and a TypeScript state that are equal produce identical text.

use crate::js;
use serde::Serialize;
use serde_json::Value;

/// Stable JSON text of any serializable value.
pub fn stringify<T: Serialize>(value: &T) -> String {
    let json = serde_json::to_value(value).expect("engine types always serialize");
    stringify_value(&json)
}

/// Stable JSON text of a JSON value.
pub fn stringify_value(value: &Value) -> String {
    let mut out = String::new();
    write(&mut out, value);
    out
}

/// True for canonical array-index strings: "0" or no leading zero, value ≤ 2^32 − 2.
pub fn array_index(key: &str) -> Option<u32> {
    if key.is_empty() || key.len() > 10 {
        return None;
    }
    if key.len() > 1 && key.starts_with('0') {
        return None;
    }
    let mut value: u64 = 0;
    for c in key.bytes() {
        if !c.is_ascii_digit() {
            return None;
        }
        value = value * 10 + u64::from(c - b'0');
    }
    if value > 4_294_967_294 {
        return None;
    }
    Some(value as u32)
}

/// JS objects enumerate array-index keys ("0".."4294967294", canonical form) first in ascending
/// numeric order, then the remaining keys in insertion order. `stableStringify` inserts keys
/// sorted, so the final order is: index keys numerically, then the rest as sorted.
pub fn order_like_js_object<T>(items: Vec<T>, key: impl Fn(&T) -> &str) -> Vec<T> {
    let mut index_keys: Vec<(u32, T)> = Vec::new();
    let mut rest: Vec<T> = Vec::new();
    for item in items {
        match array_index(key(&item)) {
            Some(index) => index_keys.push((index, item)),
            None => rest.push(item),
        }
    }
    if index_keys.is_empty() {
        return rest;
    }
    index_keys.sort_by_key(|(index, _)| *index);
    index_keys.into_iter().map(|(_, item)| item).chain(rest).collect()
}

fn write(out: &mut String, value: &Value) {
    match value {
        Value::Object(map) => {
            // Later duplicates win (JS object semantics); serde_json objects have no duplicates.
            let mut props: Vec<(&str, &Value)> = map.iter().map(|(k, v)| (k.as_str(), v)).collect();
            props.sort_by(|a, b| js::compare_strings(a.0, b.0));
            let props = order_like_js_object(props, |p| p.0);
            out.push('{');
            for (i, (name, item)) in props.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                js::push_quoted(out, name);
                out.push(':');
                write(out, item);
            }
            out.push('}');
        }
        Value::Array(items) => {
            out.push('[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                write(out, item);
            }
            out.push(']');
        }
        Value::String(s) => js::push_quoted(out, s),
        Value::Number(n) => {
            // JSON.stringify writes non-finite numbers as null.
            match n.as_f64() {
                Some(d) if d.is_finite() => out.push_str(&js::num(d)),
                _ => out.push_str("null"),
            }
        }
        Value::Bool(true) => out.push_str("true"),
        Value::Bool(false) => out.push_str("false"),
        Value::Null => out.push_str("null"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn sorts_keys_and_puts_index_keys_first() {
        let value = json!({"b": 1, "a": [1, 2.5, "x"], "10": true, "2": null, "01": 0});
        assert_eq!(stringify_value(&value), r#"{"2":null,"10":true,"01":0,"a":[1,2.5,"x"],"b":1}"#);
    }

    #[test]
    fn recognises_array_indices() {
        assert_eq!(array_index("0"), Some(0));
        assert_eq!(array_index("4294967294"), Some(4294967294));
        assert_eq!(array_index("4294967295"), None);
        assert_eq!(array_index("01"), None);
        assert_eq!(array_index(""), None);
    }
}
