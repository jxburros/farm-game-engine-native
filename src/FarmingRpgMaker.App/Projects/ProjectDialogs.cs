using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FarmEngine.Content;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Hosting;

namespace FarmingRpgMaker.App.Projects;

/// <summary>The user's choice in the New Project dialog.</summary>
public sealed record NewProjectChoice(string TemplateId, string Name);

/// <summary>UI the project commands need (swappable for tests).</summary>
public interface IProjectDialogs
{
    Task<NewProjectChoice?> ChooseNewProjectAsync(IShellHost shell, IReadOnlyList<TemplateInfo> templates);

    /// <summary>Project list (open/delete). Returns the id to open, or null.</summary>
    Task<string?> ChooseProjectAsync(IShellHost shell, ProjectStore store, string? currentId);

    /// <summary>Returns the picked file's name and text, or null when cancelled.</summary>
    Task<(string FileName, string Json)?> PickImportFileAsync(IShellHost shell);

    /// <summary>Asks where to save and writes <paramref name="json"/>. Returns the saved file name, or null.</summary>
    Task<string?> SaveExportAsync(IShellHost shell, string suggestedFileName, string json);

    Task ShowErrorsAsync(IShellHost shell, string title, IReadOnlyList<string> errors);
}

/// <summary>Avalonia implementation: modal windows + the platform file pickers.</summary>
public sealed class AvaloniaProjectDialogs : IProjectDialogs
{
    private static readonly FilePickerFileType JsonFiles = new("Project JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    public async Task<NewProjectChoice?> ChooseNewProjectAsync(IShellHost shell, IReadOnlyList<TemplateInfo> templates)
    {
        if (shell.TopLevel is not Window owner)
        {
            return null;
        }

        var window = new NewProjectWindow(templates);
        return await window.ShowDialog<NewProjectChoice?>(owner).ConfigureAwait(true);
    }

    public async Task<string?> ChooseProjectAsync(IShellHost shell, ProjectStore store, string? currentId)
    {
        if (shell.TopLevel is not Window owner)
        {
            return null;
        }

        var window = new OpenProjectWindow(store, currentId);
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }

    public async Task<(string FileName, string Json)?> PickImportFileAsync(IShellHost shell)
    {
        if (shell.TopLevel?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Project JSON",
            AllowMultiple = false,
            FileTypeFilter = [JsonFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
        using var reader = new StreamReader(stream);
        return (files[0].Name, await reader.ReadToEndAsync().ConfigureAwait(true));
    }

    public async Task<string?> SaveExportAsync(IShellHost shell, string suggestedFileName, string json)
    {
        if (shell.TopLevel?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Project JSON",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonFiles],
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(json).ConfigureAwait(true);
        return file.Name;
    }

    public async Task ShowErrorsAsync(IShellHost shell, string title, IReadOnlyList<string> errors)
    {
        if (shell.TopLevel is not Window owner)
        {
            shell.ShowStatus($"{title}: {string.Join("; ", errors)}");
            return;
        }

        await new ErrorWindow(title, errors).ShowDialog(owner).ConfigureAwait(true);
    }
}

/// <summary>Shared chrome for the small project dialogs.</summary>
internal abstract class ProjectDialogWindow : Window
{
    protected ProjectDialogWindow(string title, double width, double height)
    {
        Title = title;
        Width = width;
        Height = height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
    }

    protected static Grid Footer(params Control[] buttons)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 14, 0, 0) };
        var stack = Ui.HStack(8, buttons);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        return grid;
    }
}

/// <summary>File → New Project: name + template (web ProjectManager "New" tab).</summary>
internal sealed class NewProjectWindow : ProjectDialogWindow
{
    public NewProjectWindow(IReadOnlyList<TemplateInfo> templates)
        : base("New Project", 540, 560)
    {
        Name = "NewProjectWindow";
        var nameBox = new TextBox { Name = "NewProjectName", Text = "My Farming Game", Watermark = "Project name" };
        var list = new StackPanel { Spacing = 6 };
        RadioButton? first = null;
        foreach (var template in templates)
        {
            var radio = new RadioButton
            {
                GroupName = "template",
                Tag = template.Id,
                Name = $"Template_{template.Id}",
                Content = Ui.VStack(1, Ui.Text(template.Name, "h3"), Ui.Wrapped(template.Description, "muted", "small")),
            };
            radio.Classes.Add("template");
            ((TextBlock)((StackPanel)radio.Content).Children[0]).Foreground = (IBrush?)Application.Current?.FindResource("FarmForegroundBrush");
            first ??= radio;
            list.Children.Add(new Border { Child = radio }.WithClasses("row"));
        }

        if (first is not null)
        {
            first.IsChecked = true;
        }

        var create = Ui.Button("Create project", () =>
        {
            var selected = list.Children.OfType<Border>().Select(b => b.Child).OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
            var name = string.IsNullOrWhiteSpace(nameBox.Text) ? "Untitled Game" : nameBox.Text.Trim();
            Close(selected?.Tag is string id ? new NewProjectChoice(id, name) : null);
        }, "accent");
        create.Name = "CreateProjectButton";
        create.IsDefault = true;
        var cancel = Ui.Button("Cancel", () => Close(null), "subtle");
        cancel.IsCancel = true;

        Content = new Border
        {
            Padding = new Thickness(20),
            Child = new DockPanel
            {
                Children =
                {
                    Docked(Ui.VStack(4, Ui.Text("Create a new game", "h1"), Ui.Text("Start from a sample game or an empty map.", "muted")), Dock.Top),
                    Docked(Ui.VStack(4, Ui.Text("NAME", "section"), nameBox), Dock.Top, new Thickness(0, 16, 0, 12)),
                    Docked(Footer(cancel, create), Dock.Bottom),
                    Ui.VStack(6, Ui.Text("TEMPLATE", "section"), new ScrollViewer { Content = list }),
                },
            },
        };
        Opened += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };
    }

