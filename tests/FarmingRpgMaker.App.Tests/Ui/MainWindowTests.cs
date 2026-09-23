using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.ViewModels;
using FarmingRpgMaker.App.Views;
using FarmingRpgMaker.Updates;
using FarmingRpgMaker.Updates.Testing;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Ui;

public sealed class MainWindowTests
{
    private readonly FakeUpdateService _fake = new();

    private (MainWindow Window, MainWindowViewModel ViewModel) Open(ShellComposition? composition = null)
    {
        var coordinator = new UpdateCoordinator(_fake, new InMemorySettingsStore());
        var viewModel = new MainWindowViewModel(coordinator, composition ?? ShellComposition.CreateDefault());
        var window = new MainWindow(new RecordingUrlLauncher()) { DataContext = viewModel };
        window.Show();
        Pump();
        return (window, viewModel);
    }

    [AvaloniaFact]
    public void Header_ShowsTitleSubtitleAndModeToggle()
    {
        var (window, viewModel) = Open();

        Assert.Equal("Farming RPG Maker", window.Title);
        Assert.Equal("Farming RPG Maker", Find<TextBlock>(window, "HeaderTitle").Text);
        Assert.Equal("Editing: Untitled Game", Find<TextBlock>(window, "HeaderSubtitle").Text);
        Assert.Contains("Play Mode", AllVisibleText(Find<Button>(window, "ModeToggle")), StringComparison.Ordinal);

        Click(window, Find<Button>(window, "ModeToggle"));

        Assert.Equal(EditorMode.Play, viewModel.Mode);
        Assert.Equal("Playing: Untitled Game", Find<TextBlock>(window, "HeaderSubtitle").Text);
        Assert.Contains("Edit Mode", AllVisibleText(Find<Button>(window, "ModeToggle")), StringComparison.Ordinal);

        viewModel.ProjectName = "Sunny Acres";
        Pump();
        Assert.Equal("Playing: Sunny Acres", Find<TextBlock>(window, "HeaderSubtitle").Text);
    }

    [AvaloniaFact]
    public void MenuBar_HasExpectedMenus()
    {
        var (window, _) = Open();

        var menu = Find<Menu>(window, "MainMenu");
        var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Equal(["_File", "_Game", "_Help"], headers);

        var file = menu.Items.OfType<MenuItem>().First().Items.OfType<MenuItem>().Select(m => m.Header as string);
        Assert.Equal(["_New Project", "_Open Project…", "_Import Project JSON…", "_Export Project JSON…", "E_xit"], file);
        var help = menu.Items.OfType<MenuItem>().Last().Items.OfType<MenuItem>().Select(m => m.Header as string);
        Assert.Equal(["_Update Center…", "_About Farming RPG Maker"], help);
    }

    [AvaloniaFact]
    public void GameHost_ShowsPlaceholderByDefault()
    {
        var (window, _) = Open();

        var host = Find<ContentControl>(window, "GameHostPresenter");
        var placeholder = Assert.IsType<Border>(host.Content);
        Assert.Equal("GameSurfacePlaceholder", placeholder.Name);
        Assert.Contains("Edit mode", AllVisibleText(host), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void GameHost_UsesInjectedSurfaceAndProjectCommands()
    {
        var factory = new TestSurfaceFactory();
        var commands = new TestProjectCommands();
        var (window, viewModel) = Open(new ShellComposition(factory, commands));

        var host = Find<ContentControl>(window, "GameHostPresenter");
        Assert.Same(factory.Created, host.Content);
        Assert.Same(viewModel, factory.Shell);
        Assert.Same(window, viewModel.TopLevel);

        viewModel.PlayModeCommand.Execute(null);
        Assert.Equal([EditorMode.Play], factory.ModeChanges);

        viewModel.ImportProjectJsonCommand.Execute(null);
        PumpUntil(() => commands.Imported, "import");
        Assert.Equal("Imported farm.json", Find<TextBlock>(window, "StatusText").Text);
        Assert.Equal("Playing: Imported Farm", Find<TextBlock>(window, "HeaderSubtitle").Text);
    }

    [AvaloniaFact]
    public void UpdateBadge_AppearsWhenUpdateFound_AndOpensUpdateCenter()
    {
        _fake.NextResult = FakeUpdateService.SampleUpdate("0.2.0");
        var (window, viewModel) = Open();
        var badge = Find<Button>(window, "UpdateBadge");
        Assert.False(badge.IsVisible);

        _ = viewModel.Updates.RunStartupCheckAsync();
        PumpUntil(() => badge.IsVisible, "badge");
        Assert.Contains("Update available", AllVisibleText(badge), StringComparison.Ordinal);

        Click(window, badge);
        PumpUntil(() => window.OpenUpdateCenter is not null, "update center");
        var center = window.OpenUpdateCenter!;
        Assert.Equal("Version 0.2.0 is available", Find<TextBlock>(center, "StatusTitle").Text);
        center.Close();
        PumpUntil(() => window.OpenUpdateCenter is null, "update center closed");
    }

    [AvaloniaFact]
    public void AboutWindow_ShowsVersionAndLinks()
    {
        var launcher = new RecordingUrlLauncher();
        var window = new AboutWindow { DataContext = new AboutViewModel("0.2.0", launcher) };
        window.Show();
        Pump();

        Assert.Equal("Version 0.2.0", Find<TextBlock>(window, "AboutVersionText").Text);
        Assert.Contains("Farming RPG Maker", AllVisibleText(window), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void PlaceholderProjectCommands_ReportInStatusBar()
    {
        var (window, viewModel) = Open();
        viewModel.Mode = EditorMode.Play;

        viewModel.NewProjectCommand.Execute(null);
        PumpUntil(() => viewModel.StatusMessage == "Started a new project.", "new project");
        Assert.Equal(EditorMode.Edit, viewModel.Mode);

        viewModel.ExportProjectJsonCommand.Execute(null);
        PumpUntil(() => Find<TextBlock>(window, "StatusText").Text!.Contains("Exporting", StringComparison.Ordinal), "export status");
    }

    private sealed class TestSurfaceFactory : IGameSurfaceFactory
    {
        public Control? Created { get; private set; }

        public IShellHost? Shell { get; private set; }

        public List<EditorMode> ModeChanges { get; } = [];

        public object CreateGameSurface(IShellHost shell)
        {
            Shell = shell;
            shell.ModeChanged += (_, mode) => ModeChanges.Add(mode);
            Created = new TextBlock { Text = "Real game surface" };
            return Created;
        }
    }

    private sealed class TestProjectCommands : IProjectCommandHandler
    {
        public bool Imported { get; private set; }

        public Task NewProjectAsync(IShellHost shell) => Task.CompletedTask;

        public Task OpenProjectAsync(IShellHost shell) => Task.CompletedTask;

        public async Task ImportProjectJsonAsync(IShellHost shell)
        {
            await Task.Yield();
            shell.ProjectName = "Imported Farm";
            shell.ShowStatus("Imported farm.json");
            Imported = true;
        }

        public Task ExportProjectJsonAsync(IShellHost shell) => Task.CompletedTask;
    }
}
