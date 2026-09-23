using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Hosting;

/// <summary>
/// Composition root for the pluggable parts of the shell: the game surface (Edit/Play Mode
/// over the open project) and the File-menu project commands, sharing one
/// <see cref="ProjectWorkspace"/>.
/// </summary>
public sealed record ShellComposition(IGameSurfaceFactory GameSurfaceFactory, IProjectCommandHandler ProjectCommands)
{
    /// <summary>
    /// The real app: projects under <paramref name="dataDirectory"/> (default
    /// <see cref="AppDataPaths.DefaultRoot"/>, i.e. <c>%APPDATA%/FarmingRpgMaker</c>).
    /// </summary>
    public static ShellComposition CreateDefault(string? dataDirectory = null, GameSurfaceOptions? options = null) =>
        Create(ProjectWorkspace.CreateDefault(dataDirectory), options);

    /// <summary>Wires a surface factory and project commands over <paramref name="workspace"/>.</summary>
    public static ShellComposition Create(ProjectWorkspace workspace, GameSurfaceOptions? options = null, IProjectDialogs? dialogs = null)
    {
        dialogs ??= new AvaloniaProjectDialogs();
        return new ShellComposition(new GameSurfaceFactory(workspace, options, dialogs), new ProjectCommandHandler(workspace, dialogs));
    }
}
