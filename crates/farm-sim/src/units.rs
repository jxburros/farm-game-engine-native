//! Units: the integer and fixed-point types of every simulation quantity, and the conversions
//! at the JSON boundary (docs/NUMERICS.md).
//!
//! The simulation keeps each quantity as an integer in a fixed unit (tiles as 1/8192, energy as
//! 1/1000 point, time of day as micro-minutes, probabilities as thresholds out of 2³², …). JSON
//! (projects, cartridge content, state, saves) stays in authoring units. The serde adapters
//! here convert:
//!
//! - **reading** accepts any JSON number, multiplies it by the unit's scale and rounds to the
//!   nearest grid point (ties away from zero, like `f64::round`); values outside the integer
//!   type's range saturate (a negative count reads as 0);
//! - **writing** prints the exact authoring value of the integer: whole values as JSON integers
//!   (`5`, not `5.0`), others as the shortest decimal that reads back to the same double;
//! - the canonical binary encoding of [`crate::hash`] (a serializer that is not human-readable)
//!   gets the integer itself.
//!
//! ```text
//! #[serde(with = "units::energy")]          pub energy: i32,
//! #[serde(with = "units::energy::opt")]     pub max_energy: Option<i32>,
//! #[serde(with = "units::count::nullable")] pub respawn_days: Option<Option<u32>>,
//! ```
//!
//! This is the only module of `farm-sim` that does float arithmetic: a number read from JSON
//! times a power of two or ten, rounded once, is deterministic on every IEEE-754 machine, and
//! nothing after that is a float.
#![allow(
    clippy::float_arithmetic,
    clippy::cast_possible_truncation,
    clippy::cast_sign_loss,
    clippy::cast_precision_loss,
    clippy::cast_lossless
)]

use serde::de::Error as _;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::Value;

/// Fixed simulation rate.
pub const TICKS_PER_SECOND: u32 = 20;
/// One tile in position units (1/256 pixel at the renderer's 32-pixel tiles).
pub const TILE: i32 = 8192;
/// One energy point in energy units.
pub const ENERGY_POINT: i32 = 1000;
/// One minute in time-of-day units (micro-minutes).
pub const MINUTE: u32 = 1_000_000;
/// Minutes in a day.
pub const MINUTES_PER_DAY: u32 = 24 * 60;
/// Probability 1 as a threshold: `(rng.next_u32() as u64) < threshold` never fails.
pub const PROBABILITY_ONE: u64 = 1 << 32;
/// 1.0 in thousandths (price multipliers, rock density, fractions).
pub const MILLI_ONE: u32 = 1000;

/// A quantity kept as an integer on a fixed grid.
pub trait Unit {
    /// The integer type the simulation stores.
    type Int: Copy + Serialize;
    /// The grid point nearest to a finite authoring number.
    fn from_authoring(value: f64) -> Self::Int;
    /// The exact authoring value of a grid point.
    fn to_authoring(value: Self::Int) -> Number;
}

/// An authoring number to write: a whole number, or a double for a fractional value.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Number {
    Int(i64),
    Float(f64),
}

impl Number {
    /// The value as a double (display code, JSON values).
    pub fn as_f64(self) -> f64 {
        match self {
            Self::Int(n) => n as f64,
            Self::Float(x) => x,
        }
    }

    /// A JSON number.
    pub fn to_json(self) -> Value {
        match self {
            Self::Int(n) => Value::from(n),
            Self::Float(x) => value(x),
        }
    }

    fn serialize<S: Serializer>(self, serializer: S) -> Result<S::Ok, S::Error> {
        match self {
            Self::Int(n) => serializer.serialize_i64(n),
            Self::Float(x) => serializer.serialize_f64(x),
        }
    }
}

impl std::fmt::Display for Number {
    /// JavaScript `String(number)` formatting (`5`, `2.5`, `0.000001`, `1e+21`).
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Int(n) => write!(f, "{n}"),
            Self::Float(x) => f.write_str(&format_number(*x)),
        }
    }
}

/// `numerator / denominator` as an authoring number (whole when it divides).
fn ratio(numerator: i64, denominator: i64) -> Number {
    if numerator % denominator == 0 {
        Number::Int(numerator / denominator)
    } else {
        // Both operands are exact doubles (|numerator| < 2^53), so the quotient is the double
        // nearest the exact ratio; its shortest representation is the decimal (or dyadic) value.
        Number::Float(numerator as f64 / denominator as f64)
    }
}

/// Rounds half away from zero and saturates into `min..=max`.
fn quantize(value: f64, scale: f64, min: f64, max: f64) -> f64 {
    (value * scale).round().clamp(min, max)
}

