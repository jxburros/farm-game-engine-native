using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.ViewModels;

namespace FarmingRpgMaker.App.Projects;

/// <summary>File → Export Game…: targets, output folder, archives, Export, and the report.</summary>
internal sealed class ExportGameWindow : ProjectDialogWindow
{
    private readonly ExportGameViewModel _viewModel;
    private readonly StackPanel _report = new() { Name = "ExportReportPanel", Spacing = 8 };

    public ExportGameWindow(ExportGameViewModel viewModel)
        : base("Export Game", 640, 640)
    {
        Name = "ExportGameWindow";
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        CanResize = true;

        var windows = Check("ExportWindowsCheck", "Windows x64", nameof(ExportGameViewModel.ExportWindows));
        var linux = Check("ExportLinuxCheck", "Linux x64 (and Steam Deck)", nameof(ExportGameViewModel.ExportLinux));
        var web = Check("ExportWebCheck", "Web demo (a page for itch.io; runs in the browser)", nameof(ExportGameViewModel.ExportWeb));
        var archives = Check("ExportArchivesCheck", "Create archives (.zip for Windows and the web, .tar.gz for Linux)", nameof(ExportGameViewModel.CreateArchives));

        var folder = new TextBox { Name = "ExportFolderBox", Watermark = "Output folder" };
        folder.Bind(TextBox.TextProperty, new Binding(nameof(ExportGameViewModel.OutputFolder)) { Mode = BindingMode.TwoWay });
        var browse = Ui.Button(Ui.IconLabel("IconFolder", "Browse…"), () => { }, "subtle");
        browse.Name = "ExportBrowseButton";
        browse.Command = viewModel.BrowseCommand;
        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        folderRow.Children.Add(folder);
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(browse);

        var problems = Line(
            viewModel.IsBlockedByProblems ? "IconAlertCircle" : "IconCheckCircle",
            viewModel.ProblemsText,
            viewModel.IsBlockedByProblems ? "error" : viewModel.Summary.WarningCount > 0 ? "warning" : "success");
        problems.Name = "ExportProblems";

        var export = Ui.Button(Ui.IconLabel("IconPackage", "Export"), () => { }, "accent");
        export.Name = "ExportButton";
        export.Command = viewModel.ExportCommand;
        export.IsDefault = true;
        var close = Ui.Button("Close", Close, "subtle");
        close.Name = "ExportCloseButton";
        close.IsCancel = true;

        var header = Ui.VStack(
            2,
            Ui.Text("Export Game", "h1"),
            Ui.Text(viewModel.GameText, "h3"),
            Ui.Text(viewModel.GameDetail, "muted", "small"));

        var form = Ui.VStack(
            10,
            problems,
            Ui.VStack(6, Ui.Text("TARGETS", "section"), windows, linux, web),
            Ui.VStack(6, Ui.Text("OUTPUT FOLDER", "section"), folderRow, archives));

        Content = new Border
        {
            Padding = new Thickness(20),
            Child = new DockPanel
            {
                Children =
                {
                    NewProjectWindow.Docked(header, Dock.Top, new Thickness(0, 0, 0, 12)),
                    NewProjectWindow.Docked(form, Dock.Top, new Thickness(0, 0, 0, 12)),
                    NewProjectWindow.Docked(Footer(close, export), Dock.Bottom),
                    new ScrollViewer { Content = _report },
                },
            },
        };

        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
        RenderReport();
    }

    private static CheckBox Check(string name, string label, string property)
    {
        var check = new CheckBox { Name = name, Content = label };
        check.Bind(ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return check;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExportGameViewModel.Report) or nameof(ExportGameViewModel.IsExporting) or nameof(ExportGameViewModel.StatusText))
        {
            RenderReport();
        }
    }

    /// <summary>Rebuilds the report panel from the view model.</summary>
    private void RenderReport()
    {
        _report.Children.Clear();
        var status = Ui.Wrapped(_viewModel.StatusText, "h3");
        status.Name = "ExportStatusText";
        if (_viewModel.StatusText.Length > 0)
        {
            _report.Children.Add(status);
        }

        foreach (var error in _viewModel.ReportErrors)
        {
            _report.Children.Add(Line("IconAlertCircle", error, "error"));
        }

        foreach (var result in _viewModel.TargetResults)
        {
            var body = new StackPanel { Spacing = 3 };
            var title = Ui.HStack(8, Ui.Icon(result.Ok ? "IconCheckCircle" : "IconAlertCircle"), Ui.Text(result.DisplayName, "h3"));
            ((TextBlock)title.Children[1]).Foreground = (IBrush?)Application.Current?.FindResource("FarmForegroundBrush");
            body.Children.Add(title);
            if (result.Folder is { } folder)
            {
                var open = Ui.Button(Ui.IconLabel("IconFolder", "Open folder"), () => { }, "subtle");
                open.Name = $"OpenFolder_{result.Target}";
                open.Command = _viewModel.OpenFolderCommand;
                open.CommandParameter = folder;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                var path = Ui.Wrapped(folder, "small");
                path.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(path);
                Grid.SetColumn(open, 1);
                row.Children.Add(open);
                body.Children.Add(row);
            }

            foreach (var file in result.Files)
            {
                body.Children.Add(Ui.Text("• " + file, "muted", "small"));
            }

            if (result.Archive is { } archive)
            {
                body.Children.Add(Ui.Wrapped("Archive: " + archive, "small"));
            }

            foreach (var error in result.Errors)
            {
                body.Children.Add(Ui.Wrapped("• " + error, "small"));
            }

            foreach (var warning in result.Warnings)
            {
                body.Children.Add(Ui.Wrapped("• " + warning, "small"));
            }

            var card = new Border { Name = $"ExportResult_{result.Target}", Child = body }.WithClasses("status", result.Ok ? "success" : "error");
            _report.Children.Add(card);
        }

        if (_viewModel.ReportWarnings.Count > 0)
        {
            var warnings = new StackPanel { Spacing = 3 };
            warnings.Children.Add(Ui.Text($"Warnings ({_viewModel.ReportWarnings.Count})", "h3"));
            foreach (var warning in _viewModel.ReportWarnings)
            {
                warnings.Children.Add(Ui.Wrapped("• " + warning, "small"));
            }

            _report.Children.Add(new Border { Name = "ExportWarnings", Child = warnings }.WithClasses("status", "warning"));
        }
    }

    /// <summary>A status box with an icon and wrapping text.</summary>
    private static Border Line(string icon, string text, string kind)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        grid.Children.Add(Ui.Icon(icon));
        var message = Ui.Wrapped(text, "small");
        Grid.SetColumn(message, 1);
        grid.Children.Add(message);
        return new Border { Child = grid }.WithClasses("status", kind);
    }
}
