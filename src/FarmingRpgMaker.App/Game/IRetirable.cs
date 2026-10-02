namespace FarmingRpgMaker.App.Game;

/// <summary>
/// A view inside Edit Mode that follows the workspace (<see cref="Projects.ProjectWorkspace.ProjectChanged"/>).
/// <see cref="EditModeView.Retire"/> retires every one of them when the editor is replaced (the
/// error screen's Try Again), so a replaced editor neither leaks nor keeps failing.
/// </summary>
internal interface IRetirable
{
    /// <summary>Stops following the workspace for good.</summary>
    void Retire();
}
