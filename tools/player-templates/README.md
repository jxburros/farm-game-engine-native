# Player templates

Export Game builds games from prebuilt player templates instead of compiling
anything ([docs/EXPORT.md](../../docs/EXPORT.md), "Player templates").
`package.sh` stages one template folder:

```text
<out>/<target>/farm-player[.exe]   the player for <target> (web: farm_wasm_bg.wasm, farm_wasm.js and the page files)
<out>/<target>/template.json       { "target", "version", "sha256", "files" }: hashes export checks
<out>/<target>/THIRD-PARTY.txt     tools/player-licenses/THIRD-PARTY.txt
```

```sh
tools/player-templates/package.sh <windows-x64|linux-x64> <version> <player executable> <out dir>
tools/player-templates/package.sh web <version> <tools/wasm/dist folder> <out dir>
```

The editor refuses a template whose version differs from its own, so
`<version>` must be the version the app is published with. The release
workflow runs it for every target; development builds get the same layout
from the `FarmPlayerTemplates` target in
`src/FarmEngine.Export/FarmEngine.Export.fsproj`, which puts the host's
template in `players/` next to the app and `farmc`.
