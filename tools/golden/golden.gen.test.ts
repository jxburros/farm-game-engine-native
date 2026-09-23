/**
 * Golden-fixture generator for the C# port (farm-game-engine-native).
 *
 * This file is NOT part of the TypeScript repo. tools/golden/generate.sh
 * copies it into a farm-game-engine checkout as tests/unit/golden.gen.test.ts
 * (so the vitest config resolves the @farm-engine/* and @/ aliases), runs it
 * with GOLDEN_OUT=<dir>, and removes it again. Without GOLDEN_OUT every test
 * here is skipped, so an accidental copy never writes anything.
 *
 * Output layout (all JSON, UTF-8):
 *   migrations/project-vN.json   migrateProject over tests/fixtures/project-vN.json
 *   migrations/errors.json       migrateProject/migrateExportedGame failure cases
 *   saves/<case>.json            migrateGameState over real states (v1..v5)
 *   replays/<name>.json          scripted play sessions, hash after every input
 *   content/<name>.json          createContentFromProject (stable JSON + hash)
 *   rng.json, hash.json          low-level RNG / stable-hash vectors
 *
 * Every replay scenario asserts that it actually exercised what it claims
 * (messages, money, harvests, quests …) so a silently inert script fails.
 */
import { describe, expect, it } from 'vitest'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import type { Direction, GameProject, GameState, InventorySlot, Item, Scene } from '@farm-engine/schemas'
import {
  CURRENT_PROJECT_SCHEMA_VERSION,
  DEFAULT_PROJECT_SETTINGS,
  MineConfigSchema,
  WeatherConfigSchema,
  defaultWeatherConfig,
  migrateExportedGame,
  migrateGameState,
  migrateProject,
  validateContentPack,
} from '@farm-engine/schemas'
import {
  Rng,
  advanceTick,
  applyCommand,
  applyStateToProject,
  autoStartQuests,
  canMoveTo,
  createContentFromProject,
  createDefaultAnimalSpecies,
  createDefaultFishTables,
  createDefaultItems,
  createDefaultMineBands,
  createDefaultShop,
  createEmptyScene,
  createGameState,
  createRngState,
  hashState,
  hashStringToU32,
  nextFloat,
  nextInt,
  nextU32,
  runReplay,
  setTileLayer,
  stableStringify,
} from '@farm-engine/core'
import type { Command, Effect, EngineContext, ReplayInput } from '@farm-engine/core'
import { createInitialProject } from '@/lib/game-helpers'
import { createBlankProject } from '@/lib/projects'
import { createCozyFarmProject, createQuestRpgProject } from '@/lib/templates'

// ─── Output plumbing ──────────────────────────────────────────────────────

const OUT = process.env.GOLDEN_OUT ?? ''

/** Find the checkout root (the directory holding tests/fixtures/project-v1.json). */
function repoRoot(): string {
  let dir = import.meta.dirname
  for (let i = 0; i < 6; i++) {
    if (existsSync(path.join(dir, 'tests', 'fixtures', 'project-v1.json'))) return dir
    dir = path.dirname(dir)
  }
  return process.cwd()
}
const ROOT = repoRoot()

function writeRaw(relPath: string, text: string) {
  const file = path.join(OUT, relPath)
  mkdirSync(path.dirname(file), { recursive: true })
  writeFileSync(file, text.endsWith('\n') ? text : `${text}\n`, 'utf8')
}

/** Pretty JSON for small files. */
function writeJson(relPath: string, value: unknown) {
  writeRaw(relPath, JSON.stringify(value, null, 2))
}

/**
 * Replay files: one top-level key per line and one step per line — compact
 * enough to stay small, line-oriented enough for readable diffs.
 */
function writeFixture(relPath: string, value: Record<string, unknown>) {
  const lines = Object.entries(value)
    .filter(([, v]) => v !== undefined)
    .map(([key, v]) => {
      if (Array.isArray(v) && key === 'steps') {
        return v.length === 0
          ? `  ${JSON.stringify(key)}: []`
          : `  ${JSON.stringify(key)}: [\n${v.map(step => `    ${JSON.stringify(step)}`).join(',\n')}\n  ]`
      }
      return `  ${JSON.stringify(key)}: ${JSON.stringify(v)}`
    })
  writeRaw(relPath, `{\n${lines.join(',\n')}\n}`)
}

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T

// ─── Project building helpers ─────────────────────────────────────────────

const FIXED_TIME = 1_700_000_000_000
const DEFAULT_ITEMS = createDefaultItems()

function itemDef(id: string, catalog: Item[] = DEFAULT_ITEMS): Item {
  const found = catalog.find(item => item.id === id)
  if (!found) throw new Error(`unknown item ${id}`)
  return clone(found)
}
const slot = (id: string, quantity: number, catalog?: Item[]): InventorySlot => ({ item: itemDef(id, catalog), quantity })

const SUN_ONLY = WeatherConfigSchema.parse({
  types: [{ id: 'sun', name: 'Sunny' }],
  table: {
    spring: [{ weatherId: 'sun', weight: 1 }],
    summer: [{ weatherId: 'sun', weight: 1 }],
    fall: [{ weatherId: 'sun', weight: 1 }],
    winter: [{ weatherId: 'sun', weight: 1 }],
  },
})

function paint(scene: Scene, x: number, y: number, type: Parameters<typeof setTileLayer>[1]) {
  scene.tiles[y][x] = setTileLayer(scene.tiles[y][x], type)
}
function placeNode(scene: Scene, x: number, y: number, typeId: string, remainingHealth: number) {
  scene.tiles[y][x] = { ...scene.tiles[y][x], node: { typeId, remainingHealth } }
}
function wallBorder(scene: Scene) {
  for (let y = 0; y < scene.height; y++) {
    for (let x = 0; x < scene.width; x++) {
      if (x === 0 || y === 0 || x === scene.width - 1 || y === scene.height - 1) paint(scene, x, y, 'wall')
    }
  }
}

interface PlayerInit {
  x: number
  y: number
  direction?: Direction
  inventory?: InventorySlot[]
  money?: number
  maxInventorySize?: number
  energy?: number
  maxEnergy?: number
  activeQuests?: string[]
}

/** A lab project in the shape of engine.test.ts' makeProject (current schema). */
function labProject(id: string, scenes: Scene[], player: PlayerInit, extra: Partial<GameProject> = {}): GameProject {
  return {
    schemaVersion: CURRENT_PROJECT_SCHEMA_VERSION,
    id,
    name: id,
    version: '2.0',
    scenes,
    npcs: [],
    items: createDefaultItems(),
    events: [],
    dialogues: [],
    quests: [],
    player: {
      direction: 'up',
      sceneId: scenes[0].id,
      inventory: [],
      maxInventorySize: 20,
      money: 100,
      activeQuests: [],
      completedQuests: [],
      pixelX: 0,
      pixelY: 0,
      targetX: 0,
      targetY: 0,
      ...player,
    },
    eventFlags: {},
    startSceneId: scenes[0].id,
    mode: 'play',
    selectedTileType: 'grass',
    selectedNPCId: null,
    selectedItemId: null,
    currentTime: FIXED_TIME,
    customAssets: [],
    currentSeason: 'spring',
    currentDay: 1,
    currentTimeMinutes: 360,
    currentYear: 1,
    shops: [],
    nodeTypes: [],
    settings: DEFAULT_PROJECT_SETTINGS,
    recipes: [],
    actions: [],
    minigames: [],
    machineTypes: [],
    weather: SUN_ONLY,
    animalSpecies: [],
    animals: [],
    fishTables: [],
    mine: MineConfigSchema.parse({ enabled: false }),
    contentPacks: [],
    gameStartTime: FIXED_TIME,
    ...extra,
  } as GameProject
}

/** Sample games with wall-clock fields pinned so output is reproducible. */
function pinTimes<T extends GameProject>(project: T): T {
  return { ...project, currentTime: FIXED_TIME, gameStartTime: FIXED_TIME }
}
const starterProject = () => pinTimes(createInitialProject())

/** replay.test.ts' makeFarmProject, verbatim (its golden hash is our anchor). */
function makeFarmProject(): GameProject {
  const scene = createEmptyScene('farm', 'Farm', 10, 10)
  for (let x = 2; x <= 7; x++) scene.tiles[4][x] = setTileLayer(scene.tiles[4][x], 'soil')
  scene.tiles[2][2] = { ...scene.tiles[2][2], node: { typeId: 'node-tree', remainingHealth: 4 } }
  const items = createDefaultItems()
  return {
    schemaVersion: 4,
    id: 'replay-farm',
    name: 'Replay Farm',
    version: '2.0',
    scenes: [scene],
    npcs: [],
    items,
    events: [],
    dialogues: [],
    quests: [],
    player: {
      x: 4, y: 6, direction: 'up', sceneId: 'farm',
      inventory: [
        { item: items.find(i => i.id === 'seed-wheat')!, quantity: 20 },
        { item: items.find(i => i.id === 'tool-hoe')!, quantity: 1 },
        { item: items.find(i => i.id === 'tool-watering-can')!, quantity: 1 },
        { item: items.find(i => i.id === 'tool-axe')!, quantity: 1 },
        { item: items.find(i => i.id === 'fertilizer-basic')!, quantity: 5 },
      ],
      maxInventorySize: 20,
      money: 100,
      activeQuests: [], completedQuests: [],
      pixelX: 0, pixelY: 0, targetX: 0, targetY: 0,
    },
    eventFlags: {},
    startSceneId: 'farm',
    mode: 'play',
    selectedTileType: 'grass',
    selectedNPCId: null,
    selectedItemId: null,
    currentTime: 500_000,
    customAssets: [],
    currentSeason: 'spring',
    currentDay: 1,
    currentTimeMinutes: 6 * 60,
    currentYear: 1,
    shops: [createDefaultShop()],
    nodeTypes: [],
    settings: DEFAULT_PROJECT_SETTINGS,
    recipes: [],
    actions: [],
    minigames: [],
    machineTypes: [],
    weather: WeatherConfigSchema.parse(defaultWeatherConfig()),
    animalSpecies: [],
    animals: [],
    fishTables: [],
    mine: MineConfigSchema.parse({ enabled: false }),
    contentPacks: [],
    gameStartTime: 500_000,
  } as GameProject
}

// ─── Scripted runner ──────────────────────────────────────────────────────

interface Step {
  input: ReplayInput
  hash: string
  effects: Effect[]
}

const DIRS: Record<Direction, { dx: number; dy: number }> = {
  up: { dx: 0, dy: -1 },
  down: { dx: 0, dy: 1 },
  left: { dx: -1, dy: 0 },
  right: { dx: 1, dy: 0 },
}
const DIR_ORDER: Direction[] = ['up', 'down', 'left', 'right']

/**
 * Applies inputs, records (input, hash, effects) per step. Movement helpers
 * compute discrete `move`/`setMoveIntent` commands from the live state —
 * only the resulting commands enter the log, so the C# side replays them
 * verbatim without re-running any of this planning code.
 */
class Runner {
  steps: Step[] = []
  /** Free-form observations recorded by scripts for their verify step. */
  notes: Record<string, any> = {}
  constructor(public ctx: EngineContext, public state: GameState) {}

  get inputs(): ReplayInput[] {
    return this.steps.map(step => step.input)
  }

  apply(input: ReplayInput): Effect[] {
    const result = input.kind === 'tick'
      ? advanceTick(this.ctx, this.state, input.ticks)
      : applyCommand(this.ctx, this.state, input.command)
    this.state = result.state
    this.steps.push({ input: clone(input), hash: hashState(result.state), effects: result.effects })
    return result.effects
  }

  cmd(command: Command): Effect[] {
    return this.apply({ kind: 'command', command })
  }

  tick(ticks: number): Effect[] {
    return this.apply({ kind: 'tick', ticks })
  }

  get player() { return this.state.player }
  get tile() { return { x: Math.floor(this.state.player.x), y: Math.floor(this.state.player.y) } }
  get scene(): Scene { return this.state.world.scenes.find(s => s.id === this.state.player.sceneId)! }

  tileAt(x: number, y: number, sceneId = this.state.player.sceneId) {
    return this.state.world.scenes.find(s => s.id === sceneId)?.tiles[y]?.[x]
  }

  qty(itemId: string): number {
    return this.state.player.inventory.filter(s => s.item.id === itemId).reduce((sum, s) => sum + s.quantity, 0)
  }

  /** Face a direction without moving (intent set + cleared in the same tick). */
  face(dir: Direction) {
    if (this.state.player.direction === dir) return
    const v = DIRS[dir]
    this.cmd({ type: 'setMoveIntent', dx: v.dx, dy: v.dy })
    this.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
  }

  private walkable(x: number, y: number): boolean {
    const nodeTypes = Object.fromEntries(this.ctx.content.nodeTypes.map(def => [def.id, def]))
    const machineTypes = Object.fromEntries(this.ctx.content.machineTypes.map(def => [def.id, def]))
    return canMoveTo(this.scene, x, y, this.state.npcs, undefined, nodeTypes, machineTypes)
  }

  /** BFS distances + parent directions from the player's tile. */
  private bfs(): Map<string, { dist: number; prev: string | null; dir: Direction | null }> {
    const start = this.tile
    const key = (x: number, y: number) => `${x},${y}`
    const seen = new Map<string, { dist: number; prev: string | null; dir: Direction | null }>()
    seen.set(key(start.x, start.y), { dist: 0, prev: null, dir: null })
    const queue = [start]
    while (queue.length) {
      const cur = queue.shift()!
      const curKey = key(cur.x, cur.y)
      for (const dir of DIR_ORDER) {
        const nx = cur.x + DIRS[dir].dx
        const ny = cur.y + DIRS[dir].dy
        const k = key(nx, ny)
        if (seen.has(k) || !this.walkable(nx, ny)) continue
        seen.set(k, { dist: seen.get(curKey)!.dist + 1, prev: curKey, dir })
        queue.push({ x: nx, y: ny })
      }
    }
    return seen
  }

  canReach(x: number, y: number): boolean {
    return this.bfs().has(`${x},${y}`)
  }

  /**
   * Walk to a tile with discrete `move` commands. Stops early (returns
   * false) when a move changes scene (transition/warp/ladder).
   */
  goTo(x: number, y: number): boolean {
    const sceneId = this.state.player.sceneId
    const here = this.tile
    if (here.x === x && here.y === y) return true
    const map = this.bfs()
    if (!map.has(`${x},${y}`)) throw new Error(`goTo(${x},${y}) unreachable from (${here.x},${here.y}) in ${sceneId}`)
    const dirs: Direction[] = []
    let cursor: string | null = `${x},${y}`
    while (cursor) {
      const entry = map.get(cursor)!
      if (entry.dir) dirs.unshift(entry.dir)
      cursor = entry.prev
    }
    for (const dir of dirs) {
      const before = this.tile
      this.cmd({ type: 'move', dir })
      if (this.state.player.sceneId !== sceneId) return false
      const after = this.tile
      if (after.x !== before.x + DIRS[dir].dx || after.y !== before.y + DIRS[dir].dy) {
        throw new Error(`move ${dir} from (${before.x},${before.y}) did not arrive (now ${after.x},${after.y})`)
      }
    }
    return true
  }

  /** Walk to the nearest reachable neighbor of (x, y) and face it. */
  approach(x: number, y: number): boolean {
    const map = this.bfs()
    let best: { x: number; y: number; face: Direction; dist: number } | null = null
    for (const [ox, oy, face] of [[0, 1, 'up'], [0, -1, 'down'], [-1, 0, 'right'], [1, 0, 'left']] as const) {
      const entry = map.get(`${x + ox},${y + oy}`)
      if (entry && (!best || entry.dist < best.dist)) best = { x: x + ox, y: y + oy, face, dist: entry.dist }
    }
    if (!best) return false
    if (!this.goTo(best.x, best.y)) return false
    this.face(best.face)
    return true
  }

  /** Step onto a tile with one move (for transitions/ladders/events). */
  stepOnto(x: number, y: number): boolean {
    if (!this.approach(x, y)) return false
    this.cmd({ type: 'move', dir: this.state.player.direction })
    return true
  }

  // ── inspection over the recorded run ──
  effects(): Effect[] {
    return this.steps.flatMap(step => step.effects)
  }
  messages(): string[] {
    return this.effects().filter(e => e.type === 'message').map(e => (e as { text: string }).text)
  }
  hasMessage(fragment: string | RegExp): boolean {
    return this.messages().some(text => (typeof fragment === 'string' ? text.includes(fragment) : fragment.test(text)))
  }
  countMessages(fragment: string): number {
    return this.messages().filter(text => text.includes(fragment)).length
  }
  countEffects(type: Effect['type']): number {
    return this.effects().filter(e => e.type === type).length
  }
  harvested(cropType?: string): number {
    return this.effects()
      .filter(e => e.type === 'cropHarvested' && (!cropType || (e as { cropType: string }).cropType === cropType))
      .reduce((sum, e) => sum + (e as { quantity: number }).quantity, 0)
  }
}

interface Scenario {
  name: string
  description: string
  seed: string | null
  /** Hosts call autoStartQuests(ctx, createGameState(project)) — mirror that when true. */
  autoStartQuests?: boolean
  /** Raw project; migrated through migrateProject before use. */
  project: () => unknown
  script: (r: Runner) => void
  verify: (r: Runner, initial: GameState) => void
}

