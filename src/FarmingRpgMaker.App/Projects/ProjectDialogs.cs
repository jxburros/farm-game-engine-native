using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FarmEngine.Authoring;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.ViewModels;

namespace FarmingRpgMaker.App.Projects;

/// <summary>The user's choice in the New Project dialog.</summary>
public sealed record NewProjectChoice(string TemplateId, string Name);

/// <summary>What to do with edits that could not be saved (the unsaved-changes prompt).</summary>
public enum UnsavedChangesChoice
{
    /// <summary>Stay with the open project (closing, installing or switching is called off).</summary>
    Stay,

    /// <summary>Try to save again.</summary>
    Retry,

    /// <summary>Export Project JSON… somewhere else, then ask again.</summary>
    Export,

    /// <summary>Go on without the edits (Quit anyway, Install anyway…).</summary>
    Discard,
}

/// <summary>UI the project commands need (swappable for tests).</summary>
public interface IProjectDialogs
{
    Task<NewProjectChoice?> ChooseNewProjectAsync(IShellHost shell, IReadOnlyList<ProjectTemplateInfo> templates);

    /// <summary>Project list (open/delete). Returns the id to open, or null.</summary>
    Task<string?> ChooseProjectAsync(IShellHost shell, ProjectStore store, string? currentId);

    /// <summary>Returns the picked file's name and text, or null when cancelled.</summary>
    Task<(string FileName, string Json)?> PickImportFileAsync(IShellHost shell);

    /// <summary>Asks where to save and writes <paramref name="json"/>. Returns the saved file name, or null.</summary>
    Task<string?> SaveExportAsync(IShellHost shell, string suggestedFileName, string json);

    Task ShowErrorsAsync(IShellHost shell, string title, IReadOnlyList<string> errors);

    /// <summary>Asks for a folder (starting at <paramref name="startFolder"/> when given). Null when cancelled.</summary>
    Task<string?> PickFolderAsync(IShellHost shell, string title, string? startFolder);

    /// <summary>Shows the Export Game dialog over <paramref name="viewModel"/> until it closes.</summary>
    Task ShowExportGameAsync(IShellHost shell, ExportGameViewModel viewModel);

    /// <summary>
    /// Saving failed (<paramref name="error"/>) and the edits are about to be left behind: asks
    /// whether to retry, export a copy, go on (<paramref name="discardText"/>) or stay.
    /// <paramref name="note"/> reports the last attempt ("Still can't save…").
    /// </summary>
    Task<UnsavedChangesChoice> AskUnsavedChangesAsync(IShellHost shell, string error, string discardText, string? note);
}