    internal static Control Docked(Control control, Dock dock, Thickness? margin = null)
    {
        DockPanel.SetDock(control, dock);
        if (margin is { } m)
        {
            control.Margin = m;
        }

        return control;
    }
}

/// <summary>File → Open Project: the stored projects with open and delete (web ProjectManager list).</summary>
internal sealed class OpenProjectWindow : ProjectDialogWindow
{
    private readonly ProjectStore _store;
    private readonly string? _currentId;
    private readonly ListBox _list = new() { Name = "ProjectList" };
    private readonly Button _open;
    private readonly Button _delete;
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private string? _confirmDeleteId;

    public OpenProjectWindow(ProjectStore store, string? currentId)
        : base("Open Project", 600, 500)
    {
        Name = "OpenProjectWindow";
        _store = store;
        _currentId = currentId;
        _list.Classes.Add("projects");
        _list.SelectionChanged += (_, _) =>
        {
            _confirmDeleteId = null;
            UpdateButtons();
        };
        _list.DoubleTapped += (_, _) => OpenSelected();

        _open = Ui.Button("Open", OpenSelected, "accent");
        _open.Name = "OpenSelectedProjectButton";
        _open.IsDefault = true;
        _delete = Ui.Button(Ui.IconLabel("IconDelete", "Delete"), DeleteSelected, "subtle");
        _delete.Name = "DeleteProjectButton";
        var cancel = Ui.Button("Cancel", () => Close(null), "subtle");
        cancel.IsCancel = true;
        _hint.Classes.Add("muted");

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(_delete);
        _hint.Margin = new Thickness(12, 0);
        _hint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_hint, 1);
        footer.Children.Add(_hint);
        var right = Ui.HStack(8, cancel, _open);
        Grid.SetColumn(right, 2);
        footer.Children.Add(right);