function runScenario(scenario: Scenario) {
  const migrated = migrateProject(clone(scenario.project()))
  expect(migrated.errors).toEqual([])
  expect(migrated.ok).toBe(true)
  const project = migrated.data!
  const content = createContentFromProject(project)
  const ctx: EngineContext = { content }
  const created = createGameState(project, scenario.seed === null ? {} : { seed: scenario.seed })
  const initial = scenario.autoStartQuests ? autoStartQuests(ctx, created) : created

  const runner = new Runner(ctx, initial)
  scenario.script(runner)
  const finalState = runner.state

  // The recorded log must replay to the same state through runReplay.
  const replayed = runReplay(ctx, initial, runner.inputs)
  expect(replayed.hash).toBe(hashState(finalState))
  expect(runner.steps.length).toBeGreaterThan(0)

  if (process.env.GOLDEN_DEBUG === scenario.name) {
    // Debug aid: GOLDEN_DEBUG=<scenario> prints every step compactly.
    let replay = initial
    const lines: string[] = []
    runner.steps.forEach((step, index) => {
      const next = step.input.kind === 'tick' ? advanceTick(ctx, replay, step.input.ticks) : applyCommand(ctx, replay, step.input.command)
      replay = next.state
      const p = replay.player
      const texts = step.effects.filter(e => e.type !== 'playerMoved').map(e => e.type === 'message' ? e.text : JSON.stringify(e))
      lines.push(`#${index} ${JSON.stringify(step.input.kind === 'tick' ? { tick: step.input.ticks } : step.input.command)} → ${p.sceneId}(${p.x.toFixed(3)},${p.y.toFixed(3)}) ${p.direction} d${replay.clock.day} t${replay.clock.timeMinutes} $${p.money} e${p.energy} ${texts.join(' | ')}`)
    })
    lines.push(`notes: ${JSON.stringify(runner.notes)}`)
    writeFileSync(path.join(OUT, '..', `debug-${scenario.name}.txt`), lines.join('\n'), 'utf8')
  }
  scenario.verify(runner, initial)

  writeFixture(`replays/${scenario.name}.json`, {
    name: scenario.name,
    description: scenario.description,
    seed: scenario.seed,
    autoStartQuests: Boolean(scenario.autoStartQuests),
    project,
    contentHash: hashState(content),
    createdHash: hashState(created),
    initialHash: hashState(initial),
    initialState: initial,
    finalHash: hashState(finalState),
    stepCount: runner.steps.length,
    steps: runner.steps,
    finalState,
    finalProject: applyStateToProject(project, finalState),
  })
  return runner
}

// ─── Replay scenarios ─────────────────────────────────────────────────────

const THREE_DAYS: ReplayInput[] = [
  { kind: 'command', command: { type: 'move', dir: 'up' } },
  { kind: 'command', command: { type: 'interact' } },
  { kind: 'command', command: { type: 'useTool', tool: 'watering-can' } },
  { kind: 'tick', ticks: 200 },
  { kind: 'command', command: { type: 'sleep' } },
  { kind: 'command', command: { type: 'useTool', tool: 'watering-can' } },
  { kind: 'command', command: { type: 'sleep' } },
  { kind: 'command', command: { type: 'useTool', tool: 'watering-can' } },
  { kind: 'command', command: { type: 'sleep' } },
  { kind: 'command', command: { type: 'interact' } },
  { kind: 'command', command: { type: 'openShop', shopId: 'shop-general' } },
  { kind: 'command', command: { type: 'sellItem', itemId: 'crop-wheat', quantity: 1 } },
  { kind: 'command', command: { type: 'buyItem', itemId: 'seed-carrot', quantity: 2 } },
  { kind: 'command', command: { type: 'closeShop' } },
]

/** replay.test.ts' 1,000-command LCG fuzz log. */
function thousandCommands(): ReplayInput[] {
  let lcg = 123456789
  const nextLcg = () => {
    lcg = (Math.imul(lcg, 1103515245) + 12345) >>> 0
    return lcg
  }
  const dirs = ['up', 'down', 'left', 'right'] as const
  const inputs: ReplayInput[] = []
  for (let i = 0; i < 1000; i++) {
    const roll = nextLcg() % 12
    let cmd: Command
    if (roll < 5) cmd = { type: 'move', dir: dirs[nextLcg() % 4] }
    else if (roll < 7) cmd = { type: 'interact' }
    else if (roll === 7) cmd = { type: 'useTool', tool: 'watering-can' }
    else if (roll === 8) cmd = { type: 'useTool', tool: 'hoe' }
    else if (roll === 9) cmd = { type: 'useTool', tool: 'axe' }
    else if (roll === 10) cmd = { type: 'sleep' }
    else cmd = { type: 'closeDialogue' }
    inputs.push({ kind: 'command', command: cmd })
    if (i % 25 === 0) inputs.push({ kind: 'tick', ticks: nextLcg() % 40 })
  }
  return inputs
}

const scenarios: Scenario[] = []

// 1 ── anchor: the committed golden hash from replay.test.ts ───────────────
scenarios.push({
  name: 'replay-three-days',
  description: 'replay.test.ts THREE_DAYS on makeFarmProject (seed "golden"); final hash must equal the committed TS golden 6f884fd1bf6e2b5e.',
  seed: 'golden',
  project: () => ({ ...makeFarmProject(), schemaVersion: CURRENT_PROJECT_SCHEMA_VERSION }),
  script: r => { for (const input of THREE_DAYS) r.apply(input) },
  verify: r => {
    expect(r.steps[r.steps.length - 1].hash).toBe('6f884fd1bf6e2b5e')
    expect(r.state.clock.day).toBe(4)
    expect(r.harvested('wheat')).toBeGreaterThan(0)
    expect(r.qty('seed-carrot')).toBe(2)
  },
})

// 2 ── discrete fuzz ───────────────────────────────────────────────────────
scenarios.push({
  name: 'fuzz-thousand-commands',
  description: 'replay.test.ts 1,000 LCG-generated discrete commands (move/interact/tools/sleep/closeDialogue + small tick runs) on makeFarmProject.',
  seed: 'thousand',
  project: () => ({ ...makeFarmProject(), schemaVersion: CURRENT_PROJECT_SCHEMA_VERSION }),
  script: r => { for (const input of thousandCommands()) r.apply(input) },
  verify: r => {
    expect(r.steps.length).toBeGreaterThan(1000)
    expect(r.countEffects('dayStarted')).toBeGreaterThan(20)
    expect(r.hasMessage('Tilled soil!')).toBe(true)
    expect(r.hasMessage('Planted Wheat!')).toBe(true)
    expect(r.hasMessage('Watered!')).toBe(true)
    expect(r.countEffects('playerMoved')).toBeGreaterThan(100)
  },
})

// 3 ── starter farm (sample game) ─────────────────────────────────────────
scenarios.push({
  name: 'starter-farm-first-week',
  description: 'Starter Farm sample game (content-default): plant/water wheat, trail-mix useItem, harvest → First Harvest quest, merchant dialogue → shop (sell/buy/daily limit/season lock), buy axe, chop tree, farmer dialogue branch, 24000-tick run to 26:00 collapse.',
  seed: 'starter-golden',
  autoStartQuests: true,
  project: starterProject,
  script: r => {
    const spots = [8, 7, 6]
    for (const x of spots) {
      r.goTo(x, 9)
      r.face('up')
      r.cmd({ type: 'interact' })
      r.cmd({ type: 'useTool', tool: 'watering-can' })
    }
    r.cmd({ type: 'useItem', itemId: 'snack-trail-mix' })
    r.tick(1300)
    for (let day = 0; day < 3; day++) {
      r.cmd({ type: 'sleep' })
      if (day < 2) {
        for (const x of spots) {
          r.goTo(x, 9)
          r.face('up')
          if (r.tileAt(x, 8)?.crop) r.cmd({ type: 'useTool', tool: 'watering-can' })
          else r.cmd({ type: 'interact' })
        }
      }
    }
    for (const x of spots) {
      r.goTo(x, 9)
      r.face('up')
      r.cmd({ type: 'interact' })
    }
    // Merchant Mia at (12,3): dialogue → "Let's trade." opens the shop.
    r.approach(12, 3)
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'chooseDialogueOption', index: 0 })
    r.cmd({ type: 'sellItem', itemId: 'crop-wheat', quantity: r.qty('crop-wheat') })
    r.cmd({ type: 'sellItem', itemId: 'crop-wheat', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'tool-axe', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'seed-corn', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'fertilizer-quality', quantity: 2 })
    r.cmd({ type: 'buyItem', itemId: 'fertilizer-quality', quantity: 4 })
    r.cmd({ type: 'buyItem', itemId: 'tool-scythe', quantity: 5 })
    r.cmd({ type: 'closeShop' })
    // Chop the tree at (2,2).
    r.approach(2, 2)
    for (let i = 0; i < 4; i++) r.cmd({ type: 'useTool', tool: 'axe' })
    // Old Farmer at (3,6): take the "What crops grow best here?" branch.
    r.approach(3, 6)
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'chooseDialogueOption', index: 1 })
    r.cmd({ type: 'chooseDialogueOption', index: 0 })
    r.tick(0)
    // Stay up all day: 1200 minutes at 1 min/s = 24000 ticks → collapse.
    r.tick(24000)
    r.tick(1300)
    r.cmd({ type: 'sleep' })
  },
  verify: (r, initial) => {
    expect(initial.player.activeQuests).toContain('quest-first-harvest')
    expect(r.hasMessage('Planted Wheat! (Fertilized)')).toBe(true)
    expect(r.hasMessage('You feel refreshed!')).toBe(true)
    expect(r.harvested('wheat')).toBeGreaterThanOrEqual(3)
    expect(r.state.player.completedQuests).toContain('quest-first-harvest')
    expect(r.hasMessage(/^Sold \d+x Wheat/)).toBe(true)
    expect(r.hasMessage('Bought 1x Axe')).toBe(true)
    expect(r.hasMessage('Not available in spring.')).toBe(true)
    expect(r.hasMessage('Only 3 left today.')).toBe(true)
    expect(r.hasMessage('Tree cleared!')).toBe(true)
    expect(r.qty('material-wood')).toBeGreaterThan(0)
    expect(r.hasMessage('You collapsed from exhaustion!')).toBe(true)
    expect(r.state.clock.day).toBeGreaterThanOrEqual(6)
  },
})

// 4 ── cozy garden (sample game): energy off, slow clock ───────────────────
scenarios.push({
  name: 'cozy-garden-slow-day',
  description: 'Cozy Garden sample game: energy disabled (tool spam costs nothing), 0.5 min/s clock, collapse penalty 0 after a 48000-tick day.',
  seed: 'cozy',
  autoStartQuests: true,
  project: () => pinTimes(createCozyFarmProject()),
  script: r => {
    r.face('up')
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'useTool', tool: 'watering-can' })
    r.face('down')
    for (let i = 0; i < 3; i++) r.cmd({ type: 'useTool', tool: 'hoe' })
    r.cmd({ type: 'useTool', tool: 'axe' })
    r.cmd({ type: 'setMoveIntent', dx: -1, dy: 0 })
    r.tick(60)
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    r.tick(48000)
    r.cmd({ type: 'useTool', tool: 'watering-can' })
    r.tick(2400)
  },
  verify: r => {
    expect(r.hasMessage('Planted Wheat!')).toBe(true)
    expect(r.hasMessage('Tilled soil!')).toBe(true)
    expect(r.hasMessage("Can't use Hoe here")).toBe(true)
    expect(r.hasMessage('You need a axe!')).toBe(true)
    expect(r.hasMessage('You collapsed from exhaustion! Lost $0.')).toBe(true)
    expect(r.state.clock.day).toBe(2)
    expect(r.state.player.money).toBe(250)
  },
})

// 5 ── quest RPG (sample game) ────────────────────────────────────────────
scenarios.push({
  name: 'quest-rpg-rebuild-square',
  description: 'Quest RPG sample game: elder dialogue offers a quest, sell starting kit, buy axe+pickaxe, chop trees and break rocks (collect objectives, rock respawn over days) until "Rebuild the Square" completes.',
  seed: 'quest-rpg',
  autoStartQuests: true,
  project: () => pinTimes(createQuestRpgProject()),
  script: r => {
    r.approach(2, 3)
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'chooseDialogueOption', index: 0 })
    r.approach(12, 3)
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'chooseDialogueOption', index: 0 })
    for (const id of ['seed-wheat', 'seed-tomato', 'fertilizer-basic', 'snack-trail-mix']) {
      r.cmd({ type: 'sellItem', itemId: id, quantity: r.qty(id) })
    }
    r.cmd({ type: 'buyItem', itemId: 'tool-axe', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'tool-pickaxe', quantity: 1 })
    r.cmd({ type: 'closeShop' })
    const strike = (x: number, y: number, tool: 'axe' | 'pickaxe') => {
      if (!r.tileAt(x, y)?.node || r.tileAt(x, y)!.node!.remainingHealth <= 0) return
      r.approach(x, y)
      for (let i = 0; i < 6 && (r.tileAt(x, y)?.node?.remainingHealth ?? 0) > 0; i++) r.cmd({ type: 'useTool', tool })
    }
    strike(2, 2, 'axe')
    strike(3, 9, 'axe')
    for (let day = 0; day < 8 && r.state.player.activeQuests.includes('quest-rebuild-square'); day++) {
      strike(13, 2, 'pickaxe')
      strike(13, 9, 'pickaxe')
      r.tick(1200)
      r.cmd({ type: 'sleep' })
    }
    r.approach(2, 3)
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'chooseDialogueOption', index: 1 })
  },
  verify: r => {
    expect(r.hasMessage('New quest: Rebuild the Square')).toBe(true)
    expect(r.state.player.completedQuests).toContain('quest-rebuild-square')
    expect(r.countEffects('questCompleted')).toBeGreaterThanOrEqual(1)
    expect(r.hasMessage('Rock cleared!')).toBe(true)
    expect(r.hasMessage('Tree cleared!')).toBe(true)
  },
})

// 6 ── blank project: default seed (null), idle day to collapse ───────────
scenarios.push({
  name: 'blank-idle-collapse',
  description: 'Blank sample project with seed=null (engineSeed = "<id>:<gameStartTime>"), free-walk, missing-tool message, 24000+ idle ticks → collapse at 26:00 with $50 penalty.',
  seed: null,
  project: () => pinTimes(createBlankProject()),
  script: r => {
    r.cmd({ type: 'useTool', tool: 'hoe' })
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'setMoveIntent', dx: 1, dy: 1 })
    r.tick(25)
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: -1 })
    r.tick(200)
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    r.tick(24100)
    r.cmd({ type: 'sleep' })
  },
  verify: (r, initial) => {
    expect(initial.meta.engineSeed).toBe(`project-blank:${FIXED_TIME}`)
    expect(r.hasMessage('You need a hoe!')).toBe(true)
    expect(r.hasMessage('You collapsed from exhaustion! Lost $50.')).toBe(true)
    expect(r.state.player.money).toBe(50)
    expect(r.state.clock.day).toBe(3)
    expect(r.state.player.y).toBeCloseTo(0.3, 3)
  },
})

// 7 ── free movement ──────────────────────────────────────────────────────
function freeMovementProject(): GameProject {
  const field = createEmptyScene('field', 'Field', 10, 8)
  for (let y = 0; y <= 4; y++) paint(field, 5, y, 'wall')
  field.tiles[5][3] = { ...field.tiles[5][3], item: itemDef('seed-wheat') }
  field.tiles[6][6] = { ...field.tiles[6][6], item: itemDef('gift-flower') }
  field.transitions = [{ fromX: 8, fromY: 6, toSceneId: 'cave', toX: 2, toY: 2 }]
  const cave = createEmptyScene('cave', 'Cave', 6, 6)
  wallBorder(cave)
  cave.transitions = [{ fromX: 4, fromY: 4, toSceneId: 'field', toX: 7, toY: 3 }]
  return labProject('free-move', [field, cave], {
    x: 1, y: 5, direction: 'right', maxInventorySize: 4,
    inventory: [slot('tool-hoe', 1), slot('tool-watering-can', 1), slot('seed-carrot', 3)],
  }, {
    npcs: [{ id: 'npc-statue', name: 'Statue', x: 2, y: 2, sceneId: 'field', dialogue: [], canMove: false, appearance: 'farmer' }],
    events: [
      {
        id: 'evt-shortcut', name: 'Shortcut', sceneId: 'field', trigger: 'enter',
        conditions: [{ type: 'enterTile', x: 6, y: 1, x2: 7, y2: 2 }],
        outcomes: [{ type: 'message', message: 'Found a shortcut!' }, { type: 'giveMoney', amount: 15 }],
        active: true, repeatable: false,
      },
      {
        id: 'evt-bell', name: 'Bell', sceneId: '', trigger: 'tick',
        conditions: [{ type: 'timeOfDay', minMinute: 420, maxMinute: 421 }],
        outcomes: [{ type: 'message', message: 'Seven o\'clock bell' }],
        active: true, repeatable: false,
      },
    ],
  })
}