macro_rules! scaled_unit {
    ($(#[$doc:meta])* $name:ident, $int:ty, $scale:expr) => {
        $(#[$doc])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq)]
        pub struct $name;

        impl Unit for $name {
            type Int = $int;
            fn from_authoring(value: f64) -> $int {
                quantize(value, $scale as f64, <$int>::MIN as f64, <$int>::MAX as f64) as $int
            }
            fn to_authoring(value: $int) -> Number {
                ratio(value as i64, $scale as i64)
            }
        }
    };
}

scaled_unit!(
    /// Money, prices, costs, rewards: whole gold.
    Money, i64, 1
);
scaled_unit!(
    /// Counts: quantities, stacks, yields, floors, stages, weights, days, whole minutes, XP.
    Count, u32, 1
);
scaled_unit!(
    /// Whole signed numbers: tile coordinates, tool power/tier/durability, node health,
    /// friendship and mood points, signed deltas.
    Int, i32, 1
);
scaled_unit!(
    /// Whole signed numbers that need 64 bits (money-sized deltas, legacy millisecond times).
    Long, i64, 1
);
scaled_unit!(
    /// Clock ticks.
    Ticks, u64, 1
);
scaled_unit!(
    /// Energy: 1/1000 point.
    Energy, i32, ENERGY_POINT
);
scaled_unit!(
    /// Time of day: micro-minutes (1/1000000 minute).
    MicroMinutes, u32, MINUTE
);
scaled_unit!(
    /// Absolute game time (minutes since day 1, 00:00) in micro-minutes; signed, since day 0
    /// lies before it.
    AbsoluteMicroMinutes, i64, MINUTE
);
scaled_unit!(
    /// Positions: 1/8192 tile.
    Position, i32, TILE
);
scaled_unit!(
    /// Thousandths: price multipliers, repair cost per point (gold), densities, fractions.
    Milli, u32, MILLI_ONE
);
scaled_unit!(
    /// Signed thousandths: event/action `amount`, whose unit depends on the outcome (gold,
    /// energy points, friendship points).
    SignedMilli, i64, MILLI_ONE
);

/// A list index (`chooseDialogueOption`): whole numbers as they are; a fractional index, which
/// v8 rejected (`Number.isInteger`), reads as −1 so it still picks nothing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Index;

impl Unit for Index {
    type Int = i32;
    fn from_authoring(value: f64) -> i32 {
        if value.fract() == 0.0 {
            value.clamp(-1.0, f64::from(i32::MAX)) as i32
        } else {
            -1
        }
    }
    fn to_authoring(value: i32) -> Number {
        Number::Int(i64::from(value))
    }
}

/// Whole numbers read with JavaScript `Math.trunc` (movement intents: `0.7` reads as 0).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Truncated;

impl Unit for Truncated {
    type Int = i32;
    fn from_authoring(value: f64) -> i32 {
        value.trunc().clamp(f64::from(i32::MIN), f64::from(i32::MAX)) as i32
    }
    fn to_authoring(value: i32) -> Number {
        Number::Int(i64::from(value))
    }
}

/// Time rate: authoring minutes per real second, stored as micro-minutes per tick
/// (`rate × 10⁶ / 20`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MinuteRate;

impl Unit for MinuteRate {
    type Int = u32;
    fn from_authoring(value: f64) -> u32 {
        quantize(value, f64::from(MINUTE) / f64::from(TICKS_PER_SECOND), 0.0, f64::from(u32::MAX)) as u32
    }
    fn to_authoring(value: u32) -> Number {
        ratio(i64::from(value) * i64::from(TICKS_PER_SECOND), i64::from(MINUTE))
    }
}

/// Movement speed: authoring tiles per second, stored as 1/8192 tile per tick
/// (`speed × 8192 / 20`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Speed;

impl Unit for Speed {
    type Int = i32;
    fn from_authoring(value: f64) -> i32 {
        quantize(value, f64::from(TILE) / f64::from(TICKS_PER_SECOND), f64::from(i32::MIN), f64::from(i32::MAX)) as i32
    }
    fn to_authoring(value: i32) -> Number {
        ratio(i64::from(value) * i64::from(TICKS_PER_SECOND), i64::from(TILE))
    }
}

/// Probabilities and chances (and other 0–1 fractions such as minigame scores): a threshold
/// out of 2³², `round(p × 2³²)` clamped to `0..=2³²`, compared as
/// `(rng.next_u32() as u64) < threshold`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Probability;

impl Unit for Probability {
    type Int = u64;
    fn from_authoring(value: f64) -> u64 {
        quantize(value, PROBABILITY_ONE as f64, 0.0, PROBABILITY_ONE as f64) as u64
    }
    fn to_authoring(value: u64) -> Number {
        if value.is_multiple_of(PROBABILITY_ONE) {
            Number::Int((value / PROBABILITY_ONE) as i64)
        } else {
            // A dyadic rational: exact as a double.
            Number::Float(value as f64 / PROBABILITY_ONE as f64)
        }
    }
}

/// Reads an authoring number: any JSON number; NaN and ±Infinity (which JSON cannot carry, but
/// a buffered value might) are rejected.
fn read_number<'de, D: Deserializer<'de>>(deserializer: D) -> Result<f64, D::Error> {
    let value = f64::deserialize(deserializer)?;
    if value.is_finite() {
        Ok(value)
    } else {
        Err(D::Error::custom(format!("expected a finite number, got {value}")))
    }
}

/// Serializes a grid value: its authoring number in JSON, the integer in the canonical binary
/// encoding.
pub fn serialize_unit<U: Unit, S: Serializer>(value: U::Int, serializer: S) -> Result<S::Ok, S::Error> {
    if serializer.is_human_readable() {
        U::to_authoring(value).serialize(serializer)
    } else {
        value.serialize(serializer)
    }
}

/// Deserializes an authoring number onto the unit's grid.
pub fn deserialize_unit<'de, U: Unit, D: Deserializer<'de>>(deserializer: D) -> Result<U::Int, D::Error> {
    read_number(deserializer).map(U::from_authoring)
}

/// The grid value of an authoring number (tests, defaults, hosts).
pub fn from_authoring<U: Unit>(value: f64) -> U::Int {
    U::from_authoring(value)
}

/// A position from authoring tiles (`5.5` → 45056).
pub fn pos(tiles: f64) -> i32 {
    Position::from_authoring(tiles)
}

/// Energy from authoring points (`2.5` → 2500).
pub fn energy_points(points: f64) -> i32 {
    Energy::from_authoring(points)
}

/// A time of day from authoring minutes (`390.5` → 390500000).
pub fn time_of_day(minutes: f64) -> u32 {
    MicroMinutes::from_authoring(minutes)
}

/// An event/action outcome `amount` (thousandths) from its authoring number.
pub fn amount(value: f64) -> i64 {
    SignedMilli::from_authoring(value)
}

/// A probability threshold from a 0–1 chance.
pub fn chance(p: f64) -> u64 {
    Probability::from_authoring(p)
}

/// The authoring number of a grid value.
pub fn to_authoring<U: Unit>(value: U::Int) -> Number {
    U::to_authoring(value)
}

/// Wraps one value for serialization through a unit (containers use it element by element).
struct Ser<U: Unit>(U::Int);

impl<U: Unit> Serialize for Ser<U> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serialize_unit::<U, S>(self.0, serializer)
    }
}

