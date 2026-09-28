# Plugin sandbox guest

`farm_plugin_guest.wasm` is QuickJS compiled to WebAssembly (`wasm32-wasip1`), with fuel
metering added. `farm-plugins` embeds it and runs one instance per plugin in wasmi.

It is checked in, like the generated pixel art: building the engine never needs the C
toolchain. Rebuild it only when `crates/farm-plugin-guest` or `tools/plugin-guest/meter`
changes:

```sh
tools/plugin-guest/build.sh
```

Then put the new hash below. The `farm-plugins` tests fail until it matches the file.

## Recorded build

- sha256: `dd77c3d1ffb536a2bdad3162caff69c6b62598a02bb80b01000b9c009afeb5be`
- size: 734218 bytes

Two builds from the same inputs give the same bytes.

## Inputs

- Rust 1.94.1 (`rust-toolchain.toml`), target `wasm32-wasip1`. The Rust side links with
  Rust's own wasi-libc.
- `crates/farm-plugin-guest` and its `Cargo.lock`: `rquickjs-sys` 0.14.0 (quickjs-ng 0.16.2,
  with its pre-generated `wasm32-wasip1` bindings), `cc` 1.5.1.
- Release profile: `opt-level = "z"`, `lto = true`, `codegen-units = 1`, `panic = "abort"`,
  `strip = true`. Linker flags (`crates/farm-plugin-guest/.cargo/config.toml`):
  `--stack-first -zstack-size=1048576`.
- wasi-sdk 24.0 (`wasi-sdk-24.0-x86_64-linux.tar.gz`, sha256
  `c6c38aab56e5de88adf6c1ebc9c3ae8da72f88ec2b656fb024eda8d4167a0bc5`): its clang and
  sysroot compile the QuickJS C sources.
- No `wasm-opt`.
- `tools/plugin-guest/meter` and its `Cargo.lock` (`wasmparser` and `wasm-encoder`
  0.259.0) add the `fp_fuel` counter last.

## Interface

Exports: `memory`, `fp_buffer`, `fp_prepare`, `fp_init`, `fp_dispatch`, `fp_output_ptr`,
`fp_output_len` and the `fp_fuel` global. Imports: a few `wasi_snapshot_preview1`
functions, which the host provides as inert stubs (no files, no environment, a constant
clock, deterministic random bytes). `crates/farm-plugin-guest/src/lib.rs` documents the ABI
and the sandbox rules; `tools/plugin-guest/meter/src/main.rs` documents the metering.
