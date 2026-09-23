using Avalonia.Controls;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Projects;
using FarmingRpgMaker.App.ViewModels;
using FarmingRpgMaker.App.Views;
using FarmingRpgMaker.App.Tests.Ui;
using FarmingRpgMaker.Updates;
using FarmingRpgMaker.Updates.Testing;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>A temp data directory that is deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The real main window + game surface over a temp data dir, with the frame loop off.</summary>
internal sealed class GameTestHost : IDisposable
{
    private readonly TempDir _dir = new();

    public GameTestHost(int width = 1280, int height = 800, IProjectDialogs? dialogs = null)
    {
        Workspace = new ProjectWorkspace(
            new ProjectStore(_dir.Path),
            new AppSettingsStore(System.IO.Path.Combine(_dir.Path, "settings.json")),
            autosaveDelay: TimeSpan.Zero);
        Composition = ShellComposition.Create(Workspace, new GameSurfaceOptions { AutoRun = false }, dialogs);
        var coordinator = new UpdateCoordinator(new FakeUpdateService(), new InMemorySettingsStore());
        ViewModel = new MainWindowViewModel(coordinator, Composition);
        Window = new MainWindow(new RecordingUrlLauncher()) { DataContext = ViewModel, Width = width, Height = height };
        Window.Show();
        Pump();
    }

    public string DataDirectory => _dir.Path;

    public ProjectWorkspace Workspace { get; }

    public ShellComposition Composition { get; }

    public MainWindowViewModel ViewModel { get; }

    public MainWindow Window { get; }

    public GameWorkspaceView Surface => (GameWorkspaceView)ViewModel.GameContent!;

    public PlayModeView Play => Surface.PlayView ?? throw new InvalidOperationException("Not in play mode.");

    public void EnterPlay()
    {
        ViewModel.Mode = EditorMode.Play;
        Pump();
    }

    /// <summary>Advances <paramref name="frames"/> frames of 1/60 s and lets layout/render catch up.</summary>
    public void Frames(int frames, double delta = 1.0 / 60)
    {
        for (var i = 0; i < frames; i++)
        {
            Play.AdvanceFrame(delta);
        }

        Pump();
    }

    /// <summary>
    /// Captures the window after flushing layout and a few render ticks (one tick renders the
    /// previous commit, so a single capture can be a frame behind).
    /// </summary>
    public static Avalonia.Media.Imaging.WriteableBitmap Capture(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Pump();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        return Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window) ?? throw new InvalidOperationException("No frame rendered.");
    }

    public void Dispose()
    {
        Window.Close();
        Pump();
        _dir.Dispose();
    }
}