/// Reads one authoring number through a unit.
struct De<U: Unit>(U::Int);

impl<'de, U: Unit> Deserialize<'de> for De<U> {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        deserialize_unit::<U, D>(deserializer).map(De)
    }
}

/// `Option<Int>` through a unit (an absent key is the struct default; `null` reads as `None`).
pub fn serialize_opt<U: Unit, S: Serializer>(value: &Option<U::Int>, serializer: S) -> Result<S::Ok, S::Error> {
    match value {
        Some(inner) => serializer.serialize_some(&Ser::<U>(*inner)),
        None => serializer.serialize_none(),
    }
}

pub fn deserialize_opt<'de, U: Unit, D: Deserializer<'de>>(deserializer: D) -> Result<Option<U::Int>, D::Error> {
    Option::<De<U>>::deserialize(deserializer).map(|value| value.map(|De(inner)| inner))
}

/// `.nullable().optional()` through a unit: see [`crate::schema::nullable`].
pub fn serialize_nullable<U: Unit, S: Serializer>(
    value: &Option<Option<U::Int>>,
    serializer: S,
) -> Result<S::Ok, S::Error> {
    match value {
        None | Some(None) => serializer.serialize_none(),
        Some(Some(inner)) => serializer.serialize_some(&Ser::<U>(*inner)),
    }
}

pub fn deserialize_nullable<'de, U: Unit, D: Deserializer<'de>>(
    deserializer: D,
) -> Result<Option<Option<U::Int>>, D::Error> {
    deserialize_opt::<U, D>(deserializer).map(Some)
}

/// `Vec<Int>` through a unit.
pub fn serialize_vec<U: Unit, S: Serializer>(value: &[U::Int], serializer: S) -> Result<S::Ok, S::Error> {
    serializer.collect_seq(value.iter().map(|inner| Ser::<U>(*inner)))
}

pub fn deserialize_vec<'de, U: Unit, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<U::Int>, D::Error> {
    Vec::<De<U>>::deserialize(deserializer).map(|values| values.into_iter().map(|De(inner)| inner).collect())
}

/// `IndexMap<String, Int>` through a unit (insertion order kept).
pub fn serialize_map<U: Unit, S: Serializer>(
    value: &indexmap::IndexMap<String, U::Int>,
    serializer: S,
) -> Result<S::Ok, S::Error> {
    serializer.collect_map(value.iter().map(|(key, inner)| (key, Ser::<U>(*inner))))
}

pub fn deserialize_map<'de, U: Unit, D: Deserializer<'de>>(
    deserializer: D,
) -> Result<indexmap::IndexMap<String, U::Int>, D::Error> {
    indexmap::IndexMap::<String, De<U>>::deserialize(deserializer)
        .map(|values| values.into_iter().map(|(key, De(inner))| (key, inner)).collect())
}

