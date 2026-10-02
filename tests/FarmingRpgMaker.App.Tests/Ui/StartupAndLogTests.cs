using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Projects;
using FarmingRpgMaker.App.Services;
using FarmingRpgMaker.App.Tests.Game;
using FarmingRpgMaker.App.ViewModels;
using FarmingRpgMaker.App.Views;
using FarmingRpgMaker.Updates;
using FarmingRpgMaker.Updates.Testing;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Ui;

/// <summary>What the editor reports at startup, and its log on disk.</summary>
public sealed class StartupAndLogTests
{
    [AvaloniaFact]
    public void AStartupProjectThatFailsToLoad_IsShownOnceTheWindowHasOpened()
    {
        var dir = Path.Combine(TestAppBuilder.DataDirectory, "startup-" + Guid.NewGuid().ToString("N"));
        var store = new ProjectStore(dir);
        Directory.CreateDirectory(store.ProjectsDirectory);
        File.WriteAllText(store.PathFor("broken"), "{ not json");
        new AppSettingsStore(AppDataPaths.SettingsFile(dir)).Save(new WorkspaceSettings { LastProjectId = "broken" });

        var viewModel = new MainWindowViewModel(new UpdateCoordinator(new FakeUpdateService(), new InMemorySettingsStore()), ShellComposition.CreateDefault(dir));
        // Before the window exists, only the status line can say it.
        Assert.Equal("Opened \"Starter Farm\" instead. A project could not be loaded.", viewModel.StatusMessage);
        var window = new MainWindow(new RecordingUrlLauncher()) { DataContext = viewModel };
        window.Show();

        Window? dialog = null;
        PumpUntil(() => (dialog = window.OwnedWindows.FirstOrDefault(w => w.Name == "ProjectErrorWindow")) is not null, "startup errors");
        var text = AllVisibleText(dialog!);
        Assert.Contains("Some projects could not be loaded", text, StringComparison.Ordinal);
        Assert.Contains("broken: ", text, StringComparison.Ordinal);
        Assert.Contains("\"Starter Farm\" was opened instead", text, StringComparison.Ordinal);
        dialog!.Close();
        window.Close();
        Pump();
    }

    [Fact]
    public void FileLog_WritesTracedErrors_AndKeepsAWeekOfFiles()
    {
        using var dir = new TempDir();
        var time = new TestTime(new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero));
        for (var day = 1; day <= 9; day++)
        {
            File.WriteAllText(Path.Combine(dir.Path, $"editor-2026-09-{day:00}.log"), "old");
        }

        var log = FileLog.Install(dir.Path, time);
        Assert.NotNull(log);
        try
        {
            Trace.TraceError("Saving project proj-a failed: disk full");
            log!.TraceEvent(null, "test", TraceEventType.Warning, 0, "Braces {0} stay as written");
        }
        finally
        {
            Trace.Listeners.Remove(log);
        }

        var text = File.ReadAllText(Path.Combine(dir.Path, "editor-2026-10-01.log"));
        Assert.Contains("2026-10-01T09:30:00.000Z [Error] Saving project proj-a failed: disk full", text, StringComparison.Ordinal);
        Assert.Contains("[Warning] Braces {0} stay as written", text, StringComparison.Ordinal);
        Assert.Contains("started on", text, StringComparison.Ordinal);
        // The newest week (today's file included) is kept.
        var kept = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(FileLog.KeptFiles + 1, kept.Count);
        Assert.DoesNotContain("editor-2026-09-01.log", kept);
        Assert.Contains("editor-2026-09-09.log", kept);
    }
}