scenarios.push({
  name: 'free-movement',
  description: 'Free movement: intent clamping/truncation, integration at 4.5 tiles/s, ground pickups (and full-inventory abort for discrete moves vs. free movement), scene transitions both ways, wall sliding on diagonals, NPC and bounds collision, enter-region and tick events, 1300-tick held-intent runs.',
  seed: 'free-move',
  project: freeMovementProject,
  script: r => {
    r.cmd({ type: 'setMoveIntent', dx: 5, dy: -3 })
    r.cmd({ type: 'setMoveIntent', dx: 0.7, dy: -1.9 })
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    // East along row 5: picks up the wheat seed at (3,5).
    r.cmd({ type: 'setMoveIntent', dx: 1, dy: 0 })
    r.tick(30)
    // South onto the transition tile (8,6) → cave.
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 1 })
    r.tick(5)
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    r.tick(3)
    // Diagonal into the cave's walls (slides), then back through (4,4).
    r.cmd({ type: 'setMoveIntent', dx: -1, dy: -1 })
    r.tick(20)
    r.cmd({ type: 'setMoveIntent', dx: 1, dy: 1 })
    r.tick(25)
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    r.tick(1)
    // Back in the field, drifting south-east; cut north-west through the
    // shortcut region (6..7, 1..2).
    r.cmd({ type: 'setMoveIntent', dx: -1, dy: -1 })
    r.tick(12)
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    // Wall slide: from (4,3), up-right into the wall column x=5.
    r.goTo(4, 3)
    r.cmd({ type: 'setMoveIntent', dx: 1, dy: -1 })
    r.tick(20)
    // NPC collision: from (2,4) walk up into the statue at (2,2).
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    r.goTo(2, 4)
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: -1 })
    r.tick(40)
    // Full inventory: carrot seeds + tools + wheat = 4 slots. A discrete move
    // onto the flower aborts; free movement over it keeps going.
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    r.goTo(6, 5)
    r.cmd({ type: 'move', dir: 'down' })
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 1 })
    r.tick(6)
    // Long held-intent run against the west bound (minute events fire).
    r.cmd({ type: 'setMoveIntent', dx: -1, dy: 0 })
    r.tick(1300)
    r.cmd({ type: 'move', dir: 'left' })
    r.cmd({ type: 'setMoveIntent', dx: 1, dy: -1 })
    r.tick(1300)
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
  },
  verify: r => {
    expect(r.hasMessage('Picked up Wheat Seeds')).toBe(true)
    expect(r.hasMessage('Inventory is full!')).toBe(true)
    expect(r.effects().filter(e => e.type === 'sceneChanged').map(e => (e as { sceneId: string }).sceneId)).toEqual(expect.arrayContaining(['cave', 'field']))
    expect(r.hasMessage('Found a shortcut!')).toBe(true)
    expect(r.hasMessage("Seven o'clock bell")).toBe(true)
    expect(r.countEffects('playerMoved')).toBeGreaterThan(20)
    expect(r.state.player.money).toBe(115)
    // Wall-slide step: x pinned at the wall's west face, y slid to the bound.
    const slideIndex = r.steps.findIndex((s, i) => i > 0 && s.input.kind === 'tick' && s.input.ticks === 20 && r.steps[i - 1].input.kind === 'command' && JSON.stringify((r.steps[i - 1].input as { command: Command }).command) === JSON.stringify({ type: 'setMoveIntent', dx: 1, dy: -1 }))
    expect(slideIndex).toBeGreaterThan(0)
  },
})

// 8 ── farming lifecycle ──────────────────────────────────────────────────
function farmingProject(): GameProject {
  const farm = createEmptyScene('farm', 'Farm', 12, 10)
  return labProject('farming-lab', [farm], {
    x: 2, y: 5, direction: 'up',
    inventory: [
      slot('tool-hoe', 1), slot('tool-watering-can-2', 1), slot('tool-scythe', 1),
      slot('seed-cauliflower', 1), slot('seed-wheat', 3), slot('seed-carrot', 2), slot('seed-strawberry', 1),
      slot('seed-tomato', 3), slot('fertilizer-quality', 3),
    ],
  }, { currentDay: 25 })
}

scenarios.push({
  name: 'farming-lifecycle',
  description: 'Hoe tilling, multi-tile cauliflower, fertilizer quality, tier-2 AoE watering, day-based growth over sleeps, interact + scythe harvests, spring→summer wither and scythe clearing, season-locked planting, regrowing tomatoes harvested twice, an unwatered crop, 1300-tick runs.',
  seed: 'farming',
  project: farmingProject,
  script: r => {
    for (let x = 2; x <= 9; x++) {
      r.goTo(x, 5)
      r.face('up')
      r.cmd({ type: 'useTool', tool: 'hoe' })
    }
    for (const x of [2, 3]) {
      r.goTo(x, 4)
      r.face('up')
      r.cmd({ type: 'useTool', tool: 'hoe' })
    }
    // Cauliflower root at (2,3) covering (2..3, 3..4).
    r.goTo(2, 4)
    r.face('up')
    r.cmd({ type: 'interact' })
    for (let x = 4; x <= 9; x++) {
      r.goTo(x, 5)
      r.face('up')
      r.cmd({ type: 'interact' })
    }
    const waterAll = () => {
      for (const x of [2, 3]) {
        r.goTo(x, 4)
        r.face('up')
        r.cmd({ type: 'useTool', tool: 'watering-can' })
      }
      for (const x of [3, 6, 9]) {
        r.goTo(x, 5)
        r.face('up')
        r.cmd({ type: 'useTool', tool: 'watering-can' })
      }
    }
    const harvestRow = (useScythe: boolean) => {
      for (let x = 4; x <= 9; x++) {
        const crop = r.tileAt(x, 4)?.crop
        if (!crop || crop.withered) continue
        const def = r.ctx.content.crops[crop.type]
        if ((crop.daysGrown ?? 0) < (def.growthDays ?? 1)) continue
        r.goTo(x, 5)
        r.face('up')
        if (useScythe) r.cmd({ type: 'useTool', tool: 'scythe' })
        else r.cmd({ type: 'interact' })
      }
    }
    waterAll()
    r.tick(1300)
    // Days 25 → 29 (summer arrives on 29).
    for (let d = 0; d < 4; d++) {
      r.cmd({ type: 'sleep' })
      harvestRow(d % 2 === 1)
      waterAll()
      if (d === 1) r.tick(1300)
    }
    // Summer: withered strawberry/cauliflower.
    r.goTo(2, 4)
    r.face('up')
    r.cmd({ type: 'interact' })
    for (const [x, y, fx, fy, dir] of [[2, 3, 2, 4, 'up'], [3, 3, 3, 4, 'up'], [2, 4, 2, 5, 'up'], [3, 4, 3, 5, 'up'], [9, 4, 9, 5, 'up']] as const) {
      if (!r.tileAt(x, y)?.crop) continue
      r.goTo(fx, fy)
      r.face(dir)
      r.cmd({ type: 'useTool', tool: 'scythe' })
    }
    // Replant: tomatoes (summer) on the cleared row.
    for (const x of [4, 5, 9]) {
      if (r.tileAt(x, 4)?.crop) continue
      r.goTo(x, 5)
      r.face('up')
      r.cmd({ type: 'interact' })
    }
    for (let d = 0; d < 7; d++) {
      r.goTo(5, 5)
      r.face('up')
      r.cmd({ type: 'useTool', tool: 'watering-can' })
      if (d === 3) r.tick(1300)
      r.cmd({ type: 'sleep' })
      harvestRow(false)
    }
  },
  verify: r => {
    expect(r.hasMessage('Tilled soil!')).toBe(true)
    expect(r.hasMessage('Planted Cauliflower! (Fertilized)')).toBe(true)
    expect(r.harvested('wheat')).toBeGreaterThan(0)
    expect(r.harvested('carrot')).toBeGreaterThan(0)
    expect(r.harvested('tomato')).toBeGreaterThan(0)
    expect(r.hasMessage('Summer has arrived!')).toBe(true)
    expect(r.hasMessage('This crop withered — clear it with a scythe.')).toBe(true)
    expect(r.hasMessage('Cleared the withered crop.')).toBe(true)
    const regrown = [4, 5].map(x => r.tileAt(x, 4)?.crop?.harvestCount ?? 0)
    expect(Math.max(...regrown)).toBeGreaterThanOrEqual(2)
    const neglected = r.tileAt(9, 4)?.crop
    expect(neglected?.daysWithoutWater ?? 0).toBeGreaterThanOrEqual(3)
  },
})

// 9 ── gathering nodes & tools ────────────────────────────────────────────
function gatheringProject(): GameProject {
  const woods = createEmptyScene('woods', 'Woods', 12, 10)
  placeNode(woods, 2, 2, 'node-tree', 3)
  placeNode(woods, 4, 2, 'node-stump', 2)
  placeNode(woods, 6, 2, 'node-rock', 3)
  placeNode(woods, 8, 2, 'node-boulder', 6)
  placeNode(woods, 10, 2, 'node-crystal', 2)
  placeNode(woods, 3, 5, 'node-weeds', 1)
  placeNode(woods, 4, 5, 'node-weeds', 1)
  const rustyAxe: Item = {
    id: 'tool-rusty-axe', name: 'Rusty Axe', description: 'Barely holding together', type: 'tool',
    stackable: false, maxStack: 1, value: 20, toolType: 'axe', toolPower: 1, durability: 3, maxDurability: 20,
  }
  const items = [...createDefaultItems(), rustyAxe]
  return labProject('gathering-lab', [woods], {
    x: 2, y: 4, direction: 'up', money: 100,
    inventory: [
      { item: rustyAxe, quantity: 1 }, slot('tool-pickaxe', 1), slot('tool-scythe', 1), slot('tool-hoe', 1),
    ],
  }, {
    items,
    nodeTypes: [
      {
        id: 'node-tree', name: 'Tree', health: 3, requiredTool: 'axe', requiredToolTier: 1,
        drops: [{ itemId: 'material-wood', min: 3, max: 5, weight: 1 }], respawnDays: null, color: '#3f6d33', blocksMovement: true,
      },
      {
        id: 'node-crystal', name: 'Crystal', health: 2, requiredTool: 'pickaxe', requiredToolTier: 1,
        drops: [
          { itemId: 'gem-quartz', min: 1, max: 1, weight: 1 },
          { itemId: 'material-stone', min: 2, max: 5, weight: 3 },
          { itemId: 'material-fiber', min: 0, max: 0, weight: 1 },
        ],
        respawnDays: 1, color: '#cfe3ee', blocksMovement: true,
      },
    ],
    shops: [{
      id: 'shop-smith', name: 'Smithy', stock: [{ itemId: 'tool-pickaxe-2', price: 60 }],
      sellPriceMultiplier: 1, buysItems: true, repairsTools: true, repairCostPerPoint: 2,
    }],
  })
}

scenarios.push({
  name: 'gathering-and-tools',
  description: 'Gathering nodes: wrong-tool and too-weak-tier messages, project node-type override + custom multi-drop node, tool durability to breakage, shop repair, tier-2 pickaxe power, respawning stump/rock/crystal/weeds across days, walkable weeds, foraging/mining XP, low-energy warning and exhaustion collapse from tool spam.',
  seed: 'gathering',
  project: gatheringProject,
  script: r => {
    r.approach(2, 2)
    r.cmd({ type: 'useTool', tool: 'hoe' })
    for (let i = 0; i < 3; i++) r.cmd({ type: 'useTool', tool: 'axe' })
    r.approach(4, 2)
    r.cmd({ type: 'useTool', tool: 'axe' })
    r.approach(8, 2)
    r.cmd({ type: 'useTool', tool: 'pickaxe' })
    r.cmd({ type: 'openShop', shopId: 'shop-smith' })
    r.cmd({ type: 'repairTool', itemId: 'tool-rusty-axe' })
    r.cmd({ type: 'repairTool', itemId: 'tool-rusty-axe' })
    r.cmd({ type: 'repairTool', itemId: 'material-wood' })
    r.cmd({ type: 'sellItem', itemId: 'tool-pickaxe', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'tool-pickaxe-2', quantity: 1 })
    r.cmd({ type: 'closeShop' })
    r.approach(4, 2)
    for (let i = 0; i < 2; i++) r.cmd({ type: 'useTool', tool: 'axe' })
    r.approach(6, 2)
    for (let i = 0; i < 2; i++) r.cmd({ type: 'useTool', tool: 'pickaxe' })
    r.approach(8, 2)
    for (let i = 0; i < 3; i++) r.cmd({ type: 'useTool', tool: 'pickaxe' })
    r.approach(10, 2)
    r.cmd({ type: 'useTool', tool: 'pickaxe' })
    r.goTo(3, 6)
    r.face('up')
    r.cmd({ type: 'useTool', tool: 'scythe' })
    r.goTo(4, 4) // straight through the (walkable) weeds at (4,5)
    r.goTo(4, 7)
    // Till grass until exhaustion collapses the day.
    for (let i = 0; i < 40 && r.state.clock.day === 1; i++) {
      const x = 1 + (i % 10)
      r.goTo(x, 8)
      r.face('down')
      r.cmd({ type: 'useTool', tool: 'hoe' })
    }
    r.tick(1300)
    r.cmd({ type: 'sleep' })
    r.cmd({ type: 'sleep' })
    r.approach(6, 2)
    for (let i = 0; i < 2; i++) r.cmd({ type: 'useTool', tool: 'pickaxe' })
    r.approach(10, 2)
    r.cmd({ type: 'useTool', tool: 'pickaxe' })
  },
  verify: r => {
    expect(r.hasMessage('Tree needs a axe.')).toBe(true)
    expect(r.hasMessage("Your pickaxe isn't strong enough for boulder.")).toBe(true)
    expect(r.hasMessage('Your Rusty Axe is broken! A shop can repair it.')).toBe(true)
    expect(r.hasMessage('Repaired Rusty Axe for $40')).toBe(true)
    expect(r.hasMessage('Rusty Axe is in perfect shape.')).toBe(true)
    expect(r.hasMessage("That can't be repaired.")).toBe(true)
    expect(r.hasMessage('Boulder cleared!')).toBe(true)
    expect(r.hasMessage('Crystal cleared')).toBe(true)
    expect(r.hasMessage('Weeds cleared!')).toBe(true)
    expect(r.hasMessage('You are getting exhausted — consider sleeping.')).toBe(true)
    expect(r.hasMessage('You collapsed from exhaustion! Lost $50.')).toBe(true)
    expect(r.countMessages('Rock cleared!')).toBe(2)
    expect(r.qty('material-wood')).toBeGreaterThan(0)
    expect(r.qty('material-stone')).toBeGreaterThan(0)
    expect(r.qty('material-fiber')).toBeGreaterThan(0)
    expect(r.state.player.skills.foraging?.xp ?? 0).toBeGreaterThan(0)
    expect(r.state.player.skills.mining?.xp ?? 0).toBeGreaterThan(0)
  },
})

// 10 ── shops & economy ───────────────────────────────────────────────────
function shopProject(): GameProject {
  const market = createEmptyScene('market', 'Market', 8, 8)
  const vendorDialogue = {
    id: 'dlg-vendor', npcId: 'npc-vendor', text: 'Browse my wares!',
    options: [{ text: 'Show me.', openShopId: 'shop-picky' }, { text: 'The secret shop?', openShopId: 'shop-nope' }],
  }
  return labProject('shop-lab', [market], {
    x: 3, y: 3, direction: 'up', money: 500, maxInventorySize: 6,
    inventory: [slot('crop-wheat', 10), slot('fish-carp', 3), slot('tool-hoe', 1)],
  }, {
    npcs: [{ id: 'npc-vendor', name: 'Vendor Vic', x: 3, y: 2, sceneId: 'market', dialogue: [vendorDialogue], canMove: false, appearance: 'merchant' }],
    dialogues: [vendorDialogue],
    shops: [
      createDefaultShop(),
      {
        id: 'shop-picky', name: 'Picky Shop',
        stock: [{ itemId: 'gift-flower', price: 7, dailyLimit: 3 }, { itemId: 'seed-corn', seasons: ['summer'] }, { itemId: 'ghost-item' }],
        sellPriceMultiplier: 0.75, buysItems: true, repairsTools: false, repairCostPerPoint: 1,
      },
      { id: 'shop-museum', name: 'Museum', stock: [], sellPriceMultiplier: 1, buysItems: false, repairsTools: false, repairCostPerPoint: 0.5 },
    ],
  })
}

scenarios.push({
  name: 'shop-economy',
  description: 'Shops: no-shop and unknown-shop errors, price overrides, daily limits (partial/sold out, reset after sleep), season-gated stock, unknown/not-stocked items, zero quantities, sell multipliers with flooring, non-buying and non-repairing shops, full inventory, insufficient money, dialogue openShopId (valid and missing shop).',
  seed: 'shop',
  project: shopProject,
  script: r => {
    r.cmd({ type: 'buyItem', itemId: 'gift-flower', quantity: 1 })
    r.cmd({ type: 'openShop', shopId: 'shop-nope' })
    r.cmd({ type: 'openShop', shopId: 'shop-picky' })
    r.cmd({ type: 'buyItem', itemId: 'gift-flower', quantity: 2 })
    r.cmd({ type: 'buyItem', itemId: 'gift-flower', quantity: 2 })
    r.cmd({ type: 'buyItem', itemId: 'gift-flower', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'gift-flower', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'seed-corn', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'ghost-item', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'seed-wheat', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'gift-flower', quantity: 0 })
    r.cmd({ type: 'sellItem', itemId: 'crop-wheat', quantity: 4 })
    r.cmd({ type: 'sellItem', itemId: 'fish-carp', quantity: 5 })
    r.cmd({ type: 'sellItem', itemId: 'fish-carp', quantity: 3 })
    r.cmd({ type: 'sellItem', itemId: 'crop-wheat', quantity: 0 })
    r.cmd({ type: 'repairTool', itemId: 'tool-hoe' })
    r.cmd({ type: 'closeShop' })
    r.cmd({ type: 'closeShop' })
    r.cmd({ type: 'openShop', shopId: 'shop-museum' })
    r.cmd({ type: 'sellItem', itemId: 'crop-wheat', quantity: 1 })
    r.cmd({ type: 'openShop', shopId: 'shop-general' })
    r.cmd({ type: 'repairTool', itemId: 'tool-hoe' })
    r.cmd({ type: 'buyItem', itemId: 'fertilizer-quality', quantity: 6 })
    for (const id of ['tool-scythe', 'tool-axe', 'tool-pickaxe', 'tool-fishing-rod']) r.cmd({ type: 'buyItem', itemId: id, quantity: 1 })
    r.cmd({ type: 'sellItem', itemId: 'tool-fishing-rod', quantity: 1 })
    r.cmd({ type: 'buyItem', itemId: 'machine-preserves', quantity: 9 })
    r.cmd({ type: 'buyItem', itemId: 'seed-strawberry', quantity: 3 })
    r.cmd({ type: 'closeShop' })
    r.tick(1300)
    r.cmd({ type: 'sleep' })
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'chooseDialogueOption', index: 1 })
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'chooseDialogueOption', index: 0 })
    r.cmd({ type: 'buyItem', itemId: 'gift-flower', quantity: 3 })
    r.cmd({ type: 'closeShop' })
  },
  verify: r => {
    for (const text of [
      'No shop is open.', 'That shop does not exist.', 'Bought 2x Flower for $14', 'Only 1 left today.', 'Sold out for today!',
      'Not available in spring.', 'Unknown item.', 'Not sold here.', 'Sold 4x Wheat for $72', "You don't have that many.",
      'Sold 3x Carp for $39', "Picky Shop doesn't repair tools.", "Museum doesn't buy items.", 'Hoe is in perfect shape.',
      'Only 5 left today.', 'Inventory is full!', 'Not enough money!', 'Bought 3x Flower for $21',
    ]) {
      expect(r.hasMessage(text), text).toBe(true)
    }
    expect(r.state.shopPurchasesToday).toEqual({ 'shop-picky': { 'gift-flower': 3 } })
    expect(r.qty('gift-flower')).toBe(6)
  },
})

