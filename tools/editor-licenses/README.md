# Editor license notices

`THIRD-PARTY-dotnet.txt` lists the software the editor ships besides its own
code and the Rust engine library: every NuGet package with run-time assets in
`src/FarmingRpgMaker.App`'s restore graph (with its license and copyright from
its `.nuspec`, and the license files and third-party notices inside the
package), the .NET runtime, the Material Design Icons and the Inter typeface.
The app copies it to `licenses/THIRD-PARTY-dotnet.txt`, next to
`THIRD-PARTY-rust.txt` ([`tools/player-licenses`](../player-licenses/README.md))
and `LICENSE.txt`.

```sh
dotnet restore src/FarmingRpgMaker.App
node tools/editor-licenses/generate.mjs           # rewrite THIRD-PARTY-dotnet.txt
node tools/editor-licenses/generate.mjs --check   # fail when it is out of date (CI)
```

Run it after changing a version in `Directory.Packages.props` and commit the
result. A package under a license with no text in `texts/` stops the script:
add the license's text there (from [SPDX](https://spdx.org/licenses/)).
