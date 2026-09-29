using System.ComponentModel;
using Avalonia.Controls;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Localization;
using FarmingRpgMaker.App.Mvvm;
using FarmingRpgMaker.App.Projects;
using FarmingRpgMaker.Updates;

namespace FarmingRpgMaker.App.ViewModels;

/// <summary>
/// Main window state: header (title, "Playing/Editing" subtitle, mode toggle, update badge),
/// menu commands, status bar, and <see cref="GameContent"/> — the object shown in the
/// central <c>GameHostPresenter</c>.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IShellHost
{
    private readonly IProjectCommandHandler _projectCommands;
    private readonly AppSettingsStore? _settings;
    private string _projectName = "Untitled Game";
    private EditorMode _mode = EditorMode.Edit;
    private object? _gameContent;
    private string _statusMessage = "Ready";

    public MainWindowViewModel(UpdateCoordinator updates, ShellComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _projectCommands = composition.ProjectCommands;
        _settings = composition.Workspace?.Settings;

        NewProjectCommand = ProjectCommand(() => _projectCommands.NewProjectAsync(this));
        OpenProjectCommand = ProjectCommand(() => _projectCommands.OpenProjectAsync(this));
        ImportProjectJsonCommand = ProjectCommand(() => _projectCommands.ImportProjectJsonAsync(this));
        ExportProjectJsonCommand = ProjectCommand(() => _projectCommands.ExportProjectJsonAsync(this));
        ExportGameCommand = ProjectCommand(() => _projectCommands.ExportGameAsync(this));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        PlayModeCommand = new RelayCommand(() => Mode = EditorMode.Play);
        EditModeCommand = new RelayCommand(() => Mode = EditorMode.Edit);
        ToggleModeCommand = new RelayCommand(() => Mode = Mode == EditorMode.Play ? EditorMode.Edit : EditorMode.Play);
        OpenUpdateCenterCommand = new RelayCommand(() => UpdateCenterRequested?.Invoke(this, EventArgs.Empty));
        AboutCommand = new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty));
        WelcomeCommand = new RelayCommand(() => WelcomeRequested?.Invoke(this, EventArgs.Empty));
        CreatorGuideCommand = new RelayCommand(() => CreatorGuideRequested?.Invoke(this, EventArgs.Empty));
        ShortcutsCommand = new RelayCommand(() => ShortcutsRequested?.Invoke(this, EventArgs.Empty));

        Updates.PropertyChanged += OnUpdatesChanged;
        EditorStrings.LanguageChanged += OnLanguageChanged;
        GameContent = composition.GameSurfaceFactory.CreateGameSurface(this);
    }

    public event EventHandler<EditorMode>? ModeChanged;

    /// <summary>The view opens the Update Center dialog.</summary>
    public event EventHandler? UpdateCenterRequested;

    /// <summary>The view opens the About dialog.</summary>
    public event EventHandler? AboutRequested;

    /// <summary>The view closes the main window.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The view opens the welcome tour (Help → Welcome Tour).</summary>
    public event EventHandler? WelcomeRequested;

    /// <summary>The view opens the creator guide (Help → Creator Guide).</summary>
    public event EventHandler? CreatorGuideRequested;

    /// <summary>The view opens the keyboard shortcuts (Help → Keyboard Shortcuts).</summary>
    public event EventHandler? ShortcutsRequested;

    public UpdateCoordinator Updates { get; }

    public string Title => "Farming RPG Maker";

    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (SetProperty(ref _projectName, string.IsNullOrWhiteSpace(value) ? "Untitled Game" : value))
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public EditorMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertiesChanged(nameof(IsPlayMode), nameof(IsEditMode), nameof(Subtitle), nameof(ModeToggleText));
                ModeChanged?.Invoke(this, value);
            }
        }
    }

    public bool IsPlayMode => _mode == EditorMode.Play;

    public bool IsEditMode => _mode == EditorMode.Edit;

    /// <summary>"Playing: name" / "Editing: name", like the web header.</summary>
    public string Subtitle => EditorStrings.Format(IsPlayMode ? "header.playing" : "header.editing", _projectName);

    /// <summary>The toggle offers the other mode.</summary>
    public string ModeToggleText => EditorStrings.Get(IsPlayMode ? "mode.edit" : "mode.play");

    public string PlayModeMenuText => EditorStrings.Get("menu.playMode");

    public string EditModeMenuText => EditorStrings.Get("menu.editMode");

    public string HelpMenuText => EditorStrings.Get("menu.help");

    public string WelcomeMenuText => EditorStrings.Get("menu.welcome");

    public string CreatorGuideMenuText => EditorStrings.Get("menu.creatorGuide");

    public string ShortcutsMenuText => EditorStrings.Get("menu.shortcuts");

    public string LanguageMenuText => EditorStrings.Get("menu.language");

    public string UpdateCenterMenuText => EditorStrings.Get("menu.updateCenter");

    public string AboutMenuText => EditorStrings.Get("menu.about");

    /// <summary>The editor's language (<see cref="EditorStrings.Language"/>).</summary>
    public string Language => EditorStrings.Language;

    /// <summary>True until the welcome tour has been dismissed once (kept in the app settings).</summary>
    public bool ShouldShowWelcome => _settings is { } settings && settings.Load().WelcomeSeen != true;

    /// <summary>
    /// Content of the central <c>GameHostPresenter</c>: an Avalonia control or a view model with a
    /// registered DataTemplate. Created by <see cref="IGameSurfaceFactory"/>; may be replaced at runtime.
    /// </summary>
    public object? GameContent
    {
        get => _gameContent;
        set => SetProperty(ref _gameContent, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string VersionText => $"v{Updates.CurrentVersion}";

    public bool IsUpdateBadgeVisible => Updates.IsBadgeVisible;

    public string UpdateBadgeText => Updates.BadgeText;

    public TopLevel? TopLevel { get; set; }

    public AsyncRelayCommand NewProjectCommand { get; }

    public AsyncRelayCommand OpenProjectCommand { get; }

    public AsyncRelayCommand ImportProjectJsonCommand { get; }

    public AsyncRelayCommand ExportProjectJsonCommand { get; }

    /// <summary>File → Export Game… (standalone Windows and Linux builds).</summary>
    public AsyncRelayCommand ExportGameCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand PlayModeCommand { get; }

    public RelayCommand EditModeCommand { get; }

    public RelayCommand ToggleModeCommand { get; }

    public RelayCommand OpenUpdateCenterCommand { get; }

    public RelayCommand AboutCommand { get; }

    public RelayCommand WelcomeCommand { get; }

    public RelayCommand CreatorGuideCommand { get; }

    public RelayCommand ShortcutsCommand { get; }

    public void ShowStatus(string message) => StatusMessage = message;

    /// <summary>Remembers that the welcome tour was seen, so it no longer opens at startup.</summary>
    public void MarkWelcomeSeen() => _settings?.Update(settings => settings with { WelcomeSeen = true });

    /// <summary>Switches the editor's language and remembers the choice.</summary>
    public void SetLanguage(string code)
    {
        EditorStrings.SetLanguage(code);
        _settings?.Update(settings => settings with { EditorLanguage = EditorStrings.Language });
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertiesChanged(
        nameof(Language),
        nameof(Subtitle),
        nameof(ModeToggleText),
        nameof(PlayModeMenuText),
        nameof(EditModeMenuText),
        nameof(HelpMenuText),
        nameof(WelcomeMenuText),
        nameof(CreatorGuideMenuText),
        nameof(ShortcutsMenuText),
        nameof(LanguageMenuText),
        nameof(UpdateCenterMenuText),
        nameof(AboutMenuText));

    private AsyncRelayCommand ProjectCommand(Func<Task> action) =>
        new(action, onError: ex => ShowStatus($"Something went wrong: {ex.Message}"));

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UpdateCoordinator.IsBadgeVisible):
                OnPropertyChanged(nameof(IsUpdateBadgeVisible));
                break;
            case nameof(UpdateCoordinator.BadgeText):
                OnPropertyChanged(nameof(UpdateBadgeText));
                break;
            case nameof(UpdateCoordinator.State) when Updates.State == UpdateState.ReadyToInstall:
                ShowStatus("An update is ready — restart to install it.");
                break;
        }
    }
}
