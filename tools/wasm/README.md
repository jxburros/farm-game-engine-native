# farm-wasm build and tests

Builds [`crates/farm-wasm`](../../crates/farm-wasm/README.md), the game
player for web pages, and tests it in Node. The web editor's Play Mode and the
web demo export both use this build.

```sh
tools/wasm/build.sh             # wasm-bindgen --target web output in tools/wasm/dist (not checked in)
tools/wasm/build.sh out/dir     # somewhere else
node tools/wasm/smoke.mjs       # the player, storage, plugins and every golden replay, in Node 22
node tools/wasm/game-page.mjs   # the web demo page's script (web-template/game.js) against the build
```

`build.sh` needs the `wasm32-unknown-unknown` target (`rust-toolchain.toml`
adds it) and installs the `wasm-bindgen` CLI whose version matches the
`wasm-bindgen` crate in `Cargo.lock`. `wasm-opt` (binaryen) is optional and
used when it is on `PATH`; `FARM_WASM_OPT=0` skips it.
`FARM_WASM_SCREENSHOTS=<dir>` makes `smoke.mjs` write the web demo's frames as
PNGs.

`web-template/` holds the web demo page (`index.html`, `style.css`,
`game.js`); `tools/player-templates/package.sh web` stages it with the build
as the web player template ([docs/EXPORT.md](../../docs/EXPORT.md)).
