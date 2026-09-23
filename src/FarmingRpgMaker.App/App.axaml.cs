using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Services;
using FarmingRpgMaker.App.ViewModels;
using FarmingRpgMaker.App.Views;
using FarmingRpgMaker.Updates;
using FarmingRpgMaker.Updates.Testing;

namespace FarmingRpgMaker.App;

public sealed class App : Application
{
    /// <summary>
    /// Set to <c>available</c>, <c>uptodate</c>, <c>error</c> or <c>notinstalled</c> to run the
    /// Update Center against <see cref="FakeUpdateService"/> (UI demos, screenshots).
    /// </summary>
    public const string FakeUpdatesVariable = "FARMING_RPG_MAKER_FAKE_UPDATES";

    /// <summary>Delay before the background startup check, so it never competes with first paint.</summary>
    public static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(2);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new JsonSettingsStore();
            var service = CreateUpdateService(store.Load().Channel);
            var coordinator = new UpdateCoordinator(service, store);
            var viewModel = new MainWindowViewModel(coordinator, ShellComposition.CreateDefault());
            var window = new MainWindow(new ShellUrlLauncher()) { DataContext = viewModel };
            desktop.MainWindow = window;

            desktop.ShutdownRequested += (_, _) => coordinator.ApplyOnExitIfReady();
            window.Opened += (_, _) => DispatcherTimer.RunOnce(
                () => _ = RunStartupCheckAsync(coordinator),
                StartupCheckDelay);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunStartupCheckAsync(UpdateCoordinator coordinator)
    {
        try
        {
            await coordinator.RunStartupCheckAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed background check must never affect the app.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceWarning($"Startup update check failed: {ex}");
        }
    }

    private static IUpdateService CreateUpdateService(UpdateChannel channel)
    {
        var fake = Environment.GetEnvironmentVariable(FakeUpdatesVariable);
        if (string.IsNullOrWhiteSpace(fake))
        {
            return new VelopackUpdateService(channel);
        }

        return new FakeUpdateService
        {
            Channel = channel,
            IsInstalled = !fake.Equals("notinstalled", StringComparison.OrdinalIgnoreCase),
            CheckDelay = TimeSpan.FromMilliseconds(600),
            NextResult = fake.ToLowerInvariant() switch
            {
                "uptodate" => new UpdateCheckResult.UpToDate(),
                "error" => new UpdateCheckResult.Error("Couldn't reach GitHub. Check your internet connection and try again."),
                _ => FakeUpdateService.SampleUpdate(),
            },
        };
    }
}
