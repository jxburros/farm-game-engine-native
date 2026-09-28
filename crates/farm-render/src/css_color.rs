//! Port of `CssColor.cs`: CSS color strings the way a canvas `fillStyle` reads the forms game
//! content uses, plus the [`Color`] type every draw command carries.

use crate::num::{cs_clamp, round_half_even, to_byte};
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::fmt;

/// An sRGB color with straight (not premultiplied) alpha.
///
/// Serializes as `"#rrggbbaa"` so draw lists stay readable.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub struct Color {
    pub r: u8,
    pub g: u8,
    pub b: u8,
    pub a: u8,
}

impl Color {
    pub const TRANSPARENT: Color = Color::rgba(0, 0, 0, 0);
    pub const BLACK: Color = Color::rgb(0, 0, 0);
    pub const WHITE: Color = Color::rgb(255, 255, 255);

    pub const fn rgb(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b, a: 255 }
    }

    pub const fn rgba(r: u8, g: u8, b: u8, a: u8) -> Self {
        Self { r, g, b, a }
    }

    /// The same color with another alpha.
    pub const fn with_alpha(self, a: u8) -> Self {
        Self { a, ..self }
    }

    /// Multiplies alpha by `opacity` (canvas `globalAlpha`; C# `CssColor.WithOpacity`, which
    /// rounds half to even).
    pub fn with_opacity(self, opacity: f64) -> Self {
        self.with_alpha(to_byte(cs_clamp(round_half_even(f64::from(self.a) * opacity), 0.0, 255.0)))
    }

    /// Packed `0xRRGGBBAA` (cache keys).
    pub const fn to_u32(self) -> u32 {
        u32::from_be_bytes([self.r, self.g, self.b, self.a])
    }

    /// Parses a CSS color, or `None` when it is not one of the supported forms.
    pub fn parse(css: &str) -> Option<Color> {
        try_parse(css)
    }
}

impl fmt::Display for Color {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "#{:02x}{:02x}{:02x}{:02x}", self.r, self.g, self.b, self.a)
    }
}

impl Serialize for Color {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_str(self)
    }
}

impl<'de> Deserialize<'de> for Color {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let text = String::deserialize(deserializer)?;
        try_parse(&text).ok_or_else(|| serde::de::Error::custom(format!("not a CSS color: {text}")))
    }
}

/// What unparseable input draws as (`CssColor.Fallback`).
pub const FALLBACK: Color = Color::rgb(0x80, 0x80, 0x80);

/// Named colors understood by the reference renderer (SkiaSharp `SKColors` values).
const NAMED: &[(&str, Color)] = &[
    ("black", Color::rgb(0, 0, 0)),
    ("white", Color::rgb(255, 255, 255)),
    ("red", Color::rgb(255, 0, 0)),
    ("green", Color::rgb(0, 128, 0)),
    ("blue", Color::rgb(0, 0, 255)),
    ("yellow", Color::rgb(255, 255, 0)),
    ("orange", Color::rgb(255, 165, 0)),
    ("purple", Color::rgb(128, 0, 128)),
    ("brown", Color::rgb(165, 42, 42)),
    ("gray", Color::rgb(128, 128, 128)),
    ("grey", Color::rgb(128, 128, 128)),
    ("pink", Color::rgb(255, 192, 203)),
    ("gold", Color::rgb(255, 215, 0)),
    // SKColors.Transparent is transparent white.
    ("transparent", Color::rgba(255, 255, 255, 0)),
];

/// `CssColor.Parse`: `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa` (alpha last), `rgb()`/`rgba()`
/// (numbers or percentages, separated by commas, spaces or `/`) and a few names. Missing,
/// blank or unparseable input gives [`FALLBACK`].
pub fn parse(css: Option<&str>) -> Color {
    match css {
        Some(text) if !text.trim().is_empty() => try_parse(text).unwrap_or(FALLBACK),
        _ => FALLBACK,
    }
}

