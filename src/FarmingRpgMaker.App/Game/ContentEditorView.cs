using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Native content workspace. <see cref="ContentForm"/> builds the fields: nested records, lists,
/// reference pickers and the condition/outcome editors, each with an "Edit as JSON" box. One save
/// is one F# upsert, so it is one undo step. All saves and removals go through the F# document.
/// The lists show each entry's art (drawn by the Rust renderer) and a line from F#
/// <see cref="ContentReadouts"/>; crops and node types also list the built-ins, read-only until
/// customized. Recipe and crop profits and the crop and node type summary cards follow the
/// fields while they are edited.
/// </summary>
public sealed class ContentEditorView : UserControl, IRetirable
{
    private const double ThumbnailSize = 28;

    private sealed record Category(
        string Name,
        Type EntityType,
        Func<GameProject, IEnumerable<object>> Entries,
        Func<GameProject, object?> Create,
        Func<object, Edit> Upsert,
        Func<string, Edit> Remove,
        Func<GameProject, object, object>? Duplicate)
    {
        /// <summary>The entry's art, shown as its list thumbnail.</summary>
        public Func<object, VisualRef?>? Art { get; init; }

        /// <summary>The muted line under an entry in the list (the flag: a listed built-in).</summary>
        public Func<GameProject, object, bool, string>? Note { get; init; }

        /// <summary>The summary card above the form.</summary>
        public SummaryCard? Summary { get; init; }

        /// <summary>The profit line under the form.</summary>
        public ProfitLine? Profit { get; init; }

        /// <summary>Built-in entries listed read-only after the project's own.</summary>
        public BuiltinContent? Builtins { get; init; }
    }

    private sealed record SummaryCard(string Name, Func<GameProject, object, IReadOnlyList<ReadoutLine>> Lines);

    private sealed record ProfitLine(string Name, Func<GameProject, object, string> Text);

    /// <summary>
    /// Built-ins a category lists beside the project's own entries (web CropEditor's default
    /// crops, NodeTypeEditor's built-in types). F# says which remain (a project entry with the
    /// same id replaces one) and how one becomes a project entry. Deleting that entry (the usual
    /// F# remove) brings the built-in back with its uses intact.
    /// </summary>
    private sealed record BuiltinContent(
        string Kind,
        Func<GameProject, IEnumerable<object>> Entries,
        Func<string, bool> IsBuiltinId,
        Func<object, Edit> Customize);

    private static Category Of<T>(string name, Func<GameProject, IEnumerable<T>> entries,
        Func<GameProject, T?> create, Func<T, Edit> upsert, Func<string, Edit> remove,
        Func<GameProject, T, T>? duplicate = null) where T : class =>
        new(name, typeof(T), project => entries(project).Cast<object>(), project => create(project),
            entity => upsert((T)entity), remove,
            duplicate is null ? null : (project, entity) => duplicate(project, (T)entity));

