# TypeScript golden fixtures (v8)

The TypeScript engine (`jxburros/farm-game-engine`, a private repository)
was the reference implementation until schema v9. This folder generated JSON
fixtures from it, which now live, frozen, in `fixtures/golden/v8/`: every v8
project, save and replay must still load (the Rust tests `golden_content`,
`golden_saves` and `v8_outcomes`). Since v9 the Rust engine records the
current goldens itself (`fixtures/golden/SOURCE.txt`, docs/NUMERICS.md
"Goldens"); this tool never writes them.

## Reproduce

```bash
# clone/update the reference into tools/golden/.work/ts-ref at the pinned commit (the one
# fixtures/golden/SOURCE.txt records), regenerate, and compare with fixtures/golden/v8
tools/golden/generate.sh

# or regenerate from an existing checkout as-is (runs `npm ci --ignore-scripts` only if
# node_modules is missing)
tools/golden/generate.sh /path/to/farm-game-engine
```

The script copies `golden.gen.test.ts` into the checkout as
`tests/unit/golden.gen.test.ts`, so the repo's vitest config resolves the
`@farm-engine/*` and `@/` aliases. It runs only that file with
`GOLDEN_OUT=tools/golden/.work/out` and deletes the copied file. It then
compares `replays/`, `content/`, `saves/`, `rng.json` and `hash.json` with
`fixtures/golden/v8/` and exits 1 when they differ. Two runs produce
byte-identical files.

`--write` copies the regenerated subtrees over `fixtures/golden/v8/`. It is
refused in CI, with uncommitted changes under `fixtures/`, and for any source
other than the pinned commit: moving the v8 goldens to another TypeScript
commit means changing `PINNED_REF` in the script and `SOURCE.txt` in the same
reviewed change. `v8/outcomes` and the v9 goldens are never touched. The
fixtures are generated, so never edit them by hand.

Without `GOLDEN_OUT` the generator skips every test, so a stray copy writes
nothing. To debug one scenario, set `GOLDEN_DEBUG=<scenario-name>`. The
generator then writes a per-step trace (position, clock, money, energy,
messages) to `debug-<name>.txt` next to the output directory.

The generator also writes `migrations/`. The F# migration goldens
(`fixtures/golden/migrations`, `fixtures/projects/migrated`) replaced those
files, so they stay in `.work/out`.

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

The Rust engine plays the same inputs (`crates/farm-sim/tests/golden_replays.rs`
records the v9 goldens from them, and `v8_outcomes.rs` compares what a player
sees after every step with the v8 engine).

The runner in the generator only plans commands such as `move` paths and
facing changes. Only the resulting commands enter `steps`, so the native side
replays them verbatim. Each scenario's `verify` asserts that the session
actually exercised its systems (messages, money, harvests, quests, NPC
positions), so a script that silently does nothing fails generation.

`replay-three-days` is `replay.test.ts`' THREE_DAYS log. Its v8 final hash
equals the TS repo's committed golden `6f884fd1bf6e2b5e`; the v9 recording
(`fixtures/golden/replays`) ends at `9356330e045ae312`.

### Known TS behaviours captured in v8

- Crops from content packs, such as `demo-glow-farm:glowshroom`, never
  harvest. `harvestCrop` looks up the item `crop-${crop.type}`, but the pack
  item is namespaced as `demo-glow-farm:crop-glowshroom`. See
  `content-packs-and-plugins`. The port reproduced this for the v8 goldens.
- Harvesting a multi-tile crop only clears the tile you face. The other
  cells keep their own crop objects.
- Autostart quests only activate when the host calls `autoStartQuests` at
  game creation. Prerequisite chains do not start mid-session.
