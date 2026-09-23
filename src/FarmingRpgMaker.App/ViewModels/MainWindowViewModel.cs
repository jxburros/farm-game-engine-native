using System.ComponentModel;
using Avalonia.Controls;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Mvvm;
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
    private string _projectName = "Untitled Game";
    private EditorMode _mode = EditorMode.Edit;
    private object? _gameContent;
    private string _statusMessage = "Ready";

    public MainWindowViewModel(UpdateCoordinator updates, ShellComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _projectCommands = composition.ProjectCommands;

        NewProjectCommand = ProjectCommand(() => _projectCommands.NewProjectAsync(this));
        OpenProjectCommand = ProjectCommand(() => _projectCommands.OpenProjectAsync(this));
        ImportProjectJsonCommand = ProjectCommand(() => _projectCommands.ImportProjectJsonAsync(this));
        ExportProjectJsonCommand = ProjectCommand(() => _projectCommands.ExportProjectJsonAsync(this));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        PlayModeCommand = new RelayCommand(() => Mode = EditorMode.Play);
        EditModeCommand = new RelayCommand(() => Mode = EditorMode.Edit);
        ToggleModeCommand = new RelayCommand(() => Mode = Mode == EditorMode.Play ? EditorMode.Edit : EditorMode.Play);
        OpenUpdateCenterCommand = new RelayCommand(() => UpdateCenterRequested?.Invoke(this, EventArgs.Empty));
        AboutCommand = new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty));

        Updates.PropertyChanged += OnUpdatesChanged;
        GameContent = composition.GameSurfaceFactory.CreateGameSurface(this);
    }

    public event EventHandler<EditorMode>? ModeChanged;

    /// <summary>The view opens the Update Center dialog.</summary>
    public event EventHandler? UpdateCenterRequested;

    /// <summary>The view opens the About dialog.</summary>
    public event EventHandler? AboutRequested;

    /// <summary>The view closes the main window.</summary>
    public event EventHandler? ExitRequested;

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
    public string Subtitle => IsPlayMode ? $"Playing: {_projectName}" : $"Editing: {_projectName}";

    /// <summary>The toggle offers the other mode.</summary>
    public string ModeToggleText => IsPlayMode ? "Edit Mode" : "Play Mode";

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

    public RelayCommand ExitCommand { get; }

    public RelayCommand PlayModeCommand { get; }

    public RelayCommand EditModeCommand { get; }

    public RelayCommand ToggleModeCommand { get; }

    public RelayCommand OpenUpdateCenterCommand { get; }

    public RelayCommand AboutCommand { get; }

    public void ShowStatus(string message) => StatusMessage = message;

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
