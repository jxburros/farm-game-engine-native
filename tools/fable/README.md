# Fable build of the authoring core

The web editor runs this repository's F# authoring core
(`src/FarmEngine.Authoring`: migrations, validation, Problems, the content and
cartridge compilers) compiled to JavaScript with [Fable](https://fable.io).
See "F# conventions" in [docs/LANGUAGES.md](../../docs/LANGUAGES.md) for what
keeps the core Fable-safe.

```sh
tools/fable/build.sh        # dotnet tool restore, then Fable into tools/fable/dist (not checked in)
node tools/fable/smoke.mjs  # Node 22: run the JavaScript against the .NET results
```

`build.sh` needs the .NET SDK; Fable's version is pinned in the local tool
manifest (`dotnet-tools.json`). The entry point is `dist/WebApi.js`
(`src/FarmEngine.Authoring/WebApi.fs`): JSON text in and out.

`smoke.mjs` checks that the JavaScript build compiles the sample projects to
the same cartridges as .NET (`fixtures/golden/cartridges`), reproduces every
migration golden, and gives every template a clean Problems list. CI runs
both in the "Build & test (Linux)" job.
