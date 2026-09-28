//! Port of `Atmosphere.cs`: the day/night multiply tint, seasonal foliage tints, and the
//! deterministic weather particles (a hash of tick and index, no random source, so a frame is a
//! pure function of the snapshot).

use crate::css_color::Color;
use crate::num::{cs_clamp, round_half_even, to_byte, to_int};

/// (minute of day, RGB multiplier) keyframes; linear between them.
const DAYLIGHT: [(f64, f32, f32, f32); 8] = [
    (0.0, 0.42, 0.50, 0.86), // deep night: cool blue
    (5.0 * 60.0, 0.42, 0.50, 0.86),
    (6.0 * 60.0 + 30.0, 1.00, 0.82, 0.66), // dawn: warm
    (8.0 * 60.0, 1.0, 1.0, 1.0),           // full day
    (17.0 * 60.0, 1.0, 1.0, 1.0),
    (18.0 * 60.0 + 40.0, 1.00, 0.74, 0.56), // dusk: warm
    (20.0 * 60.0 + 30.0, 0.42, 0.50, 0.86),
    (24.0 * 60.0, 0.42, 0.50, 0.86),
];

/// The multiply tint for a minute of day: white at midday (no tint), warm around dawn and dusk,
/// cool blue at night. Always opaque.
pub fn daylight_tint(time_minutes: f64) -> Color {
    let minute = ((time_minutes % 1440.0) + 1440.0) % 1440.0;
    for i in 1..DAYLIGHT.len() {
        let (m1, r1, g1, b1) = DAYLIGHT[i];
        if minute > m1 {
            continue;
        }
        let (m0, r0, g0, b0) = DAYLIGHT[i - 1];
        let t = if m1 == m0 { 0.0 } else { ((minute - m0) / (m1 - m0)) as f32 };
        return Color::rgb(channel(r0, r1, t), channel(g0, g1, t), channel(b0, b1, t));
    }
    Color::WHITE
}

/// True when [`daylight_tint`] changes nothing (midday).
pub fn is_neutral(tint: Color) -> bool {
    tint.r == 255 && tint.g == 255 && tint.b == 255
}

/// Multiply tint for foliage (trees, weeds) per season: autumn ochre, winter frost; `None` for
/// spring, summer and unknown seasons. Grass tiles have their own seasonal art.
pub fn foliage_tint(season: Option<&str>) -> Option<Color> {
    match season {
        Some("fall" | "autumn") => Some(Color::rgb(0xF0, 0xB8, 0x70)),
        Some("winter") => Some(Color::rgb(0xB8, 0xD0, 0xE8)),
        _ => None,
    }
}

/// Rain or snow overlay for a frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WeatherOverlay {
    Rain,
    Snow,
}

/// Overlay kind for a weather id: the definition's hint (`rain` | `snow`) wins, then the id is
/// guessed (`snow`/`blizzard` → snow, `rain`/`storm` → rain).
pub fn weather_overlay(weather_id: Option<&str>, overlay_hint: Option<&str>) -> Option<WeatherOverlay> {
    match overlay_hint {
        Some("rain") => return Some(WeatherOverlay::Rain),
        Some("snow") => return Some(WeatherOverlay::Snow),
        _ => {}
    }
    let id = weather_id?.to_lowercase();
    if id.contains("snow") || id.contains("blizzard") {
        Some(WeatherOverlay::Snow)
    } else if id.contains("rain") || id.contains("storm") {
        Some(WeatherOverlay::Rain)
    } else {
        None
    }
}

/// Deterministic 32-bit mix (same inputs ⇒ same output on every platform).
pub fn hash(a: u32, b: u32) -> u32 {
    let mut h = a.wrapping_mul(0x9E37_79B1) ^ b.wrapping_add(0x7F4A_7C15);
    h ^= h >> 16;
    h = h.wrapping_mul(0x85EB_CA6B);
    h ^= h >> 13;
    h = h.wrapping_mul(0xC2B2_AE35);
    h ^= h >> 16;
    h
}

/// Uniform [0, 1) from a hash.
pub fn unit(a: u32, b: u32) -> f64 {
    f64::from(hash(a, b)) / 4_294_967_296.0
}

/// Number of weather particles for a viewport (about one per 40×40 px, 8..=600).
pub fn particle_count(view_width: f64, view_height: f64) -> i32 {
    to_int(cs_clamp(round_half_even(view_width * view_height / 1600.0), 8.0, 600.0))
}

fn channel(a: f32, b: f32, t: f32) -> u8 {
    // C# does this in single precision, then rounds half to even as a double.
    to_byte(round_half_even(f64::from(255.0_f32 * (a + ((b - a) * t)))))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn daylight_is_neutral_at_midday_warm_at_dusk_and_blue_at_night() {
        assert_eq!(daylight_tint(13.0 * 60.0), Color::WHITE);
        assert!(is_neutral(daylight_tint(12.0 * 60.0)));
        let night = daylight_tint(0.0);
        assert_eq!(night, Color::rgb(107, 128, 219));
        assert_eq!(daylight_tint(1440.0 + 60.0), daylight_tint(60.0));
        assert_eq!(daylight_tint(-60.0), daylight_tint(23.0 * 60.0));
        let dusk = daylight_tint(18.0 * 60.0 + 40.0);
        assert_eq!(dusk, Color::rgb(255, 189, 143));
        // Halfway between dawn (6:30) and full day (8:00).
        let morning = daylight_tint(7.0 * 60.0 + 15.0);
        assert_eq!(morning, Color::rgb(255, 232, 212));
    }

    #[test]
    fn foliage_and_weather_overlays() {
        assert_eq!(foliage_tint(Some("autumn")), Some(Color::rgb(0xF0, 0xB8, 0x70)));
        assert_eq!(foliage_tint(Some("winter")), Some(Color::rgb(0xB8, 0xD0, 0xE8)));
        assert_eq!(foliage_tint(Some("spring")), None);
        assert_eq!(foliage_tint(None), None);
        assert_eq!(weather_overlay(Some("sun"), Some("snow")), Some(WeatherOverlay::Snow));
        assert_eq!(weather_overlay(Some("Heavy-Storm"), None), Some(WeatherOverlay::Rain));
        assert_eq!(weather_overlay(Some("blizzard"), Some("fog")), Some(WeatherOverlay::Snow));
        assert_eq!(weather_overlay(Some("sun"), None), None);
        assert_eq!(weather_overlay(None, None), None);
    }

    #[test]
    fn particles_are_a_pure_function_of_their_index() {
        assert_eq!(hash(1, 2), hash(1, 2));
        assert_ne!(hash(1, 2), hash(2, 1));
        assert!((0.0..1.0).contains(&unit(7, 3)));
        assert_eq!(particle_count(640.0, 416.0), 166);
        assert_eq!(particle_count(10.0, 10.0), 8);
        assert_eq!(particle_count(4000.0, 4000.0), 600);
    }
}
