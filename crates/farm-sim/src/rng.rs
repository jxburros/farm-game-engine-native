//! Seeded, serializable PRNG: the single entry point for all randomness in the simulation
//! (port of engine-core/src/rng.ts / `Rng.cs`). Algorithm: xoshiro128** (Blackman & Vigna),
//! 128-bit state. State lives in `GameState.rng` so a saved game resumes its random stream
//! deterministically. All arithmetic wraps, matching `Math.imul` and `>>> 0`. Draws are used as
//! integers (docs/NUMERICS.md "Randomness"): chances compare against thresholds out of 2³², ranges
//! and weighted picks scale the draw with a 64-bit multiply and shift.

use serde::{Deserialize, Serialize};

/// Serialized PRNG state: deterministic resume is a core guarantee.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RngState {
    /// Always `"xoshiro128ss"`.
    pub algorithm: String,
    /// The four 32-bit xoshiro128** state words.
    pub s: [u32; 4],
}

impl RngState {
    fn with_words(s: [u32; 4]) -> Self {
        Self { algorithm: "xoshiro128ss".to_owned(), s }
    }
}

impl Default for RngState {
    fn default() -> Self {
        Self::with_words([0; 4])
    }
}

/// FNV-1a hash of a string (UTF-16 code units) to a u32, for string seeds.
pub fn hash_string_to_u32(input: &str) -> u32 {
    let mut hash: u32 = 0x811c9dc5;
    for unit in input.encode_utf16() {
        hash ^= u32::from(unit);
        hash = hash.wrapping_mul(0x01000193);
    }
    hash
}

/// JS `seed >>> 0` for a numeric seed (ToUint32).
pub fn to_uint32(seed: f64) -> u32 {
    crate::units::to_uint32(seed)
}

/// Initial state for a string seed.
pub fn create_rng_state(seed: &str) -> RngState {
    create_rng_state_from_u32(hash_string_to_u32(seed))
}

/// Initial state for a numeric seed (JSON seeds; `seed >>> 0`).
pub fn create_rng_state_from_number(seed: f64) -> RngState {
    create_rng_state_from_u32(to_uint32(seed))
}

/// Initial state for a 32-bit seed.
pub fn create_rng_state_from_u32(seed: u32) -> RngState {
    let mut a = seed;
    let mut next = || {
        a = a.wrapping_add(0x9e3779b9);
        let mut t = a;
        t = (t ^ (t >> 16)).wrapping_mul(0x21f0aaad);
        t = (t ^ (t >> 15)).wrapping_mul(0x735a2d97);
        t ^ (t >> 15)
    };
    let mut s = [next(), next(), next(), next()];
    // xoshiro must not be seeded with all zeros.
    if s.iter().all(|&v| v == 0) {
        s[0] = 1;
    }
    RngState::with_words(s)
}

/// Functional draw: returns the value plus the next state.
pub fn next_u32(state: &RngState) -> (u32, RngState) {
    let [s0, s1, s2, s3] = state.s;
    let result = s1.wrapping_mul(5).rotate_left(7).wrapping_mul(9);
    let t = s1 << 9;
    let mut n2 = s2 ^ s0;
    let n3 = s3 ^ s1;
    let n1 = s1 ^ n2;
    let n0 = s0 ^ n3;
    n2 ^= t;
    let n3 = n3.rotate_left(11);
    (result, RngState::with_words([n0, n1, n2, n3]))
}

/// `floor(u / 2³² × n)` for a draw `u`: the integer form of v8's `floor(nextFloat() × n)`,
/// exact for every `n` (negative too: the shift floors).
pub fn scale_draw(draw: u32, n: i64) -> i64 {
    ((i128::from(draw) * i128::from(n)) >> 32) as i64
}

/// Uniform integer in [min, max] inclusive (`((u × (max − min + 1)) >> 32) + min`).
pub fn next_int(state: &RngState, min: i64, max: i64) -> (i64, RngState) {
    let (value, next) = next_u32(state);
    (scale_draw(value, max - min + 1) + min, next)
}

/// Minimal random-source interface consumed by game math (port of `IRandomSource`).
pub trait RandomSource {
    /// One 32-bit draw.
    fn next_u32(&mut self) -> u32;
    /// Uniform integer in [min, max] inclusive.
    fn int(&mut self, min: i64, max: i64) -> i64;
}

/// Mutable convenience wrapper for command handlers: draws update the state in place; the
/// handler stores the final state back into `GameState` once.
#[derive(Debug, Clone)]
pub struct Rng {
    pub state: RngState,
}

impl Rng {
    pub fn new(state: RngState) -> Self {
        Self { state }
    }

    pub fn next_u32(&mut self) -> u32 {
        let (value, next) = next_u32(&self.state);
        self.state = next;
        value
    }

    /// One draw against a probability threshold (out of 2³²): v8's `nextFloat() < p`.
    pub fn chance(&mut self, threshold: u64) -> bool {
        u64::from(self.next_u32()) < threshold
    }

    /// One draw against an exact fraction of 2³² units: `u < numerator / denominator` (chances
    /// that are products of thresholds, compared without rounding).
    pub fn chance_below(&mut self, (numerator, denominator): (u128, u128)) -> bool {
        u128::from(self.next_u32()) * denominator < numerator
    }

    pub fn int(&mut self, min: i64, max: i64) -> i64 {
        let (value, next) = next_int(&self.state, min, max);
        self.state = next;
        value
    }

    /// Pick an index from integer weights: `(u × total) >> 32`, then walk the weights. `None`
    /// for an empty or all-zero table (no draw). The product is taken in 128 bits: content
    /// weights are `u32`s each, so their sum can pass 2³² and the product 2⁶⁴.
    pub fn weighted(&mut self, weights: &[u32]) -> Option<usize> {
        let total: u64 = weights.iter().map(|w| u64::from(*w)).sum();
        if total == 0 {
            return None;
        }
        // `u < 2³²`, so the shifted product is below `total` and fits a u64.
        let mut roll = ((u128::from(self.next_u32()) * u128::from(total)) >> 32) as u64;
        for (i, w) in weights.iter().enumerate() {
            let w = u64::from(*w);
            if roll < w {
                return Some(i);
            }
            roll -= w;
        }
        Some(weights.len() - 1)
    }
}

impl RandomSource for Rng {
    fn next_u32(&mut self) -> u32 {
        Rng::next_u32(self)
    }

    fn int(&mut self, min: i64, max: i64) -> i64 {
        Rng::int(self, min, max)
    }
}