/// `IndexMap<String, IndexMap<String, Int>>` through a unit (daily shop purchases).
/// A map of maps keyed by string (shop id → item id → count).
pub type NestedMap<T> = indexmap::IndexMap<String, indexmap::IndexMap<String, T>>;

pub fn serialize_nested_map<U: Unit, S: Serializer>(
    value: &NestedMap<U::Int>,
    serializer: S,
) -> Result<S::Ok, S::Error> {
    struct Inner<'a, U: Unit>(&'a indexmap::IndexMap<String, U::Int>);
    impl<U: Unit> Serialize for Inner<'_, U> {
        fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
            serialize_map::<U, S>(self.0, serializer)
        }
    }
    serializer.collect_map(value.iter().map(|(key, inner)| (key, Inner::<U>(inner))))
}

pub fn deserialize_nested_map<'de, U: Unit, D: Deserializer<'de>>(
    deserializer: D,
) -> Result<NestedMap<U::Int>, D::Error> {
    struct Inner<U: Unit>(indexmap::IndexMap<String, U::Int>);
    impl<'de, U: Unit> Deserialize<'de> for Inner<U> {
        fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
            deserialize_map::<U, D>(deserializer).map(Inner)
        }
    }
    indexmap::IndexMap::<String, Inner<U>>::deserialize(deserializer)
        .map(|values| values.into_iter().map(|(key, Inner(inner))| (key, inner)).collect())
}

