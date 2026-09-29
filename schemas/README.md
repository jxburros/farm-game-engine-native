# Cartridge and save schemas

`cart.fbs` defines format 2 of `game.cart`; `save.fbs` defines the binary save
file the player writes. The generated Rust readers are committed so building
the player does not need `flatc`. When a schema changes, use `flatc`
**25.2.10** and regenerate:

```sh
flatc --rust -o crates/farm-cart-schema/src schemas/cart.fbs
flatc --rust -o crates/farm-cart-schema/src schemas/save.fbs
```

The F# side has no generated code: `src/FarmEngine.Authoring/FlatBuffers.fs`
is a plain-F# builder (it also runs under Fable) that writes the same bytes as
the official builders, and `CartridgeCompiler.fs` adds the fields in the order
`flatc`'s `Create…` helpers do; `CartridgeReader.fs` reads cartridges back.
Update both when `cart.fbs` changes. Only Rust reads saves. The generated Rust
accessors live in `farm-cart-schema`, which alone allows their verified
pointer traversal. `farm-cart` and `farm-sim` forbid unsafe code. New fields
are appended to the tables; a breaking layout change also bumps `CART_FORMAT`
in `farm-cart` and `CartridgeCompiler.Format` in F# (or `SAVE_FORMAT` for
saves).

## Cartridge format 2

The player never reads project JSON. The F# compiler splits a project into
three sections:

| Section | Rust type | What it is |
|---|---|---|
| `content_json` | `GameContent` | The compiled content (built-in catalog, packs, locale strings) |
| `start_json` | `farm_sim::StartState` | Everything a new game starts from: scenes, player, clock, flags, NPC positions, quest progress |
| `presentation_json` | `farm_sim::Presentation` | Art bindings, custom assets, graphics settings, creator panels |

A `plugins` table carries the sandboxed plugins of the enabled content packs
in load order, each with the hooks its pack manifest grants, so an exported
game runs them in `farm-plugins` exactly as the editor does.

The sections are JSON so the v8 engine keeps its JavaScript number behavior.
Every base64 `data:` URL inside them moves to the `assets` table and is
replaced by `asset:<id>`, where the id is a hash of the file. The same file is
stored once. Content keeps its string ids: interning them into indexed tables
only pays off once the simulation uses interned indices, which is part of the
native-numerics cutover (phase 7 of `docs/LANGUAGES.md`).

The same project and editor version always give the same bytes. The
cross-language golden is regenerated with:

```sh
dotnet run --project src/FarmEngine.Cli -- compile fixtures/projects/project-v8.json --out fixtures/golden/cartridges/project-v8.cart
```

## Saves

A save file has a header (save format, game id, game version, cartridge
hash), a slot preview (farm name, date, money, play time, saved-at time and a
thumbnail) and the `GameState` as zstd-compressed stable JSON. The title
screen reads previews without decompressing states. `farm-cart` also reads
JSON saves with the same header and bare web `GameState`s.