/// `CssColor.TryParse`.
pub fn try_parse(css: &str) -> Option<Color> {
    let text = css.trim();
    if let Some(hex) = text.strip_prefix('#') {
        if !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
            return None;
        }
        let digit = |i: usize| u8::from_str_radix(&hex[i..=i], 16).ok();
        let pair = |i: usize| u8::from_str_radix(&hex[i..i + 2], 16).ok();
        let nibble = |i: usize| digit(i).map(|v| (v << 4) | v);
        return match hex.len() {
            3 => Some(Color::rgb(nibble(0)?, nibble(1)?, nibble(2)?)),
            4 => Some(Color::rgba(nibble(0)?, nibble(1)?, nibble(2)?, nibble(3)?)),
            6 => Some(Color::rgb(pair(0)?, pair(2)?, pair(4)?)),
            8 => Some(Color::rgba(pair(0)?, pair(2)?, pair(4)?, pair(6)?)),
            _ => None,
        };
    }

    if let Some((_, color)) = NAMED.iter().find(|(name, _)| name.eq_ignore_ascii_case(text)) {
        return Some(*color);
    }

    if text.len() >= 3 && text.as_bytes()[..3].eq_ignore_ascii_case(b"rgb") {
        let open = text.find('(')?;
        let close = text.rfind(')')?;
        if close <= open {
            return None;
        }
        let parts: Vec<&str> = text[open + 1..close].split([',', ' ', '/']).filter(|part| !part.is_empty()).collect();
        if parts.len() < 3 {
            return None;
        }
        let (r, g, b) = (channel(parts[0])?, channel(parts[1])?, channel(parts[2])?);
        let mut alpha = 1.0;
        if parts.len() >= 4 {
            alpha = parse_number(parts[3].trim_end_matches('%'))?;
            if parts[3].ends_with('%') {
                alpha /= 100.0;
            }
        }
        return Some(Color::rgba(r, g, b, to_byte(cs_clamp(round_half_even(alpha * 255.0), 0.0, 255.0))));
    }

    None
}

fn channel(text: &str) -> Option<u8> {
    let percent = text.ends_with('%');
    let number = parse_number(text.trim_end_matches('%'))?;
    let value = if percent { number * 2.55 } else { number };
    Some(to_byte(cs_clamp(round_half_even(value), 0.0, 255.0)))
}

/// `double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture)`: optional
/// surrounding whitespace, sign, digits with a decimal point, exponent, and the invariant
/// `Infinity`/`NaN` symbols.
fn parse_number(text: &str) -> Option<f64> {
    let text = text.trim();
    let unsigned = text.strip_prefix(['+', '-']).unwrap_or(text);
    if unsigned.eq_ignore_ascii_case("infinity") || unsigned.eq_ignore_ascii_case("nan") {
        let value = if unsigned.eq_ignore_ascii_case("nan") { f64::NAN } else { f64::INFINITY };
        return Some(if text.starts_with('-') { -value } else { value });
    }
    // Rust also reads `inf`/`nan` spellings .NET rejects; everything left must be digits.
    if unsigned.is_empty()
        || !unsigned.bytes().all(|b| b.is_ascii_digit() || matches!(b, b'.' | b'e' | b'E' | b'+' | b'-'))
    {
        return None;
    }
    text.parse().ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_canvas_colors() {
        for (css, expected) in [
            ("#5b9a4a", Color::rgba(0x5b, 0x9a, 0x4a, 0xff)),
            ("#ffffff80", Color::rgba(0xff, 0xff, 0xff, 0x80)),
            ("#fff", Color::rgba(0xff, 0xff, 0xff, 0xff)),
            ("#0008", Color::rgba(0x00, 0x00, 0x00, 0x88)),
            ("  #ABCDEF ", Color::rgb(0xab, 0xcd, 0xef)),
            ("rgba(10, 20, 30, 0.5)", Color::rgba(10, 20, 30, 128)),
            ("rgb(10 20 30 / 50%)", Color::rgba(10, 20, 30, 128)),
            // 50% × 2.55 is 127.49999… in doubles, so .NET rounds it down too.
            ("RGB(100%, 0%, 50%)", Color::rgb(255, 0, 127)),
            ("rgb(300, -5, 1e2)", Color::rgb(255, 0, 100)),
            ("Gold", Color::rgb(255, 215, 0)),
            ("transparent", Color::rgba(255, 255, 255, 0)),
        ] {
            assert_eq!(try_parse(css), Some(expected), "{css}");
        }
    }

    #[test]
    fn falls_back_for_garbage() {
        for css in ["not-a-color", "#12", "#12345", "#ggg", "rgb(1,2)", "rgb(1,2,x)", "rgb)1,2,3(", "rgb(inf,0,0)"] {
            assert_eq!(try_parse(css), None, "{css}");
            assert_eq!(parse(Some(css)), FALLBACK);
        }
        assert_eq!(parse(None), FALLBACK);
        assert_eq!(parse(Some("   ")), FALLBACK);
    }

    #[test]
    fn opacity_rounds_half_to_even() {
        // 0x90 * 0.5 = 72; 0x81 * 0.5 = 64.5 → 64 (banker's rounding, like Math.Round).
        assert_eq!(Color::rgba(0, 0, 0, 0x90).with_opacity(0.5).a, 72);
        assert_eq!(Color::rgba(0, 0, 0, 0x81).with_opacity(0.5).a, 64);
        assert_eq!(Color::rgba(0, 0, 0, 0xff).with_opacity(2.0).a, 255);
    }

    #[test]
    fn serializes_as_hex() {
        let json = serde_json::to_string(&Color::rgba(1, 2, 3, 4)).unwrap();
        assert_eq!(json, "\"#01020304\"");
        assert_eq!(serde_json::from_str::<Color>(&json).unwrap(), Color::rgba(1, 2, 3, 4));
    }
}
