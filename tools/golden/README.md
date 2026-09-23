# Golden parity fixtures

The TypeScript engine ([`jxburros/farm-game-engine`](https://github.com/jxburros/farm-game-engine))
is the reference implementation. This folder generates JSON fixtures from it
so `tests/FarmEngine.Core.Tests` can check that the C# port produces
byte-identical `StableJson` and `Hash.HashState` output (see `docs/PORTING.md`).

## Regenerate

```bash
# clone/update the reference into tools/golden/.work/ts-ref (REF defaults to main)
REF=main tools/golden/generate.sh

# or use an existing checkout as-is (runs `npm ci` only if node_modules is missing)
tools/golden/generate.sh /path/to/farm-game-engine
```

The script copies `golden.gen.test.ts` into the checkout as
`tests/unit/golden.gen.test.ts`, so the repo's vitest config resolves the
`@farm-engine/*` and `@/` aliases. It then runs only that file with
`GOLDEN_OUT=tests/FarmEngine.Core.Tests/Golden`, deletes the copied file, and
writes the source commit to `Golden/SOURCE.txt`. The script wipes and fully
rewrites the output, and two runs produce byte-identical files. The fixtures
are generated, so never edit them by hand.

Without `GOLDEN_OUT` the generator skips every test, so a stray copy writes
nothing. To debug one scenario, set `GOLDEN_DEBUG=<scenario-name>`. The
generator then writes a per-step trace (position, clock, money, energy,
messages) to `debug-<name>.txt` next to the output directory.

## Layout

| Path | Contents |
|------|----------|
| `migrations/project-vN.json` | `{ input, result, stable, hash, exported }`. `input` is `tests/fixtures/project-vN.json`, `result` is the full `migrateProject` return value (`ok/data/fromVersion/migrated/errors`), `stable` is `stableStringify(result.data)`, and `exported` is `migrateExportedGame` on the same input. |
| `migrations/errors.json` | Failure cases (null, number, string, future schema version) for both migrators. |
| `saves/<case>.json` | `{ input, result, stable, hash }` for `migrateGameState`. The inputs are v1, v2, v3 (grid and fractional), current v4, future v5, a save with no version, and a non-object. Each save comes from a real starter-farm state after a few commands. |
| `replays/<name>.json` | Scripted play sessions (see below). `replays/index.json` lists the scenarios. |
| `content/<name>.json` | `{ name, project, contentHash, stable, stateHash }`, where `stable = stableStringify(createContentFromProject(project))` and `stateHash = hashState(createGameState(project, { seed: "content:<name>" }))`. It covers every sample game, the pack project in fr, de and all-disabled variants, and migrated fixtures. |
| `rng.json` | For each seed (string and number, including `-1`, `2^32`, `3.7` and `1e21`): `createRngState`, 20 draws each of `nextU32`, `nextFloat`, `nextInt(1,6)` and `nextInt(-5,5)`, plus `Rng.weighted` sequences. Also `hashStringToU32` vectors. |
| `hash.json` | `{ name, input, stable, hash }` for edge-case values: floats, `1e21`, `5e-324`, `-0`, NaN/Infinity, undefined fields and array holes, key ordering (digits, uppercase, `é`, `￿` vs surrogate pairs), unicode and lone surrogates, and control characters. `input` uses a tagged encoding for values JSON cannot represent: `{"$js":"undefined"}`, `{"$js":"NaN"}`, `{"$js":"Infinity"}`, `{"$js":"-Infinity"}` and `{"$js":"-0"}`. |

### Replay fixture format

```jsonc
{
  "name": "...", "description": "...",
  "seed": "..." | null,          // null → createGameState(project) with no seed option
  "autoStartQuests": true|false, // true → initial = autoStartQuests(ctx, created) (what hosts do)
  "project": { ... },            // migrateProject(raw).data — exactly what createGameState received
  "contentHash": "...",          // hashState(createContentFromProject(project))
  "createdHash": "...",          // hashState(createGameState(project, seed))
  "initialHash": "...",          // hashState(initial state)
  "initialState": { ... },
  "finalHash": "...", "stepCount": N,
  "steps": [ { "input": {"kind":"command","command":{...}} | {"kind":"tick","ticks":N},
               "hash": "<hashState after this input>", "effects": [ ... ] } ],
  "finalState": { ... },
  "finalProject": { ... }        // applyStateToProject(project, finalState)
}
```

Suggested C# check: deserialize `project`, build the content and compare
`contentHash`, then create the state and compare `createdHash` and
`initialHash`. Next, apply each step with `Engine.ApplyCommand` or
`Engine.AdvanceTick`, and compare its hash and effects. The first mismatching
step pinpoints the divergence. At the end, compare `finalProject` via
`StableJson`.

The runner in the generator only plans commands such as `move` paths and
facing changes. Only the resulting commands enter `steps`, so the C# side
replays them verbatim. Each scenario's `verify` asserts that the session
actually exercised its systems (messages, money, harvests, quests, NPC
positions), so a script that silently does nothing fails generation.

`replay-three-days` is `replay.test.ts`' THREE_DAYS log. Its final hash must
equal the TS repo's committed golden `6f884fd1bf6e2b5e`.

### Known TS behaviours captured on purpose

- Crops from content packs, such as `demo-glow-farm:glowshroom`, never
  harvest. `harvestCrop` looks up the item `crop-${crop.type}`, but the pack
  item is namespaced as `demo-glow-farm:crop-glowshroom`. See
  `content-packs-and-plugins`. The port must reproduce this until the TS
  engine fixes it.
- Harvesting a multi-tile crop only clears the tile you face. The other
  cells keep their own crop objects.
- Autostart quests only activate when the host calls `autoStartQuests` at
  game creation. Prerequisite chains do not start mid-session.
