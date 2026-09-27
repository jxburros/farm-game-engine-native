using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FarmEngine.Authoring;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Installed pack order and an explicit manifest/permission review before installation.</summary>
public sealed class ModsEditorView : UserControl
{
    private static readonly FilePickerFileType PackFiles = new("Content pack JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };
    private readonly ProjectWorkspace _workspace;
    private readonly StackPanel _installed = new() { Name = "InstalledPacks", Spacing = 8 };
    private readonly StackPanel _review = new() { Name = "PackReview", Spacing = 8 };
    private readonly TextBlock _message = Ui.Wrapped("Choose a content pack JSON file to review it.", "muted", "small");
    private readonly Button _install;
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
            using var document = JsonDocument.Parse(json);
            var validation = PacksSchema.ValidateContentPack(document.RootElement);
            if (!validation.Ok || validation.Pack is null)
            {
                _message.Text = $"Pack is invalid: {string.Join("; ", validation.Errors)}";
                return;
            }

            var pack = validation.Pack;
            if (_workspace.Current?.ContentPacks.Any(installation => installation.Pack.Manifest.Id == pack.Manifest.Id) == true)
            {
                _message.Text = $"{pack.Manifest.Id} is already installed.";
                return;
            }

            _pending = pack;
            var manifest = pack.Manifest;
            _message.Text = $"Review {manifest.Name} before installing.";
            _review.Children.Add(Ui.Text($"{manifest.Name} · {manifest.Version}", "h2"));
            _review.Children.Add(Ui.Wrapped($"{manifest.Description ?? "No description"} · by {manifest.Author ?? "unknown author"}", "muted", "small"));
            _review.Children.Add(Ui.Wrapped($"Engine: {manifest.EngineCompatibility} · ID: {manifest.Id}", "muted", "small"));
            _review.Children.Add(Ui.Wrapped($"Permissions: content injection {(manifest.Permissions.ContentInject ? "requested" : "off")}; UI panels {(manifest.Permissions.UiPanels ? "requested" : "off")}; hooks {(manifest.Permissions.Hooks.Count == 0 ? "none" : string.Join(", ", manifest.Permissions.Hooks))}", "small"));
            if (manifest.Dependencies.Count > 0)
                _review.Children.Add(Ui.Wrapped($"Dependencies: {string.Join(", ", manifest.Dependencies.Select(dep => $"{dep.PackId} {dep.Version ?? "*"}"))}", "muted", "small"));
            if (manifest.Overrides.Count > 0)
                _review.Children.Add(Ui.Wrapped($"Overrides: {string.Join(", ", manifest.Overrides)}", "muted", "small"));
            var content = pack.Content;
            _review.Children.Add(Ui.Wrapped($"Content: {content.Items.Count} items, {content.Npcs.Count} NPCs, {content.Scenes.Count} scenes, {content.Recipes.Count} recipes, {content.Quests.Count} quests, {pack.Plugins.Count} plugins", "small"));
            foreach (var plugin in pack.Plugins)
            {
                _review.Children.Add(Ui.Text($"Plugin: {plugin.Name ?? plugin.Id} · hooks {string.Join(", ", plugin.Hooks)}", "section"));
                _review.Children.Add(new TextBox { Text = plugin.Source, IsReadOnly = true, AcceptsReturn = true, MinHeight = 80, MaxHeight = 180, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            }
            _install.IsEnabled = true;
        }
        catch (JsonException error)
        {
            _message.Text = $"Pack JSON could not be read: {error.Message}";
        }
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
        if (project.ContentPacks.Count == 0)
        {
            _installed.Children.Add(Ui.Wrapped("No packs installed.", "muted", "small"));
            return;
        }

        for (var i = 0; i < project.ContentPacks.Count; i++)
        {
            var install = project.ContentPacks[i];
            var manifest = install.Pack.Manifest;
            var id = manifest.Id;
            var header = Ui.Text($"{manifest.Name} · {manifest.Version}{(id == _selectedPackId ? "  ←" : "")}", "h3");
            var details = Ui.Wrapped($"{id} · {install.Pack.Plugins.Count} plugins · hooks: {(manifest.Permissions.Hooks.Count == 0 ? "none" : string.Join(", ", manifest.Permissions.Hooks))}", "muted", "small");
            var enabled = new CheckBox { Name = $"PackEnabled_{id}", Content = "Enabled", IsChecked = install.Enabled };
            enabled.Click += (_, _) => _workspace.Apply(Edits.SetPackEnabled(id, enabled.IsChecked == true));
            var up = Ui.Button("↑", () => _workspace.Apply(Edits.MovePack(_workspace.Current!, id, -1)), "tool", "small");
            up.Name = $"MovePackUp_{id}";
            up.IsEnabled = i > 0;
            var down = Ui.Button("↓", () => _workspace.Apply(Edits.MovePack(_workspace.Current!, id, 1)), "tool", "small");
            down.Name = $"MovePackDown_{id}";
            down.IsEnabled = i < project.ContentPacks.Count - 1;
            var import = Ui.Button("Import into project", () => _workspace.Apply(Edits.ImportPack(id)), "tool", "small");
            import.Name = $"ImportPack_{id}";
            var remove = Ui.Button("Remove", () => _workspace.Apply(Edits.RemovePack(id)), "tool", "small");
            remove.Name = $"RemovePack_{id}";
            var row = Ui.VStack(6, header, details, Ui.HStack(8, enabled, up, down, import, remove));
            _installed.Children.Add(new Border { Child = row }.WithClasses("row"));
        }
    }
}
