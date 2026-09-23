# Releasing Farming RPG Maker

Windows releases are built by GitHub Actions, packaged with
[Velopack](https://velopack.io) and published as GitHub Releases of
`jxburros/farm-game-engine-native`. The in-app **Update Center**
(Help → Update Center…) reads those releases, so publishing a release is all
it takes to ship an update.

```
git tag v0.2.0 ──► .github/workflows/release.yml (windows-latest)
                     dotnet test
                     dotnet publish -r win-x64 --self-contained -p:Version=0.2.0
                     vpk download github   (previous release → delta updates)
                     vpk pack              (Setup.exe, Portable.zip, nupkgs, feed)
                     vpk upload github     (creates/publishes the GitHub Release)
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

1. Make sure `main` is green in CI.
2. Write release notes (optional but recommended), in one of these places —
   the workflow uses the first one it finds:
   1. a section in `CHANGELOG.md` whose heading is `## [0.2.0]`, `## 0.2.0`
      or `## v0.2.0` (everything up to the next `## ` heading);
   2. the message of an **annotated** tag (`git tag -a v0.2.0 -m "…"`, or
      `git tag -a v0.2.0` to write it in your editor — Markdown is fine);
   3. otherwise, a list of commit subjects since the previous `v*` tag.
3. Tag and push:

   ```sh
   git tag -a v0.2.0 -m "## What's new

   - Fishing mini-game
   - Faster project loading"
   git push origin v0.2.0
   ```

   Or run the **Release** workflow manually (Actions → Release → Run workflow)
   with a version such as `0.2.0`; it creates the tag `v0.2.0` on the commit you
   ran it from.
4. Watch the run. When it finishes, the release is published (not a draft).

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

The same files are kept as a workflow artifact (`velopack-releases-X.Y.Z`,
30 days) for debugging.

### Versions in builds

`Directory.Build.props` defaults the version to `0.1.0-dev`. The release
workflow passes `-p:Version=X.Y.Z`, which stamps both `FarmingRpgMaker.exe` and
`FarmingRpgMaker.Updates.dll`; `vpk pack --packVersion` uses the same value.

The `vpk` tool version is pinned in `release.yml` (`VPK_VERSION`) and **must
match** the `Velopack` package version in `Directory.Packages.props` (currently
`1.2.158`). Bump both together.

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
