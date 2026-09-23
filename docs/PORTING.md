# Porting guide: TypeScript engine → C#

This repo is a native port of [`jxburros/farm-game-engine`](https://github.com/jxburros/farm-game-engine)
(the web version). The TypeScript engine is the **reference implementation**:
same seed + same command/tick log must produce a state whose
`StableJson`/`Hash.HashState` output is byte-identical to the TS
`stableStringify`/`hashState`. Golden replay fixtures generated from the TS
engine (`tools/golden/`) enforce that in `tests/FarmEngine.Core.Tests`.

Every rule below exists to keep that guarantee. When in doubt, port the TS
literally and let the golden tests tell you.

## Project map

| TypeScript package / folder             | C# project                  | Namespace              |
|-----------------------------------------|-----------------------------|------------------------|
| `packages/engine-schemas/src`           | `src/FarmEngine.Schemas`    | `FarmEngine.Schemas`   |
| (JS semantics helpers — new)            | `src/FarmEngine.Schemas/Json` | `FarmEngine.Json`    |
| `packages/engine-core/src` (+ subdirs)  | `src/FarmEngine.Core`       | `FarmEngine.Core`      |
| `packages/content-default/src`          | `src/FarmEngine.Content`    | `FarmEngine.Content`   |
| `packages/engine-runtime/src`           | `src/FarmEngine.Runtime`    | `FarmEngine.Runtime`   |
| `packages/renderer-canvas2d/src`        | `src/FarmEngine.Rendering`  | `FarmEngine.Rendering` |
| `src/` (React editor + play UI)         | `src/FarmingRpgMaker.App`   | `FarmingRpgMaker.App`  |

All of `FarmEngine.Core` uses the single namespace `FarmEngine.Core` (no
sub-namespaces), and all of `FarmEngine.Schemas` uses `FarmEngine.Schemas`.

## Files and names

- **One TS module → one C# file with one `public static class`** named after
  the module in PascalCase: `farming/crops.ts` → `Farming/Crops.cs`,
  `class Crops`. Where two modules share a basename, prefix the folder:
  `world/movement.ts` → `World/WorldMovement.cs` (`WorldMovement`),
  `npcs/movement.ts` → `Npcs/NpcMovement.cs` (`NpcMovement`),
  `farming/actions.ts` → `Farming/FarmingActions.cs` (`FarmingActions`),
  `quests/quests.ts` → `Quests/Quests.cs` (`Quests`),
  `world/tiles.ts` → `World/Tiles.cs` (`Tiles`),
  `world/pathfinding.ts` → `World/Pathfinding.cs` (`Pathfinding`),
  `events.ts` → `Events.cs` (`GameEvents` — `Events` is too generic),
  `extensibility.ts` → `Extensibility.cs` (`Extensibility`),
  `state.ts` → `State.cs` (`EngineState`), `engine.ts` → `Engine.cs` (`Engine`),
  `hash.ts` → `Hash.cs` (`Hash`), `rng.ts` → `Rng.cs` (`RngMath` static
  functions + `class Rng`), `replay.ts` → `Replay.cs` (`Replay`),
  `hooks.ts` → `Hooks.cs` (`HookBus` class + payload records),
  `commands.ts` → `Commands.cs` (`Command` union), `effects.ts` → `Effects.cs`
  (`Effect` union).
  Everything else: `time.ts` → `Time` → **`GameTime`** (avoid clashing with
  `System.TimeProvider` naming confusion), `energy.ts` → `Energy`,
  `economy.ts` → `Economy`, `gathering.ts` → `Gathering`,
  `inventory.ts` → `Inventory`, `tools.ts` → `Tools`, `dialogue.ts` → `DialogueSystem`,
  `content-builtin.ts` → `ContentBuiltin`, `packs.ts` → `Packs`,
  `validation.ts` → `Validation`, `skills.ts` → `Skills`,
  `weather.ts` → `Weather`, `crafting.ts` → `Crafting`, `social.ts` → `Social`,
  `fishing.ts` → `Fishing`, `animals.ts` → `Animals`, `mines.ts` → `Mines`.
- **Exported functions → `public static` methods**, PascalCase of the TS name
  (`handleMove` → `HandleMove`, `createGameState` → `CreateGameState`).
  Non-exported helpers → `private`/`internal static`. Because every module
  follows this rule, you can call a function from a module someone else is
  porting before it exists: `WorldMovement.HandleMove(ctx, state, dir)`.
- Parameter order and meaning stay exactly as in TS. Optional TS parameters →
  C# optional parameters with the same default.
- `export const FOO = …` → `public const` / `public static readonly` named
  `Foo` in PascalCase (e.g. `TICKS_PER_SECOND` → `Engine.TicksPerSecond`).
- Keep the TS doc comments (as `///` summaries or `//` comments) — they carry
  the design rationale.

## Types (schemas)

- A zod object → `public sealed record Xxx` with `{ get; init; }` properties
  in PascalCase. JSON names are camelCase via `JsonDefaults.Options`; add
  `[JsonPropertyName("…")]` whenever the camelCase conversion would not
  reproduce the TS key exactly (acronyms such as `selectedNPCId`, or when the
  C# name must differ because it collides with the type name).
- `.passthrough()` → add
  `[JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }`.
  Objects **without** passthrough have no `Extra` (zod strips unknown keys).
- `z.number()` (including `.int()`) → **`double`**. Always. JS has only
  doubles; C# `int` math (integer division, overflow) silently diverges.
  Convert with `(int)` only at the point of indexing a list.
- Optional (`.optional()`, TS `undefined`) → nullable (`double?`, `string?`,
  `Foo?`), default `null`, omitted from JSON when null.
- `.nullable()` (key present with value `null`) → nullable **plus**
  `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]` so `null` is written.
- `.default(x)` → property initializer `= x` (applied when the key is
  missing on deserialize, like zod). Required non-defaulted strings/lists get
  `= ""` / `= []` initializers so records are always constructible.
- `z.enum([...])` of strings → plain `string` plus a static class of
  constants (`public static class Directions { public const string Up = "up"; … }`).
  Keep them strings — content is open-ended and JSON must round-trip.
- `z.array(T)` → `List<T>`. `z.tuple` → `List<T>` / array.
- `z.record(z.string(), T)` → `OrderedDictionary<string, T>` (JS objects
  iterate in insertion order; `Dictionary` does not guarantee it).
- `z.unknown()`, `z.any()`, and scalar unions (`boolean | number | string`)
  → `JsonElement` (use `Js.Value(...)` to create, `Js.Truthy` to test).
- `z.discriminatedUnion('type', …)` → abstract record with
  `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]` and one
  `[JsonDerivedType(typeof(Sub), "literal")]` per case. Expose the tag as
  `[JsonIgnore] public abstract string Type { get; }` in the base and
  `[JsonIgnore] public override string Type => "literal";` in each subtype
  (the `[JsonIgnore]` on the override is required).
- A single object with a `type: z.enum(...)` field that is **not** a
  discriminated union (e.g. `EventOutcome`) stays a plain record with a
  `string Type` property.
- `z.infer` helper functions in schema files (e.g. `eventFiredFlag`,
  `centerCoordinate`, `classicCalendarSeasons`) → static methods on a static
  class named after the schema file (`EventsSchema.EventFiredFlag`, …).

## State updates

The TS engine is a pure reducer built from object spreads. Port it the same way:

- **Never mutate** an object, list or dictionary reachable from a
  `GameState`, `GameProject` or `GameContent`. Use `with` expressions
  (`state with { Player = state.Player with { Money = m } }`) — they are
  exactly JS spreads — and build new collections
  (`[.. list, item]`, `list.Select(...).ToList()`,
  `new OrderedDictionary<string, T>(dict) { [key] = value }`).
- A local list you just created may be mutated before it is stored.
- `JSON.parse(JSON.stringify(x))` → `JsonDefaults.DeepClone(x)`.
- Reducers return `EngineStep(State, Effects)`; ad-hoc `{ a, b }` return
  objects become small `sealed record`s or named tuples.

## JavaScript semantics (the silent-divergence list)

| TS                                  | C#                                                                   |
|-------------------------------------|----------------------------------------------------------------------|
| `Math.round(x)`                     | `Js.Round(x)` (`Math.Round` is banned in Core — it rounds to even)   |
| `Math.trunc`, `Math.floor`, `Math.ceil`, `Math.min/max/abs` | `Js.Trunc`, `Math.Floor`, `Math.Ceiling`, `Math.Min/Max/Abs` on doubles |
| `arr.sort(cmp)` (stable)            | `Js.StableSort(arr, cmp)` or LINQ `OrderBy` (stable). `List.Sort` is banned. |
| default `sort()` / `<` on strings   | `string.CompareOrdinal` / `Js.CompareStrings`                         |
| `a.localeCompare(b)`                | `Js.LocaleCompare(a, b)`                                              |
| `` `${n}` `` with a number          | `Js.Num(n)` inside the interpolation                                  |
| `x \|\| y` on numbers/strings       | respect falsiness: `0`, `NaN`, `""` are falsy (`x != 0 ? x : y`)      |
| `x ?? y`                            | `x ?? y` (null only)                                                  |
| `arr[i]` out of range → `undefined` | bounds-check (`i >= 0 && i < list.Count ? list[i] : null`)            |
| `Object.keys/entries(obj)`          | iterate the `OrderedDictionary` (insertion order)                     |
| `Number.isInteger(x)`               | `Js.IsInteger(x)`                                                     |
| `Math.imul`, `>>> 0`                | `unchecked` `uint` arithmetic                                         |
| `console.error/warn`                | drop, or `System.Diagnostics.Debug.WriteLine`                         |
| `Math.random`, `Date.now`           | never in Core (analyzer-banned) — seeded `Rng` / `state.Clock`        |

## Tests

Port each `*.test.ts` next to the module to
`tests/FarmEngine.Core.Tests/<Area>/<Name>Tests.cs` (xUnit, `[Fact]` per
`it(...)`, same test names in PascalCase). Golden parity fixtures live in
`tests/FarmEngine.Core.Tests/Golden/` and are generated, never hand-edited.
