using Avalonia.Media.Imaging;
using SkiaSharp;

namespace FarmingRpgMaker.App.Tests.Ui;

/// <summary>
/// Compares editor screenshots with the references in <c>fixtures/editor/</c>. A reference is the
/// window at half size; the comparison averages blocks of 8×8 reference pixels and fails when
/// more than a few blocks differ, so anti-aliasing and font hinting don't matter but a moved,
/// missing or recoloured control does.
/// <para>
/// After an intended change to the editor's look, rerun with <c>FARM_EDITOR_BLESS=1</c>: the
/// references that changed are rewritten and the run fails on purpose; rerun without the switch
/// and look at the images before committing them (CONTRIBUTING.md, "Updating test fixtures").
/// The references are Linux renders (the CI Linux job and the dev container); on other systems,
/// where Skia draws text with the platform's rasterizer, the capture is neither compared nor
/// recorded.
/// </para>
/// </summary>
internal static class ScreenshotGoldens
{
    public const string BlessVariable = "FARM_EDITOR_BLESS";

    /// <summary>Size of a compared block, in reference pixels.</summary>
    internal const int Block = 8;

    /// <summary>Mean absolute channel difference (0–255) above which a block counts as different.</summary>
    internal const double BlockTolerance = 12;

    /// <summary>Different blocks allowed: this fraction of all blocks, at least <see cref="MinDifferentBlocks"/>.</summary>
    internal const double MaxDifferentFraction = 0.002;

    internal const int MinDifferentBlocks = 2;

    private static bool Blessing => Environment.GetEnvironmentVariable(BlessVariable) == "1";

    /// <summary>The checked-in reference images.</summary>
    public static string ReferenceDirectory { get; } = Path.Combine(RepositoryRoot(), "fixtures", "editor");

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FarmingRpgMaker.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (FarmingRpgMaker.sln) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Checks <paramref name="frame"/> against <c>fixtures/editor/<paramref name="name"/></c>.</summary>
    public static void Check(Bitmap frame, string name)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!OperatingSystem.IsLinux())
        {
            return; // the references are Linux renders, and are recorded on Linux only
        }

        using var full = Decode(frame);
        using var actual = HalfSize(full);
        var path = Path.Combine(ReferenceDirectory, name);
        if (Blessing)
        {
            if (!File.Exists(path) || Compare(actual, path) is not null)
            {
                Directory.CreateDirectory(ReferenceDirectory);
                using var data = actual.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, data.ToArray());
                Assert.Fail($"{BlessVariable}=1 recorded {path}; rerun without it and review the image.");
            }

            return;
        }

        Assert.True(File.Exists(path), $"No reference {path}; record it with {BlessVariable}=1.");
        if (Compare(actual, path) is { } problem)
        {
            var output = Path.Combine(Path.GetTempPath(), "frm-editor-screenshots");
            Directory.CreateDirectory(output);
            var saved = Path.Combine(output, Path.GetFileNameWithoutExtension(name) + ".actual.png");
            using (var data = actual.Encode(SKEncodedImageFormat.Png, 100))
            {
                File.WriteAllBytes(saved, data.ToArray());
            }

            Assert.Fail($"{name} differs from its reference: {problem}. The capture is in {saved}; " +
                $"after an intended change, rerun with {BlessVariable}=1.");
        }
    }

    /// <summary>Why <paramref name="actual"/> doesn't match the reference image at <paramref name="path"/>, or null.</summary>
    internal static string? Compare(SKBitmap actual, string path)
    {
        using var expected = SKBitmap.Decode(path);
        if (expected is null)
        {
            return "the reference can't be decoded";
        }

        return Compare(actual, expected);
    }

    internal static string? Compare(SKBitmap actual, SKBitmap expected)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height)
        {
            return $"size {actual.Width}×{actual.Height}, expected {expected.Width}×{expected.Height}";
        }

        var a = actual.Pixels;
        var e = expected.Pixels;
        var columns = (actual.Width + Block - 1) / Block;
        var rows = (actual.Height + Block - 1) / Block;
        var different = 0;
        var worst = 0.0;
        for (var by = 0; by < rows; by++)
        {
            for (var bx = 0; bx < columns; bx++)
            {
                long sum = 0;
                var count = 0;
                for (var y = by * Block; y < Math.Min((by + 1) * Block, actual.Height); y++)
                {
                    for (var x = bx * Block; x < Math.Min((bx + 1) * Block, actual.Width); x++)
                    {
                        var p = a[(y * actual.Width) + x];
                        var q = e[(y * actual.Width) + x];
                        sum += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) + Math.Abs(p.Alpha - q.Alpha);
                        count += 4;
                    }
                }

                var mean = (double)sum / count;
                worst = Math.Max(worst, mean);
                if (mean > BlockTolerance)
                {
                    different++;
                }
            }
        }

        var allowed = Math.Max(MinDifferentBlocks, (int)(columns * rows * MaxDifferentFraction));
        return different > allowed
            ? $"{different} of {columns * rows} blocks of {Block}×{Block} differ (allowed {allowed}; worst mean difference {worst:0.0})"
            : null;
    }

    private static SKBitmap Decode(Bitmap frame)
    {
        using var png = new MemoryStream();
        frame.Save(png);
        png.Position = 0;
        using var decoded = SKBitmap.Decode(png) ?? throw new InvalidOperationException("The capture can't be decoded.");
        // A fixed pixel format, whatever the capture used.
        var converted = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(converted))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(decoded, 0, 0);
        }

        return converted;
    }

    /// <summary>The image at half size, each pixel the average of a 2×2 square.</summary>
    internal static SKBitmap HalfSize(SKBitmap full)
    {
        var width = Math.Max(1, full.Width / 2);
        var height = Math.Max(1, full.Height / 2);
        var source = full.Pixels;
        var pixels = new SKColor[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0, alpha = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var p = source[(Math.Min((2 * y) + dy, full.Height - 1) * full.Width) + Math.Min((2 * x) + dx, full.Width - 1)];
                        r += p.Red;
                        g += p.Green;
                        b += p.Blue;
                        alpha += p.Alpha;
                    }
                }

                pixels[(y * width) + x] = new SKColor((byte)((r + 2) / 4), (byte)((g + 2) / 4), (byte)((b + 2) / 4), (byte)((alpha + 2) / 4));
            }
        }

        var half = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)) { Pixels = pixels };
        return half;
    }
}
