using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FarmingRpgMaker.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