// 11 ── dialogue & social ─────────────────────────────────────────────────
function socialProject(): GameProject {
  const village = createEmptyScene('village', 'Village', 10, 10)
  const ann1 = {
    id: 'dlg-ann-1', npcId: 'npc-ann', text: 'Hi there!',
    options: [
      { text: 'Bye' },
      { text: 'Any gifts?', giveItem: 'gift-flower', giveItemQuantity: 2, nextDialogueId: 'dlg-ann-2' },
      { text: 'Spare some coin?', giveMoney: 30 },
      { text: 'Secret', requiresFriendship: 200, giveMoney: 500 },
      { text: 'Look at this flower', requiresItem: 'gift-flower', nextDialogueId: 'dlg-ann-3' },
      { text: 'Remember me?', requiresFlag: 'met-ann', offerQuestId: 'quest-ann-gifts' },
      { text: 'Introduce yourself', actionId: 'action-meet-ann' },
    ],
  }
  const ann2 = { id: 'dlg-ann-2', npcId: 'npc-ann', text: 'Here you go.', options: [{ text: 'Thanks' }] }
  const ann3 = { id: 'dlg-ann-3', npcId: 'npc-ann', text: 'Lovely flower!', options: [{ text: 'Right?', nextDialogueId: 'dlg-missing' }] }
  const bob1 = { id: 'dlg-bob-1', npcId: 'npc-bob', text: 'Nice day.', options: [{ text: 'Need help?', offerQuestId: 'quest-talk-bob' }, { text: 'Bye' }] }
  return labProject('social-lab', [village], {
    x: 5, y: 4, direction: 'up', money: 100,
    inventory: [slot('gift-flower', 1), slot('fish-carp', 2), slot('junk-boot', 1), slot('material-stone', 3), slot('crop-wheat', 3)],
  }, {
    npcs: [
      {
        id: 'npc-ann', name: 'Ann', x: 5, y: 2, sceneId: 'village', dialogue: [ann1, ann2], canMove: false, appearance: 'farmer',
        giftTastes: { loved: ['gift-flower'], liked: ['fish-carp'], disliked: ['junk-boot'], hated: ['material-stone'] },
        birthday: { season: 'spring', day: 3 },
      },
      { id: 'npc-bob', name: 'Bob', x: 8, y: 7, sceneId: 'village', dialogue: [bob1], canMove: true, movePattern: 'wander', wanderRadius: 2, appearance: 'merchant' },
    ],
    dialogues: [ann1, ann2, ann3, bob1],
    actions: [{
      id: 'action-meet-ann', name: 'Meet Ann', description: '', conditions: [], failMessage: '', energyCost: 0,
      outcomes: [{ type: 'setFlag', flagName: 'met-ann' }, { type: 'modifyFriendship', npcId: 'npc-ann', amount: 10 }, { type: 'message', message: 'Ann smiles.' }],
    }],
    quests: [
      {
        id: 'quest-ann-gifts', name: 'Ann\'s Gifts', description: 'Give Ann three gifts', status: 'not-started',
        objectives: [{ id: 'o-gift', type: 'gift', description: 'Gift Ann', targetNPCId: 'npc-ann', targetItemQuantity: 3, completed: false, progress: 0 }],
        rewards: { money: 100, items: [{ itemId: 'gift-flower', quantity: 1 }] },
      },
      {
        id: 'quest-talk-bob', name: 'Chat with Bob', description: 'Talk to Bob', status: 'not-started',
        objectives: [{ id: 'o-talk', type: 'talk', description: 'Talk to Bob', targetNPCId: 'npc-bob', completed: false, progress: 0 }],
        rewards: { money: 25 },
      },
    ],
    events: [{
      id: 'evt-warm-wave', name: 'Warm wave', sceneId: '', trigger: 'tick',
      conditions: [{ type: 'friendship', npcId: 'npc-ann', min: 200 }],
      outcomes: [{ type: 'message', message: 'Ann waves at you warmly.' }],
      active: true, repeatable: false,
    }],
  })
}

scenarios.push({
  name: 'dialogue-and-social',
  description: 'Dialogue trees (giveMoney, giveItem xN, nextDialogueId, global dialogue lookup, missing next, out-of-range index, closeDialogue), visibility gates (friendship/item/flag), option-bound action + offerQuestId, daily gifting with all five taste reactions, birthday doubling, gift quest completion with item rewards, friendship-gated tick event, plugin modifyFriendship, wandering NPC talk quest.',
  seed: 'social',
  project: socialProject,
  script: r => {
    const talkAnn = () => { r.approach(5, 2); r.cmd({ type: 'interact' }) }
    talkAnn(); r.cmd({ type: 'chooseDialogueOption', index: 4 }) // Introduce (visible: Bye, gifts, coin, flower, introduce)
    talkAnn(); r.cmd({ type: 'chooseDialogueOption', index: 4 }) // Remember me? → quest
    talkAnn(); r.cmd({ type: 'chooseDialogueOption', index: 1 }); r.cmd({ type: 'chooseDialogueOption', index: 0 })
    talkAnn(); r.cmd({ type: 'chooseDialogueOption', index: 2 })
    talkAnn(); r.cmd({ type: 'chooseDialogueOption', index: 3 }); r.cmd({ type: 'chooseDialogueOption', index: 0 })
    talkAnn(); r.cmd({ type: 'chooseDialogueOption', index: 99 })
    talkAnn(); r.cmd({ type: 'closeDialogue' })
    r.cmd({ type: 'chooseDialogueOption', index: 0 })
    r.cmd({ type: 'giveGift', itemId: 'gift-flower' })
    r.cmd({ type: 'giveGift', itemId: 'fish-carp' })
    r.cmd({ type: 'giveGift', itemId: 'not-held' })
    r.face('down')
    r.cmd({ type: 'giveGift', itemId: 'fish-carp' })
    for (const gift of ['fish-carp', 'crop-wheat', 'junk-boot', 'material-stone']) {
      r.tick(200)
      r.cmd({ type: 'sleep' })
      r.approach(5, 2)
      r.cmd({ type: 'giveGift', itemId: gift })
    }
    r.cmd({ type: 'pluginMutation', pluginId: 'golden:social', mutation: { type: 'modifyFriendship', npcId: 'npc-ann', delta: 100 } })
    r.cmd({ type: 'pluginMutation', pluginId: 'golden:social', mutation: { type: 'modifyFriendship', npcId: 'npc-nobody', delta: 5 } })
    talkAnn(); r.cmd({ type: 'chooseDialogueOption', index: 3 }) // Secret now visible
    r.tick(40)
    // Bob wanders; find him and talk twice (offer, then talk progress).
    r.tick(1300)
    for (let attempt = 0; attempt < 2; attempt++) {
      const bob = r.state.npcs['npc-bob']
      r.approach(bob.x, bob.y)
      r.cmd({ type: 'interact' })
      r.cmd({ type: 'chooseDialogueOption', index: 0 })
    }
  },
  verify: r => {
    expect(r.hasMessage('Ann smiles.')).toBe(true)
    expect(r.hasMessage("New quest: Ann's Gifts")).toBe(true)
    expect(r.hasMessage('Received Flower x2')).toBe(true)
    expect(r.hasMessage('Received $30')).toBe(true)
    expect(r.hasMessage('Ann: They love it! (+80)')).toBe(true)
    expect(r.hasMessage('Ann has already received a gift today.')).toBe(true)
    expect(r.hasMessage('No one to give that to.')).toBe(true)
    expect(r.hasMessage('Ann: They like it. (+45)')).toBe(true)
    expect(r.hasMessage('Ann: They accept it politely. (Birthday!) (+40)')).toBe(true)
    expect(r.hasMessage("Ann: They don't seem thrilled… (-20)")).toBe(true)
    expect(r.hasMessage('Ann: They hate it! (-40)')).toBe(true)
    expect(r.hasMessage('Plugin golden:social: unknown NPC \'npc-nobody\'')).toBe(true)
    expect(r.hasMessage('Received $500')).toBe(true)
    expect(r.hasMessage('Ann waves at you warmly.')).toBe(true)
    expect(r.state.player.completedQuests).toEqual(expect.arrayContaining(['quest-ann-gifts', 'quest-talk-bob']))
    expect(r.state.social['npc-ann'].friendship).toBe(215)
    const bob = r.state.npcs['npc-bob']
    expect(bob.x !== 8 || bob.y !== 7).toBe(true)
  },
})

// 12 ── events showcase ───────────────────────────────────────────────────
function eventsProject(): GameProject {
  const town = createEmptyScene('town', 'Town', 10, 10)
  paint(town, 6, 8, 'soil')
  paint(town, 7, 8, 'soil')
  paint(town, 7, 7, 'soil')
  town.tiles[7][7] = { ...town.tiles[7][7], crop: { type: 'wheat', plantedAt: 0, plantedOnDay: 1, daysGrown: 0, stage: 0, watered: false, quality: 'normal', mutation: null, harvestCount: 0, daysWithoutWater: 0 } }
  town.transitions = [{ fromX: 9, fromY: 5, toSceneId: 'shrine', toX: 1, toY: 1, locked: true }]
  const shrine = createEmptyScene('shrine', 'Shrine', 6, 6)
  shrine.transitions = [{ fromX: 4, fromY: 4, toSceneId: 'town', toX: 8, toY: 5 }]
  const guideDialogue = { id: 'dlg-guide', npcId: 'npc-guide', text: 'Welcome, traveler.', options: [{ text: 'Bye' }] }
  const ev = (e: Record<string, unknown>) => ({ active: true, repeatable: false, sceneId: 'town', ...e })
  return labProject('events-lab', [town, shrine], {
    x: 1, y: 5, direction: 'right', money: 50, inventory: [slot('tool-hoe', 1)],
  }, {
    npcs: [
      { id: 'npc-guide', name: 'Guide', x: 1, y: 8, sceneId: 'town', dialogue: [guideDialogue], canMove: false, appearance: 'farmer' },
      { id: 'npc-ghost', name: 'Ghost', x: 3, y: 3, sceneId: 'shrine', dialogue: [], canMove: false, appearance: 'farmer' },
    ],
    dialogues: [guideDialogue],
    quests: [{
      id: 'quest-shrine', name: 'Shrine Pilgrimage', description: 'Visit the shrine', status: 'not-started',
      objectives: [{ id: 'o', type: 'talk', description: 'Talk to the guide', targetNPCId: 'npc-guide', completed: false, progress: 0 }],
      rewards: { money: 40 },
    }],
    actions: [{
      id: 'action-blessing', name: 'Blessing', description: '', conditions: [], failMessage: '', energyCost: 0,
      outcomes: [{ type: 'modifyEnergy', amount: -10 }, { type: 'waterArea', radius: 1 }, { type: 'modifyEnergy', amount: 5 }, { type: 'message', message: 'Blessed.' }],
    }],
    minigames: [{ id: 'mg-lockpick', name: 'Lockpick', kind: 'timing-bar', config: {}, resultTiers: [{ minScore: 0.5, outcomes: [{ type: 'giveMoney', amount: 20 }] }] }],
    events: [
      ev({ id: 'evt-well', name: 'Well', trigger: 'enter', conditions: [{ type: 'enterTile', x: 2, y: 2 }],
        outcomes: [{ type: 'message', message: 'You found the old well.' }, { type: 'giveMoney', amount: 10 }, { type: 'setFlag', flagName: 'visited-well' }, { type: 'playSound', soundId: 'chime' }] }),
      ev({ id: 'evt-garden', name: 'Garden', trigger: 'enter', repeatable: true,
        conditions: [{ type: 'enterTile', x: 5, y: 5, x2: 6, y2: 6 }, { type: 'flag', flag: 'visited-well', value: true }, { type: 'inventorySpace', itemId: 'gift-flower', quantity: 1 }],
        outcomes: [{ type: 'giveItem', itemId: 'gift-flower', itemQuantity: 1 }] }),
      ev({ id: 'evt-sign', name: 'Sign', trigger: 'interact', conditions: [{ type: 'interactTile', x: 4, y: 1 }, { type: 'hasItem', itemId: 'gift-flower', quantity: 2 }],
        outcomes: [{ type: 'takeItem', itemId: 'gift-flower', itemQuantity: 2 }, { type: 'unlockTransition', sceneId: 'town', x: 9, y: 5 }, { type: 'message', message: 'The shrine gate creaks open.' }] }),
      ev({ id: 'evt-sign-hint', name: 'Sign hint', trigger: 'interact', repeatable: true, conditions: [{ type: 'interactTile', x: 4, y: 1 }, { type: 'flag', flag: 'event:evt-sign:fired', value: false }],
        outcomes: [{ type: 'message', message: 'Bring two flowers.' }] }),
      ev({ id: 'evt-dawn', name: 'Dawn', sceneId: '', trigger: 'tick', conditions: [{ type: 'timeOfDay', minMinute: 400, maxMinute: 410 }],
        outcomes: [{ type: 'removeNPC', npcId: 'npc-ghost' }] }),
      ev({ id: 'evt-ghost', name: 'Ghost', trigger: 'tick', conditions: [{ type: 'timeOfDay', minMinute: 420, maxMinute: 430 }, { type: 'dayRange', minDay: 1, maxDay: 1 }],
        outcomes: [{ type: 'spawnNPC', npcId: 'npc-ghost', sceneId: 'town', x: 6, y: 2 }, { type: 'message', message: 'A ghost appears!' }] }),
      ev({ id: 'evt-fountain', name: 'Fountain', trigger: 'interact', conditions: [{ type: 'interactTile', x: 8, y: 8 }],
        outcomes: [{ type: 'changeTile', tileX: 8, tileY: 8, newTileType: 'water' }, { type: 'startDialogue', npcId: 'npc-guide' }] }),
      ev({ id: 'evt-rain-dance', name: 'Rain dance', trigger: 'enter', conditions: [{ type: 'enterTile', x: 7, y: 6 }], outcomes: [{ type: 'waterArea', radius: 2 }] }),
      ev({ id: 'evt-shrine', name: 'Shrine', sceneId: 'shrine', trigger: 'enter',
        conditions: [{ type: 'enterTile', x: 2, y: 2 }, { type: 'season', seasons: ['spring'] }, { type: 'yearRange', minYear: 1, maxYear: 1 }, { type: 'weather', weatherIds: ['sun'] }],
        outcomes: [{ type: 'startQuest', questId: 'quest-shrine' }, { type: 'performAction', actionId: 'action-blessing' }, { type: 'warpPlayer', sceneId: 'town', x: 1, y: 1 }] }),
      ev({ id: 'evt-quest-done', name: 'Quest done', sceneId: '', trigger: 'tick', conditions: [{ type: 'questStatus', questId: 'quest-shrine', status: 'active' }],
        outcomes: [
          { type: 'completeQuest', questId: 'quest-shrine' }, { type: 'giveItem', itemId: 'fertilizer-basic', itemQuantity: 3 },
          { type: 'modifyFriendship', npcId: 'npc-guide', amount: 50 }, { type: 'lockTransition', sceneId: 'town', x: 9, y: 5 },
          { type: 'clearFlag', flagName: 'visited-well' }, { type: 'takeMoney', amount: 5 },
        ] }),
      ev({ id: 'evt-legacy-unlock', name: 'Legacy unlock', trigger: 'interact', conditions: [{ type: 'interactTile', x: 4, y: 1 }, { type: 'questStatus', questId: 'quest-shrine', status: 'completed' }],
        outcomes: [{ type: 'unlockScene', sceneId: 'shrine' }, { type: 'message', message: 'Legacy unlock.' }] }),
      ev({ id: 'evt-lockbox', name: 'Lockbox', trigger: 'interact', repeatable: true, conditions: [{ type: 'interactTile', x: 0, y: 3 }],
        outcomes: [{ type: 'startMinigame', minigameId: 'mg-lockpick' }] }),
      ev({ id: 'evt-disabled', name: 'Disabled', trigger: 'enter', active: false, conditions: [{ type: 'enterTile', x: 3, y: 3 }], outcomes: [{ type: 'giveMoney', amount: 999 }] }),
    ],
  })
}

