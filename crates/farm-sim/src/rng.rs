//! Seeded, serializable PRNG: the single entry point for all randomness in the simulation
//! (port of engine-core/src/rng.ts / `Rng.cs`). Algorithm: xoshiro128** (Blackman & Vigna),
//! 128-bit state. State lives in `GameState.rng` so a saved game resumes its random stream
//! deterministically. All arithmetic wraps, matching `Math.imul` and `>>> 0`.

use serde::{Deserialize, Serialize};

const U32: f64 = 4294967296.0;

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
    crate::js::to_uint32(seed)
}

/// Initial state for a string seed.
pub fn create_rng_state(seed: &str) -> RngState {
    create_from_u32(hash_string_to_u32(seed))
}

/// Initial state for a numeric seed.
pub fn create_rng_state_from_number(seed: f64) -> RngState {
    create_from_u32(to_uint32(seed))
}

fn create_from_u32(seed: u32) -> RngState {
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

/// Uniform float in [0, 1).
pub fn next_float(state: &RngState) -> (f64, RngState) {
    let (value, next) = next_u32(state);
    (f64::from(value) / U32, next)
}

/// Uniform integer in [min, max] inclusive.
pub fn next_int(state: &RngState, min: f64, max: f64) -> (f64, RngState) {
    let (value, next) = next_float(state);
    ((value * (max - min + 1.0)).floor() + min, next)
}

/// Minimal random-source interface consumed by game math (port of `IRandomSource`).
pub trait RandomSource {
    fn float(&mut self) -> f64;
    fn int(&mut self, min: f64, max: f64) -> f64;
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

    pub fn float(&mut self) -> f64 {
        let (value, next) = next_float(&self.state);
        self.state = next;
        value
    }

    pub fn int(&mut self, min: f64, max: f64) -> f64 {
        let (value, next) = next_int(&self.state, min, max);
        self.state = next;
        value
    }

    /// Pick an index from weighted entries. Returns -1 for an empty/zero table.
    #[allow(clippy::cast_possible_wrap)]
    pub fn weighted(&mut self, weights: &[f64]) -> i64 {
        let mut total = 0.0;
        for w in weights {
            total += w;
        }
        if total <= 0.0 {
            return -1;
        }
        let mut roll = self.float() * total;
        for (i, w) in weights.iter().enumerate() {
            roll -= w;
            if roll < 0.0 {
                return i as i64;
            }
        }
        weights.len() as i64 - 1
    }
}

impl RandomSource for Rng {
    fn float(&mut self) -> f64 {
        Rng::float(self)
    }

    fn int(&mut self, min: f64, max: f64) -> f64 {
        Rng::int(self, min, max)
    }
}
