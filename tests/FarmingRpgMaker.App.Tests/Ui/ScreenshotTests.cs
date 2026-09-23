using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.ViewModels;
using FarmingRpgMaker.App.Views;
using FarmingRpgMaker.Updates;
using FarmingRpgMaker.Updates.Testing;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Ui;

/// <summary>
/// Renders the main window and the Update Center with Skia. Always checks that a frame can be
/// captured; set <c>FRM_SCREENSHOT_DIR</c> to also save PNGs (used for docs/media).
/// </summary>
public sealed class ScreenshotTests
{
    private static readonly string? OutputDir = Environment.GetEnvironmentVariable("FRM_SCREENSHOT_DIR");

    [AvaloniaFact]
    public void MainWindow_WithUpdateBadge()
    {
        var fake = new FakeUpdateService { NextResult = FakeUpdateService.SampleUpdate("0.2.0") };
        var coordinator = new UpdateCoordinator(fake, new InMemorySettingsStore());
        var viewModel = new MainWindowViewModel(coordinator, ShellComposition.CreateDefault());
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
        var time = new TestTime(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
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
        var coordinator = new UpdateCoordinator(fake, new InMemorySettingsStore());
        var window = new UpdateCenterWindow { DataContext = new UpdateCenterViewModel(coordinator, new RecordingUrlLauncher()) };
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
        var coordinator = new UpdateCoordinator(fake, new InMemorySettingsStore());
        var window = new UpdateCenterWindow { DataContext = new UpdateCenterViewModel(coordinator, new RecordingUrlLauncher()) };
        window.Show();
        Pump();

        Save(window, "update-center-not-installed.png");
    }

    private static void Save(Avalonia.Controls.Window window, string fileName)
    {
        Pump();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame.PixelSize.Width > 0);
        if (!string.IsNullOrEmpty(OutputDir))
        {
            Directory.CreateDirectory(OutputDir);
            frame.Save(Path.Combine(OutputDir, fileName));
        }

        window.Close();
    }
}