/// Declares `units::<name>` (a plain field) with `opt`, `nullable`, `vec` and `map` submodules
/// for the container shapes, all through the unit `$unit`.
macro_rules! unit_module {
    ($(#[$doc:meta])* $module:ident, $unit:ty) => {
        $(#[$doc])*
        pub mod $module {
            use super::Unit;
            use serde::{Deserializer, Serializer};

            type Int = <$unit as Unit>::Int;

            pub fn serialize<S: Serializer>(value: &Int, serializer: S) -> Result<S::Ok, S::Error> {
                super::serialize_unit::<$unit, S>(*value, serializer)
            }

            pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Int, D::Error> {
                super::deserialize_unit::<$unit, D>(deserializer)
            }

            /// `Option<Int>`.
            pub mod opt {
                use super::Int;
                use serde::{Deserializer, Serializer};

                pub fn serialize<S: Serializer>(value: &Option<Int>, serializer: S) -> Result<S::Ok, S::Error> {
                    super::super::serialize_opt::<$unit, S>(value, serializer)
                }

                pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Option<Int>, D::Error> {
                    super::super::deserialize_opt::<$unit, D>(deserializer)
                }
            }

            /// `Option<Option<Int>>`: absent vs present-as-null (`.nullable().optional()`).
            pub mod nullable {
                use super::Int;
                use serde::{Deserializer, Serializer};

                pub fn serialize<S: Serializer>(
                    value: &Option<Option<Int>>,
                    serializer: S,
                ) -> Result<S::Ok, S::Error> {
                    super::super::serialize_nullable::<$unit, S>(value, serializer)
                }

                pub fn deserialize<'de, D: Deserializer<'de>>(
                    deserializer: D,
                ) -> Result<Option<Option<Int>>, D::Error> {
                    super::super::deserialize_nullable::<$unit, D>(deserializer)
                }
            }

            /// `Vec<Int>`.
            pub mod vec {
                use super::Int;
                use serde::{Deserializer, Serializer};

                pub fn serialize<S: Serializer>(value: &[Int], serializer: S) -> Result<S::Ok, S::Error> {
                    super::super::serialize_vec::<$unit, S>(value, serializer)
                }

                pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<Int>, D::Error> {
                    super::super::deserialize_vec::<$unit, D>(deserializer)
                }
            }

            /// `IndexMap<String, IndexMap<String, Int>>`.
            pub mod nested_map {
                use super::Int;
                use indexmap::IndexMap;
                use serde::{Deserializer, Serializer};

                pub fn serialize<S: Serializer>(
                    value: &IndexMap<String, IndexMap<String, Int>>,
                    serializer: S,
                ) -> Result<S::Ok, S::Error> {
                    super::super::serialize_nested_map::<$unit, S>(value, serializer)
                }

                pub fn deserialize<'de, D: Deserializer<'de>>(
                    deserializer: D,
                ) -> Result<IndexMap<String, IndexMap<String, Int>>, D::Error> {
                    super::super::deserialize_nested_map::<$unit, D>(deserializer)
                }
            }
        }
    };
}

unit_module!(
    /// [`Money`] (`i64`, whole gold).
    money, crate::units::Money
);
unit_module!(
    /// [`Count`] (`u32`, whole).
    count, crate::units::Count
);
unit_module!(
    /// [`Int`] (`i32`, whole).
    int, crate::units::Int
);
unit_module!(
    /// [`Long`] (`i64`, whole).
    long, crate::units::Long
);
unit_module!(
    /// [`Ticks`] (`u64`).
    ticks, crate::units::Ticks
);
unit_module!(
    /// [`Energy`] (`i32`, 1/1000 point).
    energy, crate::units::Energy
);
unit_module!(
    /// [`MicroMinutes`] (`u32`, time of day).
    micro_minutes, crate::units::MicroMinutes
);
unit_module!(
    /// [`AbsoluteMicroMinutes`] (`i64`, minutes since day 1).
    absolute_micro_minutes, crate::units::AbsoluteMicroMinutes
);
unit_module!(
    /// [`MinuteRate`] (`u32`, micro-minutes per tick).
    minute_rate, crate::units::MinuteRate
);
unit_module!(
    /// [`Position`] (`i32`, 1/8192 tile).
    position, crate::units::Position
);
unit_module!(
    /// [`Speed`] (`i32`, 1/8192 tile per tick).
    speed, crate::units::Speed
);
unit_module!(
    /// [`Probability`] (`u64`, threshold out of 2³²).
    probability, crate::units::Probability
);
unit_module!(
    /// [`Milli`] (`u32`, 1/1000).
    milli, crate::units::Milli
);
unit_module!(
    /// [`SignedMilli`] (`i64`, 1/1000).
    signed_milli, crate::units::SignedMilli
);
unit_module!(
    /// [`Index`] (`i32`, a list index; fractions read as −1).
    index, crate::units::Index
);
unit_module!(
    /// [`Truncated`] (`i32`, whole, read with `Math.trunc`).
    truncated, crate::units::Truncated
);

/// Screen-facing numbers (art frame rectangles, sprite-sheet sizes, the editor's pixel
/// interpolation fields): not simulation state, so they stay `f64` and are left out of the
/// state hash (the canonical encoding writes nothing for them). JSON reads any number and
/// writes whole values as integers.
pub mod screen {
    use serde::{Deserialize, Deserializer, Serializer};

    fn write<S: Serializer>(value: f64, serializer: S) -> Result<S::Ok, S::Error> {
        if !serializer.is_human_readable() {
            return serializer.serialize_unit();
        }
        if super::is_integer(value) && value.abs() < 9_007_199_254_740_992.0 {
            serializer.serialize_i64(value as i64)
        } else {
            serializer.serialize_f64(value)
        }
    }

    pub fn serialize<S: Serializer>(value: &f64, serializer: S) -> Result<S::Ok, S::Error> {
        write(*value, serializer)
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<f64, D::Error> {
        f64::deserialize(deserializer)
    }

    /// `Option<f64>`.
    pub mod opt {
        use serde::{Deserialize, Deserializer, Serialize, Serializer};

        struct Screen(f64);

        impl Serialize for Screen {
            fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
                super::write(self.0, serializer)
            }
        }

        pub fn serialize<S: Serializer>(value: &Option<f64>, serializer: S) -> Result<S::Ok, S::Error> {
            if !serializer.is_human_readable() {
                return serializer.serialize_unit();
            }
            match value {
                Some(inner) => serializer.serialize_some(&Screen(*inner)),
                None => serializer.serialize_none(),
            }
        }

        pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Option<f64>, D::Error> {
            Option::<f64>::deserialize(deserializer)
        }
    }
}

/// Numbers kept exactly as authored, never used in arithmetic: the legacy millisecond
/// timestamps (`gameStartTime` seeds the RNG by its text, so `123456.5` must stay `123456.5`).
/// JSON writes whole values as integers; the canonical encoding writes the double's bits.
pub mod exact {
    use serde::{Deserialize, Deserializer, Serialize, Serializer};

    pub fn serialize<S: Serializer>(value: &f64, serializer: S) -> Result<S::Ok, S::Error> {
        if serializer.is_human_readable() && super::is_integer(*value) && value.abs() < 9_007_199_254_740_992.0 {
            serializer.serialize_i64(*value as i64)
        } else {
            value.serialize(serializer)
        }
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<f64, D::Error> {
        f64::deserialize(deserializer)
    }
}

// ─── Integer helpers for game logic ─────────────────────────────────────────────────────────

/// `numerator / denominator` rounded half away from zero (`denominator > 0`).
pub const fn div_round(numerator: i64, denominator: i64) -> i64 {
    let half = denominator / 2;
    if numerator >= 0 {
        (numerator + half) / denominator
    } else {
        (numerator - half) / denominator
    }
}

/// `ceil(numerator / denominator)` for `denominator > 0`.
pub const fn div_ceil(numerator: i64, denominator: i64) -> i64 {
    let quotient = numerator.div_euclid(denominator);
    if numerator.rem_euclid(denominator) == 0 {
        quotient
    } else {
        quotient + 1
    }
}

/// Whole tiles → position units (the tile's top-left corner).
pub const fn tiles(tiles: i32) -> i32 {
    tiles.saturating_mul(TILE)
}

/// The center of a tile, in position units.
pub const fn tile_center(tile: i32) -> i32 {
    tiles(tile).saturating_add(TILE / 2)
}

/// The tile a position lies in (`floor(position / 8192)`).
pub const fn tile_of(position: i32) -> i32 {
    position.div_euclid(TILE)
}

/// Whole energy points → energy units.
pub const fn points(points: i32) -> i32 {
    points.saturating_mul(ENERGY_POINT)
}

/// Whole minutes → micro-minutes.
pub const fn minutes(minutes: u32) -> u32 {
    minutes.saturating_mul(MINUTE)
}

/// The whole minute a time of day lies in.
pub const fn whole_minute(micro_minutes: u32) -> u32 {
    micro_minutes / MINUTE
}

/// `threshold × numerator / denominator` without overflow, clamped to [`PROBABILITY_ONE`].
pub const fn scale_probability(threshold: u64, numerator: u64, denominator: u64) -> u64 {
    let scaled = (threshold as u128 * numerator as u128) / denominator as u128;
    if scaled > PROBABILITY_ONE as u128 {
        PROBABILITY_ONE
    } else {
        scaled as u64
    }
}

// ─── Authoring numbers at the boundary (display, JSON values, hosts) ────────────────────────

/// JavaScript `String(number)` / template-literal formatting of a number: messages, stable
/// JSON and hosts print numbers the way the web version does.
pub fn format_number(value: f64) -> String {
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

/// The authoring text of a grid value, for messages (`"2.5"`, `"12"`).
pub fn text<U: Unit>(value: U::Int) -> String {
    U::to_authoring(value).to_string()
}

/// A JSON number from a double (JS numbers are doubles). Non-finite values become null, like
/// `JSON.stringify`.
pub fn value(number: f64) -> Value {
    serde_json::Number::from_f64(number).map_or(Value::Null, Value::Number)
}

/// A JSON number as a whole number (flag and context values such as a rod tier): integers as
/// they are, finite doubles rounded (a v8 state wrote `1.0`).
pub fn json_int(value: &Value) -> Option<i64> {
    value.as_i64().or_else(|| {
        value.as_f64().filter(|x| x.is_finite()).map(|x| x.round().clamp(i64::MIN as f64, i64::MAX as f64) as i64)
    })
}

/// The largest integer a double holds exactly (JS `Number.MAX_SAFE_INTEGER`).
const MAX_SAFE_INTEGER: f64 = 9_007_199_254_740_991.0;

/// The integer a double equals, when it is whole and within ±(2⁵³−1) (`-0.0` is 0).
pub fn whole_float(x: f64) -> Option<i64> {
    // Exact: |x| ≤ 2⁵³ − 1. NaN and infinities have no zero fraction.
    (x.fract() == 0.0 && x.abs() <= MAX_SAFE_INTEGER).then_some(x as i64)
}

/// A JSON number in its canonical form: a [whole double](whole_float) becomes the integer it
/// equals (`1.0` → `1`). Saves are written as stable JSON, where `1.0` reads back as `1`;
/// holding pass-through values (flags, minigame context) in this form keeps a save/load round
/// trip equal (#142).
pub fn canonical_number(number: serde_json::Number) -> serde_json::Number {
    if number.is_i64() || number.is_u64() {
        return number;
    }
    match number.as_f64().and_then(whole_float) {
        Some(whole) => serde_json::Number::from(whole),
        None => number,
    }
}

/// [`canonical_number`] for every number inside `value`.
pub fn canonical_json(value: Value) -> Value {
    match value {
        Value::Number(number) => Value::Number(canonical_number(number)),
        Value::Array(items) => Value::Array(items.into_iter().map(canonical_json).collect()),
        Value::Object(map) => Value::Object(map.into_iter().map(|(key, value)| (key, canonical_json(value))).collect()),
        other => other,
    }
}

/// The authoring JSON number of a grid value.
pub fn json<U: Unit>(value: U::Int) -> Value {
    U::to_authoring(value).to_json()
}

/// The authoring value of a grid value as a double (renderers, UI, hosts).
pub fn to_f64<U: Unit>(value: U::Int) -> f64 {
    U::to_authoring(value).as_f64()
}

/// A position as a double in tiles (renderers).
pub fn position_to_tiles(position: i32) -> f64 {
    f64::from(position) / f64::from(TILE)
}

/// JS `Number.prototype.toFixed(digits)` for finite values below 1e21 (larger magnitudes and
/// non-finite values fall back to [`format_number`], as JavaScript does). JavaScript rounds an
/// exact tie up (`(0.25).toFixed(1)` is `"0.3"`); Rust's `{:.N}` rounds ties to even, so ties
/// are detected exactly (`value · 2^(digits+1)` is an odd integer) and rounded up by hand.
pub fn to_fixed(value: f64, digits: usize) -> String {
    if !value.is_finite() || value.abs() >= 1e21 {
        return format_number(value);
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

/// JS `Number.isInteger`.
pub fn is_integer(x: f64) -> bool {
    x.is_finite() && x.floor() == x
}

/// JS `Math.round` (halves toward +∞), for hosts and renderers that reproduce web-version
/// display rounding. The simulation itself never rounds a float.
pub fn js_round(x: f64) -> f64 {
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

/// JS `x >>> 0`: ToUint32 (numeric seeds).
pub fn to_uint32(x: f64) -> u32 {
    if !x.is_finite() {
        return 0;
    }
    let t = x.trunc();
    let m = t % 4294967296.0;
    let m = if m < 0.0 { m + 4294967296.0 } else { m };
    m as u32
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde::Deserialize;
    use serde_json::json;

    #[derive(Debug, PartialEq, Serialize, Deserialize, Default)]
    #[serde(default)]
    struct Sample {
        #[serde(with = "money")]
        money: i64,
        #[serde(with = "count")]
        count: u32,
        #[serde(with = "int")]
        int: i32,
        #[serde(with = "energy")]
        energy: i32,
        #[serde(with = "micro_minutes")]
        time: u32,
        #[serde(with = "minute_rate")]
        rate: u32,
        #[serde(with = "position")]
        x: i32,
        #[serde(with = "speed")]
        speed: i32,
        #[serde(with = "probability")]
        chance: u64,
        #[serde(with = "milli")]
        multiplier: u32,
        #[serde(with = "ticks")]
        tick: u64,
        #[serde(with = "energy::opt", skip_serializing_if = "Option::is_none")]
        max_energy: Option<i32>,
        #[serde(with = "count::nullable", skip_serializing_if = "Option::is_none")]
        respawn: Option<Option<u32>>,
        #[serde(with = "count::vec")]
        curve: Vec<u32>,
        #[serde(with = "count::nested_map")]
        bought: indexmap::IndexMap<String, indexmap::IndexMap<String, u32>>,
    }

    fn read(value: serde_json::Value) -> Sample {
        serde_json::from_value(value).expect("sample reads")
    }

    #[test]
    fn grid_values_round_trip_byte_identically() {
        let text = r#"{"money":-12,"count":7,"int":-3,"energy":2.5,"time":390.5,"rate":0.7,"x":5.5,"speed":4.5,"chance":0.25,"multiplier":0.5,"tick":123456789012,"maxEnergy":100,"respawn":null,"curve":[0,50,150],"bought":{"shop":{"seed":2}}}"#;
        // Speed 4.5 tiles/s is 1843.2 units/tick: off the grid (see below), so it is left out
        // of the byte-identical round trip.
        let text = text.replace(r#""speed":4.5"#, r#""speed":4.5"#.replace("4.5", "0.5").as_str());
        let sample: Sample = serde_json::from_str(&text.replace("maxEnergy", "max_energy")).expect("reads");
        assert_eq!(sample.energy, 2500);
        assert_eq!(sample.time, 390_500_000);
        assert_eq!(sample.rate, 35_000);
        assert_eq!(sample.x, 45_056);
        assert_eq!(sample.speed, 205); // 0.5 × 8192 / 20 = 204.8 → 205
        assert_eq!(sample.chance, 1 << 30);
        assert_eq!(sample.multiplier, 500);
        assert_eq!(sample.max_energy, Some(100_000));
        assert_eq!(sample.respawn, Some(None));
        let written = serde_json::to_string(&sample).expect("writes");
        // The speed moved to its grid point (205 units/tick = 0.50048828125 tiles/s).
        let expected = text.replace("maxEnergy", "max_energy").replace(r#""speed":0.5"#, r#""speed":0.50048828125"#);
        assert_eq!(written, expected);
        // A second trip is stable.
        let again: Sample = serde_json::from_str(&written).expect("reads again");
        assert_eq!(again, sample);
        assert_eq!(serde_json::to_string(&again).expect("writes again"), written);
    }

    #[test]
    fn whole_values_are_written_as_integers() {
        let sample = read(json!({"money": 5.0, "energy": 3.0, "x": 2.0, "chance": 1.0, "multiplier": 2.0}));
        let written = serde_json::to_value(&sample).expect("writes");
        assert_eq!(written["money"].to_string(), "5");
        assert_eq!(written["energy"].to_string(), "3");
        assert_eq!(written["x"].to_string(), "2");
        assert_eq!(written["chance"].to_string(), "1");
        assert_eq!(written["multiplier"].to_string(), "2");
    }

    #[test]
    fn off_grid_values_round_once_with_ties_away_from_zero() {
        let sample = read(json!({"money": 12.5, "count": 2.5, "int": -2.5, "energy": 0.0004, "x": -0.00006103515625}));
        assert_eq!(sample.money, 13);
        assert_eq!(sample.count, 3);
        assert_eq!(sample.int, -3);
        assert_eq!(sample.energy, 0);
        assert_eq!(sample.x, -1); // −0.5 unit → −1 (away from zero)
        let sample = read(json!({"energy": 0.0015, "chance": 0.15}));
        assert_eq!(sample.energy, 2);
        assert_eq!(sample.chance, 644_245_094); // 0.15 × 2³² = 644245094.4
        let written = serde_json::to_value(&sample).expect("writes");
        assert_eq!(written["energy"], json!(0.002));
        let moved = written["chance"].as_f64().expect("number");
        assert!(moved != 0.15 && (moved - 0.15).abs() < 1e-9);
        // Reading the written value again changes nothing.
        let again: Sample = serde_json::from_value(written.clone()).expect("reads");
        assert_eq!(serde_json::to_value(&again).expect("writes"), written);
    }

    #[test]
    fn out_of_range_values_saturate_and_probabilities_clamp() {
        let sample = read(json!({"count": -4, "chance": 1.5, "money": 1e300, "tick": -1}));
        assert_eq!(sample.count, 0);
        assert_eq!(sample.chance, PROBABILITY_ONE);
        assert_eq!(sample.money, i64::MAX);
        assert_eq!(sample.tick, 0);
        let sample = read(json!({"chance": -0.25}));
        assert_eq!(sample.chance, 0);
    }

    #[test]
    fn non_numbers_are_rejected_as_before() {
        assert!(serde_json::from_value::<Sample>(json!({"money": "5"})).is_err());
        assert!(serde_json::from_value::<Sample>(json!({"money": null})).is_err());
        assert!(serde_json::from_value::<Sample>(json!({"energy": true})).is_err());
        assert!(serde_json::from_str::<Sample>(r#"{"money": NaN}"#).is_err());
        assert!(serde_json::from_str::<Sample>(r#"{"money": 1e999}"#).is_err());
        let infinity: Result<i64, serde_json::Error> =
            deserialize_unit::<Money, _>(serde::de::value::F64Deserializer::<serde_json::Error>::new(f64::INFINITY));
        assert!(infinity.is_err());
        let nan: Result<i32, serde_json::Error> =
            deserialize_unit::<Energy, _>(serde::de::value::F64Deserializer::<serde_json::Error>::new(f64::NAN));
        assert!(nan.is_err());
    }

    #[test]
    fn options_distinguish_absent_null_and_value() {
        let absent = read(json!({}));
        assert_eq!((absent.max_energy, absent.respawn), (None, None));
        let null = read(json!({"max_energy": null, "respawn": null}));
        assert_eq!((null.max_energy, null.respawn), (None, Some(None)));
        let value = read(json!({"max_energy": 1.5, "respawn": 3}));
        assert_eq!((value.max_energy, value.respawn), (Some(1500), Some(Some(3))));
        let written = serde_json::to_value(&value).expect("writes");
        assert_eq!(written["max_energy"], json!(1.5));
        assert_eq!(written["respawn"], json!(3));
        let written = serde_json::to_value(&null).expect("writes");
        assert_eq!(written.get("max_energy"), None);
        assert_eq!(written["respawn"], serde_json::Value::Null);
    }

    #[test]
    fn integer_helpers() {
        assert_eq!(div_round(5, 2), 3);
        assert_eq!(div_round(-5, 2), -3);
        assert_eq!(div_round(4, 3), 1);
        assert_eq!(div_ceil(5, 2), 3);
        assert_eq!(div_ceil(-5, 2), -2);
        assert_eq!(div_ceil(4, 2), 2);
        assert_eq!(tile_of(-1), -1);
        assert_eq!(tile_of(8191), 0);
        assert_eq!(tile_of(8192), 1);
        assert_eq!(tile_center(3), 3 * 8192 + 4096);
        assert_eq!(scale_probability(PROBABILITY_ONE, 3, 2), PROBABILITY_ONE);
        assert_eq!(scale_probability(1000, 3, 10), 300);
    }

    #[test]
    fn formats_numbers_like_javascript() {
        assert_eq!(format_number(1.0), "1");
        assert_eq!(format_number(0.1 + 0.2), "0.30000000000000004");
        assert_eq!(format_number(1e21), "1e+21");
        assert_eq!(format_number(1e-7), "1e-7");
        assert_eq!(format_number(-0.0), "0");
        assert_eq!(text::<Energy>(2500), "2.5");
        assert_eq!(text::<Energy>(-3000), "-3");
        assert_eq!(text::<MicroMinutes>(1), "0.000001");
    }

    #[test]
    fn to_fixed_rounds_ties_up_like_javascript() {
        assert_eq!(to_fixed(1.2, 1), "1.2");
        assert_eq!(to_fixed(0.25, 1), "0.3");
        assert_eq!(to_fixed(0.35, 1), "0.3");
        assert_eq!(to_fixed(2.5, 0), "3");
        assert_eq!(to_fixed(-0.25, 1), "-0.3");
        assert_eq!(to_fixed(-0.01, 1), "-0.0");
        assert_eq!(to_fixed(1e21, 1), "1e+21");
    }
}
