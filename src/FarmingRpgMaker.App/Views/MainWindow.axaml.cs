using Avalonia.Controls;
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
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.UpdateCenterRequested -= OnUpdateCenterRequested;
            _viewModel.AboutRequested -= OnAboutRequested;
            _viewModel.ExitRequested -= OnExitRequested;
            _viewModel.TopLevel = null;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.UpdateCenterRequested += OnUpdateCenterRequested;
            _viewModel.AboutRequested += OnAboutRequested;
            _viewModel.ExitRequested += OnExitRequested;
            _viewModel.TopLevel = this;
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
