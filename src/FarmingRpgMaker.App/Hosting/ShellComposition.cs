using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Projects;
using FarmingRpgMaker.App.Services;

namespace FarmingRpgMaker.App.Hosting;

/// <summary>
/// Composition root for the pluggable parts of the shell: the game surface (Edit/Play Mode
/// over the open project) and the File-menu project commands, sharing one
/// <see cref="ProjectWorkspace"/>.
/// </summary>
public sealed record ShellComposition(IGameSurfaceFactory GameSurfaceFactory, IProjectCommandHandler ProjectCommands, ProjectWorkspace? Workspace = null)
{
    /// <summary>
    /// Ends a running playtest (honouring Keep changes) and writes pending edits to disk. The
    /// app calls this on shutdown, before "Restart &amp; install", and from the last-chance
    /// exception handler, so nothing a creator did in the last second is lost.
    /// </summary>
    public void PrepareForShutdown()
    {
        (GameSurfaceFactory as GameSurfaceFactory)?.Surface?.PrepareForShutdown();
        Workspace?.FlushPendingSave();
    }

    /// <summary>
    /// An error nothing else handled (the dispatcher's last chance): Edit Mode shows it on its
    /// error screen with Try Again instead of the app closing. False when there is no surface
    /// to show it, or the error screen itself failed; the app then saves and closes as before.
    /// </summary>
    public bool TryRecover(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return (GameSurfaceFactory as GameSurfaceFactory)?.Surface is { } surface && surface.ShowEditorError(error);
    }

    /// <summary>
    /// Once the main window is open: shows what went wrong at startup (projects that failed to
    /// load), which needs an open window to own the dialog.
    /// </summary>
    public Task ShowStartupMessagesAsync(IShellHost shell) =>
        (GameSurfaceFactory as GameSurfaceFactory)?.ShowStartupErrorsAsync(shell) ?? Task.CompletedTask;

    /// <summary>
    /// The real app: projects under <paramref name="dataDirectory"/> (default
    /// <see cref="AppDataPaths.DefaultRoot"/>, i.e. <c>%APPDATA%/FarmingRpgMaker</c>).
    /// </summary>
    public static ShellComposition CreateDefault(string? dataDirectory = null, GameSurfaceOptions? options = null) =>
        Create(ProjectWorkspace.CreateDefault(dataDirectory), options);

    /// <summary>Wires a surface factory and project commands over <paramref name="workspace"/>.</summary>
    public static ShellComposition Create(ProjectWorkspace workspace, GameSurfaceOptions? options = null, IProjectDialogs? dialogs = null, IUrlLauncher? launcher = null)
    {
        dialogs ??= new AvaloniaProjectDialogs();
        return new ShellComposition(new GameSurfaceFactory(workspace, options, dialogs), new ProjectCommandHandler(workspace, dialogs, launcher), workspace);
    }
}
