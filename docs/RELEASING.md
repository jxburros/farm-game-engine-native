# Releasing Farming RPG Maker

Windows releases are built by GitHub Actions, packaged with
[Velopack](https://velopack.io) and published as GitHub Releases of
`jxburros/farm-game-engine-native`. The in-app **Update Center**
(Help → Update Center…) reads those releases, so publishing a release is all
it takes to ship an update.

```
git tag v0.3.0 ──► .github/workflows/release.yml
                     version       on main? CI green? versions agree? not released yet?
                     player-*      templates (Windows, Steam Runtime Linux, web)
                     smoke-linux   export + replay on the release Linux template
                     release       dotnet test, dotnet publish -p:Version=0.3.0,
                                   export + replay on the release Windows template,
                                   vpk download/pack, SHA256SUMS      (read-only token)
                     publish       [environment "release": reviewer approves]
                                   attest, vpk upload github, gh release upload
                                   │
installed app ◄── Update Center ◄──┘  (Velopack GithubSource + GitHub releases API)
```

![Update Center](media/update-center.png)

## Cutting a release

Versions are [SemVer](https://semver.org). The tag is `v` + version.

| Kind        | Example tag       | GitHub Release | Offered to               |
|-------------|-------------------|----------------|--------------------------|
| Stable      | `v0.2.0`          | normal         | Stable + Pre-release     |
| Pre-release | `v0.3.0-beta.1`   | pre-release    | Pre-release channel only |

Anything with a `-suffix` is a pre-release.

1. On `main`, commit the version: set `version` in the root `Cargo.toml`
   (`[workspace.package]`) and the default `<Version>` in
   `Directory.Build.props` to the release version (run `cargo check` so
   `Cargo.lock` follows), and rename the CHANGELOG heading
   `## 0.3.0 (unreleased)` to `## 0.3.0`. `tools/release/versions.sh --release
   0.3.0` checks all three; the workflow runs it and refuses a mismatch.
   (Pre-releases such as `0.4.0-beta.1` need the version in `Cargo.toml` and
   `Directory.Build.props`, not a CHANGELOG section.)
2. Push and wait until CI is green on that commit. The workflow refuses a
   commit that is not on `main` or has no successful CI run (it waits for a
   run still in progress, up to an hour).
3. Release notes come from the first of these the workflow finds:
   1. a section in `CHANGELOG.md` whose heading is `## [0.3.0]`, `## 0.3.0`
      or `## v0.3.0` (everything up to the next `## ` heading);
   2. the message of an **annotated** tag (`git tag -a v0.3.0 -m "…"`, or
      `git tag -a v0.3.0` to write it in your editor — Markdown is fine);
   3. otherwise, a list of commit subjects since the previous `v*` tag.
4. Tag and push:

   ```sh
   git tag -a v0.3.0 -m "## What's new

   - Fishing mini-game
   - Faster project loading"
   git push origin v0.3.0
   ```

   Or run the **Release** workflow manually (Actions → Release → Run workflow)
   on `main` with a version such as `0.3.0`; it creates the tag `v0.3.0` on
   that commit.
5. Watch the run. When the build is done, the **Publish** job waits for a
   reviewer to approve the `release` environment; then the release is
   published (not a draft).
6. Start the next version: bump `Cargo.toml` and `Directory.Build.props` to
   `0.4.0-dev` and add `## 0.4.0 (unreleased)` at the top of `CHANGELOG.md`
   (CI's `tools/release/versions.sh` checks that they agree).

A version is released once. The workflow fails when its GitHub Release
already exists (or, for a manual run, its tag), and it never merges into or
replaces the files of a published release: installed apps would see
different bytes under the same version. If a publish fails half-way, run the
workflow manually with the same version and **republish** checked; it uploads
into the existing release (`vpk upload --merge`, replacing attached files).
Never use it for a release users already have; release a new version
instead.

The notes are embedded in the Velopack package (`vpk pack --releaseNotes`),
become the GitHub Release body, and are shown in the Update Center.

### What the workflow produces

Assets attached to the GitHub Release (Velopack's default `win` channel):

| File                                   | Purpose                                                              |
|----------------------------------------|----------------------------------------------------------------------|
| `FarmingRpgMaker-win-Setup.exe`        | Installer for new users. Installs per-user to `%LocalAppData%\FarmingRpgMaker`, adds Start-menu and desktop shortcuts, no admin rights needed. |
| `FarmingRpgMaker-win-Portable.zip`     | Portable copy (unzip and run; still self-updates).                   |
| `FarmingRpgMaker-X.Y.Z-full.nupkg`     | Full update package.                                                 |
| `FarmingRpgMaker-X.Y.Z-delta.nupkg`    | Delta from the previous release (small download). Only when a previous release exists. |
| `releases.win.json`, `assets.win.json` | Update feed read by the app.                                         |
| `RELEASES`                             | Legacy (Squirrel-compatible) feed.                                   |
| `player-windows-x64.zip`, `player-linux-x64.tar.gz`, `player-web.tar.gz` | Export Game player templates for this version (docs/EXPORT.md). The installer already ships them in `players/`. |
| `SHA256SUMS`                           | SHA-256 of this version's installer, packages, feeds and templates. |
| `build-info.txt`                       | Commit, .NET SDK, rustc and vpk versions and the runner image the release was built with. |

Everything `publish` uploads is kept as a workflow artifact
(`release-bundle-X.Y.Z`, 30 days) for debugging.

The player templates are built first, in their own jobs: `windows-x64` on the
Windows runner (with the placeholder icon and version resources),
`linux-x64` in the Steam Runtime 3 "sniper" SDK container and `web` with
wasm-bindgen. Their `template.json` carries the release version, which must
equal the app's. Before anything is packed, a sample game is exported with
each desktop template the release ships and replayed headless
(`tools/release/smoke-export.sh`): the exported game must report the same run
as its template, and the Windows and Linux templates the same run as each
other.

### How a release is protected

- **Only `main`, only green, only once.** The `version` job checks that the
  commit is on `main`, that `ci.yml` passed on it, that the repository is at
  the release version, and that the version is not released yet, before
  anything is built.
- **Least privilege.** The workflow token is read-only in every job that
  builds or tests (they compile hundreds of crates and run NuGet build
  targets), and no checkout keeps credentials. Only `publish` can write. It
  checks out nothing and runs no repository code: it attests and uploads the
  bundle `release` built.
- **Approval.** `publish` runs in the `release` environment. In the repository
  settings (Settings → Environments → `release`), add required reviewers and
  restrict deployments to tags matching `v*.*.*`. Also add a tag ruleset
  (Settings → Rules → Rulesets) that lets only maintainers create `v*` tags.
  Without the environment settings GitHub creates an unprotected `release`
  environment and the job runs without approval.
- **Pinned inputs.** Actions are pinned to commit SHAs (with the version in a
  comment; Dependabot bumps them), the Steam Runtime SDK image to a digest,
  `rustup-init` to a checksum (`tools/ci/install-rust.sh`), the .NET SDK to
  `global.json`'s version (CI turns off its roll-forward), Rust to
  `rust-toolchain.toml`, and every cargo build that ships uses `--locked`.
- **Checksums and provenance.** `SHA256SUMS` lists every published file, and
  GitHub's build provenance attestation covers them. To check a download:
  `sha256sum --check --ignore-missing SHA256SUMS`, or
  `gh attestation verify FarmingRpgMaker-win-Setup.exe --repo jxburros/farm-game-engine-native`.
- **Reproducibility.** .NET release builds set `ContinuousIntegrationBuild`
  (source paths become `/_/`), cargo builds map the runner's paths away
  (`--remap-path-prefix`) and ship without debug info, so no runner path ends
  up in a binary. Zip and nupkg bytes still depend on the runtime's zlib;
  `build-info.txt` records the SDK and toolchains of each release.

### Versions in builds

The Cargo workspace (`Cargo.toml`) and `Directory.Build.props` carry the
same version: the next release with `-dev` between releases (`0.3.0-dev`),
the release version in the release commit. The release workflow also passes
`-p:Version=X.Y.Z`, which stamps both `FarmingRpgMaker.exe` and
`FarmingRpgMaker.Updates.dll`; `vpk pack --packVersion` uses the same value.
Crash logs, `fe_version` and the web player's `version()` report the Cargo
version.

The `vpk` tool version is pinned in `release.yml` (`VPK_VERSION`) and **must
match** the `Velopack` package version in `Directory.Packages.props` (currently
`1.2.158`); `tools/release/versions.sh` fails CI until both are bumped
together.

### Dependencies

Dependabot (`.github/dependabot.yml`) opens weekly pull requests for the
Cargo crates, the NuGet packages (Avalonia, SkiaSharp and Svg.Skia move
together), the .NET SDK in `global.json` and the pinned actions. The
**Dependency audit** workflow (`audit.yml`, run by CI and weekly) fails on a
RustSec advisory (`cargo deny`, configured in `deny.toml`, over `Cargo.lock`
and the plugin guest's and meter's lock files), on a vulnerable NuGet package
(`tools/ci/dotnet-vulnerable.py`) and on an unused Cargo dependency
(`cargo machete`).

## How the Update Center finds updates

- `VelopackUpdateService` creates
  `new UpdateManager(new GithubSource("https://github.com/jxburros/farm-game-engine-native", accessToken: null, prerelease: channel == Prerelease))`.
  The **Stable** channel only looks at normal releases; **Pre-release** also
  considers pre-releases. Velopack never offers an older version, so switching
  from Pre-release back to Stable keeps the beta until a newer stable release
  ships.
- Release notes, publish date and release link come from
  `GET https://api.github.com/repos/jxburros/farm-game-engine-native/releases`
  (`GitHubReleaseNotesClient`), falling back to the notes inside the package.
- No token is used, so GitHub allows 60 API requests per hour per IP address.
  Rate-limit and offline errors appear as a friendly error in the Update Center,
  never as a crash.
- On startup the app checks in the background (2 s after the window opens)
  if **Check for updates on startup** is on and the last check was more than
  6 hours ago. **Download updates automatically** downloads right after a
  successful check. **Skip this version** hides the header badge and turns off
  auto-download for that version; a newer version is offered again.
- A downloaded update is installed by **Restart & install**, or silently when
  the app exits (`UpdateManager.WaitExitThenApplyUpdates`).
- Preferences live in `%APPDATA%\FarmingRpgMaker\settings.json` under
  `"updates"`.

Development builds (`dotnet run`, an IDE, or an unzipped `dotnet publish`
folder) are not Velopack installs. The Update Center then shows
**"Updates are available only in the installed app"** and offers a link to the
installer instead of checking. To work on the Update Center UI without an
install, set `FARMING_RPG_MAKER_FAKE_UPDATES` to `available`, `uptodate`,
`error` or `notinstalled`:

```sh
FARMING_RPG_MAKER_FAKE_UPDATES=available dotnet run --project src/FarmingRpgMaker.App
```

## Unsigned builds and SmartScreen

Releases are **unsigned** until a code-signing certificate is configured. On
first run of `Setup.exe`, Windows SmartScreen shows *"Windows protected your
PC"*; users must click **More info → Run anyway**. Browsers may also warn
about the download. Updates applied by Velopack afterwards don't trigger
SmartScreen again.

### Adding code signing later

`release.yml` already passes `vpk pack --signParams` when the repository
secret `WINDOWS_SIGN_PARAMS` is set; Velopack then runs `signtool` on the app
binaries and `Setup.exe`.

With a `.pfx` certificate (OV or EV exported to a file):

1. Add the certificate as a base64 secret, e.g. `WINDOWS_CERT_PFX_BASE64`
   (`base64 -w0 cert.pfx`), and its password as `WINDOWS_CERT_PASSWORD`.
2. Add a step before **Pack with Velopack** that writes it to disk:

   ```yaml
   - name: Decode signing certificate
     shell: pwsh
     env:
       PFX: ${{ secrets.WINDOWS_CERT_PFX_BASE64 }}
     run: '[IO.File]::WriteAllBytes("$env:RUNNER_TEMP\cert.pfx", [Convert]::FromBase64String($env:PFX))'
   ```

3. Set `WINDOWS_SIGN_PARAMS` to the `signtool sign` arguments, for example
   `/fd sha256 /td sha256 /tr http://timestamp.digicert.com /f D:\a\_temp\cert.pfx /p <password>`
   (`D:\a\_temp` is `RUNNER_TEMP` on hosted Windows runners).

Hardware-token EV certificates can't be used on hosted runners; use a cloud
signing service instead. For **Azure Trusted Signing**, `vpk pack` has
`--azureTrustedSignFile <metadata.json>` (log in with `azure/login` first) —
replace the `--signParams` branch in the workflow with it. Other services can
be wired through `--signTemplate "<command> {{file}}"`.

SmartScreen reputation is tied to the certificate: EV and Trusted Signing
certificates are trusted immediately, new OV certificates build reputation over
the first downloads.

## Testing an update end to end

1. Tag and publish `v0.1.0` (see above).
2. On a Windows machine, download `FarmingRpgMaker-win-Setup.exe` from the
   release and run it (SmartScreen: **More info → Run anyway**). The app installs
   and starts. Help → About shows **Version 0.1.0**.
3. Help → Update Center → **Check for updates** → *"You're up to date"*.
4. Merge any change, then tag and publish `v0.1.1` with some release notes.
   This release also contains `FarmingRpgMaker-0.1.1-delta.nupkg`.
5. In the running 0.1.0 app, click **Check for updates** (or restart the app;
   the background check runs if the last check is older than 6 hours). The header
   shows **Update available** and the Update Center shows *"Version 0.1.1 is
   available"* with your notes.
6. Click **Download 0.1.1** and watch the progress bar, then
   **Restart & install**. The app closes, updates and relaunches; About now
   shows **Version 0.1.1**.
7. Pre-release channel: publish `v0.2.0-beta.1`. On Stable the app still says
   it's up to date; switch the channel to **Pre-release** and it offers
   0.2.0-beta.1.

Also try: closing the app after downloading (the update is applied on exit),
**Skip this version** (badge disappears), and checking while offline (inline
error, no crash).

### Packaging dry run without a release

`vpk` can build Windows packages from Linux or macOS:

```sh
dotnet publish src/FarmingRpgMaker.App -c Release -r win-x64 --self-contained -p:Version=0.1.0 -o publish
dotnet tool install -g vpk --version 1.2.158
vpk "[win]" pack --packId FarmingRpgMaker --packVersion 0.1.0 --packDir publish \
  --runtime win-x64 --mainExe FarmingRpgMaker.exe --packTitle "Farming RPG Maker" \
  --icon src/FarmingRpgMaker.App/Assets/app.ico --outputDir Releases
```

Nothing is uploaded; inspect `Releases/`. (The `[win]` directive is only for
cross-packaging; the workflow runs on Windows and doesn't need it.)
