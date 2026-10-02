using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Creates the <see cref="GameWorkspaceView"/> and opens the startup project.</summary>
public sealed class GameSurfaceFactory(ProjectWorkspace workspace, GameSurfaceOptions? options = null, IProjectDialogs? dialogs = null) : IGameSurfaceFactory
{
    private IReadOnlyList<string> _startupErrors = [];

    public ProjectWorkspace Workspace { get; } = workspace ?? throw new ArgumentNullException(nameof(workspace));

    /// <summary>The surface created by the last <see cref="CreateGameSurface"/> call.</summary>
    public GameWorkspaceView? Surface { get; private set; }

    /// <summary>Projects that failed to load at startup, until <see cref="ShowStartupErrorsAsync"/> shows them.</summary>
    public IReadOnlyList<string> StartupErrors => _startupErrors;

    public object CreateGameSurface(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        IReadOnlyList<string> errors = [];
        if (Workspace.Current is null)
        {
            try
            {
                errors = Workspace.OpenStartupProject();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors = [$"The project folder is not accessible ({Workspace.Store.ProjectsDirectory}): {ex.Message}"];
            }
        }

        Surface = new GameWorkspaceView(shell, Workspace, options);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                System.Diagnostics.Trace.TraceError($"Startup project could not be loaded: {error}");
            }

            // The window isn't open yet: the details are shown once it is (ShowStartupErrorsAsync).
            _startupErrors = errors;
            var opened = Workspace.Current is { } current ? $"Opened \"{current.Name}\" instead. " : "";
            shell.ShowStatus($"{opened}{(errors.Count == 1 ? "A project" : $"{errors.Count} projects")} could not be loaded.");
        }
        else if (Workspace.Current is { } project)
        {
            shell.ShowStatus($"Opened \"{project.Name}\".");
        }

        return Surface;
    }

    /// <summary>
    /// Shows the projects that failed to load at startup (once). Called when the main window has
    /// opened: a dialog needs an open owner window.
    /// </summary>
    public async Task ShowStartupErrorsAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (_startupErrors.Count == 0 || dialogs is null)
        {
            return;
        }

        var errors = _startupErrors;
        _startupErrors = [];
        IReadOnlyList<string> details = Workspace.Current is { } project
            ? [.. errors, $"\"{project.Name}\" was opened instead. The files that failed are still in {Workspace.Store.ProjectsDirectory}."]
            : errors;
        await dialogs.ShowErrorsAsync(shell, "Some projects could not be loaded", details).ConfigureAwait(true);
    }
}
