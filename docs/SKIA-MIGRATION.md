# SkiaSharp 3 and Avalonia 12 migration plan

Status: **planned, not started.** The mitigations below are in place; the
version upgrade itself is one change still to make (#114).

The editor renders with Avalonia 11, which draws with SkiaSharp 2.88. SVG
import (Svg.Skia) and Export Game's icons (`FarmEngine.Export`) use the same
SkiaSharp. 2.88.9 is the last 2.88 release: fixes to Skia's bundled codecs
(libpng, libjpeg-turbo, libwebp, the GIF and BMP decoders) and to FreeType
and HarfBuzz no longer reach it. The editor decodes images that creators and
players did not make themselves, so this matters.

## What decodes untrusted data

| Path | Input | Code |
|---|---|---|
| Art import | PNG, JPEG, WebP, GIF or BMP files the creator picks | `ArtImport.Reencode` |
| SVG import | SVG files the creator picks (XML is parsed in managed code by Svg.Custom; embedded images are decoded by Skia) | `ArtImport.RasterizeSvg`, `SvgSafety` |
| Project and pack art | `data:image/…` URLs inside imported projects and content packs, shown as thumbnails, previews and sprite sheets | `ArtBitmaps` (Avalonia `Bitmap`, which decodes with Skia) |
| Export Game icons | the project's icon asset | `FarmEngine.Export/Icons.fs` |
| Text | project text is shaped with HarfBuzz and drawn with the bundled Inter font | Avalonia |

Not part of this plan: the Rust player and renderer decode images with the
`image` crate (`crates/farm-render/src/images.rs`, with its own header size
limits), not with Skia.

## Mitigations in place

- **Managed header check before every Skia decode of untrusted data.**
  `ImageHeaders.Read` reads the format and size of PNG, JPEG, GIF, WebP and
  BMP images in managed code. Anything else (ICO, WBMP, HEIF, AVIF, DNG/RAW
  and the other formats Skia sniffs) never reaches a Skia codec, and images
  over the import limits (8192 pixels per side, 16 megapixels) are refused
  before Skia parses them. This covers art import, `ArtBitmaps`
  (thumbnails, previews, the sprite sheet) and images embedded in SVGs
  (`SvgSafety`'s asset loader). Art import and `ArtBitmaps` then also check
  Skia's own reading of the header (`SKCodec`) against the same limits
  before pixels are allocated.
- **Export icons are PNG only.** `Icons.render` checks the PNG signature and
  the codec's format before decoding, although Skia would decode any format it
  knows behind a `data:image/png` label.
- **SVG import is sanitized and fetches nothing** (#74). `ArtImport.CheckSvg`
  refuses scripts, event handlers, foreign HTML, DTD declarations, `xml:base`,
  CSS imports (also written with CSS escapes) and every link that is not a
  `#fragment` or an embedded base64 image. Embedded SVGs are checked
  recursively, at most two deep, and embedded raster images by their header.
  Under that, `SvgSafety` switches off Svg.Skia's external loading
  (`SvgDocument.ResolveExternal*`, DTD processing of nested documents),
  refuses non-`data:` image links in the parsed document, makes
  `WebRequest` refuse http, https, ftp and file URLs for the process (Svg.Model
  loads non-`data:` images with it), and decodes embedded images only after
  the header check.

## Target versions

Checked against the packages' published dependencies (October 2026):

| Package | Now | Target | Why |
|---|---|---|---|
| Avalonia, Avalonia.* | 11.3.22 | 12.1.x | Avalonia 12.1.3's `Avalonia.Skia` depends on SkiaSharp **3.119.4** |
| SkiaSharp, SkiaSharp.NativeAssets.Linux | 2.88.9 | 3.119.4 | must be the version `Avalonia.Skia` uses: one native Skia per process |
| Svg.Skia | 3.7.0 | 5.1.1 | Svg.Skia 4.9–5.1 build on SkiaSharp 3.119; **5.2 and later need SkiaSharp 4.148**, which no Avalonia release uses yet |
| Avalonia.Headless.XUnit | 11.3.22 | 12.1.x | needs **xUnit v3** (`xunit.v3.extensibility.core` 3.2) |

SkiaSharp 4.x (4.153 at the time of writing) waits for an Avalonia release
that renders with it.

An interim step is possible without touching SkiaSharp: **Svg.Skia 4.0 to
4.8 still build on SkiaSharp 2.88.9**, so the SVG library alone can move to
4.8 first. Dependabot's ignore rule (`.github/dependabot.yml`) therefore
only skips Svg.Skia 4.9 and later, and still skips SkiaSharp 3 and later;
both rules go when the upgrade lands, and the `avalonia` group then moves
Avalonia, SkiaSharp and Svg.Skia together.

## Steps

1. **Svg.Skia 4.8 on SkiaSharp 2.88** (optional, separate PR). Check the API
   `SvgSafety` uses (below) and run `SvgImportSafetyTests`.
2. **Bump the packages together** in `Directory.Packages.props`: every
   `Avalonia*`, `SkiaSharp` and `SkiaSharp.NativeAssets.Linux` (3.119.4),
   `Svg.Skia` (5.1.1). Remove the two Dependabot ignore rules.
3. **Move the app tests to xUnit v3**: `xunit` → `xunit.v3` in
   `tests/FarmingRpgMaker.App.Tests` (the F# tests can stay on xUnit 2 with
   FsCheck.Xunit, or move separately). xUnit v3 test projects are
   executables (`OutputType` Exe), `IAsyncLifetime` returns `ValueTask`, and
   `Assert` and `TheoryData` live in the same namespaces.
4. **Fix the build** (the API changes below). The build treats warnings as
   errors, so obsolete members fail it.
5. **Run the safety net** on Linux and Windows: the whole app test suite
   (headless UI tests with real Skia rendering, the accessibility audit,
   `SvgImportSafetyTests`, `ImportSafetyTests`), the Export tests
   (`IconTests`, the PE icon test) and the editor screenshot comparison
   (`fixtures/editor`). Expect the screenshots to change slightly (text
   rasterization): look at every difference before re-recording with
   `FARM_EDITOR_BLESS=1`.
6. **Check by hand** what the headless tests can't: GPU rendering, window
   chrome, file pickers, the clipboard, IME and high-DPI on Windows; X11 and
   Wayland on Linux.
7. **Regenerate the editor license notices** (`node
   tools/editor-licenses/generate.mjs`), add a CHANGELOG entry, and remove
   the "planned" status from this file.

## API changes found in the code

Checked by loading the target packages' assemblies and looking up every
member this repository uses:

- **SkiaSharp 3.119:** `SKPaint.FilterQuality` / `SKFilterQuality` are
  obsolete ("Use SKSamplingOptions instead"). `Icons.fs` (`renderOne`) sets
  `FilterQuality = SKFilterQuality.High` and calls `DrawBitmap`; draw an
  `SKImage` with `SKSamplingOptions` instead (for example
  `SKSamplingOptions(SKCubicResampler.Mitchell)`), then compare the icon
  tests' output. Everything else the code uses still exists unchanged:
  `SKCodec.Create(SKData)`, `SKCodec.EncodedFormat`, `SKBitmap.Decode(SKCodec)`,
  `SKData.CreateCopy(byte[])`, `SKImage.FromBitmap`, `SKImage.Encode(format,
  quality)`, `SKCanvas.DrawPicture`, `SKSurface.Create(SKImageInfo)`,
  `SKSurface.Snapshot`, `SKBitmap.Pixels` and `GetPixel` (tests).
- **Svg.Skia / Svg.Model / Svg.Custom 5.1:** what `SvgSafety` uses is still
  there: `SKSvg.ToPicture(SvgFragment, SkiaModel, ISvgAssetLoader)`,
  `ISvgAssetLoader` with the same five members, `SvgService.Open(XmlReader)`,
  the static `SvgDocument.ResolveExternalImages`, `ResolveExternalElements`,
  `ResolveExternalXmlEntites` and `DisableDtdProcessing`, and
  `SvgImage.Href`. Svg.Model 5.1 still loads non-`data:` images through
  `WebRequest` (`GetImageFromWeb`), so the `WebRequest` block keeps working;
  re-check it in every Svg.Skia update, because
  `SvgImportSafetyTests.TheRendererFetchesNothingEvenWithoutTheCheck` (a
  loopback listener and a local file) is what notices when it stops. New in
  5.x and to be kept off: a JavaScript runtime hook (`EnableJavaScript`,
  `EnableExternalJavaScript`, `JavaScriptRuntimeFactory` in
  `SKSvgJavaScriptRuntimeSettings`) and a new `ISvgImageAssetLoader`
  interface; `SvgSafety` must leave scripting disabled and route image loads
  through its checked loader.
- **Avalonia 12** (to check while fixing the build; the app's uses, by
  file):
  - headless testing: `[AvaloniaFact]` / `[AvaloniaTheory]` (27 test files),
    `CaptureRenderedFrame`, `AvaloniaHeadlessPlatform.ForceRenderTimerTick`,
    `KeyPressQwerty`, `UseHeadlessDrawing = false` (`TestAppBuilder`);
  - `WriteableBitmap.Lock()` with `PixelFormat.Rgba8888` /
    `AlphaFormat.Premul` (`MapCanvas`, `PlayerSurface`, `VisualPreview`: the
    frames copied from the Rust renderer);
  - `Bitmap.DecodeToWidth` / `DecodeToHeight` (`ArtBitmaps`);
  - the storage provider file pickers (`ProjectDialogs`, `ArtEditorView`,
    `ModsEditorView`) and the clipboard (`IShellHost`);
  - compiled bindings with `x:DataType` in the AXAML views, the Fluent theme
    and `Avalonia.Fonts.Inter`.

## Risks

- **Rendering differences.** Skia 3 rasterizes text and images slightly
  differently. The screenshot comparison tolerates anti-aliasing; anything it
  flags must be looked at, not re-recorded blindly.
- **xUnit v3 migration** touches every app test file's attributes and the
  test project's packages; it is mechanical but large, and the CI coverage
  collection (`coverlet.collector`) must keep working.
- **SVG security regressions.** Svg.Skia 5 adds features (scripting hooks,
  new loaders). The sanitizer is the first line; the renderer-level tests are
  the check that the second line still holds.
- **One native Skia per process.** Avalonia, Svg.Skia and `FarmEngine.Export`
  must resolve to the same SkiaSharp; a mismatch fails at run time
  (`EntryPointNotFoundException`) rather than at build time. Check
  `dotnet list package --include-transitive` after the bump.
- **Velopack, `Microsoft.NET.Test.Sdk` and the smaller packages** are not tied
  to Skia and update through the normal Dependabot groups.
