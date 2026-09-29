# Native numerics (schema v9)

**Status:** design for phase 7's last step ([LANGUAGES.md](LANGUAGES.md#phases)).
This file is the reference for the unit and integer type of every quantity the
simulation keeps. Read it before you add a field to `farm-sim`.

## Why

Until v8 the Rust core ran with JavaScript number semantics (`f64` everywhere,
the `js` helpers, FNV-1a over stable JSON) so that it reproduced the
TypeScript engine's goldens bit for bit. Now that the web version plays
through `farm-wasm` and authors through the Fable-compiled F# core, there is
one engine, and the constraints change:

- **Determinism across machines** (replays, saves, future lockstep play) is
  easier to guarantee with integer arithmetic than with floats, whose results
  depend on operation order and, for transcendental functions, on the libm.
- **Exact rules.** "Adds 0.1 energy per tick" drifts in binary floating
  point; "adds 100 milli-points per tick" does not.
- **A cheaper, stronger state hash** over a binary encoding instead of stable
  JSON.

Rendering and audio may keep using floats: they read the state and never
write it.

## The rule

1. `farm-sim` keeps every quantity of the game state and the compiled content
   as an **integer in a fixed unit** (the tables below). Game logic never does
   float arithmetic: the crate has `#![deny(clippy::float_arithmetic)]`, with
   `#[allow]` only in `farm_sim::units` (the conversions below).
2. **JSON stays in authoring units.** Project JSON, cartridge content JSON,
   state JSON and saves keep the numbers creators and the web version know
   (tiles, energy points, minutes, probabilities in 0–1). Serde adapters in
   `farm_sim::units` convert at the boundary: reading rounds the number to the
   unit's grid (ties away from zero, like `f64::round`), writing prints the
   exact value of the integer in authoring units. A value already on the grid
   round-trips unchanged; one that isn't is moved to the nearest grid point
   once, when it is read.
3. **Conversions happen once**, at load. A `f64` read from JSON times a power
   of two or ten, rounded to an integer, is deterministic on every IEEE-754
   machine; nothing after that is.

## Units

| Quantity | Type | Unit | JSON (authoring) |
|---|---|---|---|
| Money, prices, costs, rewards, repair cost, animal purchase cost | `i64` | whole gold | gold |
| Price multipliers (`sellPriceMultiplier`) | `u32` | 1/1000 | factor (0.5) |
| Counts: quantities, stacks, yields, harvest counts, inventory size, floors, stages, weights of drops/fish/rocks/weather | `u32` (`i64` where a difference can be negative) | whole | count |
| Energy, max energy, energy costs | `i32` | 1/1000 point | points (2.5) |
| Absolute day, day of season, year, days grown, days without water, age and interval days | `u32` | whole day | days |
| Time of day | `u32` | 1/1000000 minute ("micro-minutes"; v8 already quantized to this) | minutes (390.5) |
| Minutes a machine processes, schedule minutes | `u32` | whole minute | minutes |
| Clock ticks | `u64` | tick (20 per second) | ticks |
| Time rate (`minutesPerRealSecond`) | `u32` | micro-minutes per tick, at load (`rate × 10⁶ / 20`) | minutes per second |
| Position of the player, NPCs, animals, entrance points | `i32` | 1/8192 tile (1/256 pixel at the renderer's 32-pixel tiles) | tiles (5.5) |
| Movement speed | `i32` | 1/8192 tile per tick, at load (`speed × 8192 / 20`) | tiles per second |
| Probabilities and chances (mutation, ladder, junk, weather, crop damage, drop chance) | `u64` | threshold out of 2³² (`p × 2³²`, clamped to 0..=2³²) | 0–1 |
| Friendship, mood | `i32` | points | points |
| Skill XP, skill levels, curve thresholds | `u32` | whole | whole |
| Tool power, tier, durability; node health | `i32` | whole | whole |
| Density of mine rocks | `u32` | 1/1000 | fraction |

Screen-facing fields (`pixelX`, sprite frame sizes, asset sizes, panel
layout) are not simulation state; they stay `f64` in the schema and are not
hashed.

Anything a creator can type that isn't on the grid is rounded when read: a
price of 12.5 gold becomes 13 (ties away from zero), an energy cost of
0.0004 becomes 0. F# Problems warns about such values (see "Project
migration" below), so they don't surprise anyone.

## Randomness

The RNG is unchanged (xoshiro128\*\* with the v8 seeding). What changes is how
its output is used:

| v8 (`f64`) | v9 (integer) | Same outcome? |
|---|---|---|
| `next_float() < p` | `(next_u32() as u64) < threshold(p)` with `threshold = round(p × 2³²)` | Yes, whenever `p × 2³²` is an integer (every probability with at most 32 binary digits, including all built-in content); otherwise it can differ by one draw in 2³² |
| `floor(next_float() × n) + min` (`next_int`) | `((next_u32() as u64 × n) >> 32) + min` | Yes, exactly (`next_float` is `u / 2³²`) |
| weighted pick: `next_float() × total`, walk the weights | `(next_u32() as u64 × total) >> 32`, walk integer weights | Yes for integer weights |

## State hash

v9 replaces FNV-1a over stable JSON with **xxh3-64 over the canonical binary
encoding** of the state: a serde serializer in `farm_sim::hash` that writes
integers little-endian, booleans as one byte, strings and sequences with a
`u32` length prefix, map entries in their stored order and options as a tag
byte. It serializes the integer fields as integers (the serde adapters know
whether they write JSON or the canonical form). The hash text is 16 lowercase
hex digits, as before.

## Saves

`CURRENT_SAVE_VERSION` goes from 4 to 5 (saves have their own numbering). The
v4→v5 migration is the quantization in the serde adapters, applied when the
v4 state is read, plus the version bump. A v8-era save therefore loads,
keeps playing, and is written back as v5 on the next save. The FlatBuffers
`SavePreview` fields keep their `double` type (a schema field is never
retyped); they hold whole numbers.

## Project migration (F#)

`ProjectSchema.CurrentProjectSchemaVersion` goes from 8 to 9. The v8→v9
project migration applies the same quantization to the values a project
carries into play (player and NPC positions, energy, the current time of
day, animal positions and mood), so the editor shows what the game will run.
Content definitions (prices, chances) keep what the creator typed; the Rust
loader quantizes them, and Problems reports a warning (`numbers.offGrid`)
where that changes a value in a way a player could notice (a fractional
price or count).

## Goldens

The TypeScript goldens (`fixtures/golden/replays`, `saves`, `hash.json`,
`rng.json`) pinned the compatibility phase. From v9, Rust is the reference:

- the v8 goldens move to `fixtures/golden/v8/` and stay as **migration
  inputs** (every v8 save and project must still load);
- replays, save and hash goldens are re-recorded from Rust
  (`cargo test -p farm-sim -- --ignored record_goldens`, reviewed as a diff);
- before re-recording, a comparison test ran every v8 replay on the v8 and
  the v9 engine and checked that the outcomes a player can see (money,
  inventory, day and season, quests, flags, crop stages, tile contents)
  agree. Its notes are in the CHANGELOG entry for v9.

## What goes away

- `farm_sim::js` (`round`, `trunc`, `modulo`, `to_int32`, number formatting
  for the hash). What remains of it moves to `farm_sim::units` (reading and
  writing authoring numbers) and `farm_sim::text` (UTF-16 key order for
  stable JSON, which the JSON boundary still uses).
- `stable_json` as the hash input (it stays for debug output and for the
  golden fixtures' readable form).
