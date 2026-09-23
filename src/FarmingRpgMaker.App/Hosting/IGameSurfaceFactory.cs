namespace FarmingRpgMaker.App.Hosting;

/// <summary>
/// Creates the central game/editor view shown in <c>MainWindow.GameHostPresenter</c>.
/// Return an Avalonia <c>Control</c>, or a view model that has a matching
/// <c>DataTemplate</c> registered in <c>App.axaml</c>. Called once, when the main window's
/// view model is created.
/// </summary>
public interface IGameSurfaceFactory
{
    object CreateGameSurface(IShellHost shell);
}
