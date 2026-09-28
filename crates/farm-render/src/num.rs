//! .NET number semantics the C# renderer relied on. Rendering may use floats (docs/LANGUAGES.md,
//! "Determinism rules for Rust"), but the draw list must pick the same pixels and frames as the
//! Skia reference, so the few places where C# and Rust disagree are spelled out here.

use std::cmp::Ordering;

/// C# `Math.Round(x)`: rounds half to even (banker's rounding), unlike JavaScript `Math.round`.
pub(crate) fn round_half_even(x: f64) -> f64 {
    x.round_ties_even()
}

/// C# `Math.Min(double, double)`: NaN wins (Rust's `f64::min` ignores it).
pub(crate) fn cs_min(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else if a < b {
        a
    } else {
        b
    }
}

/// C# `Math.Max(double, double)`: NaN wins.
pub(crate) fn cs_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else if a > b {
        a
    } else {
        b
    }
}

/// C# `Math.Clamp(double, min, max)`: NaN passes through.
pub(crate) fn cs_clamp(value: f64, min: f64, max: f64) -> f64 {
    if value < min {
        min
    } else if value > max {
        max
    } else {
        value
    }
}

/// C# `double.CompareTo`: NaN sorts before every number and equals itself.
pub(crate) fn cs_compare(a: f64, b: f64) -> Ordering {
    match a.partial_cmp(&b) {
        Some(order) => order,
        None if a.is_nan() && b.is_nan() => Ordering::Equal,
        None if a.is_nan() => Ordering::Less,
        None => Ordering::Greater,
    }
}

/// C# `(int)double`: truncates toward zero, saturates, NaN → 0 (.NET 9+ on every platform).
pub(crate) fn to_int(x: f64) -> i32 {
    x as i32
}

/// C# `(byte)double` for values the renderer already clamped to 0..=255.
pub(crate) fn to_byte(x: f64) -> u8 {
    x as u8
}

/// True for finite whole numbers (`double.IsFinite(x) && Math.Floor(x) == x`).
pub(crate) fn is_integer(x: f64) -> bool {
    x.is_finite() && x.floor() == x
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rounds_like_dotnet() {
        assert_eq!(round_half_even(0.5), 0.0);
        assert_eq!(round_half_even(1.5), 2.0);
        assert_eq!(round_half_even(2.5), 2.0);
        assert_eq!(round_half_even(-2.5), -2.0);
        assert_eq!(round_half_even(2.6), 3.0);
        assert!(round_half_even(f64::NAN).is_nan());
    }

    #[test]
    fn min_max_and_compare_keep_nan() {
        assert!(cs_min(1.0, f64::NAN).is_nan());
        assert!(cs_max(f64::NAN, 1.0).is_nan());
        assert_eq!(cs_min(1.0, 2.0), 1.0);
        assert_eq!(cs_max(1.0, 2.0), 2.0);
        assert!(cs_clamp(f64::NAN, 0.0, 1.0).is_nan());
        assert_eq!(cs_compare(f64::NAN, 0.0), Ordering::Less);
        assert_eq!(cs_compare(0.0, f64::NAN), Ordering::Greater);
        assert_eq!(cs_compare(f64::NAN, f64::NAN), Ordering::Equal);
        assert_eq!(to_int(f64::NAN), 0);
        assert_eq!(to_int(-2.7), -2);
        assert!(is_integer(3.0) && !is_integer(3.5) && !is_integer(f64::INFINITY));
    }
}
