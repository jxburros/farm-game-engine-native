# Plugin sandbox guest build

`build.sh` builds the guest half of the plugin sandbox: QuickJS
(`crates/farm-plugin-guest`, quickjs-ng through `rquickjs-sys`) compiled to
`wasm32-wasip1`, then fuel-metered by `meter/`. The result is checked in as
`crates/farm-plugins/guest/farm_plugin_guest.wasm`, so building the engine
never needs the C toolchain. [Its README](../../crates/farm-plugins/guest/README.md)
records the hashes of the current build.

```sh
tools/plugin-guest/build.sh          # rebuild and replace the checked-in guest
tools/plugin-guest/build.sh --check  # rebuild and compare byte for byte (CI)
tools/plugin-guest/build.sh --lint   # cargo fmt, clippy and the meter's tests on both crates (CI)
```

Rebuild only when `crates/farm-plugin-guest`, `meter/` or `build.sh` changes,
then copy the printed sha256 and inputs hash into the guest's README; the
`farm-plugins` tests fail until they match. The script downloads wasi-sdk 24.0
(checked against its sha256) into `target/plugin-guest`, or uses `$WASI_SDK`
(required on hosts other than x86_64 Linux).

Changing `build.sh` changes the inputs hash, so a comment edit there also
means updating the guest's README. When the guest's C or Rust dependencies
change, update the license texts in `tools/player-licenses/plugin-guest`.
