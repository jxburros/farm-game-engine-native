using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Native content workspace. <see cref="ContentForm"/> builds the fields: nested records, lists,
/// reference pickers and the condition/outcome editors, each with an "Edit as JSON" box. One save
/// is one F# upsert, so it is one undo step. All saves and removals go through the F# document.
/// </summary>
public sealed class ContentEditorView : UserControl
{
    private sealed record Category(
        string Name,
        Type EntityType,
        Func<GameProject, IEnumerable<object>> Entries,
        Func<GameProject, object?> Create,
        Func<object, Edit> Upsert,
        Func<string, Edit> Remove);

    private static Category Of<T>(string name, Func<GameProject, IEnumerable<T>> entries,
        Func<GameProject, T?> create, Func<T, Edit> upsert, Func<string, Edit> remove) where T : class =>
        new(name, typeof(T), project => entries(project).Cast<object>(), project => create(project),
            entity => upsert((T)entity), remove);

    private static readonly Category[] Categories =
    [
        Of("NPCs", p => p.Npcs, p => Defaults.NewNpc(p, "New NPC"), Edits.UpsertNpc, Edits.RemoveNpc),
        Of("Dialogue", p => p.Dialogues, p => p.Npcs.Length > 0 ? Defaults.NewDialogue(p, p.Npcs[0].Id) : null, Edits.UpsertDialogue, Edits.RemoveDialogue),
        Of("Items", p => p.Items, Defaults.NewItem, Edits.UpsertItem, Edits.RemoveItem),
        Of("Crops", p => p.CustomCrops.OrEmpty(), Defaults.NewCrop, Edits.UpsertCrop, Edits.RemoveCrop),
        Of("Quests", p => p.Quests, Defaults.NewQuest, Edits.UpsertQuest, Edits.RemoveQuest),
        Of("Events", p => p.Events, Defaults.NewEvent, Edits.UpsertEvent, Edits.RemoveEvent),
        Of("Shops", p => p.Shops, Defaults.NewShop, Edits.UpsertShop, Edits.RemoveShop),
        Of("Recipes", p => p.Recipes, Defaults.NewRecipe, Edits.UpsertRecipe, Edits.RemoveRecipe),
        Of("Node types", p => p.NodeTypes, Defaults.NewNodeType, Edits.UpsertNodeType, Edits.RemoveNodeType),
        Of("Machine types", p => p.MachineTypes, Defaults.NewMachineType, Edits.UpsertMachineType, Edits.RemoveMachineType),
        Of("Animal species", p => p.AnimalSpecies, Defaults.NewAnimalSpecies, Edits.UpsertAnimalSpecies, Edits.RemoveAnimalSpecies),
        Of("Fish tables", p => p.FishTables, Defaults.NewFishTable, Edits.UpsertFishTable, Edits.RemoveFishTable),
        Of("Actions", p => p.Actions, Defaults.NewAction, Edits.UpsertAction, Edits.RemoveAction),
        Of("Minigames", p => p.Minigames, Defaults.NewMinigame, Edits.UpsertMinigame, Edits.RemoveMinigame),
    ];

    private readonly ProjectWorkspace _workspace;
    private readonly ComboBox _category = new() { Name = "ContentCategory", MinWidth = 175 };
    private readonly ListBox _entities = new() { Name = "ContentEntities", MinHeight = 250 };
    private readonly StackPanel _form = new() { Name = "ContentFields", Spacing = 10 };
    private readonly TextBlock _message = Ui.Wrapped("Select an entry to edit it.", "muted", "small");
    private readonly Button _add;
    private readonly Button _delete;
    private readonly Button _save;
    private readonly Button _revert;
    private Category _selectedCategory = Categories[0];
    private string? _selectedId;
    private bool _refreshing;
    private object? _editing;
    private ContentForm? _contentForm;

