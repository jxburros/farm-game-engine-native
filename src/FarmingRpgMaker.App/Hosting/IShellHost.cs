using Avalonia.Controls;

namespace FarmingRpgMaker.App.Hosting;

/// <summary>
/// What the app shell (main window) offers to the game/editor integration. Implemented by
/// <see cref="ViewModels.MainWindowViewModel"/>; handed to <see cref="IGameSurfaceFactory"/>
/// and <see cref="IProjectCommandHandler"/>.
/// </summary>
public interface IShellHost
{
    /// <summary>Name shown in the header subtitle ("Playing: …" / "Editing: …").</summary>
    string ProjectName { get; set; }

    /// <summary>Current mode. Setting it updates the header toggle and raises <see cref="ModeChanged"/>.</summary>
    EditorMode Mode { get; set; }

    /// <summary>Raised after <see cref="Mode"/> changes (from the menu, the header toggle, or code).</summary>
    event EventHandler<EditorMode>? ModeChanged;

    /// <summary>
    /// The main window as a <see cref="Avalonia.Controls.TopLevel"/> — use
    /// <c>TopLevel.StorageProvider</c> for Open/Save file pickers and <c>TopLevel.Clipboard</c>.
    /// Null until the window is shown (and in some headless tests).
    /// </summary>
    TopLevel? TopLevel { get; }

    /// <summary>Shows a short message in the status bar.</summary>
    void ShowStatus(string message);
}
