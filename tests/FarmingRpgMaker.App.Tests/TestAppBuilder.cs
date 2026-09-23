using Avalonia;
using Avalonia.Headless;
using FarmingRpgMaker.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace FarmingRpgMaker.App.Tests;

/// <summary>Headless Avalonia with real Skia rendering, so windows can be captured to PNG.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<FarmingRpgMaker.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