    private static readonly Category[] Categories =
    [
        Of("NPCs", p => p.Npcs, p => Defaults.NewNpc(p, "New NPC"), Edits.UpsertNpc, Edits.RemoveNpc) with
        {
            Art = entity => ((Npc)entity).Visual.OrNull(),
        },
        Of("Dialogue", p => ProjectContent.Dialogues(p), p => p.Npcs.Length > 0 ? Defaults.NewDialogue(p, p.Npcs[0].Id) : null, Edits.UpsertDialogue, Edits.RemoveDialogue),
        Of("Items", p => p.Items, Defaults.NewItem, Edits.UpsertItem, Edits.RemoveItem) with
        {
            Art = entity => ((Item)entity).Visual.OrNull(),
        },
        Of("Crops", p => p.CustomCrops.OrEmpty(), Defaults.NewCrop, Edits.UpsertCrop, Edits.RemoveCrop, Defaults.DuplicateCrop) with
        {
            Art = entity => (entity as CustomCropDefinition)?.Visual.OrNull() ?? (entity as CropDefinition)?.Visual.OrNull(),
            Note = (_, entity, _) => entity switch
            {
                CustomCropDefinition crop => ContentReadouts.CropNote(crop),
                CropDefinition crop => ContentReadouts.BuiltinCropNote(crop),
                _ => "",
            },
            Summary = new SummaryCard("CropSummary", (project, entity) => entity switch
            {
                CustomCropDefinition crop => ContentReadouts.CropSummary(project, crop),
                CropDefinition crop => ContentReadouts.CropSummary(project, crop),
                _ => [],
            }),
            Profit = new ProfitLine("CropProfit", (_, entity) => ContentReadouts.CropProfitText((CustomCropDefinition)entity)),
            Builtins = new BuiltinContent("crop", ContentReadouts.BuiltinCrops, ContentReadouts.IsBuiltinCrop,
                entity => Edits.UpsertCrop(ContentReadouts.CustomizeCrop((CropDefinition)entity))),
        },
        Of("Quests", p => p.Quests, Defaults.NewQuest, Edits.UpsertQuest, Edits.RemoveQuest),
        Of("Events", p => p.Events, Defaults.NewEvent, Edits.UpsertEvent, Edits.RemoveEvent),
        Of("Shops", p => p.Shops, Defaults.NewShop, Edits.UpsertShop, Edits.RemoveShop),
        Of("Recipes", p => p.Recipes, Defaults.NewRecipe, Edits.UpsertRecipe, Edits.RemoveRecipe) with
        {
            Note = (project, entity, _) => ContentReadouts.RecipeNote(project, (RecipeDefinition)entity),
            Profit = new ProfitLine("RecipeProfit", (project, entity) => ContentReadouts.RecipeSummary(project, (RecipeDefinition)entity)),
        },
        Of("Node types", p => p.NodeTypes, Defaults.NewNodeType, Edits.UpsertNodeType, Edits.RemoveNodeType) with
        {
            Art = entity => ((NodeTypeDefinition)entity).Visual.OrNull(),
            Note = (_, entity, builtin) => ContentReadouts.NodeTypeListNote((NodeTypeDefinition)entity, builtin),
            Summary = new SummaryCard("NodeTypeSummary", (project, entity) => ContentReadouts.NodeTypeSummary(project, (NodeTypeDefinition)entity)),
            Builtins = new BuiltinContent("node type", ContentReadouts.BuiltinNodeTypes, ContentReadouts.IsBuiltinNodeType,
                entity => Edits.UpsertNodeType((NodeTypeDefinition)entity)),
        },
        Of("Machine types", p => p.MachineTypes, Defaults.NewMachineType, Edits.UpsertMachineType, Edits.RemoveMachineType) with
        {
            Art = entity => ((MachineTypeDefinition)entity).Visual.OrNull(),
        },
        Of("Animal species", p => p.AnimalSpecies, Defaults.NewAnimalSpecies, Edits.UpsertAnimalSpecies, Edits.RemoveAnimalSpecies) with
        {
            Art = entity => ((AnimalSpeciesDefinition)entity).Visual.OrNull(),
        },
        Of("Fish tables", p => p.FishTables, Defaults.NewFishTable, Edits.UpsertFishTable, Edits.RemoveFishTable),
        Of("Actions", p => p.Actions, Defaults.NewAction, Edits.UpsertAction, Edits.RemoveAction, Defaults.DuplicateAction),
        Of("Minigames", p => p.Minigames, Defaults.NewMinigame, Edits.UpsertMinigame, Edits.RemoveMinigame, Defaults.DuplicateMinigame),
    ];

    /// <summary>The entry list's width until the creator drags the splitter.</summary>
    public const double DefaultListWidth = 230;

