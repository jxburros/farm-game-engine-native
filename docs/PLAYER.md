# Headless player

`farm-player` is the first standalone Rust entry point for a compiled
`game.cart`. It currently runs headless: it verifies the cartridge, creates
the initial state or loads a compatible save, applies a replay log, and
prints a JSON report with the game id, version, content and state hashes,
effects and save warnings. The graphical game shell and controller support
are still ahead. Export Game ships this player, renamed, next to the game's
`game.cart` ([EXPORT.md](EXPORT.md)).

```sh
cargo run -p farm-player -- --headless --cart game.cart
cargo run -p farm-player -- --headless --cart game.cart --replay replay.json --save slot1.sav
cargo run -p farm-player -- --headless --cart game.cart --load slot1.sav
```

`--cart` defaults to `game.cart` beside the executable, matching the planned
export layout. A replay file has this shape:

```json
{
  "seed": "optional-seed",
  "autoStartQuests": false,
  "inputs": [
    { "kind": "tick", "ticks": 20 },
    { "kind": "command", "command": { "type": "sleep" } }
  ],
  "expectedHash": "optional-16-character-hash"
}
```

The player exits with an error for a malformed cartridge, an incompatible
save or a replay hash mismatch. `--save` writes a binary save
(`schemas/save.fbs`); a path ending in `.json` writes the JSON form instead.
`--load` reads either, and bare web `GameState` JSON too. The checked-in cartridge and replay in
`fixtures/golden/cartridges` are exercised by Rust tests on Windows and Linux.