/// <summary>Avalonia implementation: modal windows + the platform file pickers.</summary>
public sealed class AvaloniaProjectDialogs : IProjectDialogs
{
    private static readonly FilePickerFileType JsonFiles = new("Project JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    public async Task<NewProjectChoice?> ChooseNewProjectAsync(IShellHost shell, IReadOnlyList<ProjectTemplateInfo> templates)
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

        try
        {
            return (files[0].Name, await PickedFiles.ReadTextAsync(files[0], PickedFiles.MaxProjectBytes, "project files").ConfigureAwait(true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await ShowErrorsAsync(shell, $"{files[0].Name} could not be imported", [error.Message]).ConfigureAwait(true);
            return null;
        }
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

        try
        {
            await PickedFiles.WriteTextAsync(file, json).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await ShowErrorsAsync(shell, $"{file.Name} could not be saved", [error.Message]).ConfigureAwait(true);
            return null;
        }

        return file.Name;
    }

    public async Task<string?> PickFolderAsync(IShellHost shell, string title, string? startFolder)
    {
        if (shell.TopLevel?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var start = startFolder is null ? null : await storage.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true);
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        }).ConfigureAwait(true);
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task ShowExportGameAsync(IShellHost shell, ExportGameViewModel viewModel)
    {
        if (shell.TopLevel is not Window owner)
        {
            return;
        }

        await new ExportGameWindow(viewModel).ShowDialog(owner).ConfigureAwait(true);
    }

    public async Task<UnsavedChangesChoice> AskUnsavedChangesAsync(IShellHost shell, string error, string discardText, string? note)
    {
        if (shell.TopLevel is not Window owner)
        {
            return UnsavedChangesChoice.Stay;
        }

        // Over the dialog that asked (the Update Center's Restart & install), else the main window.
        var parent = owner.OwnedWindows.LastOrDefault(w => w.IsVisible && w.IsActive) ?? owner;
        return await new UnsavedChangesWindow(error, discardText, note).ShowDialog<UnsavedChangesChoice>(parent).ConfigureAwait(true);
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
    public NewProjectWindow(IReadOnlyList<ProjectTemplateInfo> templates)
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
    private readonly Button _rename;
    private readonly Button _duplicate;
    private readonly TextBox _renameBox = new() { Name = "RenameProjectName", Watermark = "Project name", MinWidth = 260 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private string? _confirmDeleteId;
    private string? _notice;

    public OpenProjectWindow(ProjectStore store, string? currentId)
        : base("Open Project", 640, 560)
    {
        Name = "OpenProjectWindow";
        _store = store;
        _currentId = currentId;
        _list.Classes.Add("projects");
        _list.SelectionChanged += (_, _) =>
        {
            _confirmDeleteId = null;
            _notice = null;
            _renameBox.Text = SelectedSummary?.Name ?? "";
            UpdateButtons();
        };
        _list.DoubleTapped += (_, _) => OpenSelected();

        _open = Ui.Button("Open", OpenSelected, "accent");
        _open.Name = "OpenSelectedProjectButton";
        _open.IsDefault = true;
        _delete = Ui.Button(Ui.IconLabel("IconDelete", "Delete"), DeleteSelected, "subtle");
        _delete.Name = "DeleteProjectButton";
        _rename = Ui.Button("Rename", RenameSelected, "subtle");
        _rename.Name = "RenameProjectButton";
        _duplicate = Ui.Button("Duplicate", DuplicateSelected, "subtle");
        _duplicate.Name = "DuplicateProjectButton";
        _renameBox.TextChanged += (_, _) => UpdateButtons();
        _renameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                RenameSelected();
                e.Handled = true;
            }
        };
        var cancel = Ui.Button("Cancel", () => Close(null), "subtle");
        cancel.IsCancel = true;
        _hint.Classes.Add("muted");
        _hint.Name = "ProjectListHint";
        var manage = Ui.HStack(8, _renameBox, _rename, _duplicate);
        manage.Margin = new Thickness(0, 10, 0, 0);

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
                    NewProjectWindow.Docked(manage, Dock.Bottom),
                    _list,
                },
            },
        };
        Reload();
    }

    private void Reload(string? select = null)
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
            _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, select)) ?? _list.Items[0];
        }

        UpdateButtons();
    }

    private string? SelectedId => (_list.SelectedItem as ListBoxItem)?.Tag as string;

    private ProjectSummary? SelectedSummary => SelectedId is { } id ? _store.List().FirstOrDefault(p => p.Id == id) : null;

    private void UpdateButtons()
    {
        _open.IsEnabled = SelectedId is not null;
        _delete.IsEnabled = SelectedId is not null && SelectedId != _currentId;
        _delete.Content = Ui.IconLabel("IconDelete", _confirmDeleteId is not null ? "Confirm delete" : "Delete");
        _rename.IsEnabled = SelectedId is not null && !string.IsNullOrWhiteSpace(_renameBox.Text);
        _duplicate.IsEnabled = SelectedId is not null;
        _hint.Text = _confirmDeleteId is not null
            ? "Deleting can't be undone — click again to confirm."
            : _notice ?? (SelectedId == _currentId && SelectedId is not null ? "The open project can't be deleted." : "");
    }

    private void RenameSelected()
    {
        if (SelectedId is not { } id || string.IsNullOrWhiteSpace(_renameBox.Text))
        {
            return;
        }

        var renamed = _store.Rename(id, _renameBox.Text);
        Reload(id);
        _notice = renamed.Ok ? $"Renamed to \"{renamed.Project!.Name}\"." : string.Join("; ", renamed.Errors);
        UpdateButtons();
    }

    private void DuplicateSelected()
    {
        if (SelectedId is not { } id)
        {
            return;
        }

        var copy = _store.Duplicate(id);
        Reload(copy.Ok ? copy.Project!.Id : id);
        _notice = copy.Ok ? $"Created \"{copy.Project!.Name}\"." : string.Join("; ", copy.Errors);
        UpdateButtons();
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

        _confirmDeleteId = null;
        try
        {
            _store.Delete(id);
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked or read-only file: say so here instead of taking the editor down.
            Reload(id);
            _notice = $"Could not delete the project: {ex.Message}";
            UpdateButtons();
        }
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

/// <summary>
/// Edits could not be saved and are about to be left behind (closing the window, installing an
/// update, opening another project): Export Project JSON…, Retry save, go on anyway, or stay.
/// </summary>
internal sealed class UnsavedChangesWindow : ProjectDialogWindow
{
    public UnsavedChangesWindow(string error, string discardText, string? note)
        : base("Your changes are not saved", 560, 330)
    {
        Name = "UnsavedChangesWindow";
        var heading = Ui.HStack(10, Ui.Icon("IconAlertCircle", 24), Ui.Wrapped("Your changes could not be saved", "h2"));
        ((PathIcon)heading.Children[0]).Foreground = (IBrush?)Application.Current?.FindResource("FarmDestructiveBrush");
        var message = Ui.Wrapped($"Saving the project failed: {error} The changes are only open in this window. Export a copy to another folder, or free up disk space or fix the folder's permissions and retry.", "small");
        message.Name = "UnsavedChangesMessage";
        var status = Ui.Wrapped(note ?? "", "muted", "small");
        status.Name = "UnsavedChangesNote";
        status.IsVisible = note is not null;
        Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Assertive);

        var export = Ui.Button("Export Project JSON…", () => Close(UnsavedChangesChoice.Export), "accent");
        export.Name = "UnsavedExportButton";
        export.IsDefault = true;
        var retry = Ui.Button("Retry save", () => Close(UnsavedChangesChoice.Retry), "subtle");
        retry.Name = "UnsavedRetryButton";
        var discard = Ui.Button(discardText, () => Close(UnsavedChangesChoice.Discard), "subtle");
        discard.Name = "UnsavedDiscardButton";
        var stay = Ui.Button("Cancel", () => Close(UnsavedChangesChoice.Stay), "subtle");
        stay.Name = "UnsavedStayButton";
        stay.IsCancel = true;
        Content = new Border
        {
            Padding = new Thickness(20),
            Child = new DockPanel
            {
                Children =
                {
                    NewProjectWindow.Docked(heading, Dock.Top, new Thickness(0, 0, 0, 12)),
                    NewProjectWindow.Docked(Footer(discard, stay, retry, export), Dock.Bottom),
                    Ui.VStack(10, message, status),
                },
            },
        };
    }
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