scenarios.push({
  name: 'events-showcase',
  description: 'Event runtime: enter/interact/tick triggers, region + flag + inventorySpace + hasItem + dayRange + season + yearRange + weather + questStatus + timeOfDay conditions, fire-once vs repeatable, inactive events, outcomes message/giveMoney/setFlag/playSound/giveItem/takeItem/unlockTransition/lockTransition/removeNPC/spawnNPC/changeTile/startDialogue/waterArea/startQuest/performAction(modifyEnergy)/warpPlayer/completeQuest/modifyFriendship/clearFlag/takeMoney/unlockScene/startMinigame.',
  seed: 'events',
  project: eventsProject,
  script: r => {
    r.approach(4, 1)
    r.cmd({ type: 'interact' }) // hint
    r.tick(1000) // → 6:50, dawn removes the ghost
    r.goTo(2, 2)
    r.goTo(5, 5)
    r.goTo(6, 5)
    r.goTo(6, 6)
    r.approach(4, 1)
    r.cmd({ type: 'interact' }) // takes 2 flowers, unlocks gate
    r.tick(400) // → 7:10, ghost spawns at (6,2)
    r.approach(8, 8)
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'closeDialogue' })
    r.goTo(7, 6)
    r.goTo(8, 5)
    r.cmd({ type: 'move', dir: 'right' }) // → shrine (1,1)
    r.goTo(2, 1)
    r.cmd({ type: 'move', dir: 'down' }) // (2,2): quest + blessing + warp home
    r.tick(20)
    r.goTo(8, 5)
    r.cmd({ type: 'move', dir: 'right' }) // locked again: stays in town
    r.approach(4, 1)
    r.cmd({ type: 'interact' }) // legacy unlockScene
    r.goTo(8, 5)
    r.cmd({ type: 'move', dir: 'right' }) // → shrine
    r.goTo(4, 3)
    r.cmd({ type: 'move', dir: 'down' }) // (4,4) → back to town (8,5)
    r.goTo(3, 3)
    r.goTo(1, 3)
    r.face('left')
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'resolveMinigame', score: 0.75 })
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'cancelMinigame' })
    r.tick(1300)
    r.cmd({ type: 'sleep' })
    r.tick(1300)
  },
  verify: r => {
    for (const text of ['Bring two flowers.', 'You found the old well.', 'Received Flower', 'The shrine gate creaks open.', 'A ghost appears!', 'The surrounding soil is watered.', 'Blessed.', 'New quest: Shrine Pilgrimage', 'Legacy unlock.', 'Paid $5', 'Received Basic Fertilizer x3']) {
      expect(r.hasMessage(text), text).toBe(true)
    }
    expect(r.effects().some(e => e.type === 'sound' && (e as { id: string }).id === 'chime')).toBe(true)
    expect(r.state.player.completedQuests).toContain('quest-shrine')
    expect(r.tileAt(8, 8, 'town')?.type).toBe('water')
    expect(r.state.npcs['npc-ghost']).toEqual({ x: 6, y: 2, sceneId: 'town' })
    expect(r.state.social['npc-guide'].friendship).toBe(50)
    expect(r.state.flags['visited-well']).toBe(false)
    expect(r.state.player.money).toBe(50 + 10 + 40 - 5 + 20)
    expect(r.effects().filter(e => e.type === 'sceneChanged').length).toBeGreaterThanOrEqual(4)
    expect(r.state.world.scenes.find(s => s.id === 'town')!.transitions[0].locked).toBe(false)
  },
})

// 13 ── crafting & machines (starter content) ─────────────────────────────
scenarios.push({
  name: 'crafting-and-machines',
  description: 'Crafting on the Starter Farm catalog: unknown/machine-only/locked/station-gated recipe errors, hand crafts (hay, furnace, kitchen, jar, soup, charm), craft quest objective, skill unlock via plugin grantXp, place/load/settle/collect machines across ticks (furnace, 2400 ticks) and overnight (preserves), wrong-recipe/idle/busy messages, machine collision, magic altar + useItem charm.',
  seed: 'crafting',
  project: () => {
    const project = starterProject()
    project.player.inventory.push(
      ...(['material-stone:30', 'material-wood:30', 'ore-copper:5', 'material-fiber:9', 'gem-quartz:1', 'crop-carrot:2', 'crop-potato:2', 'crop-wheat:3', 'machine-altar:1'].map(entry => {
        const [id, q] = entry.split(':')
        return slot(id, Number(q), project.items)
      })),
    )
    project.quests.push({
      id: 'quest-craft-hay', name: 'Bale Some Hay', description: 'Craft hay once', status: 'active',
      objectives: [{ id: 'o', type: 'craft', description: 'Craft hay', targetItemId: 'recipe-craft-hay', completed: false, progress: 0 }],
      rewards: { money: 15 },
    })
    project.player.activeQuests.push('quest-craft-hay')
    return project
  },
  script: r => {
    r.cmd({ type: 'craft', recipeId: 'recipe-nope' })
    r.cmd({ type: 'craft', recipeId: 'recipe-smelt-copper' })
    r.cmd({ type: 'craft', recipeId: 'recipe-craft-preserves-jar' })
    r.cmd({ type: 'craft', recipeId: 'recipe-cook-veggie-soup' })
    r.cmd({ type: 'craft', recipeId: 'recipe-craft-hay' })
    r.cmd({ type: 'craft', recipeId: 'recipe-craft-furnace' })
    r.cmd({ type: 'craft', recipeId: 'recipe-craft-kitchen' })
    r.cmd({ type: 'placeMachine', machineTypeId: 'machine-preserves' })
    r.cmd({ type: 'placeMachine', machineTypeId: 'machine-unknown' })
    r.face('down')
    r.cmd({ type: 'placeMachine', machineTypeId: 'machine-furnace' })
    r.cmd({ type: 'placeMachine', machineTypeId: 'machine-furnace' })
    r.cmd({ type: 'machineLoad', recipeId: 'recipe-smelt-copper' })
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'machineLoad', recipeId: 'recipe-smelt-copper' })
    r.cmd({ type: 'move', dir: 'down' })
    r.face('left')
    r.cmd({ type: 'placeMachine', machineTypeId: 'machine-kitchen' })
    r.cmd({ type: 'craft', recipeId: 'recipe-cook-veggie-soup' })
    r.cmd({ type: 'machineLoad', recipeId: 'recipe-smelt-copper' })
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'pluginMutation', pluginId: 'golden:xp', mutation: { type: 'grantXp', skill: 'farming', amount: 60 } })
    r.cmd({ type: 'craft', recipeId: 'recipe-craft-preserves-jar' })
    r.face('right')
    r.cmd({ type: 'placeMachine', machineTypeId: 'machine-preserves' })
    r.cmd({ type: 'machineLoad', recipeId: 'recipe-preserve-wheat' })
    r.face('down')
    r.tick(2400)
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'machineLoad', recipeId: 'recipe-smelt-copper' })
    r.face('right')
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'machineLoad', recipeId: 'recipe-preserve-wheat' })
    r.cmd({ type: 'sleep' })
    r.cmd({ type: 'interact' })
    r.face('up')
    r.cmd({ type: 'placeMachine', machineTypeId: 'machine-altar' })
    r.cmd({ type: 'craft', recipeId: 'recipe-growth-charm' })
    r.cmd({ type: 'useItem', itemId: 'charm-growth' })
    r.cmd({ type: 'useItem', itemId: 'charm-growth' })
    r.face('left')
    r.cmd({ type: 'machineLoad', recipeId: 'recipe-craft-hay' })
    r.tick(1300)
  },
  verify: r => {
    for (const text of [
      'Unknown recipe.', 'That recipe needs a machine — load it there.', 'Recipe not unlocked yet.',
      'You need to be near a Kitchen to craft that.', 'Crafted 2x Hay', 'Crafted 1x Furnace', 'Crafted 1x Kitchen',
      'You need a Preserves Jar in your inventory.', 'Unknown machine.', 'Placed Furnace', 'You need a Furnace in your inventory.',
      'Started Copper Bar', 'Still working…', 'It is already working.', 'Crafted 1x Veggie Soup',
      'This machine cannot run that recipe.', 'Kitchen is idle — load a recipe.', 'Farming level 1!', 'Crafted 1x Preserves Jar',
      'Started Wheat Preserves', 'Crafted 1x Copper Bar', 'Missing ingredients.', 'Crafted 1x Preserves',
      "Placed Enchanter's Altar", 'Crafted 1x Growth Charm', 'A warm green light washes over the farm…', "You don't have that item.",
    ]) {
      expect(r.hasMessage(text), text).toBe(true)
    }
    expect(r.state.player.completedQuests).toContain('quest-craft-hay')
    expect(r.qty('bar-copper')).toBe(1)
    expect(r.qty('food-preserves')).toBe(1)
    expect(r.qty('food-veggie-soup')).toBe(1)
    expect(r.state.flags['growth-blessing-today']).toBe(true)
    expect(r.state.player.y).toBe(9.5)
  },
})

// 14 ── weather over a long run (starter farm, default table) ─────────────
scenarios.push({
  name: 'weather-seasons-long-run',
  description: 'Starter Farm with the default 4-type weather table over 120 days: rain auto-watering, storm crop damage + NPCs staying inside, snow, all four season rollovers and a year rollover, daily water/harvest/replant with a tier-2 can and scythe, a scheduled NPC (walks on clear days, stays in during storms), 1400+1300-tick runs every 7th day.',
  seed: 'weather-long',
  project: () => {
    const project = starterProject()
    project.player.inventory = project.player.inventory.map(entry =>
      entry.item.id === 'tool-watering-can' ? { item: itemDef('tool-watering-can-2', project.items), quantity: 1 } : entry)
    project.player.inventory.push(slot('tool-scythe', 1, project.items), slot('seed-carrot', 30, project.items))
    project.npcs.push({
      id: 'npc-sam', name: 'Sam', x: 10, y: 10, sceneId: 'scene-farm', dialogue: [], canMove: false, appearance: 'farmer',
      schedule: [{ minute: 420, sceneId: 'scene-farm', x: 13, y: 7 }, { minute: 460, sceneId: 'scene-farm', x: 10, y: 10 }],
    })
    return project
  },
  script: r => {
    for (const x of [6, 8]) {
      r.goTo(x, 9)
      r.face('up')
      r.cmd({ type: 'interact' })
    }
    r.goTo(7, 9)
    r.face('up')
    r.cmd({ type: 'interact' })
    for (let day = 0; day < 120; day++) {
      const crop = r.tileAt(7, 8)?.crop
      if (crop?.withered) r.cmd({ type: 'useTool', tool: 'scythe' })
      else if (crop) {
        const def = r.ctx.content.crops[crop.type]
        if ((crop.daysGrown ?? 0) >= (def.growthDays ?? 1)) r.cmd({ type: 'interact' })
      }
      if (!r.tileAt(7, 8)?.crop) r.cmd({ type: 'interact' })
      r.cmd({ type: 'useTool', tool: 'watering-can' })
      if (day % 7 === 3) {
        r.tick(1400) // → 7:10: Sam's 7:00 entry walks him to (13,7) unless a storm keeps him home
        r.notes.sam = [...(r.notes.sam ?? []), { weather: r.state.clock.weatherId, ...r.state.npcs['npc-sam'] }]
        r.tick(1300) // → 8:15: heading home again
      }
      r.cmd({ type: 'sleep' })
    }
  },
  verify: r => {
    const weathers = new Set(r.messages().filter(t => t.startsWith('Weather: ')))
    expect(weathers).toEqual(new Set(['Weather: Sunny', 'Weather: Rain', 'Weather: Storm', 'Weather: Snow']))
    for (const text of ['Summer has arrived!', 'Fall has arrived!', 'Winter has arrived!', 'Spring has arrived!', 'Year 2 begins!']) {
      expect(r.hasMessage(text), text).toBe(true)
    }
    expect(r.state.clock.year).toBe(2)
    expect(r.harvested()).toBeGreaterThan(5)
    expect(r.hasMessage('Cleared the withered crop.')).toBe(true)
    // Sam walks his schedule on non-storm days and stays home during storms.
    const sam = r.notes.sam as Array<{ weather: string; x: number; y: number }>
    expect(sam.some(s => s.weather !== 'storm' && s.x === 13 && s.y === 7)).toBe(true)
    for (const s of sam.filter(s => s.weather === 'storm')) expect([s.x, s.y]).toEqual([10, 10])
  },
})

// 15 ── animals ───────────────────────────────────────────────────────────
scenarios.push({
  name: 'animals-ranch',
  description: 'Ranching: feed → collect → pet priority, "content" once done, eggs (interval 1) and milk (interval 2, cow grows to adult), a neglected day (mood −15, no product), a low-mood chicken that never produces, an unknown-species animal that is ignored.',
  seed: 'ranch',
  project: () => labProject('ranch-lab', [createEmptyScene('ranch', 'Ranch', 9, 6)], {
    x: 1, y: 3, direction: 'right', inventory: [slot('feed-hay', 14)],
  }, {
    animalSpecies: createDefaultAnimalSpecies(),
    animals: [
      { id: 'animal-1', speciesId: 'animal-chicken', name: 'Clucky', sceneId: 'ranch', x: 2, y: 2, mood: 70, fedToday: false, pettedToday: false, ageDays: 5, daysSinceProduct: 0, productReady: false },
      { id: 'animal-2', speciesId: 'animal-cow', name: 'Bessie', sceneId: 'ranch', x: 4, y: 2, mood: 60, fedToday: false, pettedToday: false, ageDays: 3, daysSinceProduct: 0, productReady: false },
      { id: 'animal-3', speciesId: 'animal-chicken', name: 'Grumpy', sceneId: 'ranch', x: 6, y: 2, mood: 10, fedToday: false, pettedToday: false, ageDays: 9, daysSinceProduct: 0, productReady: false },
      { id: 'animal-4', speciesId: 'animal-dragon', name: 'Ghosty', sceneId: 'ranch', x: 7, y: 4, mood: 50, fedToday: false, pettedToday: false, ageDays: 1, daysSinceProduct: 0, productReady: false },
    ],
  }),
  script: r => {
    for (let day = 0; day < 8; day++) {
      if (day !== 3) {
        for (const x of [2, 4, 6]) {
          if (day === 5 && x === 6) continue
          r.goTo(x, 3)
          r.face('up')
          for (let i = 0; i < 3; i++) r.cmd({ type: 'interact' })
        }
      }
      if (day === 1) {
        r.approach(7, 4)
        r.cmd({ type: 'interact' })
        r.tick(1300)
      }
      r.cmd({ type: 'sleep' })
    }
  },
  verify: r => {
    expect(r.hasMessage('Fed Clucky')).toBe(true)
    expect(r.hasMessage('Clucky looks happy! ♥')).toBe(true)
    expect(r.hasMessage('Clucky is content.')).toBe(true)
    expect(r.hasMessage('Collected Egg from Clucky')).toBe(true)
    expect(r.hasMessage('Collected Milk from Bessie')).toBe(true)
    expect(r.qty('product-egg')).toBeGreaterThanOrEqual(3)
    expect(r.qty('product-milk')).toBeGreaterThanOrEqual(1)
    // Grumpy starts at mood 10: the mood ≥ 30 gate delays its first egg.
    const firstEgg = (name: string) => r.steps.findIndex(s => s.effects.some(e => e.type === 'message' && e.text === `Collected Egg from ${name}`))
    expect(firstEgg('Clucky')).toBeGreaterThan(0)
    expect(firstEgg('Grumpy')).toBeGreaterThan(firstEgg('Clucky'))
    expect(r.hasMessage('Fed Grumpy')).toBe(true)
    const ghost = r.state.animals.find(a => a.id === 'animal-4')!
    expect(ghost.ageDays).toBe(1)
    expect(r.state.animals.find(a => a.id === 'animal-2')!.ageDays).toBe(11)
  },
})

// 16 ── fishing (instant path) ────────────────────────────────────────────
function fishingProject(minigame: boolean): GameProject {
  const lake = createEmptyScene('lake', 'Lake', 8, 8)
  for (let y = 0; y <= 2; y++) for (let x = 0; x < 8; x++) paint(lake, x, y, 'water')
  lake.transitions = [{ fromX: 7, fromY: 6, toSceneId: 'puddle', toX: 1, toY: 1 }]
  const puddle = createEmptyScene('puddle', 'Puddle', 4, 4)
  paint(puddle, 2, 1, 'water')
  const proRod: Item = {
    id: 'tool-rod-pro', name: 'Pro Rod', description: 'A tier-3 rod', type: 'tool', stackable: false, maxStack: 1, value: 300,
    toolType: 'fishing-rod', toolTier: 3, durability: 50, maxDurability: 50,
  }
  return labProject(minigame ? 'fishing-minigame-lab' : 'fishing-lab', [lake, puddle], {
    x: 3, y: 3, direction: 'up', inventory: [minigame ? { item: proRod, quantity: 1 } : slot('tool-fishing-rod', 1)],
    activeQuests: ['quest-catfish'],
  }, {
    items: [...createDefaultItems(), proRod],
    fishTables: [
      { id: 'ft-empty', name: 'Empty', entries: [], junkChance: 0 },
      {
        id: 'ft-lake', name: 'Lake', sceneIds: ['lake'], seasons: ['spring'], junkChance: 0.2, junkItemId: 'junk-boot',
        entries: [
          { itemId: 'fish-carp', weight: 6, difficulty: 0.15 },
          { itemId: 'fish-perch', weight: 3, difficulty: 0.5 },
          { itemId: 'fish-catfish', weight: 2, difficulty: 0.9 },
        ],
      },
    ],
    quests: [{
      id: 'quest-catfish', name: 'Whiskers', description: 'Catch a catfish', status: 'active',
      objectives: [{ id: 'o', type: 'collect', description: 'Catfish', targetItemId: 'fish-catfish', targetItemQuantity: 1, completed: false, progress: 0 }],
      rewards: { money: 60 },
    }],
    minigames: minigame
      ? [{ id: 'fishing', name: 'Fishing', kind: 'timing-bar', config: { speed: 0.9, targetSize: 0.2 }, resultTiers: [{ minScore: 0.95, outcomes: [{ type: 'message', message: 'Perfect cast!' }] }] }]
      : [],
  })
}

