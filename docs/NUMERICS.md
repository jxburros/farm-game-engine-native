# Native numerics (schema v9)

**Status:** implemented (schema v9, save version 5; phase 7's last step,
[LANGUAGES.md](LANGUAGES.md#phases)).
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
   `#[allow]` only in `farm_sim::units` (the conversions below). The
   `f64::round` ban of the compatibility phase is gone with it (`clippy.toml`).
2. **JSON stays in authoring units.** Project JSON, cartridge content JSON,
   state JSON and saves keep the numbers creators and the web version know
   (tiles, energy points, minutes, probabilities in 0–1). Serde adapters in
   `farm_sim::units` convert at the boundary: reading rounds the number to the
   unit's grid (ties away from zero, like `f64::round`) and saturates into
   the integer type's range (a negative count reads as 0), writing prints the
   exact value of the integer in authoring units (whole values as JSON
   integers, others as the shortest decimal that reads back to the same
   double). A value already on the grid round-trips unchanged; one that isn't
   is moved to the nearest grid point once, when it is read. Each unit is a
   module (`units::energy`, `units::position`, …) with `opt`, `nullable`,
   `vec` and `nested_map` variants for `#[serde(with)]`.
3. **Conversions happen once**, at load. A `f64` read from JSON times a power
   of two or ten, rounded to an integer, is deterministic on every IEEE-754
   machine; nothing after that is.

## Units

| Quantity | Type | Unit | JSON (authoring) |
|---|---|---|---|
| Money, prices, costs, rewards, collapse penalty, animal purchase cost | `i64` | whole gold | gold |
| Price multipliers (`sellPriceMultiplier`), repair cost per durability point (`repairCostPerPoint`), collapse energy fraction | `u32` | 1/1000 | factor (0.5) |
| Event outcome `amount` (money, friendship; applied as whole gold/points, rounded) | `i64` | 1/1000 | number |
| Counts: quantities, stacks, yields, harvest counts, inventory size, floors, stages, weights of drops/fish/rocks/weather | `u32` (`i64` where a difference can be negative) | whole | count |
| Energy, max energy, energy costs | `i32` | 1/1000 point | points (2.5) |
| Absolute day, day of season, year, days grown, days without water, age and interval days | `u32` | whole day | days |
| Time of day, `timeOfDay` condition bounds | `u32` | 1/1000000 minute ("micro-minutes"; v8 already quantized to this) | minutes (390.5) |
| Absolute in-game time (a machine's `completesAtMinute`) | `i64` | micro-minute | minutes |
| Legacy wall-clock fields (`growthTime`, `regrowthTime`, `plantedAt`, ms) | `i64` | whole | ms |
| Minutes a machine processes, schedule minutes | `u32` | whole minute | minutes |
| Clock ticks | `u64` | tick (20 per second) | ticks |
| Time rate (`minutesPerRealSecond`) | `u32` | micro-minutes per tick, at load (`rate × 10⁶ / 20`) | minutes per second |
| Position of the player, NPCs, animals | `i32` | 1/8192 tile (1/256 pixel at the renderer's 32-pixel tiles) | tiles (5.5) |
| Tile coordinates (tiles, scene size, warps, mine entrance `entranceX/Y`, path points) | `i32` | whole tile | tiles |
| Move intent (`dx`, `dy`) | `i32` | whole, truncated toward zero on read (as v8's `Math.trunc`) | −1..1 |
| Command list index (`chooseDialogueOption.index`) | `i32` | whole; a fractional index (v8 rejected it) reads as −1 and picks nothing | index |
| Movement speed | `i32` | 1/8192 tile per tick, at load (`speed × 8192 / 20`) | tiles per second |
| Probabilities and chances (mutation, ladder, junk, weather, crop damage, drop chance), fish difficulty, minigame scores and tier `minScore` | `u64` | threshold out of 2³² (`p × 2³²`, clamped to 0..=2³²) | 0–1 |
| Friendship, mood | `i32` | whole points | points |
| Skill XP, skill levels, curve thresholds | `u32` | whole | whole |
| Tool power, tier, durability; node health | `i32` | whole | whole |
| Density of mine rocks | `u32` | 1/1000 | fraction |

Screen-facing fields (`pixelX`, sprite frame sizes, asset sizes, panel
layout) are not simulation state; they stay `f64` in the schema
(`units::screen`) and the canonical encoding writes only a unit value for
them, so they are not hashed. Two project fields stay exact doubles
(`units::exact`): `gameStartTime` and `currentTime`, editor timestamps whose
text is part of the engine seed (`"<id>:<gameStartTime>"`), so rounding them
would change every seeded game.

Anything a creator can type that isn't on the grid is rounded when read: a
price of 12.5 gold becomes 13 (ties away from zero), an energy cost of
0.0004 becomes 0. F# Problems warns about such values (see "Project
migration" below), so they don't surprise anyone.

## Randomness

The RNG is unchanged (xoshiro128\*\* with the v8 seeding). What changes is how
its output is used:

| v8 (`f64`) | v9 (integer) | Same outcome? |
|---|---|---|
| `next_float() < p` | `(next_u32() as u64) < threshold(p)` with `threshold = round(p × 2³²)` | Yes, whenever `p × 2³²` is an integer (every probability with at most 32 binary digits); otherwise it can differ by one draw in 2³². Built-in decimal chances such as 0.01 or 0.15 are not dyadic, so a draw exactly at the boundary can land on the other side (none of the golden replays does) |
| `floor(next_float() × n) + min` (`next_int`) | `((next_u32() as u64 × n) >> 32) + min` | Yes, exactly (`next_float` is `u / 2³²`) |
| weighted pick: `next_float() × total`, walk the weights | `(next_u32() as u64 × total) >> 32`, walk integer weights | Yes for integer weights |

## State hash

v9 replaces FNV-1a over stable JSON with **xxh3-64 over the canonical binary
encoding** of the state: a serde serializer in `farm_sim::hash` that writes
integers little-endian, booleans as one byte, floats (only free-form JSON
values such as flags carry them) as their IEEE-754 bits, except that a whole
float within ±(2⁵³−1) is written as the integer it equals (stable JSON writes
`1.0` as `1`, which reads back as an integer), strings and
sequences with a `u32` length prefix, options as a tag byte, struct fields
behind a presence byte (so a skipped optional field and a present value never
collide) and enum variants as their index. Map entries are **sorted by their
encoded key**, not written in stored order: a save (stable JSON) does not keep
insertion order, and the hash must survive a save and load. It serializes the
integer fields as integers (the serde adapters know whether they write JSON or
the canonical form, from `is_human_readable`). The hash text is 16 lowercase
hex digits, as before. The cartridge hash in save files (`cart_hash`) uses the
same function, so a v8-era save file sees a different cartridge hash and
reconciles its items with the content once when it loads.
`hash_text` (FNV-1a over UTF-16) stays for the FFI's `fe_hash_text`.

## Saves

`CURRENT_SAVE_VERSION` goes from 4 to 5 (saves have their own numbering). The
v4→v5 migration is the quantization in the serde adapters, applied when the
v4 state is read, plus the version bump. A v8-era save therefore loads,
keeps playing, and is written back as v5 on the next save. The FlatBuffers
`SavePreview` fields keep their `double` type (a schema field is never
retyped); they hold whole numbers. The v3→v4 step (centering tile positions)
still runs on the JSON numbers before the v5 read.

## Project migration (F#)

`ProjectSchema.CurrentProjectSchemaVersion` goes from 8 to 9. The v8→v9
project migration applies the same quantization to the values a project
carries into play (player and NPC positions, energy, the current time of
day, animal positions and mood), so the editor shows what the game will run.
Content definitions (prices, chances) keep what the creator typed; the Rust
loader quantizes them, and Problems reports a warning (`numbers.offGrid`)
where that changes a value in a way a player could notice (a fractional
price or count). "Keep changes" (the hosts' synced project) writes the running
state into the project JSON as the editor sent it
(`state::apply_state_to_project_json`), so it never replaces content values
with their quantized form.

## Goldens

The TypeScript goldens (`fixtures/golden/replays`, `saves`, `hash.json`,
`rng.json`) pinned the compatibility phase. From v9, Rust is the reference:

- the v8 goldens move to `fixtures/golden/v8/` and stay as **migration
  inputs** (every v8 save and project must still load);
- replays, content, save, RNG and hash goldens are re-recorded from Rust
  with `FARM_RECORD_GOLDENS=1` on the test that checks them
  (`golden_replays`, `golden_content`, `golden_primitives` in `farm-sim`,
  `golden_saves` in `farm-cart`; see `fixtures/golden/SOURCE.txt`) and
  reviewed as a diff; the v8 replays' inputs (project, seed, input log) are
  the recorded replays' inputs;
- the comparison is a test, `farm-sim/tests/v8_outcomes.rs`: the v8 engine
  recorded the outcomes a player can see after every step of every v8 replay
  (money, energy, inventory, skills, quests, calendar, player tile, NPCs,
  friendship, animals, mine, open modals, tile contents, effects) into
  `fixtures/golden/v8/outcomes`, and v9 must reproduce them. All replays
  agree; sub-tile player positions differ by less than 1/1000 tile (the
  default speed of 4.5 tiles per second is 1843/8192 tile per tick). Its
  notes are in the CHANGELOG entry for v9.

## What goes away

- `farm_sim::js` (`round`, `trunc`, `modulo`, `to_int32`, number formatting
  for the hash). What remains of it moves to `farm_sim::units` (reading and
  writing authoring numbers) and `farm_sim::text` (UTF-16 key order for
  stable JSON, which the JSON boundary still uses).
- `stable_json` as the hash input (it stays for debug output and for the
  golden fixtures' readable form).
