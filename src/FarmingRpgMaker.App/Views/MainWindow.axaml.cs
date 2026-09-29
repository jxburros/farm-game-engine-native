using Avalonia.Controls;
using FarmingRpgMaker.App.Localization;
using FarmingRpgMaker.App.Services;
using FarmingRpgMaker.App.ViewModels;

namespace FarmingRpgMaker.App.Views;

public partial class MainWindow : Window
{
    private readonly IUrlLauncher _launcher;
    private MainWindowViewModel? _viewModel;

    public MainWindow()
        : this(new ShellUrlLauncher())
    {
    }

    public MainWindow(IUrlLauncher launcher)
    {
        _launcher = launcher;
        InitializeComponent();
        BuildLanguageMenu();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.UpdateCenterRequested -= OnUpdateCenterRequested;
            _viewModel.AboutRequested -= OnAboutRequested;
            _viewModel.ExitRequested -= OnExitRequested;
            _viewModel.WelcomeRequested -= OnWelcomeRequested;
            _viewModel.CreatorGuideRequested -= OnCreatorGuideRequested;
            _viewModel.ShortcutsRequested -= OnShortcutsRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TopLevel = null;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.UpdateCenterRequested += OnUpdateCenterRequested;
            _viewModel.AboutRequested += OnAboutRequested;
            _viewModel.ExitRequested += OnExitRequested;
            _viewModel.WelcomeRequested += OnWelcomeRequested;
            _viewModel.CreatorGuideRequested += OnCreatorGuideRequested;
            _viewModel.ShortcutsRequested += OnShortcutsRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.TopLevel = this;
        }

        UpdateLanguageChecks();
    }

    /// <summary>The open welcome tour, if any.</summary>
    public WelcomeWindow? OpenWelcome { get; private set; }

    /// <summary>The open creator guide, if any (one at a time).</summary>
    public CreatorGuideWindow? OpenCreatorGuide { get; private set; }

    /// <summary>The open shortcuts list, if any (one at a time).</summary>
    public ShortcutsWindow? OpenShortcuts { get; private set; }

    /// <summary>
    /// Opens the welcome tour when it has never been dismissed (the app calls this once the
    /// window is open); closing it remembers that it was seen. Returns whether it opened.
    /// </summary>
    public bool ShowWelcomeIfFirstRun()
    {
        if (_viewModel is not { ShouldShowWelcome: true })
        {
            return false;
        }

        ShowWelcome();
        return true;
    }

    private void OnWelcomeRequested(object? sender, EventArgs e) => ShowWelcome();

    private void ShowWelcome()
    {
        if (OpenWelcome is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new WelcomeWindow();
        window.GuideRequested += (_, _) => ShowCreatorGuide(window);
        window.Closed += (_, _) =>
        {
            OpenWelcome = null;
            _viewModel?.MarkWelcomeSeen();
        };
        OpenWelcome = window;
        window.Show(this);
    }

    private void OnCreatorGuideRequested(object? sender, EventArgs e) => ShowCreatorGuide(this);

    private void ShowCreatorGuide(Window owner)
    {
        if (OpenCreatorGuide is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new CreatorGuideWindow(_launcher);
        window.Closed += (_, _) => OpenCreatorGuide = null;
        OpenCreatorGuide = window;
        window.Show(owner);
    }

    private void OnShortcutsRequested(object? sender, EventArgs e)
    {
        if (OpenShortcuts is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new ShortcutsWindow();
        window.Closed += (_, _) => OpenShortcuts = null;
        OpenShortcuts = window;
        window.Show(this);
    }

    /// <summary>Help → Language: one radio item per table in <see cref="EditorStrings.Languages"/>.</summary>
    private void BuildLanguageMenu()
    {
        var menu = this.FindControl<MenuItem>("LanguageMenuItem");
        if (menu is null)
        {
            return;
        }

        foreach (var language in EditorStrings.Languages)
        {
            var item = new MenuItem
            {
                Name = $"LanguageMenuItem_{language.Code}",
                Header = language.NativeName,
                Tag = language.Code,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "language",
            };
            item.Click += (_, _) => _viewModel?.SetLanguage(language.Code);
            menu.Items.Add(item);
        }

        UpdateLanguageChecks();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Language))
        {
            UpdateLanguageChecks();
        }
    }

    private void UpdateLanguageChecks()
    {
        foreach (var item in this.FindControl<MenuItem>("LanguageMenuItem")?.Items.OfType<MenuItem>() ?? [])
        {
            item.IsChecked = Equals(item.Tag, EditorStrings.Language);
        }
    }

    /// <summary>The currently open Update Center, if any (one at a time).</summary>
    public UpdateCenterWindow? OpenUpdateCenter { get; private set; }

    private async void OnUpdateCenterRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (OpenUpdateCenter is { } existing)
        {
            existing.Activate();
            return;
        }

        using var viewModel = new UpdateCenterViewModel(_viewModel.Updates, _launcher);
        var window = new UpdateCenterWindow { DataContext = viewModel };
        OpenUpdateCenter = window;
        try
        {
            await window.ShowDialog(this).ConfigureAwait(true);
        }
        finally
        {
            OpenUpdateCenter = null;
        }
    }

    private async void OnAboutRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var window = new AboutWindow { DataContext = new AboutViewModel(_viewModel.Updates.CurrentVersion, _launcher) };
        await window.ShowDialog(this).ConfigureAwait(true);
    }

    private void OnExitRequested(object? sender, EventArgs e) => Close();
}
