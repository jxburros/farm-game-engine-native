using Avalonia;
using Avalonia.Headless;
using FarmingRpgMaker.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace FarmingRpgMaker.App.Tests;

/// <summary>Headless Avalonia with real Skia rendering, so windows can be captured to PNG.</summary>
public static class TestAppBuilder
{
    /// <summary>Per-run data directory so no test ever touches the real %APPDATA% projects.</summary>
    public static readonly string DataDirectory = CreateDataDirectory();

    private static string CreateDataDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "frm-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable(FarmingRpgMaker.App.Projects.AppDataPaths.DataDirectoryVariable, path);
        return path;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<FarmingRpgMaker.App.App>()
            .AfterSetup(_ => GC.KeepAlive(DataDirectory))
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