scenarios.push({
  name: 'fishing-instant',
  description: 'Fishing without a fishing minigame: scene/season-filtered tables (empty table skipped), junk rolls, weighted fish, escape rolls, fishing XP, collect-quest completion, 3 energy per cast until exhaustion collapse, and a scene with no table ("The water is quiet").',
  seed: 'fishing',
  project: () => fishingProject(false),
  script: r => {
    for (let i = 0; i < 40; i++) r.cmd({ type: 'useTool', tool: 'fishing-rod' })
    r.tick(1300)
    r.goTo(7, 5)
    r.cmd({ type: 'move', dir: 'down' })
    r.face('right')
    r.cmd({ type: 'useTool', tool: 'fishing-rod' })
  },
  verify: r => {
    expect(r.hasMessage(/^Caught a /)).toBe(true)
    expect(r.hasMessage('You fished up Old Boot…')).toBe(true)
    expect(r.hasMessage('It got away!')).toBe(true)
    expect(r.hasMessage('You collapsed from exhaustion!')).toBe(true)
    expect(r.hasMessage('The water is quiet — nothing seems to live here.')).toBe(true)
    expect(r.state.player.skills.fishing?.xp ?? 0).toBeGreaterThan(0)
    expect(r.state.player.sceneId).toBe('puddle')
  },
})

// 17 ── fishing via the minigame binding ──────────────────────────────────
scenarios.push({
  name: 'fishing-minigame',
  description: 'Fishing with a declared "fishing" minigame and a tier-3 rod: cast opens a session (movement frozen while open), scores 1/0/0.5/out-of-range resolve the pending catch + score tiers, cancel, casting again while a session is open (instant path), resolve with no session.',
  seed: 'fishing-mg',
  project: () => fishingProject(true),
  script: r => {
    r.cmd({ type: 'resolveMinigame', score: 1 })
    r.cmd({ type: 'useTool', tool: 'fishing-rod' })
    r.cmd({ type: 'setMoveIntent', dx: 1, dy: 0 })
    r.tick(20)
    r.cmd({ type: 'resolveMinigame', score: 1 })
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    r.face('up')
    for (const score of [0, 0.5, 7, -3, 0.96, 0.2, 1, 0.8]) {
      r.cmd({ type: 'useTool', tool: 'fishing-rod' })
      r.cmd({ type: 'resolveMinigame', score })
    }
    r.cmd({ type: 'useTool', tool: 'fishing-rod' })
    r.cmd({ type: 'cancelMinigame' })
    r.cmd({ type: 'useTool', tool: 'fishing-rod' })
    r.cmd({ type: 'useTool', tool: 'fishing-rod' })
    r.cmd({ type: 'resolveMinigame', score: 1 })
    r.tick(1300)
  },
  verify: r => {
    expect(r.hasMessage('Perfect cast!')).toBe(true)
    expect(r.countMessages('Caught a ')).toBeGreaterThanOrEqual(3)
    expect(r.state.minigame).toBeNull()
    // Movement was frozen while the session was open.
    const frozen = r.steps.find(s => s.input.kind === 'tick' && s.input.ticks === 20)!
    expect(frozen.effects).toEqual([])
    expect(r.state.player.x).toBe(3.5)
  },
})

// 18 ── mines ─────────────────────────────────────────────────────────────
scenarios.push({
  name: 'mines-descent',
  description: 'Mining: entrance interact, procedurally generated floors (seeded per engineSeed+floor), pickaxe strikes on band rocks with drops, iron needing tier 2, ladder reveals and stepping onto ladders to descend, elevator checkpoints, descendMine clamping, exit by interacting with the entry tile and via exitMine (generated floors dropped).',
  seed: 'mines',
  project: () => labProject('mine-lab', [createEmptyScene('surface', 'Surface', 8, 8)], {
    x: 5, y: 4, direction: 'down', energy: 3000, maxEnergy: 3000, maxInventorySize: 30,
    inventory: [slot('tool-pickaxe', 1)],
  }, {
    mine: MineConfigSchema.parse({
      enabled: true, entranceSceneId: 'surface', entranceX: 5, entranceY: 5, floors: 12,
      floorWidth: 10, floorHeight: 8, bands: createDefaultMineBands(), ladderChance: 0.35, elevatorEvery: 3,
    }),
  }),
  script: r => {
    r.cmd({ type: 'interact' }) // entrance → floor 1
    let budget = 400
    const mineFloor = () => {
      const startFloor = r.state.mine.currentFloor
      while (budget > 0 && r.state.mine.currentFloor === startFloor && r.state.player.sceneId.startsWith('mine-floor-')) {
        const scene = r.scene
        // Walk to a revealed ladder if one is reachable.
        let ladder: { x: number; y: number } | null = null
        for (let y = 0; y < scene.height && !ladder; y++) for (let x = 0; x < scene.width; x++) {
          if (scene.tiles[y][x].ladderDown && r.canReach(x, y)) { ladder = { x, y }; break }
        }
        if (ladder) { r.stepOnto(ladder.x, ladder.y); budget--; continue }
        // Otherwise break the nearest breakable rock.
        let target: { x: number; y: number } | null = null
        for (let y = 1; y < scene.height - 1 && !target; y++) for (let x = 1; x < scene.width - 1; x++) {
          const node = scene.tiles[y][x].node
          if (!node || node.remainingHealth <= 0 || node.typeId === 'node-iron-ore') continue
          if ([[0, 1], [0, -1], [1, 0], [-1, 0]].some(([dx, dy]) => r.canReach(x + dx, y + dy))) { target = { x, y }; break }
        }
        if (!target) break
        r.approach(target.x, target.y)
        for (let i = 0; i < 5 && (r.tileAt(target.x, target.y)?.node?.remainingHealth ?? 0) > 0; i++) {
          r.cmd({ type: 'useTool', tool: 'pickaxe' })
          budget--
        }
      }
    }
    for (let i = 0; i < 10 && r.state.mine.deepestFloor < 6; i++) mineFloor()
    r.cmd({ type: 'exitMine' }) // lands on the entrance tile itself
    r.approach(5, 5)
    r.cmd({ type: 'interact' }) // elevator checkpoint (floor 3)
    // Climb out via the entry tile (1,1) when reachable, else exitMine.
    if (r.canReach(2, 1)) { r.goTo(2, 1); r.face('left'); r.cmd({ type: 'interact' }) }
    else if (r.canReach(1, 2)) { r.goTo(1, 2); r.face('up'); r.cmd({ type: 'interact' }) }
    else r.cmd({ type: 'exitMine' })
    r.cmd({ type: 'descendMine', floor: 99 })
    // Floor 12 is in the iron band: a tier-1 pickaxe can't break iron.
    const reachableIron = () => r.scene.tiles.flat().find(t => t.node?.typeId === 'node-iron-ore' && t.node.remainingHealth > 0 && [[0, 1], [0, -1], [1, 0], [-1, 0]].some(([dx, dy]) => r.canReach(t.x + dx, t.y + dy)))
    for (let i = 0; i < 6 && !reachableIron(); i++) {
      const rock = r.scene.tiles.flat().find(t => t.node && t.node.remainingHealth > 0 && t.node.typeId !== 'node-iron-ore' && [[0, 1], [0, -1], [1, 0], [-1, 0]].some(([dx, dy]) => r.canReach(t.x + dx, t.y + dy)))
      if (!rock) break
      r.approach(rock.x, rock.y)
      for (let k = 0; k < 5 && (r.tileAt(rock.x, rock.y)?.node?.remainingHealth ?? 0) > 0; k++) r.cmd({ type: 'useTool', tool: 'pickaxe' })
    }
    const iron = reachableIron()
    if (iron) { r.approach(iron.x, iron.y); r.cmd({ type: 'useTool', tool: 'pickaxe' }) }
    r.tick(1300)
    r.cmd({ type: 'exitMine' })
    r.cmd({ type: 'sleep' })
  },
  verify: r => {
    expect(r.hasMessage('Mine — floor 1')).toBe(true)
    expect(r.hasMessage('A ladder to the next floor appears!')).toBe(true)
    expect(r.state.mine.deepestFloor).toBe(12)
    expect(r.hasMessage('(elevator checkpoint)')).toBe(true)
    expect(r.hasMessage("Your pickaxe isn't strong enough for iron node.")).toBe(true)
    expect(r.countMessages('A ladder to the next floor appears!')).toBeGreaterThanOrEqual(4)
    expect(r.countMessages('You climb back to the surface.')).toBeGreaterThanOrEqual(3)
    expect(r.qty('material-stone') + r.qty('ore-copper') + r.qty('gem-quartz')).toBeGreaterThan(3)
    expect(r.state.world.scenes.every(s => !s.id.startsWith('mine-floor-'))).toBe(true)
    expect(r.state.player.sceneId).toBe('surface')
  },
})

// 19 ── calendar, festivals, custom time ──────────────────────────────────
function calendarProject(): GameProject {
  const farm = createEmptyScene('farm', 'Farm', 8, 6)
  paint(farm, 3, 3, 'soil')
  const snowpea = {
    id: 'snowpea', name: 'Snow Pea', seedCost: 5, baseHarvestValue: 15, growthTime: 10000, growthDays: 2, stages: 3,
    seasons: ['frost', 'thaw'], canRegrow: false, yieldMin: 1, yieldMax: 3, mutationChance: 0.2,
  }
  const items = [
    ...createDefaultItems(),
    { id: 'seed-snowpea', name: 'Snow Pea Seeds', description: 'Cold-hardy', type: 'seed' as const, stackable: true, maxStack: 99, value: 5, cropType: 'snowpea' },
    { id: 'crop-snowpea', name: 'Snow Pea', description: 'Crisp', type: 'crop' as const, stackable: true, maxStack: 99, value: 15, cropType: 'snowpea' },
  ]
  return labProject('calendar-lab', [farm], {
    x: 3, y: 4, direction: 'up', money: 200,
    inventory: [slot('seed-snowpea', 20, items), slot('tool-watering-can', 1), slot('tool-scythe', 1)],
  }, {
    items,
    customCrops: [snowpea],
    currentSeason: 'thaw',
    currentTimeMinutes: 300,
    settings: {
      ...DEFAULT_PROJECT_SETTINGS,
      maxEnergy: 60,
      collapseEnergyFraction: 0.25,
      collapseMoneyPenalty: 25,
      time: { dayStartMinute: 300, dayEndMinute: 1200, minutesPerRealSecond: 30 },
      calendar: {
        seasons: [{ id: 'thaw', name: 'Thaw', days: 5 }, { id: 'bloom', name: 'Bloom', days: 4 }, { id: 'frost', name: 'Frost', days: 3 }],
        festivals: [
          { id: 'lantern', name: 'Lantern Night', seasonId: 'bloom', day: 2 },
          { id: 'first-light', name: 'First Light', seasonId: 'thaw', day: 1 },
        ],
      },
    },
    weather: WeatherConfigSchema.parse({
      types: [{ id: 'sun', name: 'Sunny' }, { id: 'rain', name: 'Rain', watersOutdoorSoil: true }, { id: 'snow', name: 'Snow' }],
      table: { bloom: [], thaw: [{ weatherId: 'sun', weight: 1 }, { weatherId: 'rain', weight: 1 }], spring: [{ weatherId: 'snow', weight: 1 }] },
    }),
    quests: [{
      id: 'quest-bloom', name: 'Bloom Harvest', description: 'Harvest snow peas', status: 'not-started', availableSeasons: ['bloom'],
      objectives: [{ id: 'o', type: 'harvest', description: 'Snow peas', targetCropType: 'snowpea', targetCropQuantity: 2, completed: false, progress: 0 }],
      rewards: { money: 70 },
    }],
    events: [
      { id: 'evt-lantern', name: 'Lanterns', sceneId: 'farm', trigger: 'interact', active: true, repeatable: true,
        conditions: [{ type: 'interactTile', x: 5, y: 1 }, { type: 'festivalId', festivalId: 'lantern' }],
        outcomes: [{ type: 'giveMoney', amount: 5 }, { type: 'message', message: 'The lanterns glow.' }] },
      { id: 'evt-quest-board', name: 'Quest board', sceneId: 'farm', trigger: 'interact', active: true, repeatable: true,
        conditions: [{ type: 'interactTile', x: 6, y: 1 }],
        outcomes: [{ type: 'startQuest', questId: 'quest-bloom' }] },
      { id: 'evt-frost', name: 'Frost', sceneId: '', trigger: 'tick', active: true, repeatable: false,
        conditions: [{ type: 'season', seasons: ['frost'] }],
        outcomes: [{ type: 'message', message: 'Frost settles in.' }] },
    ],
  })
}

scenarios.push({
  name: 'calendar-festivals',
  description: 'Custom calendar (Thaw 5 / Bloom 4 / Frost 3 days) over 2+ years: season/year rollovers, festival announcements and a festivalId-gated event, custom-season crop withering, season-gated quest availability, weather-table fallback for seasons without a table, custom time (300→1200 at 30 min/s, 2-minute boundary jumps) with day-end collapses (25$ penalty, 25% energy), settings.maxEnergy.',
  seed: 'calendar',
  project: calendarProject,
  script: r => {
    for (let day = 0; day < 27; day++) {
      r.goTo(3, 4)
      r.face('up')
      const crop = r.tileAt(3, 3)?.crop
      if (crop?.withered) r.cmd({ type: 'useTool', tool: 'scythe' })
      else if (crop && (crop.daysGrown ?? 0) >= 2) r.cmd({ type: 'interact' })
      if (!r.tileAt(3, 3)?.crop) r.cmd({ type: 'interact' })
      r.cmd({ type: 'useTool', tool: 'watering-can' })
      r.approach(5, 1)
      r.cmd({ type: 'interact' })
      r.approach(6, 1)
      r.cmd({ type: 'interact' })
      if (day % 4 === 1) r.tick(700) // stay up past 20:00 → collapse
      else {
        r.tick(40)
        r.cmd({ type: 'sleep' })
      }
    }
  },
  verify: r => {
    for (const text of ['Bloom has arrived!', 'Frost has arrived!', 'Thaw has arrived!', 'Year 2 begins!', 'Year 3 begins!', 'Today is the Lantern Night!', 'Today is the First Light!', 'The lanterns glow.', 'Frost settles in.', 'New quest: Bloom Harvest', 'You collapsed from exhaustion! Lost $25.', 'Snow Pea cannot grow in bloom!', 'Weather: Rain']) {
      expect(r.hasMessage(text), text).toBe(true)
    }
    expect(r.hasMessage('Day 5 of thaw, Year 1')).toBe(true)
    expect(r.harvested('snowpea')).toBeGreaterThan(0)
    expect(r.state.player.maxEnergy).toBe(60)
    expect(r.state.clock.year).toBe(3)
    expect(r.hasMessage('Weather: Snow')).toBe(false)
  },
})

