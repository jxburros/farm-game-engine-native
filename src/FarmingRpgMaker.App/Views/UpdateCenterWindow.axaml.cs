using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FarmingRpgMaker.App.Views;

public partial class UpdateCenterWindow : Window
{
    public UpdateCenterWindow()
    {
        InitializeComponent();
        ReleaseNotesView.LinkClicked += (_, url) =>
        {
            if (DataContext is ViewModels.UpdateCenterViewModel viewModel && viewModel.OpenLinkCommand.CanExecute(url))
            {
                viewModel.OpenLinkCommand.Execute(url);
            }
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
