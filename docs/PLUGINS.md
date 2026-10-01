# Pack plugins

A content pack can ship **plugins**: small JavaScript programs that react to
what happens in the game. A plugin registers handlers with
`api.on(hook, fn)`; a handler gets the hook's payload as plain JSON and
answers with a list of **mutations** such as
`{ type: 'giveMoney', amount: 10 }`. Plugins never touch the game state
themselves. Each answer is checked and then runs as a `pluginMutation`
command, so it enters the command log like anything the player does, and a
replay of that log needs no plugins.

Plugins run in `farm-plugins`: QuickJS compiled to WebAssembly, one isolated
instance per plugin, run by the wasmi interpreter with deterministic budgets.
The same sandbox runs in the editor's Play Mode, in exported games and in the
web player (`farm-wasm`).

```json
{
  "manifest": {
    "id": "glow-farm", "name": "Glow Farm", "version": "1.0.0",
    "permissions": {
      "hooks": ["onDayStart"],
      "mutations": ["message", "giveItem"]
    }
  },
  "plugins": [{
    "id": "morning-hum",
    "hooks": ["onDayStart"],
    "source": "api.on('onDayStart', p => [{ type: 'message', text: 'Day ' + p.day }, { type: 'giveItem', itemId: 'seed-glowshroom', quantity: 1 }])"
  }]
}
```

## Permissions

The manifest's `permissions` are what the player approves when installing
the pack. The Mods tab's install review shows them.

| Permission | Default | What it allows |
|---|---|---|
| `hooks` | none | The hooks the pack's plugins may hear. A plugin hears the hooks in its own `hooks` list that the manifest also lists. |
| `mutations` | see below | What the plugins may answer with. |
| `contentInject` | `true` | Whether the pack's content (definitions, `playerStart`, string tables) loads. When it is `false`, nothing of it loads, the Problems panel warns if the pack has content, and its plugins still run. |
| `uiPanels` | `false` | Reserved. Packs cannot add game panels yet, so this allows nothing. |

### Mutation capabilities

Each entry of `permissions.mutations` allows one mutation type:

| Entry | Allows |
|---|---|
| `"message"`, `"playSound"` | Messages and sounds. |
| `"giveMoney"`, `"takeMoney"`, `"modifyEnergy"`, `"grantXp"`, `"setWeather"` | That change, with any value the schema allows. |
| `"setFlag"`, `"giveItem"`, `"takeItem"`, `"modifyFriendship"`, `"startQuest"`, `"warpPlayer"`, `"startDialogue"`, `"performAction"`, `"startMinigame"` | That change, for the pack's **own** content only (see below). `"giveItem:own"` means the same. |
| `"giveItem:any"` (any of the types above with `:any`) | That change for any id: your game's content, base content and other packs. |
| `"*"` | Everything, with any ids. |

**Own content.** For the types that name content, a plain id means the
plugin's own pack: `setFlag` with `flag: 'met'` from pack `glow-farm` sets
the flag `glow-farm:met`, and `giveItem` with `itemId: 'seed'` gives
`glow-farm:seed`. Ids already written as `glow-farm:…` are kept. Ids of
other packs (`other-pack:seed`) are refused unless the pack has `type:any`;
so is your game's own content, because a plain id always means the pack's. With `:any`, ids are used exactly as
written. A base pack's own definitions keep plain ids, so its plugins need
`:any` to name them.

**Without `permissions.mutations`** (packs written before it existed) the
plugins get `message`, `playSound`, `setFlag`, `giveItem` and `takeItem` for
their own content, and their answers to `onEffect` and `onCommand` are
dropped: those hooks are for watching unless the pack declares what its
plugins do.

**Never allowed:** flags starting with `event:`. The engine keeps one-shot
events there (`event:<id>:fired`), so a plugin could re-arm or switch off the
game's events.

A refused or malformed mutation is dropped; the others in the same answer
still run. The plugin errors list (the editor's Play Mode drawer, the game's
error toasts) says why. Unknown entries in `permissions.mutations` are
ignored and the Problems panel warns about them.

## Timing

