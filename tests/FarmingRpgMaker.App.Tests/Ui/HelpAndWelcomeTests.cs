using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Localization;
using FarmingRpgMaker.App.Markdown;
using FarmingRpgMaker.App.Projects;
using FarmingRpgMaker.App.ViewModels;
using FarmingRpgMaker.App.Views;
using FarmingRpgMaker.Updates;
using FarmingRpgMaker.Updates.Testing;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Ui;

/// <summary>The first-run welcome tour, the Help menu's windows and the editor's language.</summary>
public sealed class HelpAndWelcomeTests
{
    private static string NewDataDirectory()
    {
        var path = Path.Combine(TestAppBuilder.DataDirectory, "help-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel, RecordingUrlLauncher Launcher) Open(string dataDirectory)
    {
        var coordinator = new UpdateCoordinator(new FakeUpdateService(), new InMemorySettingsStore());
        var viewModel = new MainWindowViewModel(coordinator, ShellComposition.CreateDefault(dataDirectory));
        var launcher = new RecordingUrlLauncher();
        var window = new MainWindow(launcher) { DataContext = viewModel };
        window.Show();
        Pump();
        return (window, viewModel, launcher);
    }

    private static List<string?> HelpHeaders(Window window) =>
        Find<MenuItem>(window, "HelpMenu").Items.OfType<MenuItem>().Select(item => item.Header as string).ToList();

    [Fact]
    public void EditorStrings_HaveEveryKeyInEveryLanguage_AndFallBack()
    {
        var english = EditorStrings.Keys(EditorStrings.English);
        Assert.NotEmpty(english);
        foreach (var language in EditorStrings.Languages)
        {
            var keys = EditorStrings.Keys(language.Code);
            Assert.Empty(english.Except(keys));
            Assert.Empty(keys.Except(english));
            foreach (var key in keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(EditorStrings.Get(language.Code, key)), $"{language.Code} {key}");
                // Templates keep their placeholders.
                Assert.Equal(
                    EditorStrings.Get(EditorStrings.English, key).Contains("{0}", StringComparison.Ordinal),
                    EditorStrings.Get(language.Code, key).Contains("{0}", StringComparison.Ordinal));
            }
        }

        // The web version's mode and toolbar names.
        Assert.Equal("Play Mode", EditorStrings.Get("en", "mode.play"));
        Assert.Equal("Modo Juego", EditorStrings.Get("es", "mode.play"));
        Assert.Equal("Proyectos", EditorStrings.Get("es", "mode.projects"));
        Assert.Equal("Reiniciar", EditorStrings.Get("es", "toolbar.restart"));
        Assert.Equal("Depurar", EditorStrings.Get("es", "toolbar.debug"));
        // Unknown languages and keys fall back to English, then to the key.
        Assert.Equal("Keep changes", EditorStrings.Get("fr", "toolbar.keepChanges"));
        Assert.Equal("no.such.key", EditorStrings.Get("es", "no.such.key"));
        Assert.Equal("es", EditorStrings.FromTag("es_AR.UTF-8"));
        Assert.Null(EditorStrings.FromTag("C"));
        Assert.Equal("es", EditorStrings.Resolve(null, "es-MX"));
        Assert.Equal("en", EditorStrings.Resolve("en", "es-MX"));
        Assert.Equal("en", EditorStrings.Resolve(null, "de-DE"));
    }

    [Fact]
    public void CreatorGuide_IsEmbeddedAndParses()
    {
        var markdown = HelpContent.CreatorGuideMarkdown();
        var blocks = MarkdownParser.Parse(markdown);
        var headings = blocks.OfType<MarkdownHeading>().Select(h => string.Concat(h.Inlines.Select(i => i.Text))).ToList();
        Assert.StartsWith("Creator guide", headings[0], StringComparison.Ordinal);
        Assert.Contains(headings, h => h.Contains("Edit Mode", StringComparison.Ordinal));
        Assert.Contains(headings, h => h.Contains("Play Mode", StringComparison.Ordinal));
        Assert.Contains(headings, h => h.Contains("Share your game", StringComparison.Ordinal));
        Assert.Contains("Export Game", markdown, StringComparison.Ordinal);
        Assert.Equal(HelpContent.DocsBaseUrl + "EXPORT.md", HelpContent.ResolveLink("EXPORT.md"));
        Assert.Equal("https://example.com/", HelpContent.ResolveLink("https://example.com/"));
    }

    [AvaloniaFact]
    public void Welcome_OpensOnFirstRun_AndNotAfterItWasDismissed()
    {
        var data = NewDataDirectory();
        var (window, viewModel, _) = Open(data);
        Assert.True(viewModel.ShouldShowWelcome);

        Assert.True(window.ShowWelcomeIfFirstRun());
        Pump();
        var welcome = Assert.IsType<WelcomeWindow>(window.OpenWelcome);
        Assert.Equal(WelcomeWindow.Steps.Count, FindByName<StackPanel>(welcome, "WelcomeSteps").Children.Count);
        var text = AllVisibleText(welcome);
        Assert.Contains("Welcome to Farming RPG Maker", text, StringComparison.Ordinal);
        Assert.Contains("F5", text, StringComparison.Ordinal);
        Assert.Contains("Export Game", text, StringComparison.Ordinal);

        // "Full creator guide" opens the guide in the app.
        Click(welcome, FindByName<HyperlinkButton>(welcome, "WelcomeGuideLink"));
        PumpUntil(() => window.OpenCreatorGuide is not null, "creator guide");
        window.OpenCreatorGuide!.Close();

        Click(welcome, FindByName<Button>(welcome, "WelcomeStartButton"));
        PumpUntil(() => window.OpenWelcome is null, "welcome closed");
        Assert.False(viewModel.ShouldShowWelcome);
        Assert.True(new AppSettingsStore(Path.Combine(data, "settings.json")).Load().WelcomeSeen);
        Assert.False(window.ShowWelcomeIfFirstRun());
        window.Close();

        // The next launch remembers it.
        var (again, againViewModel, _) = Open(data);
        Assert.False(againViewModel.ShouldShowWelcome);
        Assert.False(again.ShowWelcomeIfFirstRun());
        Assert.Null(again.OpenWelcome);
        again.Close();
    }

    [AvaloniaFact]
    public void HelpMenu_OpensTheTourTheGuideAndTheShortcuts()
    {
        var (window, viewModel, launcher) = Open(NewDataDirectory());
        Assert.Equal(
            ["_Welcome Tour", "_Creator Guide", "_Keyboard Shortcuts", "_Language", "_Update Center…", "_About Farming RPG Maker"],
            HelpHeaders(window));

        viewModel.WelcomeCommand.Execute(null);
        PumpUntil(() => window.OpenWelcome is not null, "welcome tour");
        window.OpenWelcome!.Close();
        PumpUntil(() => window.OpenWelcome is null, "welcome closed");

        viewModel.CreatorGuideCommand.Execute(null);
        PumpUntil(() => window.OpenCreatorGuide is not null, "creator guide");
        var guide = window.OpenCreatorGuide!;
        Assert.True(guide.Guide.Document.Children.Count > 20);
        var guideText = AllVisibleText(guide);
        Assert.Contains("Keep changes", guideText, StringComparison.Ordinal);
        Assert.Contains("Workshop", guideText, StringComparison.Ordinal);
        // Its links open on GitHub.
        var link = guide.Guide.GetVisualDescendants().OfType<HyperlinkButton>()
            .Concat(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(guide.Guide).OfType<HyperlinkButton>())
            .First();
        link.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.StartsWith(HelpContent.DocsBaseUrl, Assert.Single(launcher.Opened), StringComparison.Ordinal);
        Click(guide, FindByName<Button>(guide, "CreatorGuideCloseButton"));
        PumpUntil(() => window.OpenCreatorGuide is null, "guide closed");

        viewModel.ShortcutsCommand.Execute(null);
        PumpUntil(() => window.OpenShortcuts is not null, "shortcuts");
        var shortcuts = AllVisibleText(window.OpenShortcuts!);
        Assert.Contains("Ctrl+Shift+E", shortcuts, StringComparison.Ordinal);
        Assert.Contains("F5", shortcuts, StringComparison.Ordinal);
        Assert.Contains("Watering can", shortcuts, StringComparison.Ordinal);
        window.OpenShortcuts!.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Language_SwitchesTheModeNamesAndMenus_AndIsRemembered()
    {
        var data = NewDataDirectory();
        var (window, viewModel, _) = Open(data);
        try
        {
            Assert.Contains("Play Mode", AllVisibleText(Find<Button>(window, "ModeToggle")), StringComparison.Ordinal);
            var languages = Find<MenuItem>(window, "LanguageMenuItem").Items.OfType<MenuItem>().ToList();
            Assert.Equal(["English", "Español"], languages.Select(item => item.Header as string).ToList());
            Assert.True(languages[0].IsChecked);

            languages[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Pump();

            Assert.Equal("es", EditorStrings.Language);
            Assert.True(languages[1].IsChecked);
            Assert.False(languages[0].IsChecked);
            Assert.Contains("Modo Juego", AllVisibleText(Find<Button>(window, "ModeToggle")), StringComparison.Ordinal);
            Assert.Equal("Editando: " + viewModel.ProjectName, Find<TextBlock>(window, "HeaderSubtitle").Text);
            Assert.Equal("Ay_uda", Find<MenuItem>(window, "HelpMenu").Header as string);
            Assert.Equal("Modo _Juego", Find<MenuItem>(window, "PlayModeMenuItem").Header as string);
            // The whole menu bar follows the language, not only the Help menu.
            Assert.Equal("_Archivo", Find<MenuItem>(window, "FileMenu").Header as string);
            Assert.Equal("_Juego", Find<MenuItem>(window, "GameMenu").Header as string);
            Assert.Equal("Exportar _juego…", Find<MenuItem>(window, "ExportGameMenuItem").Header as string);
            Assert.Equal("es", new AppSettingsStore(Path.Combine(data, "settings.json")).Load().EditorLanguage);

            viewModel.ShortcutsCommand.Execute(null);
            PumpUntil(() => window.OpenShortcuts is not null, "shortcuts");
            Assert.Contains("Atajos de teclado", window.OpenShortcuts!.Title, StringComparison.Ordinal);
            window.OpenShortcuts.Close();

            viewModel.SetLanguage("en");
            Pump();
            Assert.Contains("Play Mode", AllVisibleText(Find<Button>(window, "ModeToggle")), StringComparison.Ordinal);
            Assert.Equal("en", new AppSettingsStore(Path.Combine(data, "settings.json")).Load().EditorLanguage);
        }
        finally
        {
            EditorStrings.SetLanguage(EditorStrings.English);
            window.Close();
        }
    }
}