// 20 ── extensibility: actions, useItem, minigames ────────────────────────
scenarios.push({
  name: 'actions-items-minigames',
  description: 'Creator actions (unknown, outcomes, gated with failMessage, energy cost, recursion depth cap, exhaustion collapse aborting outcomes), dialogue-option actionId, useItem (consume on success only, non-consumable, not usable, not held), minigames (start, frozen movement, tiered resolve incl. clamping, no-tier resolve, unknown, cancel, resolve without session, start while open).',
  seed: 'extensibility',
  project: () => {
    const items = [
      ...createDefaultItems(),
      { id: 'item-snack', name: 'Snack', description: '', type: 'material' as const, stackable: true, maxStack: 10, value: 1, useActionId: 'action-snack', consumeOnUse: true },
      { id: 'item-lamp', name: 'Lamp', description: '', type: 'material' as const, stackable: true, maxStack: 10, value: 1, useActionId: 'action-gated', consumeOnUse: true },
      { id: 'item-orb', name: 'Orb', description: '', type: 'material' as const, stackable: true, maxStack: 1, value: 1, useActionId: 'action-orb', consumeOnUse: false },
    ]
    const tinkerDialogue = { id: 'dlg-tinker', npcId: 'npc-tinker', text: 'Need oil?', options: [{ text: 'Yes please', actionId: 'action-oil' }, { text: 'No' }] }
    return labProject('ext-lab', [createEmptyScene('lab', 'Lab', 6, 6)], {
      x: 3, y: 3, direction: 'up',
      inventory: [slot('item-snack', 2, items), slot('item-lamp', 2, items), slot('item-orb', 1, items), slot('seed-wheat', 1)],
    }, {
      items,
      npcs: [{ id: 'npc-tinker', name: 'Tinker', x: 3, y: 2, sceneId: 'lab', dialogue: [tinkerDialogue], canMove: false, appearance: 'merchant' }],
      dialogues: [tinkerDialogue],
      actions: [
        { id: 'action-snack', name: 'Snack', description: '', conditions: [], failMessage: '', energyCost: 0,
          outcomes: [{ type: 'giveMoney', amount: 5 }, { type: 'setFlag', flagName: 'snacked' }, { type: 'modifyEnergy', amount: 20 }] },
        { id: 'action-gated', name: 'Lamp', description: '', conditions: [{ type: 'flag', flag: 'lamp-oil', value: true }], failMessage: 'The lamp is dry.', energyCost: 0,
          outcomes: [{ type: 'message', message: 'The lamp glows.' }] },
        { id: 'action-tiring', name: 'Push-ups', description: '', conditions: [], failMessage: '', energyCost: 30, outcomes: [{ type: 'message', message: 'Phew.' }] },
        { id: 'action-loop', name: 'Loop', description: '', conditions: [], failMessage: '', energyCost: 0,
          outcomes: [{ type: 'giveMoney', amount: 1 }, { type: 'performAction', actionId: 'action-loop' }] },
        { id: 'action-orb', name: 'Orb', description: '', conditions: [], failMessage: '', energyCost: 0, outcomes: [{ type: 'startMinigame', minigameId: 'mg-orb' }] },
        { id: 'action-oil', name: 'Oil', description: '', conditions: [], failMessage: '', energyCost: 0, hotkey: 'o',
          outcomes: [{ type: 'setFlag', flagName: 'lamp-oil' }, { type: 'playSound', soundId: 'drip' }] },
        { id: 'action-exhaust', name: 'Marathon', description: '', conditions: [], failMessage: '', energyCost: 500,
          outcomes: [{ type: 'giveMoney', amount: 1000 }] },
      ],
      minigames: [
        { id: 'mg-orb', name: 'Orb', kind: 'orb-dance', config: { difficulty: 3, label: 'Dance' }, resultTiers: [
          { minScore: 0, outcomes: [{ type: 'message', message: 'The orb is silent.' }] },
          { minScore: 0.8, outcomes: [{ type: 'giveMoney', amount: 100 }, { type: 'message', message: 'The orb sings!' }] },
          { minScore: 0.3, outcomes: [{ type: 'giveItem', itemId: 'gift-flower', itemQuantity: 1 }] },
        ] },
        { id: 'mg-empty', name: 'Empty', kind: 'timing-bar', config: {}, resultTiers: [] },
      ],
    })
  },
  script: r => {
    r.cmd({ type: 'performAction', actionId: 'nope' })
    r.cmd({ type: 'performAction', actionId: 'action-tiring' })
    r.cmd({ type: 'performAction', actionId: 'action-snack' })
    r.cmd({ type: 'performAction', actionId: 'action-gated' })
    r.cmd({ type: 'useItem', itemId: 'item-lamp' })
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'chooseDialogueOption', index: 0 })
    r.cmd({ type: 'useItem', itemId: 'item-lamp' })
    r.cmd({ type: 'useItem', itemId: 'item-snack' })
    r.cmd({ type: 'performAction', actionId: 'action-tiring' })
    r.cmd({ type: 'performAction', actionId: 'action-loop' })
    r.cmd({ type: 'useItem', itemId: 'item-orb' })
    r.cmd({ type: 'startMinigame', minigameId: 'mg-empty' })
    r.cmd({ type: 'setMoveIntent', dx: -1, dy: 0 })
    r.tick(40)
    r.cmd({ type: 'resolveMinigame', score: 0.85 })
    r.cmd({ type: 'useItem', itemId: 'item-orb' })
    r.cmd({ type: 'resolveMinigame', score: 0.5 })
    r.cmd({ type: 'useItem', itemId: 'item-orb' })
    r.cmd({ type: 'resolveMinigame', score: -2 })
    r.cmd({ type: 'useItem', itemId: 'item-orb' })
    r.cmd({ type: 'resolveMinigame', score: 42 })
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    r.cmd({ type: 'startMinigame', minigameId: 'mg-empty' })
    r.cmd({ type: 'resolveMinigame', score: 1 })
    r.cmd({ type: 'startMinigame', minigameId: 'mg-missing' })
    r.cmd({ type: 'startMinigame', minigameId: 'mg-orb' })
    r.cmd({ type: 'cancelMinigame' })
    r.cmd({ type: 'cancelMinigame' })
    r.cmd({ type: 'resolveMinigame', score: 1 })
    r.cmd({ type: 'useItem', itemId: 'seed-wheat' })
    r.cmd({ type: 'useItem', itemId: 'item-unknown' })
    r.cmd({ type: 'performAction', actionId: 'action-exhaust' })
    r.tick(1300)
  },
  verify: r => {
    for (const text of ["Unknown action 'nope'", 'Phew.', 'Received $5', 'The lamp is dry.', 'The lamp glows.', 'The orb sings!', 'Received Flower', 'The orb is silent.', "Unknown minigame 'mg-missing'", "Wheat Seeds can't be used like that.", "You don't have that item.", 'You collapsed from exhaustion!']) {
      expect(r.hasMessage(text), text).toBe(true)
    }
    expect(r.qty('item-lamp')).toBe(1)
    expect(r.qty('item-snack')).toBe(1)
    expect(r.qty('item-orb')).toBe(1)
    expect(r.state.flags['lamp-oil']).toBe(true)
    expect(r.effects().some(e => e.type === 'sound' && (e as { id: string }).id === 'drip')).toBe(true)
    expect(r.hasMessage('Received $1000')).toBe(false)
    const frozen = r.steps.find(s => s.input.kind === 'tick' && s.input.ticks === 40)!
    expect(frozen.effects).toEqual([])
  },
})

// 21 ── content packs + plugin mutations ──────────────────────────────────
function makePack(raw: Record<string, unknown>) {
  const result = validateContentPack(raw)
  if (!result.ok) throw new Error(result.errors.join('\n'))
  return result.pack!
}

function packsProject(): GameProject {
  const project = starterProject()
  const demoMod = makePack(JSON.parse(readFileSync(path.join(ROOT, 'docs/modding/examples/demo-mod.json'), 'utf8')))
  const baguette = { id: 'baguette', name: 'Baguette', description: 'Crusty', type: 'crop', stackable: true, maxStack: 99, value: 15 }
  const langPack = makePack({
    manifest: { id: 'lang-pack', name: 'Language Pack', version: '2.1.0' },
    content: {
      items: [baguette],
      quests: [{
        id: 'bonus', name: 'Bonus Bread', description: 'Bake bread', status: 'not-started',
        objectives: [{ id: 'o', type: 'collect', description: 'Baguette', targetItemId: 'baguette', targetItemQuantity: 1, completed: false, progress: 0 }],
        rewards: { money: 5 },
      }],
      strings: {
        fr: {
          'quest:bonus:name': 'Quête bonus',
          'item:crop-wheat:name': 'Blé',
          'item:crop-wheat:description': 'Du blé frais',
          'item:baguette:name': 'Baguette française',
          'dialogue:dialogue-farmer-greeting:text': 'Bienvenue à la ferme !',
          'quest:quest-first-harvest:name': 'Première récolte',
          'quest:quest-first-harvest:description': 'Récoltez votre première culture.',
          'npc:npc-farmer:name': 'Vieux fermier',
        },
        de: { 'item:crop-wheat:name': 'Weizen' },
      },
    },
  })
  const addon = makePack({
    manifest: { id: 'addon', name: 'Addon', version: '0.1.0', dependencies: [{ packId: 'lang-pack' }], engineCompatibility: '>=0.4.0' },
    content: {
      items: [{ id: 'jam', name: 'Jam', description: 'Sweet', type: 'crop', stackable: true, maxStack: 99, value: 40 }],
      recipes: [{ id: 'jam-recipe', name: 'Jam', inputs: [{ itemId: 'lang-pack:baguette', quantity: 1 }], outputs: [{ itemId: 'jam', quantity: 1 }], processingMinutes: 0, category: 'cooking' }],
    },
  })
  const rebalance = makePack({
    manifest: { id: 'rebalance', name: 'Rebalance', version: '1.0.0', base: true, overrides: ['crop-wheat'] },
    content: {
      items: [
        { ...itemDef('crop-wheat'), value: 99 },
        { ...itemDef('crop-carrot'), value: 1 },
      ],
    },
  })
  const retired = makePack({
    manifest: { id: 'retired', name: 'Retired', version: '0.0.1' },
    content: { items: [{ id: 'old-coin', name: 'Old Coin', description: 'Shiny', type: 'material', stackable: true, maxStack: 99, value: 3 }] },
  })
  return {
    ...project,
    currentSeason: 'fall',
    settings: { ...project.settings, locale: 'fr' },
    contentPacks: [
      { pack: addon, enabled: true },
      { pack: langPack, enabled: true },
      { pack: demoMod, enabled: true },
      { pack: rebalance, enabled: true },
      { pack: retired, enabled: false },
    ],
    player: {
      ...project.player,
      inventory: [
        ...project.player.inventory.filter(entry => entry.item.type !== 'seed'),
        { item: { id: 'retired:old-coin', name: 'Old Coin', description: 'Shiny', type: 'material', stackable: true, maxStack: 99, value: 3 }, quantity: 2 },
      ],
    },
    quarantinedItems: [{ item: { ...baguette, id: 'lang-pack:baguette' } as Item, quantity: 3 }],
  } as GameProject
}

scenarios.push({
  name: 'content-packs-and-plugins',
  description: 'Starter Farm + 5 installed packs (dependency hoisting, namespacing, cross-pack refs, declared override vs. undeclared conflict, disabled pack quarantine + quarantined item restore, fr locale string tables), namespaced glowshroom grown in fall, namespaced vat machine without an item, pack recipe craft, and every pluginMutation type (valid and failing).',
  seed: 'packs',
  autoStartQuests: true,
  project: packsProject,
  script: r => {
    const plugin = (mutation: Record<string, unknown>) => r.cmd({ type: 'pluginMutation', pluginId: 'demo-glow-farm:morning-hum', mutation: mutation as never })
    plugin({ type: 'giveItem', itemId: 'demo-glow-farm:seed-glowshroom', quantity: 3 })
    plugin({ type: 'giveItem', itemId: 'nope', quantity: 1 })
    const spots = [6, 7, 8]
    for (const x of spots) {
      r.goTo(x, 9)
      r.face('up')
      r.cmd({ type: 'interact' })
    }
    for (let day = 0; day < 7; day++) {
      for (const x of spots) {
        r.goTo(x, 9)
        r.face('up')
        const crop = r.tileAt(x, 8)?.crop
        if (crop && !crop.withered && (crop.daysGrown ?? 0) >= 6) r.cmd({ type: 'interact' })
        else if (crop) r.cmd({ type: 'useTool', tool: 'watering-can' })
      }
      if (day === 2) r.tick(1300)
      r.cmd({ type: 'sleep' })
    }
    for (const x of spots) {
      r.goTo(x, 9)
      r.face('up')
      if (r.tileAt(x, 8)?.crop) r.cmd({ type: 'interact' })
    }
    r.cmd({ type: 'craft', recipeId: 'addon:jam-recipe' })
    if (r.qty('demo-glow-farm:crop-glowshroom') < 2) plugin({ type: 'giveItem', itemId: 'demo-glow-farm:crop-glowshroom', quantity: 2 })
    r.face('down')
    r.cmd({ type: 'placeMachine', machineTypeId: 'demo-glow-farm:machine-glow-vat' })
    r.cmd({ type: 'machineLoad', recipeId: 'demo-glow-farm:recipe-glow-jelly' })
    r.tick(3600)
    r.cmd({ type: 'interact' })
    // Every plugin mutation type.
    plugin({ type: 'takeItem', itemId: 'lang-pack:baguette', quantity: 1 })
    plugin({ type: 'giveMoney', amount: 250 })
    plugin({ type: 'takeMoney', amount: 40 })
    plugin({ type: 'setFlag', flag: 'mod:bool', value: true })
    plugin({ type: 'setFlag', flag: 'mod:num', value: 7 })
    plugin({ type: 'setFlag', flag: 'mod:str', value: 'glow' })
    plugin({ type: 'message', text: 'The glowshrooms hum softly on day 8...' })
    plugin({ type: 'setWeather', weatherId: 'storm' })
    plugin({ type: 'setWeather', weatherId: 'sharknado' })
    plugin({ type: 'modifyFriendship', npcId: 'npc-farmer', delta: 300 })
    plugin({ type: 'modifyFriendship', npcId: 'npc-farmer', delta: -1000 })
    plugin({ type: 'grantXp', skill: 'social', amount: 160 })
    plugin({ type: 'modifyEnergy', delta: -30 })
    plugin({ type: 'modifyEnergy', delta: 12 })
    plugin({ type: 'modifyEnergy', delta: 0 })
    plugin({ type: 'startQuest', questId: 'quest-go-shopping' }) // prerequisite unmet → no-op
    plugin({ type: 'startQuest', questId: 'lang-pack:bonus' })
    plugin({ type: 'warpPlayer', sceneId: 'scene-farm', x: 4, y: 10 })
    plugin({ type: 'warpPlayer', sceneId: 'scene-nowhere', x: 1, y: 1 })
    plugin({ type: 'startDialogue', npcId: 'npc-farmer' })
    r.cmd({ type: 'chooseDialogueOption', index: 0 })
    plugin({ type: 'startDialogue', npcId: 'npc-merchant', dialogueId: 'dialogue-merchant-greeting' })
    r.cmd({ type: 'closeDialogue' })
    plugin({ type: 'playSound', soundId: 'glow-hum' })
    plugin({ type: 'performAction', actionId: 'action-growth-blessing' })
    plugin({ type: 'startMinigame', minigameId: 'fishing' })
    r.cmd({ type: 'resolveMinigame', score: 1 })
    r.tick(1300)
  },
  verify: (r, initial) => {
    const content = r.ctx.content
    expect(content.items.find(i => i.id === 'crop-wheat')!.name).toBe('Blé')
    expect(content.items.find(i => i.id === 'crop-wheat')!.value).toBe(99)
    expect(content.items.find(i => i.id === 'crop-carrot')!.value).toBe(20)
    expect(content.items.find(i => i.id === 'lang-pack:baguette')!.name).toBe('Baguette française')
    expect(content.npcs.find(n => n.id === 'npc-farmer')!.name).toBe('Vieux fermier')
    expect(content.crops['demo-glow-farm:glowshroom']).toBeDefined()
    expect(initial.quarantinedItems.map(s => s.item.id)).toEqual(['retired:old-coin'])
    expect(initial.player.inventory.some(s => s.item.id === 'lang-pack:baguette')).toBe(true)
    expect(initial.meta.packs.map(p => p.id)).toEqual(['addon', 'lang-pack', 'demo-glow-farm', 'rebalance'])
    expect(r.hasMessage('Planted Glowshroom!')).toBe(true)
    // TS quirk captured on purpose: harvestCrop looks up `crop-${crop.type}`
    // ('crop-demo-glow-farm:glowshroom'), but the pack item is namespaced as
    // 'demo-glow-farm:crop-glowshroom' — so mature pack crops silently refuse
    // to harvest (interact returns no effects). The port must match.
    expect(r.harvested('demo-glow-farm:glowshroom')).toBe(0)
    expect(r.tileAt(7, 8)?.crop?.daysGrown ?? 0).toBeGreaterThanOrEqual(6)
    expect(r.hasMessage('Crafted 1x Jam')).toBe(true)
    expect(r.hasMessage('Crafted 1x Glow Jelly')).toBe(true)
    expect(r.hasMessage("Plugin demo-glow-farm:morning-hum: unknown item 'nope'")).toBe(true)
    expect(r.hasMessage("Plugin demo-glow-farm:morning-hum: unknown weather 'sharknado'")).toBe(true)
    expect(r.hasMessage('Social level 2!')).toBe(true)
    expect(r.hasMessage('New quest: Supply Run')).toBe(false)
    expect(r.hasMessage('New quest: Quête bonus')).toBe(true)
    expect(content.quests.find(q => q.id === 'quest-first-harvest')!.name).toBe('Première récolte')
    expect(r.hasMessage('A warm green light washes over the farm…')).toBe(true)
    expect(r.effects().some(e => e.type === 'sound' && (e as { id: string }).id === 'glow-hum')).toBe(true)
    expect(r.state.flags['mod:num']).toBe(7)
    expect(r.state.flags['mod:str']).toBe('glow')
    expect(r.state.social['npc-farmer'].friendship).toBe(0)
    expect(r.state.player.sceneId).toBe('scene-farm')
  },
})

// 22 ── NPC movement & schedules ──────────────────────────────────────────
function npcProject(): GameProject {
  const plaza = createEmptyScene('plaza', 'Plaza', 12, 10)
  for (let x = 0; x <= 9; x++) paint(plaza, x, 5, 'wall')
  paint(plaza, 3, 8, 'wall'); paint(plaza, 4, 7, 'wall'); paint(plaza, 5, 8, 'wall'); paint(plaza, 4, 9, 'wall')
  placeNode(plaza, 7, 3, 'node-rock', 3)
  placeNode(plaza, 8, 3, 'node-weeds', 1)
  const annex = createEmptyScene('annex', 'Annex', 5, 5)
  const npc = (id: string, x: number, y: number, extra: Record<string, unknown>) => ({ id, name: id, x, y, sceneId: 'plaza', dialogue: [], canMove: false, appearance: 'farmer', ...extra })
  return labProject('npc-lab', [plaza, annex], { x: 2, y: 1, direction: 'left' }, {
    npcs: [
      npc('npc-walker', 1, 1, { schedule: [{ minute: 600, sceneId: 'annex', x: 2, y: 2 }, { minute: 360, sceneId: 'plaza', x: 1, y: 8 }, { minute: 700, sceneId: 'plaza', x: 11, y: 0 }] }),
      npc('npc-guard', 8, 1, { canMove: true, movePattern: 'patrol', patrolPoints: [{ x: 8, y: 1 }, { x: 8, y: 4 }, { x: 4, y: 3 }] }),
      npc('npc-cat', 6, 7, { canMove: true, movePattern: 'wander', wanderRadius: 2 }),
      npc('npc-stuck', 10, 8, { schedule: [{ minute: 360, sceneId: 'plaza', x: 4, y: 8 }] }),
      npc('npc-idle', 10, 1, { canMove: true, movePattern: 'stationary' }),
    ],
    weather: WeatherConfigSchema.parse({
      types: [{ id: 'sun', name: 'Sunny' }, { id: 'storm', name: 'Storm', npcsStayInside: true, cropDamageChance: 0.5, watersOutdoorSoil: true }],
      table: { spring: [{ weatherId: 'sun', weight: 1 }, { weatherId: 'storm', weight: 1 }] },
    }),
  })
}

