using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FarmingRpgMaker.App.Hosting;

/// <summary>Stand-in for the game view until the engine integration plugs in its own factory.</summary>
public sealed class PlaceholderGameSurfaceFactory : IGameSurfaceFactory
{
    public object CreateGameSurface(IShellHost shell)
    {
        var modeText = new TextBlock
        {
            Classes = { "muted" },
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        void Refresh(EditorMode mode) => modeText.Text = mode == EditorMode.Play
            ? "Play mode: the game will run here."
            : "Edit mode: the map editor will appear here.";

        Refresh(shell.Mode);
        shell.ModeChanged += (_, mode) => Refresh(mode);

        return new Border
        {
            Name = "GameSurfacePlaceholder",
            Classes = { "placeholder" },
            Child = new StackPanel
            {
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Game view",
                        Classes = { "h2" },
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    modeText,
                },
            },
        };
    }
}

/// <summary>Stand-in for the project manager until the engine integration provides one.</summary>
public sealed class PlaceholderProjectCommandHandler : IProjectCommandHandler
{
    public Task NewProjectAsync(IShellHost shell)
    {
        shell.ProjectName = "Untitled Game";
        shell.Mode = EditorMode.Edit;
        shell.ShowStatus("Started a new project.");
        return Task.CompletedTask;
    }

    public Task OpenProjectAsync(IShellHost shell) => NotYet(shell, "Opening projects");

    public Task ImportProjectJsonAsync(IShellHost shell) => NotYet(shell, "Importing project JSON");

    public Task ExportProjectJsonAsync(IShellHost shell) => NotYet(shell, "Exporting project JSON");

    private static Task NotYet(IShellHost shell, string what)
    {
        shell.ShowStatus($"{what} isn't available in this build yet.");
        return Task.CompletedTask;
    }
}
