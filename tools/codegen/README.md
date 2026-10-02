# Code generators

Generated sources are checked in, so a build never needs these tools. CI runs
`tools/codegen/check.sh` (in the "Build & test (Linux)" job), which fails when
a committed file is not what its generator makes.

| Generated file | Generator | Run after |
|---|---|---|
| `crates/farm-cart-schema/src/cart_generated.rs`, `save_generated.rs` | `flatc --rust` 25.2.10 over `schemas/*.fbs` ([schemas/README.md](../../schemas/README.md)) | a `.fbs` change |
| The codec and JSON-key tables in `src/FarmEngine.Authoring.Net/RecordJson.fs` | `python3 tools/codegen/record-json.py` (reads the decoders and encoders in `SchemaJson.fs`) | a `SchemaJson.fs` change |
| `src/FarmEngine.Authoring.Net/RecordWith.fs` (the `WithField` extensions the C# app builds records with) | `dotnet build -c Release src/FarmEngine.Authoring && dotnet fsi tools/codegen/record-with.fsx` (reflects over the built schema records) | a schema record change |

```sh
tools/codegen/check.sh                  # check all three (downloads flatc on x86_64 Linux)
python3 tools/codegen/record-json.py --check
dotnet fsi tools/codegen/record-with.fsx --check
```

`check.sh` uses `$FLATC`, else a `flatc` 25.2.10 on `PATH`, else (on x86_64
Linux) the release binary, downloaded once into `target/codegen` and checked
against its sha256.
