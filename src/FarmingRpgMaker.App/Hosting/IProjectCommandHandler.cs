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
}
