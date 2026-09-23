using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Creates the <see cref="GameWorkspaceView"/> and opens the startup project.</summary>
public sealed class GameSurfaceFactory(ProjectWorkspace workspace, GameSurfaceOptions? options = null, IProjectDialogs? dialogs = null) : IGameSurfaceFactory
{
    public ProjectWorkspace Workspace { get; } = workspace ?? throw new ArgumentNullException(nameof(workspace));

    /// <summary>The surface created by the last <see cref="CreateGameSurface"/> call.</summary>
    public GameWorkspaceView? Surface { get; private set; }

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
            shell.ShowStatus($"Some projects could not be loaded: {string.Join("; ", errors)}");
            if (dialogs is not null)
            {
                _ = dialogs.ShowErrorsAsync(shell, "Some projects could not be loaded", errors);
            }
        }
        else if (Workspace.Current is { } project)
        {
            shell.ShowStatus($"Opened \"{project.Name}\".");
        }

        return Surface;
    }
}
