using Avalonia.Controls;
using FarmEngine.Interop;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Knobs for the game surface (tests turn the frame loop off and drive frames).</summary>
public sealed record GameSurfaceOptions
{
    /// <summary>Run the frame loop in Play Mode.</summary>
    public bool AutoRun { get; init; } = true;

    /// <summary>Options for the Rust player of each playtest (seed, reduced motion, UI scale).</summary>
    public RustPlayerOptions? Player { get; init; }
}

/// <summary>
/// The central game surface (<see cref="IGameSurfaceFactory"/> output): Edit Mode or Play
/// Mode for the workspace's project, following <see cref="IShellHost.Mode"/>. Owns the
/// playtest lifecycle like the web App.tsx: entering Play snapshots the project; exiting
/// restores the snapshot, or keeps the played state ("Keep changes") through the Rust player's
/// <c>applyStateToProject</c>.
/// </summary>
public sealed class GameWorkspaceView : UserControl
{
    private readonly IShellHost _shell;
    private readonly ProjectWorkspace _workspace;
    private readonly GameSurfaceOptions _options;
    private PlayModeView? _play;
    private FarmEngine.Schemas.GameProject? _snapshot;

    public GameWorkspaceView(IShellHost shell, ProjectWorkspace workspace, GameSurfaceOptions? options = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = options ?? new GameSurfaceOptions();
        Name = "GameWorkspace";
        EditView = new EditModeView(workspace);
        Content = EditView;

        _shell.ModeChanged += OnModeChanged;
        _workspace.ProjectChanged += OnProjectChanged;
        SyncProjectName();
        if (_shell.Mode == EditorMode.Play)
        {
            StartPlaytest();
        }
    }

    public EditModeView EditView { get; }

    /// <summary>The Play Mode view while playtesting.</summary>
    public PlayModeView? PlayView => _play;

    public ProjectWorkspace Workspace => _workspace;

    /// <summary>Starts a playtest of the current project (snapshotting it first).</summary>
    public void StartPlaytest()
    {
        if (_play is not null || _workspace.Current is null)
        {
            return;
        }

        _workspace.FlushPendingSave();
        if (_workspace.Current.Scenes.Count == 0)
        {
            _shell.ShowStatus("This project has no scenes to play yet.");
            _shell.Mode = EditorMode.Edit;
            return;
        }

        RustPlayer player;
        try
        {
            player = CreatePlayer(_workspace.Current);
        }
#pragma warning disable CA1031 // A broken project must not take the app down; report and stay in Edit Mode.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _shell.ShowStatus($"This project can't be played: {ex.Message}");
            _shell.Mode = EditorMode.Edit;
            return;
        }

        _snapshot = _workspace.Current;
        _workspace.IsPlaytesting = true;
        _play = new PlayModeView(player, _options.AutoRun);
        _play.RestartRequested += OnRestartRequested;
        _play.Faulted += OnPlayFaulted;
        Content = _play;
        _shell.ShowStatus($"Playtesting {DisplayName()} — changes are discarded on exit unless you keep them.");
        _play.Focus();
    }

    /// <summary>Ends the playtest: keep-changes writes the final state back, else the snapshot stays.</summary>
    public void EndPlaytest()
    {
        if (_play is null)
        {
            return;
        }

        var play = _play;
        // Read the state back only when it is kept: after a fault (Keep changes is then off)
        // the Rust player refuses every call.
        FarmEngine.Schemas.GameProject? finalProject = null;
        var unreadable = false;
        if (play.KeepChanges)
        {
            try
            {
                finalProject = play.Use(player => player.SyncedProject());
            }
            catch (FarmFfiException ex)
            {
                unreadable = true;
                System.Diagnostics.Trace.TraceError($"Playtest state could not be read back: {ex}");
            }
        }

        play.RestartRequested -= OnRestartRequested;
        play.Faulted -= OnPlayFaulted;
        play.Close();
        _play = null;
        _workspace.IsPlaytesting = false;
        Content = EditView;

        if (finalProject is not null)
        {
            _workspace.KeepPlaytestResult(finalProject);
            _shell.ShowStatus("Playtest changes kept.");
        }
        else if (unreadable)
        {
            _shell.ShowStatus("Playtest changes could not be kept: the engine stopped with an error.");
        }
        else
        {
            _shell.ShowStatus("Playtest changes discarded (use \"Keep changes\" to save them).");
        }

        _snapshot = null;
    }

    /// <summary>
    /// Ends a running playtest (honouring Keep changes) and flushes pending edits. Called
    /// when the window closes, when the app shuts down, and before "Restart & install".
    /// </summary>
    public void PrepareForShutdown()
    {
        if (_play is not null)
        {
            EndPlaytest();
        }

        _workspace.FlushPendingSave();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Window closing: the surface lives as long as the window.
        PrepareForShutdown();
        _shell.ModeChanged -= OnModeChanged;
        _workspace.ProjectChanged -= OnProjectChanged;
    }

    private void OnPlayFaulted(object? sender, Exception exception)
    {
        if (_play is null)
        {
            return;
        }

        // The state that threw is not trusted: never write it back into the project.
        _play.KeepChanges = false;
        _shell.Mode = EditorMode.Edit;
        _shell.ShowStatus($"The playtest stopped because of an error: {exception.Message}");
        System.Diagnostics.Trace.TraceError($"Playtest faulted: {exception}");
    }

    private RustPlayer CreatePlayer(FarmEngine.Schemas.GameProject project) => RustPlayer.Create(project, _options.Player);

    private void OnRestartRequested(object? sender, EventArgs e)
    {
        if (_play is null || _snapshot is null)
        {
            return;
        }

        RustPlayer player;
        try
        {
            player = CreatePlayer(_snapshot);
        }
        catch (FarmFfiException ex)
        {
            _play.ShowToast(new ToastMessage($"Could not restart: {ex.Message}", ToastKind.Error));
            return;
        }

        _play.Attach(player);
        _play.ShowToast(new ToastMessage("Playtest restarted", ToastKind.Success));
    }

    private void OnModeChanged(object? sender, EditorMode mode)
    {
        if (mode == EditorMode.Play)
        {
            StartPlaytest();
        }
        else
        {
            EndPlaytest();
        }
    }

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        SyncProjectName();
        if (e.Kind == ProjectChangeKind.Opened && _play is not null)
        {
            // A different project was opened mid-playtest: discard the playtest.
            _play.KeepChanges = false;
            _shell.Mode = EditorMode.Edit;
        }
    }

    private void SyncProjectName() => _shell.ProjectName = DisplayName();

    private string DisplayName() => string.IsNullOrWhiteSpace(_workspace.Current?.Name) ? "Untitled Game" : _workspace.Current!.Name;
}
