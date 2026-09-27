//! JSON for host *views* of the live state (the C# state mirror, hook payloads).
//!
//! Unlike [`farm_sim::stable_json`], which sorts object keys so equal states hash alike, this
//! keeps the order the engine holds: struct fields in declaration order and map entries in
//! insertion order (`serde_json` is built with `preserve_order`). The host deserializes these
//! into ordered records, so a map it iterates (quests, NPCs, flags) lists its entries in the
//! same order the C# engine would. Numbers and strings are written like JavaScript's
//! `JSON.stringify`, as in stable JSON.

use farm_sim::js;
use serde::Serialize;
use serde_json::Value;

/// JSON text of `value` in engine order (see the module docs).
pub fn to_json<T: Serialize>(value: &T) -> String {
    let json = serde_json::to_value(value).expect("engine types always serialize");
    let mut out = String::new();
    write(&mut out, &json);
    out
}

/// Appends `"key":<value in engine order>` to `out`.
pub fn push_member<T: Serialize>(out: &mut String, key: &str, value: &T) {
    js::push_quoted(out, key);
    out.push(':');
    let json = serde_json::to_value(value).expect("engine types always serialize");
    write(out, &json);
}

fn write(out: &mut String, value: &Value) {
    match value {
        Value::Object(map) => {
            out.push('{');
            for (i, (name, item)) in map.iter().enumerate() {
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
        // JSON.stringify writes non-finite numbers as null.
        Value::Number(n) => match n.as_f64() {
            Some(d) if d.is_finite() => out.push_str(&js::num(d)),
            _ => out.push_str("null"),
        },
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
    fn keeps_insertion_order_and_js_numbers() {
        let value = json!({"b": 1.0, "a": [1, 2.5, "x"], "10": true, "2": null});
        assert_eq!(to_json(&value), r#"{"b":1,"a":[1,2.5,"x"],"10":true,"2":null}"#);
    }
}
