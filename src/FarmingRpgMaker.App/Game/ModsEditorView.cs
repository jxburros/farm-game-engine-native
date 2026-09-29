using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Installed pack order, an explicit manifest/permission review before installation, the curated
/// registry, and "Export selection as pack" (web ModsEditor).
/// </summary>
public sealed class ModsEditorView : UserControl
{
    private static readonly FilePickerFileType PackFiles = new("Content pack JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };
    private readonly ProjectWorkspace _workspace;
    private readonly StackPanel _installed = new() { Name = "InstalledPacks", Spacing = 8 };
    private readonly StackPanel _review = new() { Name = "PackReview", Spacing = 8 };
    private readonly TextBlock _message = Ui.Wrapped("Choose a content pack JSON file to review it.", "muted", "small");
    private readonly Button _install;
    private readonly StackPanel _registry = new() { Name = "PackRegistry", Spacing = 6 };
    private readonly TextBox _exportName = new() { Name = "ExportPackName", Text = "my-pack", MinWidth = 220 };
    private readonly StackPanel _exportCategories = new() { Name = "ExportCategories", Spacing = 4 };
    private readonly TextBlock _exportMessage = Ui.Wrapped("", "muted", "small");
    private readonly HashSet<string> _exportKeys = [.. Mods.DefaultExportKeys];
    private readonly HashSet<(string Key, string Id)> _exportExcluded = [];
    private ContentPack? _pending;
    private string? _selectedPackId;

    public ModsEditorView(ProjectWorkspace workspace)
    {
        _workspace = workspace;
        Name = "ModsEditorView";
        _message.Name = "ModsMessage";
        var layout = new StackPanel { Spacing = 12, Margin = new Thickness(20), MaxWidth = 900 };
        layout.Children.Add(Ui.Text("INSTALLED PACKS", "section"));
        layout.Children.Add(_installed);
        layout.Children.Add(Ui.Text("ADD PACK", "section"));
        var choose = Ui.Button("Choose pack JSON", async () => await ChoosePackAsync(), "tool");
        choose.Name = "ChoosePackButton";
        layout.Children.Add(choose);
        layout.Children.Add(_message);
        layout.Children.Add(_review);
        _install = Ui.Button("Install reviewed pack", InstallReviewed, "accent");
        _install.Name = "InstallPackButton";
        _install.IsEnabled = false;
        layout.Children.Add(_install);
        layout.Children.Add(Ui.Text("REGISTRY", "section"));
        layout.Children.Add(Ui.Wrapped("Curated packs that ship with the editor. Install opens the same review as a pack file.", "muted", "small"));
        layout.Children.Add(_registry);
        layout.Children.Add(Ui.Text("EXPORT SELECTION AS PACK", "section"));
        layout.Children.Add(Ui.Wrapped("Turn what you built here into a shareable content pack. Tick a type to include all of it, or open it to pick entries.", "muted", "small"));
        layout.Children.Add(Ui.HStack(8, Ui.Text("Pack name", "muted", "small"), _exportName));
        layout.Children.Add(_exportCategories);
        var export = Ui.Button("Save pack JSON…", async () => await ExportSelectionAsync(), "accent");
        export.Name = "ExportPackButton";
        layout.Children.Add(export);
        _exportMessage.Name = "ExportPackMessage";
        layout.Children.Add(_exportMessage);
        Content = new ScrollViewer { Content = layout };
        _workspace.ProjectChanged += (_, _) =>
        {
            if (IsEffectivelyVisible) Refresh();
        };
        Refresh();
    }

    public void SelectPack(string id)
    {
        _selectedPackId = id;
        Refresh();
    }

    public void ReviewPackJson(string json)
    {
        _pending = null;
        _review.Children.Clear();
        _install.IsEnabled = false;
        try
        {
            var parsed = JsonModule.parse(json);
            if (parsed.IsError) throw new JsonException(parsed.ErrorValue);
            var validation = PackRules.validateContentPack(parsed.ResultValue);
            if (validation.IsError)
            {
                _message.Text = $"Pack is invalid: {string.Join("; ", validation.ErrorValue)}";
                return;
            }

            ReviewPack(validation.ResultValue);
        }
        catch (JsonException error)
        {
            _message.Text = $"Pack JSON could not be read: {error.Message}";
        }
    }

    /// <summary>Shows a validated pack's manifest, permissions and plugins before installing it.</summary>
    public void ReviewPack(ContentPack pack)
    {
        _pending = null;
        _review.Children.Clear();
        _install.IsEnabled = false;
        if (_workspace.Current is { } project && Mods.IsInstalled(project, pack.Manifest.Id))
        {
            _message.Text = $"{pack.Manifest.Id} is already installed.";
            return;
        }

        _pending = pack;
        var manifest = pack.Manifest;
        _message.Text = $"Review {manifest.Name} before installing.";
        _review.Children.Add(Ui.Text($"{manifest.Name} · {manifest.Version}", "h2"));
        _review.Children.Add(Ui.Wrapped($"{manifest.Description.OrNull() ?? "No description"} · by {manifest.Author.OrNull() ?? "unknown author"}", "muted", "small"));
        _review.Children.Add(Ui.Wrapped($"Engine: {manifest.EngineCompatibility} · ID: {manifest.Id}", "muted", "small"));
        _review.Children.Add(Ui.Wrapped($"Permissions: content injection {(manifest.Permissions.ContentInject ? "requested" : "off")}; UI panels {(manifest.Permissions.UiPanels ? "requested" : "off")}; hooks {(manifest.Permissions.Hooks.Length == 0 ? "none" : string.Join(", ", manifest.Permissions.Hooks))}", "small"));
        if (manifest.Dependencies.Length > 0)
            _review.Children.Add(Ui.Wrapped($"Dependencies: {string.Join(", ", manifest.Dependencies.Select(dep => $"{dep.PackId} {dep.Version.OrNull() ?? "*"}"))}", "muted", "small"));
        if (manifest.Overrides.Length > 0)
            _review.Children.Add(Ui.Wrapped($"Overrides: {string.Join(", ", manifest.Overrides)}", "muted", "small"));
        var content = pack.Content;
        _review.Children.Add(Ui.Wrapped($"Content: {content.Items.Length} items, {content.Npcs.Length} NPCs, {content.Scenes.Length} scenes, {content.Recipes.Length} recipes, {content.Quests.Length} quests, {pack.Plugins.Length} plugins", "small"));
        foreach (var plugin in pack.Plugins)
        {
            _review.Children.Add(Ui.Text($"Plugin: {plugin.Name.OrNull() ?? plugin.Id} · hooks {string.Join(", ", plugin.Hooks)}", "section"));
            _review.Children.Add(new TextBox { Text = plugin.Source, IsReadOnly = true, AcceptsReturn = true, MinHeight = 80, MaxHeight = 180, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }
        _install.IsEnabled = true;
    }

    private async Task ChoosePackAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install content pack",
            AllowMultiple = false,
            FileTypeFilter = [PackFiles, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            ReviewPackJson(await reader.ReadToEndAsync());
        }
        catch (IOException error)
        {
            _message.Text = $"Could not open pack: {error.Message}";
        }
    }

    private void RefreshRegistry(GameProject project)
    {
        _registry.Children.Clear();
        foreach (var entry in Mods.Registry)
        {
            var installed = Mods.IsInstalled(project, entry.Id);
            var install = Ui.Button(installed ? "Installed" : "Install", () => ReviewPack(entry.Pack), "tool", "small");
            install.Name = $"RegistryInstall_{entry.Id}";
            install.IsEnabled = !installed;
            var text = Ui.VStack(2, Ui.Text($"{entry.Name} · by {entry.Author}", "h3"), Ui.Wrapped(entry.Description, "muted", "small"));
            _registry.Children.Add(Ui.Row(text, install));
        }
    }

    private void RefreshExport(GameProject project)
    {
        _exportCategories.Children.Clear();
        foreach (var category in Mods.ExportCategories(project))
        {
            var key = category.Key;
            var count = category.Entries.Length;
            var include = new CheckBox
            {
                Name = $"ExportCategory_{key}",
                Content = $"{category.Label} ({count})",
                IsChecked = count > 0 && _exportKeys.Contains(key),
                IsEnabled = count > 0,
            };
            var entries = new StackPanel { Spacing = 2, Margin = new Thickness(24, 0, 0, 0) };
            foreach (var option in category.Entries)
            {
                var id = option.Id;
                var pick = new CheckBox { Name = $"ExportEntry_{key}_{id}", Content = option.Label, IsChecked = !_exportExcluded.Contains((key, id)) };
                pick.Click += (_, _) =>
                {
                    if (pick.IsChecked == true) _exportExcluded.Remove((key, id));
                    else _exportExcluded.Add((key, id));
                };
                entries.Children.Add(pick);
            }

            include.Click += (_, _) =>
            {
                if (include.IsChecked == true) _exportKeys.Add(key);
                else _exportKeys.Remove(key);
            };
            var expander = new Expander { Name = $"ExportEntries_{key}", Header = "Entries", Content = entries, IsEnabled = count > 0 };
            _exportCategories.Children.Add(Ui.HStack(8, include, expander));
        }
    }

    /// <summary>The pack the Export button would save: the ticked types, less unticked entries.</summary>
    public PackExportResult BuildSelectedPack()
    {
        var project = _workspace.Current ?? throw new InvalidOperationException("No project is open.");
        var selection = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var category in Mods.ExportCategories(project))
        {
            if (!_exportKeys.Contains(category.Key)) continue;
            selection[category.Key] = category.Entries.Select(entry => entry.Id).Where(id => !_exportExcluded.Contains((category.Key, id))).ToList();
        }

        return Mods.ExportPack(project, _exportName.Text ?? "", selection);
    }

    private async Task ExportSelectionAsync()
    {
        if (_workspace.Current is null) return;
        var result = BuildSelectedPack();
        if (!result.Ok)
        {
            _exportMessage.Text = $"Could not export: {string.Join("; ", result.Errors)}";
            return;
        }

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            _exportMessage.Text = "Saving files is not available here.";
            return;
        }

        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save content pack",
                SuggestedFileName = result.FileName,
                DefaultExtension = "json",
                FileTypeChoices = [PackFiles],
                ShowOverwritePrompt = true,
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(result.Text);
            _exportMessage.Text = $"Saved {file.Name}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _exportMessage.Text = $"Could not save the pack: {error.Message}";
        }
    }

    private void InstallReviewed()
    {
        if (_pending is not { } pack) return;
        if (_workspace.Apply(Edits.InstallPack(pack)))
        {
            _pending = null;
            _review.Children.Clear();
            _install.IsEnabled = false;
            _message.Text = $"Installed {pack.Manifest.Name}.";
        }
    }

    public void Refresh()
    {
        _installed.Children.Clear();
        if (_workspace.Current is not { } project) return;
        RefreshRegistry(project);
        RefreshExport(project);
        if (project.ContentPacks.Length == 0)
        {
            _installed.Children.Add(Ui.Wrapped("No packs installed.", "muted", "small"));
            return;
        }

        for (var i = 0; i < project.ContentPacks.Length; i++)
        {
            var install = project.ContentPacks[i];
            var manifest = install.Pack.Manifest;
            var id = manifest.Id;
            var header = Ui.Text($"{manifest.Name} · {manifest.Version}{(id == _selectedPackId ? "  ←" : "")}", "h3");
            var details = Ui.Wrapped($"{id} · {install.Pack.Plugins.Length} plugins · hooks: {(manifest.Permissions.Hooks.Length == 0 ? "none" : string.Join(", ", manifest.Permissions.Hooks))}", "muted", "small");
            var enabled = new CheckBox { Name = $"PackEnabled_{id}", Content = "Enabled", IsChecked = install.Enabled };
            enabled.Click += (_, _) => _workspace.Apply(Edits.SetPackEnabled(id, enabled.IsChecked == true));
            var up = Ui.Button("↑", () => _workspace.Apply(Edits.MovePack(_workspace.Current!, id, -1)), "tool", "small");
            up.Name = $"MovePackUp_{id}";
            up.IsEnabled = i > 0;
            var down = Ui.Button("↓", () => _workspace.Apply(Edits.MovePack(_workspace.Current!, id, 1)), "tool", "small");
            down.Name = $"MovePackDown_{id}";
            down.IsEnabled = i < project.ContentPacks.Length - 1;
            var import = Ui.Button("Import into project", () => _workspace.Apply(Edits.ImportPack(id)), "tool", "small");
            import.Name = $"ImportPack_{id}";
            var remove = Ui.Button("Remove", () => _workspace.Apply(Edits.RemovePack(id)), "tool", "small");
            remove.Name = $"RemovePack_{id}";
            var row = Ui.VStack(6, header, details, Ui.HStack(8, enabled, up, down, import, remove));
            _installed.Children.Add(new Border { Child = row }.WithClasses("row"));
        }
    }
}
