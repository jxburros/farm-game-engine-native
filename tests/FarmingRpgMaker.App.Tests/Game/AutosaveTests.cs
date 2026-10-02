using Avalonia.Headless.XUnit;
using FarmEngine.Authoring;
using FarmingRpgMaker.App.Projects;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// The debounced autosave on a fake clock (<see cref="TestTime"/>): edits are written once
/// they have been quiet for the delay, every edit restarts it, and a flush stops it.
/// </summary>
public sealed class AutosaveTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    private static (ProjectWorkspace Workspace, TestTime Time) Open(TempDir dir)
    {
        var time = new TestTime(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var workspace = new ProjectWorkspace(new ProjectStore(dir.Path, time), new AppSettingsStore(Path.Combine(dir.Path, "settings.json")), Delay, time);
        Assert.Empty(workspace.OpenStartupProject());
        return (workspace, time);
    }

    private static string Saved(ProjectWorkspace workspace) => workspace.Store.Load(workspace.Current!.Id).Project!.Name;

    private static void Rename(ProjectWorkspace workspace, string name) =>
        Assert.True(workspace.Apply(Edits.SetProjectInfo(name, workspace.Current!.Version)));

    /// <summary>Moves the fake clock and runs what its timers posted to the UI thread.</summary>
    private static void Wait(TestTime time, double seconds)
    {
        time.Advance(TimeSpan.FromSeconds(seconds));
        Pump();
    }

    [AvaloniaFact]
    public void EditsAreSavedOnceTheyHaveBeenQuietForTheDelay()
    {
        using var dir = new TempDir();
        var (workspace, time) = Open(dir);
        var original = Saved(workspace);

        Rename(workspace, "First");
        Assert.True(workspace.HasPendingSave);
        Wait(time, 0.6);
        Assert.Equal(original, Saved(workspace));

        // Another edit restarts the delay: 1.2 s after the first edit, nothing is written yet.
        Rename(workspace, "Second");
        Wait(time, 0.6);
        Assert.True(workspace.HasPendingSave);
        Assert.Equal(original, Saved(workspace));

        Wait(time, 0.4);
        Assert.False(workspace.HasPendingSave);
        Assert.Equal("Second", Saved(workspace));
        Assert.Equal(0, time.PendingTimers);
        workspace.ReleaseLock();
    }

    [AvaloniaFact]
    public void AFlushWritesAtOnceAndCancelsTheTimer()
    {
        using var dir = new TempDir();
        var (workspace, time) = Open(dir);

        Rename(workspace, "Flushed");
        Assert.Equal(1, time.PendingTimers);
        workspace.FlushPendingSave();
        Assert.Equal("Flushed", Saved(workspace));
        Assert.Equal(0, time.PendingTimers);

        // A later edit waits the whole delay again; the stopped timer never fires on its own.
        Rename(workspace, "Later");
        Wait(time, 0.99);
        Assert.Equal("Flushed", Saved(workspace));
        Wait(time, 0.01);
        Assert.Equal("Later", Saved(workspace));
        workspace.ReleaseLock();
    }

    [AvaloniaFact]
    public void ATickPostedBeforeARestartIsDropped()
    {
        var fired = 0;
        var time = new TestTime(DateTimeOffset.UnixEpoch);
        var timer = new DebounceTimer(time, Delay, () => fired++);

        timer.Start();
        time.Advance(Delay); // the tick is posted to the UI thread, not run yet
        timer.Start();       // re-armed before the UI thread ran it
        Pump();
        Assert.Equal(0, fired);
        Assert.True(timer.IsRunning);

        Wait(time, 1);
        Assert.Equal(1, fired);
        Assert.False(timer.IsRunning);

        timer.Start();
        timer.Stop();
        Wait(time, 5);
        Assert.Equal(1, fired);
    }
}