- **Answers apply before the next tick.** A handler runs right after the step
  that fired its hook (a tick, a player command, another plugin's mutation).
  Its mutations wait in a queue and run right before the next tick, in the
  order they arrived. The game advances one tick at a time, so this does not
  depend on the frame rate: the same input gives the same game at 30 and at
  144 frames per second.
- **Chains stop.** A step caused by a plugin mutation does not fire
  `onCommand` or `onEffect`, and mutations three reactions deep are dropped
  (a plugin answering a mutation's hook, another answering that, and so on).
  A plugin cannot make the game loop on itself.
- **`onWeatherRoll` is a notification.** The engine lets the weather roll be
  overridden while the night runs, but plugins answer only after the step.
  A `setWeather` answer changes the weather for the day, yet the night's rain
  watering and storm damage already used the rolled weather. The Problems
  panel warns about plugins that listen to `onWeatherRoll`.

## Limits

Budgets are counted in **fuel**: one unit per WebAssembly instruction the
plugin's JavaScript engine runs (about 0.5–1.3 billion a second in a release
build). They never depend on the machine.

| Limit | Default | When it is hit |
|---|---|---|
| Fuel per handler call | 50 million (40–110 ms) | The call stops; a strike. |
| Fuel per plugin per second of game time | 200 million | The plugin is skipped until the next second; a strike. |
| JavaScript heap / linear memory per plugin | 16 MB / 40 MiB | The call stops; a strike. |
| Recursion | a few thousand JavaScript calls deep | The call stops; a strike. |
| Strikes | 3 within 100 calls of the plugin | The plugin is off for the session. |
| Restarts after a strike | 10 per session | The plugin is off for the session. |
| Startup fuel per plugin / all plugins | 500 million / 2 billion | The plugin does not start. |
| Plugins | 32 | Later plugins do not start. |
| Memory of all plugins at startup | 512 MiB | Later plugins do not start. |
| Mutations per answer | 64 | The rest are dropped. |
| Ids, flag names and skills | 256 characters | The mutation is dropped. |
| String flag values / messages | 4096 / 500 characters | The mutation is dropped. |
| Different flags / skills per plugin per session | 256 / 32 | Further new ones are dropped. |
| An answer's size | 1 MiB | The answer is dropped (like a throw). |
| An error message | 1 KB | The rest is cut off. |

A handler that throws gives no mutations and is not a strike, but the fuel it
burned counts toward its per-second budget. After a strike the plugin is
rebuilt from its source (its JavaScript variables start over) with at most
twice the fuel its first start used.

## Determinism

- Budgets and timeouts are counted in instructions, so whether a handler
  finishes is the same on every machine and in every build.
- `Date.now()` is always 0, and `Math.random()` gives the same sequence in
  every plugin and every run.
- Plugin JavaScript state (variables kept between calls) is not saved. It
  starts over when the game loads, when Play Mode restarts, and when the
  plugin is rebuilt after a strike, so a plugin's answers can depend on the
  session's history. Replays are not affected: the command log, with the
  plugins' answers in it, is the replay.
- In the web player (`farm-wasm`) plugins run on the calling thread (a page
  that wants them off the main thread runs the whole player in a Worker), and
  the budgets above were measured natively: wasmi inside WebAssembly is
  slower, so the same fuel takes longer there.

## Sandbox hardening

Notes for engine developers (the 2026-09-30 audit's defence-in-depth items).

- **wasmi's tail-call dispatch.** wasmi runs each WebAssembly instruction
  with a tail call that LLVM turns into a jump at opt-level 2 and above. If a
  build broke that (wasmi below opt-level 2, debug assertions in wasmi,
  sanitizers), every instruction would grow the native stack and a plugin
  using its whole budget would crash the process, editor included. The root
  `Cargo.toml` turns off wasmi's debug assertions in every profile, and the
  `farm-plugins` test
  `a_plugin_running_its_whole_budget_fits_in_a_small_native_stack` runs a
  full 50-million-instruction budget on a 512 KiB thread, so a broken build
  fails CI instead of crashing players. Builds that cannot keep those
  settings turn on `farm-plugins`' `portable-dispatch` feature (wasmi's loop
  dispatch: slower, never stack-bound).
- **WASI stubs.** The guest imports only inert `wasi_snapshot_preview1`
  stubs. `random_get` checks the range against linear memory before it
  allocates anything (the current guest does not import it).
- **Output sizes.** The host never copies more than 1 MiB of an answer or
  1 KB of an error text out of the guest, however large the plugin makes
  them. The guest itself still copies a thrown message in full into its own
  memory (within its 40 MiB); cutting it there too needs a guest rebuild
  (`tools/plugin-guest/build.sh`), which is left for the next guest change.
