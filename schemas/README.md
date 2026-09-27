# Cartridge schema

`cart.fbs` defines format 1 of `game.cart`. The generated C# and Rust readers
are committed so building the editor or player does not need `flatc`. When the
schema changes, use `flatc` **25.2.10** and regenerate both languages:

```sh
flatc --csharp -o src/FarmEngine.Schemas/Generated schemas/cart.fbs
flatc --rust -o crates/farm-cart-schema/src schemas/cart.fbs
```

The generated Rust accessors live in `farm-cart-schema`, which alone allows
their verified pointer traversal. `farm-cart` and `farm-sim` forbid unsafe
code. New fields are appended to the tables; a breaking layout change also
bumps `CART_FORMAT` in `farm-cart` and the F# compiler.

The first cartridge embeds project and compiled content as UTF-8 JSON. This
preserves the v8 engine's JavaScript number behavior while content families
move into binary tables. It is reproducible for a given project and editor
version. The cross-language golden is regenerated with:

```sh
dotnet run --project src/FarmEngine.Cli -- compile tests/FarmEngine.Core.Tests/Fixtures/project-v8.json --out fixtures/golden/cartridges/project-v8.cart
```
