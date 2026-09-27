//! JavaScript semantics the port depends on (port of `FarmEngine.Json.Js`).
//!
//! The TypeScript engine is the reference implementation; wherever Rust and JavaScript differ
//! (rounding, number formatting, sort stability, string ordering) the port calls into here so
//! behaviour, and therefore state hashes, stay byte-identical.

use serde_json::Value;
use std::cmp::Ordering;

/// JS `Math.round`: halves round toward +∞ (`f64::round` rounds half away from zero).
pub fn round(x: f64) -> f64 {
    if x.is_nan() || x.is_infinite() {
        return x;
    }
    let floor = x.floor();
    if x - floor >= 0.5 {
        floor + 1.0
    } else {
        floor
    }
}

/// JS `Math.trunc`.
pub fn trunc(x: f64) -> f64 {
    x.trunc()
}

/// JS `Number.isInteger`.
pub fn is_integer(x: f64) -> bool {
    x.is_finite() && x.floor() == x
}

/// JS `x % y` (sign follows the dividend, like Rust's `%` on floats).
pub fn modulo(x: f64, y: f64) -> f64 {
    x % y
}

/// JS `x | 0`: ToInt32.
pub fn to_int32(x: f64) -> i32 {
    to_uint32(x) as i32
}

/// JS `x >>> 0`: ToUint32.
pub fn to_uint32(x: f64) -> u32 {
    if !x.is_finite() {
        return 0;
    }
    let t = x.trunc();
    let m = t % 4294967296.0;
    let m = if m < 0.0 { m + 4294967296.0 } else { m };
    m as u32
}

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

/// Stable sort returning a new vector (JS `Array.prototype.sort` is stable since ES2019).
/// `slice::sort_by` is stable too; this exists for call sites that want the JS shape.
pub fn stable_sort<T: Clone, F: FnMut(&T, &T) -> Ordering>(source: &[T], compare: F) -> Vec<T> {
    let mut out = source.to_vec();
    out.sort_by(compare);
    out
}

/// JS `String(number)` / template-literal formatting of a number.
pub fn num(value: f64) -> String {
    if value.is_nan() {
        return "NaN".to_owned();
    }
    if value == f64::INFINITY {
        return "Infinity".to_owned();
    }
    if value == f64::NEG_INFINITY {
        return "-Infinity".to_owned();
    }
    if value == 0.0 {
        return "0".to_owned();
    }
    let mut buffer = ryu_js::Buffer::new();
    buffer.format(value).to_owned()
}

/// JS `Number.prototype.toFixed(digits)` for finite values below 1e21 (larger magnitudes and
/// non-finite values fall back to [`num`], as JavaScript does). JavaScript rounds an exact tie
/// up (`(0.25).toFixed(1)` is `"0.3"`); Rust's `{:.N}` rounds ties to even, so ties are detected
/// exactly (`value · 2^(digits+1)` is an odd integer) and rounded up by hand.
pub fn to_fixed(value: f64, digits: usize) -> String {
    if !value.is_finite() || value.abs() >= 1e21 {
        return num(value);
    }
    if value < 0.0 {
        // JS keeps the sign even when the rounded magnitude is zero ("-0.0").
        return format!("-{}", to_fixed(-value, digits));
    }
    // -0 formats like 0 in JavaScript (`x < 0` is false for it).
    let value = value.abs();
    let scaled = value * 2f64.powi(digits as i32 + 1);
    let is_tie = scaled.fract() == 0.0 && scaled % 2.0 == 1.0;
    if !is_tie {
        return format!("{value:.digits$}");
    }
    // Exact tie: JavaScript picks the larger candidate n / 10^digits.
    let n = (value * 10f64.powi(digits as i32)).ceil();
    let mut text = format!("{n:.0}");
    if digits == 0 {
        return text;
    }
    while text.len() <= digits {
        text.insert(0, '0');
    }
    text.insert(text.len() - digits, '.');
    text
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

/// A JSON number from an `f64` (JS numbers are always doubles). Non-finite values become null,
/// like `JSON.stringify`.
pub fn value(number: f64) -> Value {
    serde_json::Number::from_f64(number).map(Value::Number).unwrap_or(Value::Null)
}

/// The `f64` inside a JSON value (`null`/other → `None`).
pub fn as_f64(value: &Value) -> Option<f64> {
    value.as_f64()
}

/// JS `String(value)` for the scalar kinds used in flags and conditions.
pub fn to_js_string(value: &Value) -> String {
    match value {
        Value::Null => "null".to_owned(),
        Value::Bool(b) => b.to_string(),
        Value::Number(n) => num(n.as_f64().unwrap_or(f64::NAN)),
        Value::String(s) => s.clone(),
        other => other.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rounds_halves_toward_positive_infinity() {
        assert_eq!(round(2.5), 3.0);
        assert_eq!(round(-2.5), -2.0);
        assert_eq!(round(0.49999999999999994), 0.0);
        assert_eq!(round(-0.5), 0.0);
    }

    #[test]
    fn formats_numbers_like_javascript() {
        assert_eq!(num(1.0), "1");
        assert_eq!(num(0.1 + 0.2), "0.30000000000000004");
        assert_eq!(num(1e21), "1e+21");
        assert_eq!(num(1e-7), "1e-7");
        assert_eq!(num(123456789012345680000.0), "123456789012345680000");
        assert_eq!(num(-0.0), "0");
        assert_eq!(num(5e-324), "5e-324");
        assert_eq!(num(0.000001), "0.000001");
    }

    #[test]
    fn to_fixed_rounds_ties_up_like_javascript() {
        assert_eq!(to_fixed(1.2, 1), "1.2");
        assert_eq!(to_fixed(0.25, 1), "0.3");
        assert_eq!(to_fixed(0.35, 1), "0.3"); // 0.35 is just below the tie in binary
        assert_eq!(to_fixed(2.5, 0), "3");
        assert_eq!(to_fixed(0.125, 2), "0.13");
        assert_eq!(to_fixed(0.0625, 3), "0.063");
        assert_eq!(to_fixed(-0.25, 1), "-0.3");
        assert_eq!(to_fixed(-0.01, 1), "-0.0");
        assert_eq!(to_fixed(-0.0, 1), "0.0");
        assert_eq!(to_fixed(0.001, 1), "0.0");
        assert_eq!(to_fixed(10.0, 1), "10.0");
        assert_eq!(to_fixed(1e21, 1), "1e+21");
    }

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
}
