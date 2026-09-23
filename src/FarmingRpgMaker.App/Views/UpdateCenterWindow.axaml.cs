using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FarmingRpgMaker.App.Views;

public partial class UpdateCenterWindow : Window
{
    public UpdateCenterWindow()
    {
        InitializeComponent();
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
