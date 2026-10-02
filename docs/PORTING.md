# Porting guide: TypeScript engine → Rust, F# and C#

This repo is a native port of `jxburros/farm-game-engine` (the web version).
The TypeScript engine was the **reference implementation** until schema v9:
the same seed and command/tick log had to give a state whose stable JSON and
`hashState` were byte-identical to the TypeScript ones, and golden replays
generated from it (`tools/golden/`) enforced that. Since v9 the Rust engine is
the reference, both apps run it, and the goldens are recorded from Rust; the
TypeScript ones stay in `fixtures/golden/v8` as migration inputs
([NUMERICS.md](NUMERICS.md)).

The layout and naming rules below still apply when you port more of the web
version (one TypeScript module, one Rust module). The numbers follow
[NUMERICS.md](NUMERICS.md): integers in fixed units in `farm-sim`, authoring
units in JSON.

> **History.** The first port was a C# engine (`FarmEngine.Core`, `.Runtime`,
> `.Rendering`, `.Content`). It was the stepping stone to the Rust core and
> has been retired; [LANGUAGES.md](LANGUAGES.md) describes the split that
> replaced it.

## Project map

| TypeScript package / folder             | Native home                                   |
|-----------------------------------------|-----------------------------------------------|
| `packages/engine-schemas/src`           | `crates/farm-sim/src/schema` (Rust), F# records, migrations and checks in `src/FarmEngine.Authoring` (`Schema.fs`, `SchemaJson.fs`) |
| `packages/engine-core/src` (+ subdirs)  | `crates/farm-sim`                             |
| `packages/content-default/src`, `src/lib/templates.ts` | `src/FarmEngine.Authoring` (`StarterContent`, `SampleProjects`, `ProjectCatalog`) |
| `packages/engine-runtime/src`           | `crates/farm-runtime`, plugins in `crates/farm-plugins` |
| `packages/renderer-canvas2d/src`, `packages/game-shell` | `crates/farm-render`, `crates/farm-ui`, `crates/farm-player` |
| `src/` (React editor)                   | `src/FarmingRpgMaker.App` (Avalonia) over `src/FarmEngine.Authoring` (F#) |

## Files and names (Rust)

- **One TS module → one Rust module** in snake_case, in the same folder
  structure: `farming/crops.ts` → `farming/crops.rs`, `time.ts` →
  `game_time.rs`, `dialogue.ts` → `dialogue_system.rs`. Exported functions
  keep their names in snake_case (`handleMove` → `handle_move`) and their
  parameter order.
- Keep the TS doc comments — they carry the design rationale.
- `export const FOO` → `pub const FOO`.

## Types (schemas)

The schema exists twice: as Rust types in `farm-sim::schema` (serde, the
engine's own) and as F# records in `src/FarmEngine.Authoring/Schema.fs` (the
editor's data model, also compiled to JavaScript for the web editor), with
their JSON in `SchemaJson.fs`. Both must round-trip the same JSON. The rules
for the F# records:

- A zod object → an F# record in PascalCase with a `static member Default`
  (every field at its schema default). The decoder and encoder in
  `SchemaJson.fs` name each JSON key explicitly, so acronyms such as
  `selectedNPCId` need nothing special.
- `.passthrough()` → an `Extra: (string * Json) list` field: undeclared keys in
  order, written back after the declared ones. Records **without** passthrough
  have no `Extra` (zod strips unknown keys).
- `z.number()` (including `.int()`) → **`float`**, like the TypeScript. The
  engine keeps its own integer units (docs/NUMERICS.md); the project keeps what
  the creator typed.
- Optional (`.optional()`, TS `undefined`) → `option`, omitted from JSON when
  `None`.
- `.nullable()` (key present with value `null`) → `option`, and the encoder
  writes `null` for `None`. `.nullable().optional()` → `'T option option`
  (`None` absent, `Some None` null).
- `.default(x)` → the decoder's value for a missing key (and the record's
  `Default`).
- `z.enum([...])` of strings → plain `string` plus a module of `[<Literal>]`
  constants with an `All` list (`Directions.Up`, `SchemaConstants.fs`).
- `z.array(T)` → `'T list`. `z.record(z.string(), T)` → `(string * 'T) list`
  (JS objects iterate in insertion order).
- `z.unknown()`, `z.any()`, and scalar unions → `Json`.
- `z.discriminatedUnion('type', …)` → an F# union whose cases carry one
  record each (`EventCondition.Flag of FlagCondition`), with a `Type` member
  for the tag.
- A single object with a `type: z.enum(...)` field that is **not** a
  discriminated union (e.g. `EventOutcome`) stays a plain record with a
  `Type: string` field.
- `z.infer` helper functions in schema files (e.g. `eventFiredFlag`,
  `centerCoordinate`, `classicCalendarSeasons`) → functions in a module named
  after the schema file (`EventsSchema.EventFiredFlag`, …).
- After changing a record, regenerate the C# helpers:
  `tools/codegen/record-with.fsx` (the `WithField` extensions) and
  `tools/codegen/record-json.py` (codec and key tables).

## State and numbers (Rust)

- **Numbers:** every quantity in `farm-sim` is an integer in a fixed unit,
  with float arithmetic denied outside the JSON adapters. The unit of each
  quantity, the serde adapters (`farm_sim::units`), randomness and the state
  hash are in [NUMERICS.md](NUMERICS.md). Read it before you add a field.
- `z.record(z.string(), T)` → `IndexMap<String, T>` (JS objects iterate in
  insertion order). `HashMap`/`HashSet` are banned by `clippy.toml`.
- `.optional()` → `Option<T>` with `skip_serializing_if`; `.nullable()` keeps
  the key and writes `null`. Keep absent and `null` apart where TS does.
- `z.unknown()`/`z.any()` → `serde_json::Value`.
- The engine mutates its own `GameState` in place (`&mut`); content is
  immutable.

## Behaviour that must still match the web version

The JavaScript-semantics helpers (`js::round`, `js::trunc`, the `f64`
compatibility rules) went away with v9 ([NUMERICS.md](NUMERICS.md#what-goes-away)).
A few TypeScript behaviours still matter when porting game rules:

| TS | Rust |
|---|---|
| `arr.sort(cmp)` (stable) | `sort_by` (stable); `sort_unstable*` is banned (`clippy.toml`) |
| default `sort()` / `<` on strings | `text::compare_strings` (UTF-16 code units) where the order is observable |
| `x \|\| y` on numbers/strings | respect falsiness: `0`, `NaN`, `""` are falsy |
| `arr[i]` out of range → `undefined` | `get(i)` |
| `Math.random`, `Date.now` | never in the simulation: the seeded `rng` and the game clock (`Instant`/`SystemTime` are banned) |

## Tests

Port each `*.test.ts` next to the module to the Rust crate's tests
(`crates/<crate>/tests/<name>.rs`, one `#[test]` per `it(...)`, same test names
in snake_case); schema-only tests go to `tests/FarmEngine.Authoring.Tests`
(`SchemaRecordTests.fs`). Golden parity fixtures live in `fixtures/golden/`
(generated, never hand-edited) and the shared project fixtures in
`fixtures/projects/`; the Rust and F# tests both read them.
