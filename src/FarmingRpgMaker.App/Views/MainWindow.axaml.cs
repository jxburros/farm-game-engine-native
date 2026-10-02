using Avalonia.Controls;
using FarmingRpgMaker.App.Localization;
using FarmingRpgMaker.App.Services;
using FarmingRpgMaker.App.ViewModels;

namespace FarmingRpgMaker.App.Views;

public partial class MainWindow : Window
{
    /// <summary>The way out of the unsaved-changes prompt when closing.</summary>
    internal const string QuitAnyway = "Quit anyway";

    /// <summary>The way out of the unsaved-changes prompt before Restart &amp; install.</summary>
    internal const string InstallAnyway = "Install anyway";

    private readonly IUrlLauncher _launcher;
    private MainWindowViewModel? _viewModel;
    private bool _exitConfirmed;
    private bool _askingAboutUnsavedChanges;

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
            _viewModel.Updates.Restarting -= OnUpdateRestarting;
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
            _viewModel.Updates.Restarting += OnUpdateRestarting;
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

    /// <summary>True once the window may close (or has closed) without asking about unsaved edits.</summary>
    public bool IsExitConfirmed => _exitConfirmed;

    /// <summary>
    /// Closing writes pending edits first. When saving still fails, the close is called off and
    /// the unsaved-changes prompt offers Export Project JSON…, Retry save or Quit anyway, so edits
    /// that exist only in memory are never thrown away silently.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _exitConfirmed || _viewModel is null)
        {
            return;
        }

        if (_askingAboutUnsavedChanges || !_viewModel.PrepareForExit())
        {
            e.Cancel = true;
            _ = ConfirmUnsavedThenAsync(QuitAnyway, Close);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_viewModel is { } viewModel)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ShowStartupMessagesAsync(viewModel));
        }
    }

    private static async Task ShowStartupMessagesAsync(MainWindowViewModel viewModel)
    {
        try
        {
            await viewModel.ShowStartupMessagesAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed message must not take the editor down; the status line has the gist.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError($"Showing the startup messages failed: {ex}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _exitConfirmed = true;
        base.OnClosed(e);
    }

    /// <summary>Restart &amp; install ends the process: the same check as closing, before it happens.</summary>
    private void OnUpdateRestarting(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitConfirmed || _viewModel is null)
        {
            return;
        }

        if (_askingAboutUnsavedChanges || !_viewModel.PrepareForExit())
        {
            e.Cancel = true;
            var updates = _viewModel.Updates;
            _ = ConfirmUnsavedThenAsync(InstallAnyway, updates.ApplyAndRestart);
        }
    }

    private async Task ConfirmUnsavedThenAsync(string discardText, Action proceed)
    {
        if (_askingAboutUnsavedChanges || _viewModel is null)
        {
            return;
        }

        _askingAboutUnsavedChanges = true;
        bool confirmed;
        try
        {
            confirmed = await _viewModel.ResolveUnsavedChangesAsync(discardText).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The prompt failed (an export error): stay open, edits and all.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError($"Asking about unsaved changes failed: {ex}");
            _viewModel.ShowStatus($"Something went wrong: {ex.Message}");
            confirmed = false;
        }
        finally
        {
            _askingAboutUnsavedChanges = false;
        }

        if (confirmed)
        {
            _exitConfirmed = true;
            proceed();
        }
    }
}
