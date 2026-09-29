using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using FarmEngine.Authoring;
using FarmEngine.Interop;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Knobs for the game surface (tests turn the frame loop off and drive frames).</summary>
public sealed record GameSurfaceOptions
{
    /// <summary>Run the frame loop in Play Mode.</summary>
    public bool AutoRun { get; init; } = true;

    /// <summary>Play the game's sounds (tests turn this off).</summary>
    public bool Audio { get; init; } = true;

    /// <summary>Options for the Rust player of each playtest (seed, reduced motion, UI scale).</summary>
    public RustPlayerOptions? Player { get; init; }
}

/// <summary>
/// The central game surface (<see cref="IGameSurfaceFactory"/> output): Edit Mode or Play
/// Mode for the workspace's project, following <see cref="IShellHost.Mode"/>. Owns the
/// playtest lifecycle like the web App.tsx: entering Play snapshots the project; exiting
/// restores the snapshot, or keeps the played state ("Keep changes") through the Rust player's
/// <c>applyStateToProject</c>. An unexpected error while editing replaces Edit Mode with
/// <see cref="EditorErrorView"/> (web <c>ErrorFallback</c>), and a banner above the editor says
/// when the project can't be saved.
/// </summary>
public sealed class GameWorkspaceView : UserControl
{
    private readonly IShellHost _shell;
    private readonly ProjectWorkspace _workspace;
    private readonly GameSurfaceOptions _options;
    private readonly DockPanel _editHost = new() { Name = "EditHost" };
    private readonly Border _saveBanner = new() { Name = "SaveErrorBanner", IsVisible = false, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _saveBannerText = Ui.Wrapped("", "small");
    private readonly Func<Exception, bool> _viewErrorHandler;
    private PlayModeView? _play;
    private FarmEngine.Schemas.GameProject? _snapshot;

    public GameWorkspaceView(IShellHost shell, ProjectWorkspace workspace, GameSurfaceOptions? options = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = options ?? new GameSurfaceOptions();
        Name = "GameWorkspace";
        BuildSaveBanner();
        EditView = new EditModeView(workspace);
        _editHost.Children.Add(_saveBanner);
        _editHost.Children.Add(EditView);
        Content = _editHost;

        _shell.ModeChanged += OnModeChanged;
        _workspace.ProjectChanged += OnProjectChanged;
        _workspace.SaveStatusChanged += OnSaveStatusChanged;
        _viewErrorHandler = ShowEditorError;
        _workspace.ViewErrorHandler = _viewErrorHandler;
        SyncProjectName();
        SyncSaveBanner();
        if (_shell.Mode == EditorMode.Play)
        {
            StartPlaytest();
        }
    }

    public EditModeView EditView { get; private set; }

    /// <summary>The error screen while one is shown, else null.</summary>
    public EditorErrorView? ErrorView => Content as EditorErrorView;

    /// <summary>
    /// Shows <paramref name="error"/> instead of the editor, after ending any playtest (its
    /// changes are discarded) and saving what can be saved. False when the error screen is
    /// already up, so an error it raises itself is not caught again.
    /// </summary>
    public bool ShowEditorError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Content is EditorErrorView)
        {
            return false;
        }

        System.Diagnostics.Trace.TraceError($"Editor error: {error}");
        if (_play is not null)
        {
            _play.KeepChanges = false;
            EndPlaytest();
            if (_shell.Mode == EditorMode.Play)
            {
                _shell.Mode = EditorMode.Edit;
            }
        }

        _workspace.EndStroke();
        _workspace.FlushPendingSave();
        Content = new EditorErrorView(error, _workspace.SaveError, _workspace.CanUndo, TryAgain, () =>
        {
            _workspace.Undo();
            TryAgain();
        });
        _shell.ShowStatus($"The editor ran into a problem: {error.Message}");
        return true;
    }

    /// <summary>"Try Again": a fresh editor over the open project (web <c>resetErrorBoundary</c>).</summary>
    public void TryAgain()
    {
        _editHost.Children.Remove(EditView);
        EditView.Retire();
        EditView = new EditModeView(_workspace);
        _editHost.Children.Add(EditView);
        Content = _editHost;
        SyncSaveBanner();
        _shell.ShowStatus("Editor reopened.");
    }

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
        if (_workspace.Current.Scenes.Length == 0)
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
                var state = play.Use(player => player.StateJson());
                finalProject = Playtests.ApplyState(_snapshot!, state);
            }
            catch (Exception ex) when (ex is FarmFfiException or FormatException)
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
        Content = _editHost;

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
        _workspace.SaveStatusChanged -= OnSaveStatusChanged;
        if (ReferenceEquals(_workspace.ViewErrorHandler, _viewErrorHandler))
        {
            _workspace.ViewErrorHandler = null;
        }
    }

    private void BuildSaveBanner()
    {
        // Success announces itself (SaveStatusChanged); a failure again says so here.
        var retry = Ui.Button("Retry save", () =>
        {
            if (!_workspace.RetrySave())
            {
                _shell.ShowStatus($"Still can't save: {_workspace.SaveError}");
            }
        }, "accent");
        retry.Name = "RetrySaveButton";
        ToolTip.SetTip(retry, "Try to write the project file again");
        _saveBannerText.Name = "SaveErrorText";
        AutomationProperties.SetLiveSetting(_saveBannerText, AutomationLiveSetting.Assertive);
        var icon = Ui.Icon("IconAlertCircle", 18);
        icon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        var message = new DockPanel();
        message.Children.Add(icon);
        message.Children.Add(_saveBannerText);
        _saveBanner.Child = Ui.Row(message, retry);
        _saveBanner.Classes.Add("save-error");
        DockPanel.SetDock(_saveBanner, Dock.Top);
    }

    private void SyncSaveBanner()
    {
        var error = _workspace.SaveError;
        _saveBanner.IsVisible = error is not null;
        _saveBannerText.Text = error is null
            ? ""
            : $"Your project could not be saved: {error} Your changes are still open here. Free up disk space or check the folder's permissions, then choose Retry save (or File → Export Project JSON… to keep a copy).";
    }

    private void OnSaveStatusChanged(object? sender, EventArgs e)
    {
        SyncSaveBanner();
        _shell.ShowStatus(_workspace.SaveError is { } error
            ? $"Your project could not be saved: {error}"
            : "Saving works again — your project is being stored.");
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

    private RustPlayer CreatePlayer(FarmEngine.Schemas.GameProject project)
    {
        // The game's interface follows the editor's language until the player picks one.
        var options = _options.Player ?? new RustPlayerOptions();
        // Play Mode runs the cartridge F# compiles, like an exported game (Keep changes then
        // writes the final state back through F#; see EndPlaytest).
        return RustPlayer.CreateCartridge(
            Playtests.Cartridge(project),
            options with { Audio = _options.Audio, Locale = options.Locale ?? Localization.EditorStrings.Language });
    }

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
