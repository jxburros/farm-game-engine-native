namespace FarmingRpgMaker.App.Hosting;

/// <summary>
/// Handles the File-menu project commands (the web app's <c>ProjectManager</c>).
/// Each method may show file pickers through <see cref="IShellHost.TopLevel"/>.
/// Exceptions are caught by the shell and shown in the status bar.
/// </summary>
public interface IProjectCommandHandler
{
    Task NewProjectAsync(IShellHost shell);

    Task OpenProjectAsync(IShellHost shell);

    Task ImportProjectJsonAsync(IShellHost shell);

    Task ExportProjectJsonAsync(IShellHost shell);

    /// <summary>File → Export Game…: standalone Windows and Linux builds (docs/EXPORT.md).</summary>
    Task ExportGameAsync(IShellHost shell);

    /// <summary>
    /// Before the open project's edits are left behind (closing, installing an update): saves
    /// them, and while saving fails asks what to do. True when it is fine to go on;
    /// <paramref name="discardText"/> labels the choice that goes on without them.
    /// </summary>
    Task<bool> ResolveUnsavedChangesAsync(IShellHost shell, string discardText) => Task.FromResult(true);
}