    private readonly ProjectWorkspace _workspace;
    private readonly ComboBox _category = new() { Name = "ContentCategory", MinWidth = 175 };
    private readonly TextBox _filter = new() { Name = "ContentFilter", Watermark = "Filter by name or id" };
    private readonly DraftBar _draftBar;
    private readonly ListBox _entities = new() { Name = "ContentEntities", MinHeight = 250 };
    private readonly StackPanel _form = new() { Name = "ContentFields", Spacing = 10 };
    private readonly Border _summary = new() { Name = "ContentSummary", IsVisible = false };
    private readonly StackPanel _readouts = new() { Name = "ContentReadouts", Spacing = 6 };
    private readonly TextBlock _message = Ui.Wrapped("Select an entry to edit it.", "muted", "small");
    private readonly Button _add;
    private readonly Button _delete;
    private readonly Button _duplicate;
    private readonly Button _addToInventory;
    private readonly Button _save;
    private readonly Button _revert;
    private readonly Button _customize;
    private readonly VisualPreview _art = new();
    private readonly List<(Border Holder, VisualRef Visual)> _thumbnails = [];
    private Category _selectedCategory = Categories[0];
    private string? _selectedId;
    private bool _refreshing;
    private bool _attached;
    private bool _readoutsQueued;
    private object? _editing;
    /// <summary>The category and id the form on show was built for.</summary>
    private (Category Category, string Id)? _formFor;
    private object? _builtin;
    private ContentForm? _contentForm;
    private TextBlock? _profit;

    private readonly Action<string, string, Action<int, int>>? _pickOnMap;

