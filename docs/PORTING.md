# Porting guide: TypeScript engine → Rust, F# and C#

This repo is a native port of [`jxburros/farm-game-engine`](https://github.com/jxburros/farm-game-engine)
(the web version). The TypeScript engine is the **reference implementation**:
same seed + same command/tick log must produce a state whose stable JSON and
`hashState` output is byte-identical to the TS `stableStringify`/`hashState`.
Golden replay fixtures generated from the TS engine (`tools/golden/`) enforce
that in the Rust tests (`crates/farm-sim/tests`).

Every rule below exists to keep that guarantee. When in doubt, port the TS
literally and let the golden tests tell you.

> **History.** The first port was a C# engine (`FarmEngine.Core`, `.Runtime`,
> `.Rendering`, `.Content`). It was the stepping stone to the Rust core and
> has been retired; [LANGUAGES.md](LANGUAGES.md) describes the split that
> replaced it. The JavaScript-semantics rules it followed carry over to Rust
> until the native-numerics switch (phase 7, [NUMERICS.md](NUMERICS.md)).

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

- Numbers are **`f64`** everywhere in the compatibility phase: JS has only
  doubles. Convert to an integer only to index.
- `z.record(z.string(), T)` → `IndexMap<String, T>` (JS objects iterate in
  insertion order). `HashMap`/`HashSet` are banned by `clippy.toml`.
- `.optional()` → `Option<T>` with `skip_serializing_if`; `.nullable()` keeps
  the key and writes `null`. Keep absent and `null` apart where TS does.
- `z.unknown()`/`z.any()` → `serde_json::Value` (`js::truthy`, `js::value`).
- The engine mutates its own `GameState` in place (`&mut`), but a step must
  produce exactly the state the TS reducer would; content is immutable.

## JavaScript semantics (the silent-divergence list)

| TS                                  | Rust                                                                  |
|-------------------------------------|-----------------------------------------------------------------------|
| `Math.round(x)`                     | `js::round(x)` (`f64::round` is banned — it rounds half away from zero) |
| `Math.trunc`, `%`                   | `js::trunc`, `js::modulo`                                             |
| `arr.sort(cmp)` (stable)            | `sort_by` (stable) or `js::stable_sort`; `sort_unstable*` is banned   |
| default `sort()` / `<` on strings   | `js::compare_strings` (UTF-16 code units)                             |
| `` `${n}` `` with a number          | `js::num(n)`; `toFixed` → `js::to_fixed`                              |
| `x \|\| y` on numbers/strings       | respect falsiness: `0`, `NaN`, `""` are falsy                         |
| `arr[i]` out of range → `undefined` | `get(i)`                                                              |
| `Number.isInteger(x)`               | `js::is_integer(x)`                                                   |
| `Math.imul`, `\|0`, `>>> 0`          | `js::to_int32`, `js::to_uint32`, wrapping arithmetic                  |
| `Math.random`, `Date.now`           | never in the simulation — the seeded `rng` and `state.clock` (`Instant`/`SystemTime` are banned) |

## Tests

Port each `*.test.ts` next to the module to the Rust crate's tests
(`crates/<crate>/tests/<name>.rs`, one `#[test]` per `it(...)`, same test names
in snake_case); schema-only tests go to `tests/FarmEngine.Authoring.Tests`
(`SchemaRecordTests.fs`). Golden parity fixtures live in `fixtures/golden/`
(generated, never hand-edited) and the shared project fixtures in
`fixtures/projects/`; the Rust and F# tests both read them.