    public ContentEditorView(ProjectWorkspace workspace)
    {
        _workspace = workspace;
        Name = "ContentEditorView";
        _message.Name = "ContentMessage";
        foreach (var category in Categories)
        {
            _category.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category });
        }

        _category.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _category.SelectedItem is not ComboBoxItem { Tag: Category category }) return;
            _selectedCategory = category;
            _selectedId = null;
            Refresh();
        };
        _entities.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _selectedId = (_entities.SelectedItem as ListBoxItem)?.Tag as string;
            BuildForm();
        };
        _add = Ui.Button("New", Add, "accent");
        _add.Name = "AddContentButton";
        _delete = Ui.Button("Delete", Delete, "tool");
        _delete.Name = "DeleteContentButton";
        _save = Ui.Button("Save changes", Save, "accent");
        _save.Name = "SaveContentButton";
        _revert = Ui.Button("Revert fields", BuildForm, "tool");
        _revert.Name = "RevertContentButton";

        var listSide = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 16, 0) };
        listSide.Children.Add(Ui.Text("CONTENT", "section"));
        listSide.Children.Add(_category);
        listSide.Children.Add(_entities);
        listSide.Children.Add(Ui.HStack(8, _add, _delete));
        listSide.Children.Add(Ui.Wrapped("Select a type, then edit its fields. Every nested field also has an Edit as JSON box.", "muted", "small"));
        var editor = new StackPanel { Spacing = 12 };
        editor.Children.Add(Ui.Text("DETAILS", "section"));
        editor.Children.Add(_message);
        editor.Children.Add(_form);
        editor.Children.Add(Ui.HStack(8, _save, _revert));
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*"), Margin = new Thickness(20) };
        layout.Children.Add(listSide);
        var formScroll = new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(formScroll, 1);
        layout.Children.Add(formScroll);
        Content = layout;
        _workspace.ProjectChanged += (_, _) =>
        {
            if (IsEffectivelyVisible) Refresh();
        };
        _category.SelectedIndex = 0;
        Refresh();
    }

    public void SelectCategory(string name)
    {
        _category.SelectedItem = _category.Items.OfType<ComboBoxItem>().First(item => (string)item.Content! == name);
    }

    public void SelectEntry(string category, string id)
    {
        SelectCategory(category);
        _selectedId = id;
        Refresh();
    }

    private static string IdOf(object entity) => (string)(entity.GetType().GetProperty("Id")?.GetValue(entity) ?? "");

    private static string LabelOf(object entity)
    {
        var name = entity.GetType().GetProperty("Name")?.GetValue(entity) as string;
        return string.IsNullOrWhiteSpace(name) ? IdOf(entity) : $"{name}  ·  {IdOf(entity)}";
    }

    public void Refresh()
    {
        var project = _workspace.Current;
        _refreshing = true;
        try
        {
            _entities.SelectedItem = null;
            _entities.Items.Clear();
            if (project is not null)
            {
                foreach (var entity in _selectedCategory.Entries(project))
                {
                    _entities.Items.Add(new ListBoxItem { Content = LabelOf(entity), Tag = IdOf(entity) });
                }
            }

            _entities.SelectedItem = _entities.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, _selectedId));
            if (_entities.SelectedItem is null) _selectedId = null;
            _add.IsEnabled = project is not null;
            _delete.IsEnabled = _selectedId is not null;
        }
        finally
        {
            _refreshing = false;
        }

        BuildForm();
    }

    private void BuildForm()
    {
        _form.Children.Clear();
        _contentForm = null;
        _editing = _selectedId is { } id && _workspace.Current is { } project
            ? _selectedCategory.Entries(project).FirstOrDefault(entity => IdOf(entity) == id)
            : null;
        _save.IsEnabled = _editing is not null;
        _revert.IsEnabled = _editing is not null;
        _delete.IsEnabled = _editing is not null;
        if (_editing is null || _workspace.Current is not { } current)
        {
            _message.Text = "Select an entry to edit it.";
            return;
        }

        _message.Text = $"Editing {_selectedCategory.Name.ToLowerInvariant()} · {_selectedId}";
        _contentForm = new ContentForm(current, _editing, _selectedCategory.EntityType, _form, message => _message.Text = message);
    }

    private void Add()
    {
        if (_workspace.Current is not { } project) return;
        var created = _selectedCategory.Create(project);
        if (created is null)
        {
            _message.Text = "Create an NPC first so this dialogue has an owner.";
            return;
        }
        _selectedId = IdOf(created);
        if (!_workspace.Apply(_selectedCategory.Upsert(created))) _message.Text = "No changes were made.";
    }

    private void Delete()
    {
        if (_selectedId is not { } id) return;
        _selectedId = null;
        _workspace.Apply(_selectedCategory.Remove(id));
    }

    private void Save()
    {
        if (_editing is null || _contentForm is null) return;
        try
        {
            var updated = _contentForm.Commit();
            if (IdOf(updated) != _selectedId) throw new JsonException("The id cannot be changed here.");
            if (!_workspace.Apply(_selectedCategory.Upsert(updated))) _message.Text = "No changes were made.";
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException)
        {
            _message.Text = $"Could not save: {error.Message}";
        }
    }
}
