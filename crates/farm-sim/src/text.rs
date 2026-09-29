//! Text and JSON-value semantics the JSON boundary shares with the web version: UTF-16 key
//! order and `JSON.stringify` escaping for stable JSON, and JavaScript truthiness and
//! `String(value)` for flag values (flags hold `boolean | number | string`).

use crate::units;
use serde_json::Value;
use std::cmp::Ordering;

/// JS default `Array.prototype.sort` comparison / `<` on strings: UTF-16 code units.
pub fn compare_strings(a: &str, b: &str) -> Ordering {
    let mut ia = a.encode_utf16();
    let mut ib = b.encode_utf16();
    loop {
        match (ia.next(), ib.next()) {
            (None, None) => return Ordering::Equal,
            (None, Some(_)) => return Ordering::Less,
            (Some(_), None) => return Ordering::Greater,
            (Some(x), Some(y)) => match x.cmp(&y) {
                Ordering::Equal => continue,
                other => return other,
            },
        }
    }
}

/// JS `JSON.stringify` escaping of a string, including the quotes.
pub fn quote_string(value: &str) -> String {
    let mut out = String::with_capacity(value.len() + 2);
    push_quoted(&mut out, value);
    out
}

/// Appends the `JSON.stringify` encoding of `value` (with quotes) to `out`.
pub fn push_quoted(out: &mut String, value: &str) {
    out.push('"');
    for c in value.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => {
                out.push_str(&format!("\\u{:04x}", c as u32));
            }
            c => out.push(c),
        }
    }
    out.push('"');
}

/// JS truthiness of an arbitrary JSON value (flags, plugin payloads).
pub fn truthy(value: Option<&Value>) -> bool {
    match value {
        None | Some(Value::Null) => false,
        Some(Value::Bool(b)) => *b,
        Some(Value::Number(n)) => n.as_f64().is_some_and(|d| d != 0.0 && !d.is_nan()),
        Some(Value::String(s)) => !s.is_empty(),
        Some(_) => true,
    }
}

/// JS `String(value)` for the scalar kinds used in flags and conditions.
pub fn to_js_string(value: &Value) -> String {
    match value {
        Value::Null => "null".to_owned(),
        Value::Bool(b) => b.to_string(),
        Value::Number(n) => units::format_number(n.as_f64().unwrap_or(f64::NAN)),
        Value::String(s) => s.clone(),
        other => other.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compares_by_utf16_code_units() {
        assert_eq!(compare_strings("a", "b"), Ordering::Less);
        // U+FFFF sorts after a surrogate pair in UTF-16 (opposite of code point order).
        assert_eq!(compare_strings("\u{FFFF}", "\u{1F600}"), Ordering::Greater);
    }

    #[test]
    fn quotes_control_characters() {
        assert_eq!(quote_string("a\"b\\c\n\u{1}"), "\"a\\\"b\\\\c\\n\\u0001\"");
    }

    #[test]
    fn truthiness_follows_javascript() {
        assert!(!truthy(None));
        assert!(!truthy(Some(&Value::from(0))));
        assert!(truthy(Some(&Value::from(2.5))));
        assert!(!truthy(Some(&Value::from(""))));
        assert!(truthy(Some(&Value::from("x"))));
    }
}