        Content = new Border
        {
            Padding = new Thickness(20),
            Child = new DockPanel
            {
                Children =
                {
                    NewProjectWindow.Docked(Ui.VStack(4, Ui.Text("Your projects", "h1"), Ui.Text($"Stored in {store.ProjectsDirectory}", "muted", "small")), Dock.Top, new Thickness(0, 0, 0, 12)),
                    NewProjectWindow.Docked(footer, Dock.Bottom),
                    _list,
                },
            },
        };
        Reload();
    }

    private void Reload()
    {
        _list.Items.Clear();
        foreach (var project in _store.List())
        {
            var title = Ui.HStack(8, Ui.Text(project.Name, "h3"));
            ((TextBlock)title.Children[0]).Foreground = (IBrush?)Application.Current?.FindResource("FarmForegroundBrush");
            if (project.Id == _currentId)
            {
                title.Children.Add(new Border { Child = Ui.Text("Open now") }.WithClasses("qty-chip"));
            }

            var detail = Ui.Text($"Updated {Relative(project.UpdatedAt)} · {Size(project.SizeBytes)} · {project.Id}", "muted", "small");
            _list.Items.Add(new ListBoxItem { Tag = project.Id, Content = Ui.VStack(2, title, detail) });
        }

        if (_list.ItemCount > 0)
        {
            _list.SelectedIndex = 0;
        }

        UpdateButtons();
    }

    private string? SelectedId => (_list.SelectedItem as ListBoxItem)?.Tag as string;

    private void UpdateButtons()
    {
        _open.IsEnabled = SelectedId is not null;
        _delete.IsEnabled = SelectedId is not null && SelectedId != _currentId;
        _delete.Content = Ui.IconLabel("IconDelete", _confirmDeleteId is not null ? "Confirm delete" : "Delete");
        _hint.Text = _confirmDeleteId is not null
            ? "Deleting can't be undone — click again to confirm."
            : SelectedId == _currentId && SelectedId is not null ? "The open project can't be deleted." : "";
    }

    private void OpenSelected()
    {
        if (SelectedId is { } id)
        {
            Close(id);
        }
    }

    private void DeleteSelected()
    {
        if (SelectedId is not { } id || id == _currentId)
        {
            return;
        }

        if (_confirmDeleteId != id)
        {
            _confirmDeleteId = id;
            UpdateButtons();
            return;
        }

        _store.Delete(id);
        _confirmDeleteId = null;
        Reload();
    }

    private static string Relative(DateTimeOffset time)
    {
        var age = DateTimeOffset.UtcNow - time;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} h ago";
        }

        return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}

/// <summary>Shows load/migration errors.</summary>
internal sealed class ErrorWindow : ProjectDialogWindow
{
    public ErrorWindow(string title, IReadOnlyList<string> errors)
        : base(title, 520, 360)
    {
        Name = "ProjectErrorWindow";
        var list = new StackPanel { Spacing = 4 };
        foreach (var error in errors.Take(20))
        {
            list.Children.Add(Ui.Wrapped("• " + error, "small"));
        }

        if (errors.Count > 20)
        {
            list.Children.Add(Ui.Text($"…and {errors.Count - 20} more", "muted", "small"));
        }

        var ok = Ui.Button("OK", Close, "accent");
        ok.IsDefault = true;
        ok.IsCancel = true;
        var heading = Ui.HStack(10, Ui.Icon("IconAlertCircle", 24), Ui.Wrapped(title, "h2"));
        ((PathIcon)heading.Children[0]).Foreground = (IBrush?)Application.Current?.FindResource("FarmDestructiveBrush");
        Content = new Border
        {
            Padding = new Thickness(20),
            Child = new DockPanel
            {
                Children =
                {
                    NewProjectWindow.Docked(heading, Dock.Top, new Thickness(0, 0, 0, 12)),
                    NewProjectWindow.Docked(Footer(ok), Dock.Bottom),
                    new ScrollViewer { Content = new Border { Child = list }.WithClasses("status", "error") },
                },
            },
        };
    }
}
