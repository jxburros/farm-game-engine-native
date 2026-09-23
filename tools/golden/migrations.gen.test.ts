/**
 * Generator for tests/FarmEngine.Core.Tests/Fixtures/migrated/ — the TS
 * reference outputs the C# migration port (Migrations.cs, SaveMigrations.cs)
 * must reproduce byte-for-byte via StableJson.
 *
 * Usage (from a farm-game-engine checkout with node_modules):
 *   cp <native>/tools/golden/migrations.gen.test.ts tests/unit/
 *   MIGRATIONS_OUT=<native>/tests/FarmEngine.Core.Tests/Fixtures/migrated \
 *     npx vitest run tests/unit/migrations.gen.test.ts
 *   rm tests/unit/migrations.gen.test.ts
 *
 * project-edge-v1.input.json is hand-authored and read from MIGRATIONS_OUT;
 * every other file there is (re)written. Without MIGRATIONS_OUT nothing runs.
 */
import { it } from 'vitest'
import { readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { migrateProject, migrateExportedGame, migrateGameState } from '@farm-engine/schemas'
import { createGameState, stableStringify } from '@farm-engine/core'
import { createInitialProject } from '@/lib/game-helpers'

const out = process.env.MIGRATIONS_OUT
const fixturesDir = path.resolve(import.meta.dirname, '../fixtures')
const load = (name: string) => JSON.parse(readFileSync(path.join(fixturesDir, name), 'utf8'))
const meta = (r: any) => ({ ok: r.ok, fromVersion: r.fromVersion, migrated: r.migrated, errors: r.errors })

it.skipIf(!out)('generates migration parity fixtures', () => {
  const dir = out!
  const write = (name: string, text: string) => writeFileSync(path.join(dir, name), text)
  const summary: Record<string, unknown> = {}

  for (let v = 1; v <= 8; v++) {
    const r = migrateProject(load(`project-v${v}.json`))
    write(`project-v${v}.stable.json`, stableStringify(r.data))
    summary[`project-v${v}`] = meta(r)
  }

  // Exported games: the legacy case from migrations.test.ts + project fixtures.
  const legacy = {
    version: '1.0', name: 'Shared Game', scenes: load('project-v1.json').scenes,
    startSceneId: 'scene-farm', currentSeason: 'summer', currentDay: 3, gameStartTime: 0,
  }
  write('exported-legacy.input.json', JSON.stringify(legacy, null, 2))
  let r: any = migrateExportedGame(legacy)
  write('exported-legacy.stable.json', stableStringify(r.data))
  summary['exported-legacy'] = meta(r)
  for (const v of [3, 5, 7, 8]) {
    r = migrateExportedGame(load(`project-v${v}.json`))
    write(`exported-from-project-v${v}.stable.json`, stableStringify(r.data))
    summary[`exported-from-project-v${v}`] = meta(r)
  }

  // Saves: the raw states save-migrations.test.ts builds.
  const mk = (seed: string) => JSON.parse(JSON.stringify(createGameState(createInitialProject(), { seed }))) as Record<string, any>
  const v1 = mk('save-migration')
  v1.meta.saveVersion = 1
  delete v1.meta.packs
  for (const k of ['timeMinutes', 'day', 'season', 'year', 'weatherId']) delete v1.clock[k]
  delete v1.player.energy; delete v1.player.maxEnergy; delete v1.player.skills
  for (const k of ['shopPurchasesToday', 'social', 'animals', 'mine', 'quarantinedItems']) delete v1[k]
  const v3 = mk('v3-save'); v3.meta.saveVersion = 3; v3.player.x = 3; v3.player.y = 4; delete v3.player.moveIntent
  const v3f = mk('v3-frac'); v3f.meta.saveVersion = 3; v3f.player.x = 2.25; v3f.player.y = 6.75
  const cur = mk('current-save')
  for (const [name, raw] of Object.entries({ 'save-v1': v1, 'save-v3': v3, 'save-v3-frac': v3f, 'save-current': cur })) {
    write(`${name}.input.json`, JSON.stringify(raw, null, 2))
    const res = migrateGameState(JSON.parse(JSON.stringify(raw)))
    write(`${name}.stable.json`, stableStringify(res.data))
    summary[name] = meta(res)
  }

  // Error cases (for reference; the C# tests assert the same texts).
  const broken = load('project-v3.json'); broken.player.money = 'lots'
  summary['broken-money'] = meta(migrateProject(broken))
  summary['exported-name-42'] = meta(migrateExportedGame({ name: 42 }))

  // Hand-authored edge-case input.
  const edge = () => JSON.parse(readFileSync(path.join(dir, 'project-edge-v1.input.json'), 'utf8'))
  r = migrateProject(edge())
  write('project-edge-v1.stable.json', stableStringify(r.data))
  summary['project-edge-v1'] = meta(r)
  r = migrateExportedGame(edge())
  write('exported-edge-v1.stable.json', stableStringify(r.data))
  summary['exported-edge-v1'] = meta(r)

  write('summary.json', JSON.stringify(summary, null, 2))
})
