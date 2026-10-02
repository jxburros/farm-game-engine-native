using Avalonia.Headless.XUnit;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Tests.Game;
using FarmingRpgMaker.App.ViewModels;
using FarmingRpgMaker.App.Views;
using FarmingRpgMaker.Updates;
using FarmingRpgMaker.Updates.Testing;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Ui;

/// <summary>
/// Renders the main window and the Update Center with Skia and compares each frame with its
/// reference in <c>fixtures/editor/</c> (<see cref="ScreenshotGoldens"/>; re-record with
/// <c>FARM_EDITOR_BLESS=1</c>). Set <c>FRM_SCREENSHOT_DIR</c> to also save full-size PNGs (used
/// for docs/media). Every window runs on a fixed clock, so no date or "time ago" moves.
/// </summary>
public sealed class ScreenshotTests
{
    private static readonly string? OutputDir = Environment.GetEnvironmentVariable("FRM_SCREENSHOT_DIR");

    private static TestTime Clock() => new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

    [AvaloniaFact]
    public void MainWindow_WithUpdateBadge()
    {
        var fake = new FakeUpdateService { NextResult = FakeUpdateService.SampleUpdate("0.2.0") };
        var coordinator = new UpdateCoordinator(fake, new InMemorySettingsStore(), Clock());
        // Its own data folder: other tests open and create projects in the shared one.
        using var data = new TempDir();
        var viewModel = new MainWindowViewModel(coordinator, ShellComposition.CreateDefault(data.Path));
        var window = new MainWindow(new RecordingUrlLauncher()) { DataContext = viewModel, Width = 1200, Height = 720 };
        window.Show();
        _ = coordinator.CheckAsync();
        PumpUntil(() => viewModel.IsUpdateBadgeVisible, "badge");

        Save(window, "main-window.png");
    }

    [AvaloniaFact]
    public void UpdateCenter_UpdateAvailable()
    {
        var fake = new FakeUpdateService { CurrentVersion = "0.1.0", NextResult = FakeUpdateService.SampleUpdate("0.2.0") };
        var time = Clock();
        var coordinator = new UpdateCoordinator(fake, new InMemorySettingsStore(), time);
        var window = new UpdateCenterWindow { DataContext = new UpdateCenterViewModel(coordinator, new RecordingUrlLauncher(), time), Height = 900 };
        window.Show();
        _ = coordinator.CheckAsync();
        PumpUntil(() => coordinator.State == UpdateState.Available, "available");

        Save(window, "update-center.png");
    }

    [AvaloniaFact]
    public void UpdateCenter_Downloading()
    {
        var fake = new FakeUpdateService { NextResult = FakeUpdateService.SampleUpdate("0.2.0"), DownloadSteps = [62, 100], HoldDownload = true };
        var time = Clock();
        var coordinator = new UpdateCoordinator(fake, new InMemorySettingsStore(), time);
        var window = new UpdateCenterWindow { DataContext = new UpdateCenterViewModel(coordinator, new RecordingUrlLauncher(), time) };
        window.Show();
        _ = coordinator.CheckAsync();
        PumpUntil(() => coordinator.State == UpdateState.Available, "available");
        _ = coordinator.DownloadAsync();
        PumpUntil(() => coordinator.DownloadProgress == 62, "62%");

        Save(window, "update-center-downloading.png");
        fake.ReleaseDownload();
        PumpUntil(() => coordinator.State == UpdateState.ReadyToInstall, "ready");
    }

    [AvaloniaFact]
    public void UpdateCenter_NotInstalled()
    {
        var fake = new FakeUpdateService { IsInstalled = false, CurrentVersion = "0.1.0-dev" };
        var time = Clock();
        var coordinator = new UpdateCoordinator(fake, new InMemorySettingsStore(), time);
        var window = new UpdateCenterWindow { DataContext = new UpdateCenterViewModel(coordinator, new RecordingUrlLauncher(), time) };
        window.Show();
        Pump();

        Save(window, "update-center-not-installed.png");
    }

    private static void Save(Avalonia.Controls.Window window, string fileName)
    {
        var frame = GameTestHost.Capture(window);
        Assert.True(frame.PixelSize.Width > 0);
        if (!string.IsNullOrEmpty(OutputDir))
        {
            Directory.CreateDirectory(OutputDir);
            frame.Save(Path.Combine(OutputDir, fileName));
        }

        window.Close();
        ScreenshotGoldens.Check(frame, fileName);
    }

    [Fact]
    public void TheComparisonToleratesNoiseButNotAMissingControl()
    {
        using var reference = Picture(withButton: true, noise: 0);
        using var noisy = Picture(withButton: true, noise: 6);
        using var missing = Picture(withButton: false, noise: 0);
        Assert.Null(ScreenshotGoldens.Compare(noisy, reference));
        Assert.Contains("blocks", ScreenshotGoldens.Compare(missing, reference), StringComparison.Ordinal);
        using var smaller = ScreenshotGoldens.HalfSize(reference);
        Assert.Contains("size", ScreenshotGoldens.Compare(smaller, reference), StringComparison.Ordinal);
    }

    /// <summary>A 320×200 "window" with an optional 40×16 "button" and a pseudo-random dither.</summary>
    private static SkiaSharp.SKBitmap Picture(bool withButton, int noise)
    {
        var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(320, 200, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        var random = new Random(7);
        var pixels = new SkiaSharp.SKColor[320 * 200];
        for (var y = 0; y < 200; y++)
        {
            for (var x = 0; x < 320; x++)
            {
                var button = withButton && x is >= 100 and < 140 && y is >= 50 and < 66;
                var shade = (button ? 40 : 235) + (noise == 0 ? 0 : random.Next(-noise, noise + 1));
                var value = (byte)Math.Clamp(shade, 0, 255);
                pixels[(y * 320) + x] = new SkiaSharp.SKColor(value, value, value, 255);
            }
        }

        bitmap.Pixels = pixels;
        return bitmap;
    }
}
