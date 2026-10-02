using Avalonia;
using FarmingRpgMaker.App.Projects;
using FarmingRpgMaker.App.Services;
using Velopack;

namespace FarmingRpgMaker.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks and may exit the process.
        VelopackApp.Build().Run();

        // Everything traced (the editor's diagnostics and Avalonia's own warnings) also goes to
        // a rolling log under the data folder, so a release build has a log to send (Help → About).
        FileLog.Install(AppDataPaths.LogsDirectory());
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            System.Diagnostics.Trace.TraceError($"Unhandled exception (terminating: {e.IsTerminating}): {e.ExceptionObject}");
            System.Diagnostics.Trace.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Trace.TraceError($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            System.Diagnostics.Trace.Flush();
        }
    }

    /// <summary>Also used by the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