    /// <param name="workspace">The open project.</param>
    /// <param name="pickOnMap">"Pick on map" for the forms' tile coordinates (scene id, prompt, what to do with the tile); null hides it.</param>
    public ContentEditorView(ProjectWorkspace workspace, Action<string, string, Action<int, int>>? pickOnMap = null)
    {
        _workspace = workspace;
        _pickOnMap = pickOnMap;
        Name = "ContentEditorView";
        _message.Name = "ContentMessage";
        Ui.Label((_category, "Content type"), (_entities, "Entries"));
        foreach (var category in Categories)
        {
            _category.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category });
        }

        Ui.Label((_filter, "Filter entries"));
        _draftBar = new DraftBar("Content", SaveDraft, () => BuildForm());
        _category.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _category.SelectedItem is not ComboBoxItem { Tag: Category category } || ReferenceEquals(category, _selectedCategory)) return;
            // Unsaved fields are not dropped by picking another type (#87): ask first.
            ShowCategory(_selectedCategory);
            Navigate(() =>
            {
                _selectedCategory = category;
                _selectedId = null;
                ShowCategory(category);
                Refresh();
            });
        };
        _entities.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            var id = (_entities.SelectedItem as ListBoxItem)?.Tag as string;
            if (id == _selectedId) return;
            ShowSelection();
            Navigate(() =>
            {
                _selectedId = id;
                ShowSelection();
                BuildForm();
            });
        };
        _filter.TextChanged += (_, _) => RefreshList(_workspace.Current);
        // Readouts follow the fields: any edit inside the form (typing, a picker, a checkbox)
        // bubbles up here. The refresh is posted, so it runs after a rebuild the edit starts.
        _form.AddHandler(TextBox.TextChangedEvent, (_, _) => QueueReadouts(), handledEventsToo: true);
        _form.AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, _) => QueueReadouts(), handledEventsToo: true);
        _form.AddHandler(ToggleButton.IsCheckedChangedEvent, (_, _) => QueueReadouts(), handledEventsToo: true);
        _add = Ui.Button("New", Add, "accent");
        _add.Name = "AddContentButton";
        _delete = Ui.Button("Delete", Delete, "tool");
        _delete.Name = "DeleteContentButton";
        _duplicate = Ui.Button("Duplicate", Duplicate, "tool");
        _duplicate.Name = "DuplicateContentButton";
        _addToInventory = Ui.Button("Add to inventory", AddToInventory, "tool");
        _addToInventory.Name = "AddToInventoryButton";
        ToolTip.SetTip(_addToInventory, "Give the player one of this item at the start of the game");
        _save = Ui.Button("Save changes", Save, "accent");
        _save.Name = "SaveContentButton";
        _revert = Ui.Button("Revert fields", BuildForm, "tool");
        _revert.Name = "RevertContentButton";
        _customize = Ui.Button("Customize", Customize, "accent");
        _customize.Name = "CustomizeBuiltinButton";
        _customize.IsVisible = false;

        var listSide = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 16, 0) };
        listSide.Children.Add(Ui.Text("CONTENT", "section"));
        listSide.Children.Add(_category);
        listSide.Children.Add(_filter);
        listSide.Children.Add(_entities);
        listSide.Children.Add(Ui.HStack(8, _add, _delete));
        listSide.Children.Add(Ui.HStack(8, _duplicate, _addToInventory));
        listSide.Children.Add(Ui.Wrapped("Select a type, then edit its fields. Every nested field also has an Edit as JSON box.", "muted", "small"));
        var editor = new StackPanel { Spacing = 12 };
        editor.Children.Add(Ui.Text("DETAILS", "section"));
        editor.Children.Add(_draftBar);
        editor.Children.Add(_message);
        editor.Children.Add(_summary);
        editor.Children.Add(_form);
        editor.Children.Add(_readouts);
        editor.Children.Add(Ui.HStack(8, _customize, _save, _revert));
        // The list is as wide as the creator dragged it last (#56); the splitter remembers it.
        var listWidth = Math.Clamp(_workspace.Settings.Load().ContentListWidth ?? DefaultListWidth, 160, 640);
        var layout = new Grid { Name = "ContentLayout", Margin = new Thickness(20) };
        layout.ColumnDefinitions.Add(new ColumnDefinition(listWidth, GridUnitType.Pixel) { MinWidth = 160, MaxWidth = 640 });
        layout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        layout.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        layout.Children.Add(listSide);
        listSide.Margin = new Thickness(0, 0, 8, 0);
        var splitter = new GridSplitter { Name = "ContentSplitter", Width = 6, ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(splitter, "Resize the entry list");
        splitter.DragCompleted += (_, _) =>
        {
            var width = layout.ColumnDefinitions[0].ActualWidth;
            if (width > 0) _workspace.Settings.TryUpdate(settings => settings with { ContentListWidth = Math.Round(width) });
        };
        Grid.SetColumn(splitter, 1);
        layout.Children.Add(splitter);
        var formScroll = new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(formScroll, 2);
        layout.Children.Add(formScroll);
        Content = layout;
        _workspace.ProjectChanged += OnProjectChanged;
        _category.SelectedIndex = 0;
        Refresh();
    }

    /// <summary>Stops following the project (the editor that built this view was replaced).</summary>
    public void Retire() => _workspace.ProjectChanged -= OnProjectChanged;

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        // Another project: its entries are not the form's (the fields were saved, if they
        // could be, before the switch; see EditModeView.SaveDrafts).
        if (e.Kind == ProjectChangeKind.Opened)
        {
            _draftBar.Hide();
            _formFor = null;
            _selectedId = null;
        }

        if (IsEffectivelyVisible) Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        DrawThumbnails();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _art.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    public void SelectCategory(string name)
    {
        _category.SelectedItem = _category.Items.OfType<ComboBoxItem>().First(item => (string)item.Content! == name);
    }

    /// <summary>Opens <paramref name="id"/> of <paramref name="category"/> (Problems, search), asking first about unsaved fields.</summary>
    public void SelectEntry(string category, string id)
    {
        var target = Categories.First(c => c.Name == category);
        Navigate(() =>
        {
            _selectedCategory = target;
            _selectedId = id;
            _filter.Text = "";
            ShowCategory(target);
            Refresh();
        });
    }

    /// <summary>True while the form holds edits that are not saved (#87).</summary>
    public bool HasUnsavedChanges => _contentForm?.IsDirty == true;

    /// <summary>The selected entry's id (null when none).</summary>
    public string? SelectedId => _selectedId;

    /// <summary>
    /// Saves the form's unsaved fields, if any (switching projects, closing the editor). False
    /// when they could not be saved; the message says why.
    /// </summary>
    public bool SaveDraft()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        Save();
        return !HasUnsavedChanges;
    }

    /// <summary>
    /// Every entry of every type whose name or id contains <paramref name="query"/> (ignoring
    /// case), names that start with it first: the quick-open search (#56).
    /// </summary>
    public static IReadOnlyList<(string Category, string Id, string Label)> Search(GameProject project, string query, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(project);
        var text = (query ?? "").Trim();
        if (text.Length == 0) return [];
        var found = new List<(string Category, string Id, string Label, int Rank)>();
        foreach (var category in Categories)
        {
            foreach (var entity in category.Entries(project).Concat(category.Builtins?.Entries(project) ?? []))
            {
                var id = IdOf(entity);
                var name = entity.GetType().GetProperty("Name")?.GetValue(entity) as string ?? "";
                var rank = Rank(name, text) is { } byName ? byName : Rank(id, text) is { } byId ? byId + 2 : (int?)null;
                if (rank is { } r && !found.Any(f => f.Category == category.Name && f.Id == id)) found.Add((category.Name, id, LabelOf(entity), r));
            }
        }

        return found.OrderBy(f => f.Rank).ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase).Take(limit).Select(f => (f.Category, f.Id, f.Label)).ToList();
    }

    /// <summary>0 when <paramref name="value"/> starts with <paramref name="query"/>, 1 when it contains it, else null.</summary>
    internal static int? Rank(string value, string query) =>
        value.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
        : value.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1
        : null;

    /// <summary>Runs <paramref name="next"/> now, or once the creator saved or discarded the form's unsaved fields.</summary>
    private void Navigate(Action next)
    {
        if (!HasUnsavedChanges)
        {
            _draftBar.Hide();
            next();
            return;
        }

        _draftBar.Ask(_editing is null ? "this entry" : LabelOf(_editing), next);
    }

    /// <summary>Shows <paramref name="category"/> in the type picker without reacting to it.</summary>
    private void ShowCategory(Category category)
    {
        _refreshing = true;
        try
        {
            _category.SelectedItem = _category.Items.OfType<ComboBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, category));
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Shows the selected entry in the list without reacting to it.</summary>
    private void ShowSelection()
    {
        _refreshing = true;
        try
        {
            _entities.SelectedItem = _entities.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, _selectedId));
        }
        finally
        {
            _refreshing = false;
        }
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
        RefreshList(project);
        // A form with unsaved fields stays as typed when the tab is shown again or the project
        // changes elsewhere (undo, a map edit), as long as its entry is still there (#87).
        if (_formFor is { } shown && ReferenceEquals(shown.Category, _selectedCategory) && shown.Id == _selectedId && project is not null
            && _selectedCategory.Entries(project).FirstOrDefault(entity => IdOf(entity) == _selectedId) is { } stored && HasUnsavedChanges)
        {
            if (!Equals(stored, _editing))
            {
                _message.Text = $"{LabelOf(stored)} changed elsewhere while you edited it. Save changes keeps what you typed; Revert fields shows the saved entry.";
            }

            return;
        }

        BuildForm();
    }

    /// <summary>The entry list of the selected type, filtered by the filter box (the selected entry always shows).</summary>
    private void RefreshList(GameProject? project)
    {
        _refreshing = true;
        try
        {
            _entities.SelectedItem = null;
            _entities.Items.Clear();
            _thumbnails.Clear();
            var filter = (_filter.Text ?? "").Trim();
            bool Shown(object entity) => filter.Length == 0 || IdOf(entity) == _selectedId
                || Rank(LabelOf(entity), filter) is not null;
            if (project is not null)
            {
                foreach (var entity in _selectedCategory.Entries(project).Where(Shown))
                {
                    _entities.Items.Add(ListRow(project, entity, builtin: false));
                }

                foreach (var entity in (_selectedCategory.Builtins?.Entries(project) ?? []).Where(Shown))
                {
                    _entities.Items.Add(ListRow(project, entity, builtin: true));
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

        DrawThumbnails();
    }

    /// <summary>
    /// One list entry: its art (or an empty slot, so names line up), "Name · id" and the muted
    /// line. <c>Tag</c> is the id; screen readers read the "Name · id" label and the line as help.
    /// </summary>
    private ListBoxItem ListRow(GameProject project, object entity, bool builtin)
    {
        var id = IdOf(entity);
        var label = LabelOf(entity);
        var note = _selectedCategory.Note?.Invoke(project, entity, builtin);
        var text = Ui.VStack(1, Trimmed(Ui.Text(label)));
        text.VerticalAlignment = VerticalAlignment.Center;
        if (!string.IsNullOrEmpty(note)) text.Children.Add(Trimmed(Ui.Text(note, "muted", "small")));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (_selectedCategory.Art is { } art)
        {
            var holder = new Border
            {
                Name = $"ContentThumb_{id}",
                Width = ThumbnailSize,
                Height = ThumbnailSize,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (art(entity) is { } visual) _thumbnails.Add((holder, visual));
            row.Children.Add(holder);
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var item = new ListBoxItem { Content = row, Tag = id };
        AutomationProperties.SetName(item, label);
        if (!string.IsNullOrEmpty(note)) AutomationProperties.SetHelpText(item, note);
        // The narrow list trims long lines; the tooltip has them whole.
        ToolTip.SetTip(item, string.IsNullOrEmpty(note) ? label : $"{label}\n{note}");
        return item;
    }

    private static TextBlock Trimmed(TextBlock block)
    {
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }

    /// <summary>Draws the listed entries' art with the Rust renderer, once the view is on screen.</summary>
    private void DrawThumbnails()
    {
        if (!_attached || _workspace.Current is not { } project) return;
        foreach (var (holder, visual) in _thumbnails)
        {
            holder.Child ??= _art.Render(project, visual, 0, ThumbnailSize);
        }
    }

    private void BuildForm()
    {
        _draftBar.Hide();
        _formFor = null;
        _form.Children.Clear();
        _readouts.Children.Clear();
        _readouts.IsVisible = false;
        _summary.Child = null;
        _summary.IsVisible = false;
        _contentForm?.ReleaseImages();
        _contentForm = null;
        _profit = null;
        _editing = null;
        _builtin = null;
        var project = _workspace.Current;
        if (_selectedId is { } id && project is not null)
        {
            _editing = _selectedCategory.Entries(project).FirstOrDefault(entity => IdOf(entity) == id);
            if (_editing is null) _builtin = _selectedCategory.Builtins?.Entries(project).FirstOrDefault(entity => IdOf(entity) == id);
        }

        _save.IsEnabled = _editing is not null;
        _revert.IsEnabled = _editing is not null;
        _delete.IsEnabled = _editing is not null;
        _duplicate.IsVisible = _selectedCategory.Duplicate is not null;
        _duplicate.IsEnabled = _editing is not null;
        _addToInventory.IsVisible = _selectedCategory.EntityType == typeof(Item);
        _addToInventory.IsEnabled = _editing is not null;
        _customize.IsVisible = _builtin is not null;
        _form.IsVisible = _editing is not null;
        var builtins = _selectedCategory.Builtins;
        ToolTip.SetTip(_delete, _editing is not null && builtins is not null && builtins.IsBuiltinId(_selectedId!)
            ? $"Remove your changes and use the built-in {builtins.Kind} again"
            : null);
        if (project is null)
        {
            _message.Text = "Select an entry to edit it.";
            return;
        }

        if (_builtin is not null && builtins is not null)
        {
            _message.Text = $"This is a built-in {builtins.Kind}. Customize it to change it.";
            ToolTip.SetTip(_customize, $"Copy this built-in {builtins.Kind} into the project so you can edit it");
            ShowReadouts(project, _builtin);
            return;
        }

        if (_editing is null)
        {
            _message.Text = "Select an entry to edit it.";
            return;
        }

        _message.Text = $"Editing {_selectedCategory.Name.ToLowerInvariant()} · {_selectedId}";
        _contentForm = new ContentForm(project, _editing, _selectedCategory.EntityType, _form, message => _message.Text = message, _pickOnMap);
        _formFor = (_selectedCategory, _selectedId!);
        if (_selectedCategory.Profit is { } profit)
        {
            var (box, text) = ContentReadoutCard.Line(profit.Name);
            _profit = text;
            _readouts.Children.Add(box);
            _readouts.IsVisible = true;
        }

        ShowReadouts(project, _editing);
    }

    /// <summary>The summary card and profit line for <paramref name="entity"/> (saved, built-in or as edited).</summary>
    private void ShowReadouts(GameProject project, object entity)
    {
        if (_selectedCategory.Summary is { } summary)
        {
            _summary.Child = ContentReadoutCard.Card(summary.Name, summary.Lines(project, entity));
            _summary.IsVisible = true;
        }

        if (_profit is not null && _selectedCategory.Profit is { } profit) _profit.Text = profit.Text(project, entity);
    }

    /// <summary>
    /// Recomputes the readouts from the fields after the current input settles. While a field
    /// holds something invalid the last readout stays.
    /// </summary>
    private void QueueReadouts()
    {
        if (_readoutsQueued || _contentForm is null || (_selectedCategory.Summary is null && _selectedCategory.Profit is null)) return;
        _readoutsQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _readoutsQueued = false;
            if (_contentForm?.Preview() is { } edited && _workspace.Current is { } project) ShowReadouts(project, edited);
        });
    }

    private void Add() => Navigate(AddNow);

    private void AddNow()
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

    /// <summary>The selected built-in becomes a project entry with the same id (it replaces the built-in).</summary>
    private void Customize() => Navigate(CustomizeNow);

    private void CustomizeNow()
    {
        if (_builtin is not { } builtin || _selectedCategory.Builtins is not { } builtins) return;
        _selectedId = IdOf(builtin);
        if (_workspace.Apply(builtins.Customize(builtin)))
            _message.Text = $"Customized {LabelOf(builtin)}. Delete it to use the built-in {builtins.Kind} again.";
    }

    private void Duplicate() => Navigate(DuplicateNow);

    private void DuplicateNow()
    {
        if (_editing is null || _selectedCategory.Duplicate is not { } duplicate || _workspace.Current is not { } project) return;
        var copy = duplicate(project, _editing);
        _selectedId = IdOf(copy);
        if (_workspace.Apply(_selectedCategory.Upsert(copy))) _message.Text = $"Duplicated as {LabelOf(copy)}.";
    }

    private void AddToInventory()
    {
        if (_editing is not Item item || _workspace.Current is not { } project) return;
        var message = ContentActions.AddToInventoryMessage(project, item.Id);
        _workspace.Apply(Edits.AddToInventory(item.Id));
        _message.Text = message;
    }

    /// <summary>Removes the entry; one that replaces a built-in goes back to the built-in (still selected).</summary>
    private void Delete()
    {
        if (_selectedId is not { } id) return;
        // A replacement of a built-in gives way to it: the built-in stays selected.
        if (_selectedCategory.Builtins is { } builtins && builtins.IsBuiltinId(id))
        {
            if (_workspace.Apply(_selectedCategory.Remove(id))) _message.Text = $"Removed your changes: {id} is the built-in {builtins.Kind} again.";
            return;
        }

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
            var saved = _workspace.Apply(_selectedCategory.Upsert(updated));
            // The fields now match the project: show the saved entry.
            BuildForm();
            if (!saved) _message.Text = "No changes were made.";
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException)
        {
            _message.Text = $"Could not save: {error.Message}";
        }
    }
}
