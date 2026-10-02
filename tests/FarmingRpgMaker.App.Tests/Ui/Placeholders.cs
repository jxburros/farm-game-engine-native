using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

// Test doubles for the shell's pluggable parts. They keep the app's Hosting namespace (they
// used to ship in the app), so the shell tests read as before.
namespace FarmingRpgMaker.App.Hosting;

/// <summary>Stand-in game view: a shell with no engine behind it (the shell tests' embedder).</summary>
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

/// <summary>Stand-in project commands that only report in the status bar.</summary>
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

    public Task ExportGameAsync(IShellHost shell) => NotYet(shell, "Exporting games");

    private static Task NotYet(IShellHost shell, string what)
    {
        shell.ShowStatus($"{what} isn't available in this build yet.");
        return Task.CompletedTask;
    }
}