scenarios.push({
  name: 'npc-schedules-and-movement',
  description: 'NPC runtime on minute boundaries: A* schedule walking around walls (sorted schedule, latest entry wins), cross-scene teleport, unreachable goal, patrol loop through a rock/weeds field, seeded wander within radius, player-adjacency pause, player blocking paths, storms keeping scheduled NPCs home, several full days of ticks.',
  seed: 'npcs',
  project: npcProject,
  script: r => {
    const snap = (label: string) => { r.notes[label] = clone(r.state.npcs); r.notes[label + ':t'] = r.state.clock.timeMinutes }
    r.tick(400) // walker is adjacent to the player → paused
    snap('paused')
    r.cmd({ type: 'move', dir: 'right' })
    r.cmd({ type: 'move', dir: 'right' })
    r.tick(1200)
    snap('walked')
    r.goTo(10, 6)
    r.tick(3400) // → 10:10: walker teleported to the annex at 10:00
    snap('annex')
    r.tick(2400) // → 12:10: the 11:40 entry teleports the walker back to (11,0)
    snap('late')
    r.cmd({ type: 'sleep' })
    for (let day = 0; day < 4; day++) {
      r.tick(6000)
      snap(`day${day}`)
      r.notes[`weather${day}`] = r.state.clock.weatherId
      r.cmd({ type: 'sleep' })
    }
    r.tick(24000)
  },
  verify: r => {
    const n = r.notes
    expect(n.paused['npc-walker']).toMatchObject({ sceneId: 'plaza', x: 1, y: 1 })
    expect(n.walked['npc-walker']).toMatchObject({ sceneId: 'plaza', x: 1, y: 8 })
    expect(n.annex['npc-walker']).toMatchObject({ sceneId: 'annex', x: 2, y: 2 })
    expect(n.late['npc-walker']).toMatchObject({ sceneId: 'plaza', x: 11, y: 0 })
    const guardIdx = ['paused', 'walked', 'day0', 'day1'].map(k => n[k]['npc-guard'].patrolIndex)
    expect(new Set(guardIdx).size).toBeGreaterThanOrEqual(3)
    // Storm days keep the scheduled walker home at (11,0); sunny days walk.
    expect(n.weather0).toBe('storm')
    expect(n.day0['npc-walker']).toMatchObject({ sceneId: 'plaza', x: 11, y: 0 })
    expect(n.weather1).toBe('sun')
    expect(n.day1['npc-walker'].y).toBeGreaterThan(0)
    expect(n.walked['npc-stuck']).toMatchObject({ x: 10, y: 8 })
    expect(n.walked['npc-idle']).toMatchObject({ x: 10, y: 1 })
    const catMoves = ['paused', 'walked', 'annex', 'late'].map(k => `${n[k]['npc-cat'].x},${n[k]['npc-cat'].y}`)
    expect(new Set(catMoves).size).toBeGreaterThan(1)
    for (const k of ['paused', 'walked', 'annex', 'late']) {
      expect(Math.abs(n[k]['npc-cat'].x - 6)).toBeLessThanOrEqual(2)
      expect(Math.abs(n[k]['npc-cat'].y - 7)).toBeLessThanOrEqual(2)
    }
    expect([0, 1, 2, 3].map(d => n[`weather${d}`])).toContain('storm')
    expect(r.hasMessage('Weather: Storm')).toBe(true)
    expect(r.hasMessage('You collapsed from exhaustion!')).toBe(true)
  },
})

// 23 ── legacy fixture migrated then played ───────────────────────────────
scenarios.push({
  name: 'legacy-v1-fixture-play',
  description: 'tests/fixtures/project-v1.json migrated v1→v8 (layered tiles, backfills, default weather/settings) and played: legacy crop without daysGrown grows over sleeps (watered flag from v1), door tile walking, blocked moves, default weather rolls.',
  seed: 'legacy-v1',
  project: () => JSON.parse(readFileSync(path.join(ROOT, 'tests/fixtures/project-v1.json'), 'utf8')),
  script: r => {
    r.cmd({ type: 'move', dir: 'left' })
    r.cmd({ type: 'move', dir: 'down' })
    r.face('left')
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'sleep' })
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'setMoveIntent', dx: 1, dy: -1 })
    r.tick(30)
    r.cmd({ type: 'setMoveIntent', dx: 0, dy: 0 })
    for (let i = 0; i < 5; i++) r.cmd({ type: 'sleep' })
    r.tick(1300)
    r.goTo(1, 1)
    r.face('left')
    r.cmd({ type: 'interact' })
    r.cmd({ type: 'interact' })
  },
  verify: r => {
    expect(r.hasMessage('Crop is not ready to harvest yet')).toBe(true)
    expect(r.state.clock.day).toBe(7)
    expect(r.countEffects('dayStarted')).toBe(6)
    expect(r.hasMessage('Weather: Rain')).toBe(true)
    expect(r.harvested('wheat')).toBeGreaterThan(0)
    expect(r.hasMessage('No seeds in inventory')).toBe(true)
  },
})

// ─── Static vector files ──────────────────────────────────────────────────

function writeMigrationFixtures() {
  for (let v = 1; v <= CURRENT_PROJECT_SCHEMA_VERSION; v++) {
    const file = path.join(ROOT, 'tests', 'fixtures', `project-v${v}.json`)
    if (!existsSync(file)) continue
    const input = JSON.parse(readFileSync(file, 'utf8'))
    const result = migrateProject(clone(input))
    expect(result.ok).toBe(true)
    const exported = migrateExportedGame(clone(input))
    writeJson(`migrations/project-v${v}.json`, {
      input,
      result,
      stable: stableStringify(result.data),
      hash: hashState(result.data),
      exported: { result: exported, stable: exported.data ? stableStringify(exported.data) : null },
    })
  }
  const futureProject = { ...JSON.parse(readFileSync(path.join(ROOT, 'tests/fixtures/project-v8.json'), 'utf8')), schemaVersion: 99 }
  const cases: Array<{ name: string; input: unknown }> = [
    { name: 'null', input: null },
    { name: 'number', input: 42 },
    { name: 'string', input: 'junk' },
    { name: 'future-version', input: futureProject },
  ]
  writeJson('migrations/errors.json', cases.map(c => ({
    name: c.name,
    input: c.input,
    project: migrateProject(clone(c.input)),
    exported: migrateExportedGame(clone(c.input)),
  })))
}

function writeSaveFixtures() {
  // A real mid-game state: starter farm after a few commands and a sleep.
  const project = migrateProject(clone(starterProject())).data!
  const ctx: EngineContext = { content: createContentFromProject(project) }
  let state = autoStartQuests(ctx, createGameState(project, { seed: 'save-fixture' }))
  for (const command of [
    { type: 'setMoveIntent', dx: 0, dy: -1 }, { type: 'setMoveIntent', dx: 0, dy: 0 }, { type: 'interact' },
    { type: 'useTool', tool: 'watering-can' }, { type: 'sleep' },
  ] as Command[]) state = applyCommand(ctx, state, command).state
  state = advanceTick(ctx, state, 137).state
  const base = clone(state) as Record<string, any>

  const v1 = clone(base)
  v1.meta.saveVersion = 1
  delete v1.meta.packs
  for (const key of ['timeMinutes', 'day', 'season', 'year', 'weatherId']) delete v1.clock[key]
  for (const key of ['energy', 'maxEnergy', 'skills', 'moveIntent']) delete v1.player[key]
  v1.player.x = 8
  v1.player.y = 9
  for (const key of ['shopPurchasesToday', 'social', 'animals', 'mine', 'quarantinedItems', 'shop']) delete v1[key]

  const v2 = clone(base)
  v2.meta.saveVersion = 2
  delete v2.clock.weatherId
  delete v2.player.skills
  delete v2.player.moveIntent
  v2.player.x = 8
  v2.player.y = 8
  for (const key of ['social', 'animals', 'mine', 'quarantinedItems']) delete v2[key]

  const v3Grid = clone(base)
  v3Grid.meta.saveVersion = 3
  v3Grid.player.x = 3
  v3Grid.player.y = 4
  delete v3Grid.player.moveIntent

  const v3Frac = clone(base)
  v3Frac.meta.saveVersion = 3
  v3Frac.player.x = 2.25
  v3Frac.player.y = 6.75
  v3Frac.player.moveIntent = { dx: 1, dy: 0 }

  const future = clone(base)
  future.meta.saveVersion = 5

  const noMeta = clone(base)
  delete noMeta.meta.saveVersion

  const cases: Record<string, unknown> = {
    'save-v1': v1, 'save-v2': v2, 'save-v3-grid': v3Grid, 'save-v3-fractional': v3Frac,
    'save-v4-current': base, 'save-v5-future': future, 'save-no-version': noMeta,
    'save-not-object': 'nope',
  }
  for (const [name, input] of Object.entries(cases)) {
    const result = migrateGameState(clone(input))
    writeJson(`saves/${name}.json`, {
      input,
      result,
      stable: result.data ? stableStringify(result.data) : null,
      hash: result.data ? hashState(result.data) : null,
    })
  }
  expect(migrateGameState(clone(v1)).ok).toBe(true)
  expect(migrateGameState(clone(v2)).ok).toBe(true)
  expect(migrateGameState(clone(v3Grid)).ok).toBe(true)
}

function writeRngFixture() {
  const seeds: Array<string | number> = ['golden', '', 'replay-farm:500000', 'ünïcødé 🌾', 'a\u0000b', 0, 1, 42, -1, 4294967295, 4294967296, 3.7, -3.7, 2 ** 31, 2 ** 53, 1e21]
  const draws = (seed: string | number) => {
    const initial = createRngState(seed)
    let s = initial
    const u32: number[] = []
    for (let i = 0; i < 20; i++) { const r = nextU32(s); u32.push(r.value); s = r.state }
    const u32End = s
    s = initial
    const float: number[] = []
    for (let i = 0; i < 20; i++) { const r = nextFloat(s); float.push(r.value); s = r.state }
    s = initial
    const intDie: number[] = []
    for (let i = 0; i < 20; i++) { const r = nextInt(s, 1, 6); intDie.push(r.value); s = r.state }
    s = initial
    const intSigned: number[] = []
    for (let i = 0; i < 20; i++) { const r = nextInt(s, -5, 5); intSigned.push(r.value); s = r.state }
    const rng = new Rng(initial)
    const weighted = [
      { weights: [6, 3, 1], picks: Array.from({ length: 20 }, () => rng.weighted([6, 3, 1])) },
      { weights: [0, 0], picks: Array.from({ length: 3 }, () => rng.weighted([0, 0])) },
      { weights: [], picks: [rng.weighted([])] },
      { weights: [1.5, 2.5, 0.25], picks: Array.from({ length: 20 }, () => rng.weighted([1.5, 2.5, 0.25])) },
    ]
    return { seed, initial, u32, u32End, float, intDie, intSigned, weighted, rngAfterWeighted: rng.state }
  }
  const strings = ['', 'a', 'golden', 'replay-farm:500000', 'seed-x:mine:3', 'ünïcødé 🌾', '\ud83c', 'a\u0000b']
  writeJson('rng.json', {
    seeds: seeds.map(draws),
    hashStringToU32: strings.map(input => ({ input, value: hashStringToU32(input) })),
  })
}

/** Tagged encoding for values JSON cannot carry: {"$js": "undefined" | "NaN" | "Infinity" | "-Infinity" | "-0"}. */
function tag(value: unknown): unknown {
  if (value === undefined) return { $js: 'undefined' }
  if (typeof value === 'number') {
    if (Number.isNaN(value)) return { $js: 'NaN' }
    if (value === Infinity) return { $js: 'Infinity' }
    if (value === -Infinity) return { $js: '-Infinity' }
    if (Object.is(value, -0)) return { $js: '-0' }
    return value
  }
  if (Array.isArray(value)) return value.map(tag)
  if (value && typeof value === 'object') {
    // Keep keys (including undefined-valued ones) so the C# side sees them.
    const out: Record<string, unknown> = {}
    for (const key of Object.keys(value)) out[key] = tag((value as Record<string, unknown>)[key])
    return out
  }
  return value
}

function writeHashFixture() {
  const cases: Array<{ name: string; value: unknown }> = [
    { name: 'empty-object', value: {} },
    { name: 'empty-array', value: [] },
    { name: 'null', value: null },
    { name: 'true', value: true },
    { name: 'zero', value: 0 },
    { name: 'empty-string', value: '' },
    { name: 'floats', value: [0.1 + 0.2, 1e21, 1e-7, 1e-6, 123456789012345680000, 5e-324, 1.7976931348623157e308, -1.5, 100, 1 / 3, 2 ** 53, 2 ** 53 + 2, 0.000001234, 1e20, 123e-20, 4.35, 0.5, -0, 1_700_000_000_000, 360.05, 1439.9999999999998] },
    { name: 'non-finite', value: { nan: NaN, inf: Infinity, ninf: -Infinity, arr: [NaN, Infinity, -0] } },
    { name: 'undefined-fields', value: { a: 1, b: undefined, c: { d: undefined, e: null }, list: [1, undefined, 3] } },
    { name: 'key-order', value: { b: 1, a: 2, B: 3, _: 4, '1': 5, '10': 6, '2': 7, 'é': 8, aa: 9, 'a b': 10, '': 11, '￿': 12, '😀': 13, 'Z': 14 } },
    { name: 'nested-key-order', value: { z: { y: [{ b: 1, a: [{ d: 1, c: 2 }] }], x: { k: { j: { i: 'deep' } } } }, a: [] } },
    { name: 'unicode', value: { emoji: '🌾🐔', accents: 'Blé crème brûlée', cjk: '農場', rtl: 'مزرعة', lone: '\ud83c', loneLow: '\udc00x', bom: '﻿' } },
    { name: 'control-chars', value: { all: Array.from({ length: 32 }, (_, i) => String.fromCharCode(i)).join(''), del: '\u007f', seps: '  ', quote: '"\'\\/', html: '</script><!--' } },
    { name: 'numbers-as-keys', value: { 0: 'zero', 1: 'one', '-1': 'neg', '01': 'lead', '1.5': 'frac' } },
    { name: 'mixed-array', value: [1, 'two', null, true, { three: 3 }, [4, [5]], false, 0, ''] },
    { name: 'long-string', value: 'x'.repeat(1000) + '\n' + 'y'.repeat(10) },
  ]
  writeJson('hash.json', cases.map(c => ({
    name: c.name,
    input: tag(c.value),
    stable: stableStringify(c.value),
    hash: hashState(c.value),
  })))
}

function writeContentFixtures() {
  const sample: Record<string, () => unknown> = {
    'starter-farm': starterProject,
    'cozy-garden': () => pinTimes(createCozyFarmProject()),
    'quest-rpg': () => pinTimes(createQuestRpgProject()),
    blank: () => pinTimes(createBlankProject()),
    'packs-fr': packsProject,
    'packs-de': () => ({ ...packsProject(), settings: { ...packsProject().settings, locale: 'de' } }),
    'packs-all-disabled': () => ({ ...packsProject(), contentPacks: packsProject().contentPacks.map(install => ({ ...install, enabled: false })) }),
    'fixture-v1': () => JSON.parse(readFileSync(path.join(ROOT, 'tests/fixtures/project-v1.json'), 'utf8')),
    'fixture-v8': () => JSON.parse(readFileSync(path.join(ROOT, 'tests/fixtures/project-v8.json'), 'utf8')),
    'lab-calendar': calendarProject,
  }
  for (const [name, build] of Object.entries(sample)) {
    const migrated = migrateProject(clone(build()))
    expect(migrated.ok).toBe(true)
    const project = migrated.data!
    const content = createContentFromProject(project)
    writeFixture(`content/${name}.json`, {
      name,
      project,
      contentHash: hashState(content),
      stable: stableStringify(content),
      stateHash: hashState(createGameState(project, { seed: `content:${name}` })),
    })
  }
}

// ─── Test entry points ────────────────────────────────────────────────────

describe.skipIf(!OUT)('golden fixtures (TS reference → C# port)', () => {
  it('writes migration fixtures', () => writeMigrationFixtures())
  it('writes save-migration fixtures', () => writeSaveFixtures())
  it('writes rng vectors', () => writeRngFixture())
  it('writes stable-hash vectors', () => writeHashFixture())
  it('writes content fixtures', () => writeContentFixtures())
  for (const scenario of scenarios) {
    it(`replay: ${scenario.name}`, () => { runScenario(scenario) }, 120_000)
  }
  it('writes the scenario index', () => {
    writeJson('replays/index.json', scenarios.map(s => ({ name: s.name, description: s.description, seed: s.seed, autoStartQuests: Boolean(s.autoStartQuests) })))
  })
})
