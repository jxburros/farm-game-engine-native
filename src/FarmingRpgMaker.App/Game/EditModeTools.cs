using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

public enum MapTool
{
    Inspect, Brush, Rectangle, Fill, Pick, Select, Paste, Erase, Block, Unblock, Door, PlayerStart,
    PlaceNpc, PlaceNode, PlaceItem, PlaceMachine, PlaceAnimal, Remove,
}

/// <summary>The creator-facing map tools. All mutations go through the F# document.</summary>
public sealed partial class EditModeView
{
    private readonly WrapPanel _tools = new() { Name = "MapTools" };
    private readonly WrapPanel _placeTools = new() { Name = "PlaceTools" };
    private readonly ComboBox _placeChoice = new() { Name = "PlaceChoice", MinWidth = 200, IsEnabled = false, PlaceholderText = "Choose a Place tool" };
    private readonly StackPanel _sceneAnimals = new() { Name = "SceneAnimals", Spacing = 6 };
    private readonly ComboBox _newSceneTile = new() { Name = "NewSceneTile", MinWidth = 110 };
    private readonly ComboBox _layerSelector = new() { Name = "LayerSelector", MinWidth = 140 };
    private readonly TextBox _sceneName = new() { Name = "SceneName", Watermark = "Scene name" };
    private readonly TextBox _sceneWidth = new() { Name = "SceneWidth", Width = 58, Watermark = "Width" };
    private readonly TextBox _sceneHeight = new() { Name = "SceneHeight", Width = 58, Watermark = "Height" };
    private readonly Button _deleteScene = new() { Name = "DeleteSceneButton", Content = "Delete scene" };
    private readonly Button _startScene = new() { Name = "SetStartSceneButton", Content = "Set as start" };
    private readonly Button _copy = new() { Name = "CopySelectionButton", Content = "Copy selection" };
    private readonly Button _paste = new() { Name = "PasteToolButton", Content = "Paste on map" };
    private readonly Button _fillScene = new() { Name = "FillSceneButton", Content = "Fill scene" };
    private readonly ComboBox _doorDestination = new() { Name = "DoorDestination", MinWidth = 140 };
    private readonly TextBox _doorX = new() { Name = "DoorX", Width = 58, Text = "0" };
    private readonly TextBox _doorY = new() { Name = "DoorY", Width = 58, Text = "0" };
    private readonly CheckBox _doorReturn = new() { Name = "DoorReturn", Content = "Return door" };
    private readonly CheckBox _sceneIndoor = new() { Name = "SceneIndoor", Content = "Indoor (no weather)" };
    private bool _showingSceneIndoor;
    private readonly Button _removeDoor = new() { Name = "RemoveDoorButton", Content = "Remove door" };
    private readonly StackPanel _transitionList = new() { Name = "TransitionList", Spacing = 6 };
    private readonly TextBlock _transitionCount = Ui.Text("", "muted", "small");
    private readonly Button _clearTransitions = new() { Name = "ClearTransitionsButton", Content = "Clear all" };
    private readonly TextBlock _doorFrom = Ui.Wrapped("Choose Door, then click a departure tile.", "muted", "small");
    private readonly TextBlock _editorMessage = Ui.Wrapped("", "muted", "small");
    private readonly TextBlock _sceneSizeInfo = Ui.Wrapped("", "muted", "small");
    private TileLayer _selectedLayer = Edits.LayerFor(TileTypes.Grass);
    private MapTool _tool = MapTool.Inspect;
    private (int X, int Y)? _gestureStart;
    private (int X0, int Y0, int X1, int Y1)? _selection;
    private List<List<Tile>>? _copiedTiles;
    private (int X, int Y)? _doorAt;
    private string? _sceneControlsSceneId;
    private string? _shownSceneName;
    private string? _shownSceneWidth;
    private string? _shownSceneHeight;
    /// <summary>The last choice of each Place tool, by placement kind.</summary>
    private readonly Dictionary<string, string> _placeChoices = new(StringComparer.Ordinal);
    private string? _placeKind;
    private bool _updatingPlaceChoice;
    private List<string> _shownTransitions = [];
    private List<string> _shownAnimals = [];

    public MapTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            if (_gestureStart is not null)
            {
                // Another tool drops a rectangle's first corner (marked from the keyboard).
                _gestureStart = null;
                _hover.IsVisible = false;
            }
            foreach (var button in _tools.Children.Concat(_placeTools.Children).OfType<ToggleButton>())
            {
                button.IsChecked = Equals(button.Tag, value);
            }
            if (_workspace.Current is { } project) RefreshPlaceChoice(project);
            _canvas.Cursor = new Avalonia.Input.Cursor(value == MapTool.Inspect ? Avalonia.Input.StandardCursorType.Arrow : Avalonia.Input.StandardCursorType.Hand);
        }
    }

    public TileLayer Layer
    {
        get => _selectedLayer;
        set
        {
            _selectedLayer = value;
            SyncLayerSelector();
        }
    }

    public (int X0, int Y0, int X1, int Y1)? Selection => _selection;

    /// <summary>What the Place tool puts down (an id from <see cref="MapPlacement.Choices"/>), or null.</summary>
    public string? PlaceChoice
    {
        get => (_placeChoice.SelectedItem as ComboBoxItem)?.Tag as string;
        set => _placeChoice.SelectedItem = _placeChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
    }

    /// <summary>The placement kind of a Place tool (see <see cref="MapPlacement"/>), or null.</summary>
    private static string? PlacementKind(MapTool tool) => tool switch
    {
        MapTool.PlaceNpc => "npc",
        MapTool.PlaceNode => "nodeType",
        MapTool.PlaceItem => "item",
        MapTool.PlaceMachine => "machineType",
        MapTool.PlaceAnimal => "animalSpecies",
        _ => null,
    };

    private void BuildEditorPanels(StackPanel side)
    {
        void ToolButton(MapTool tool, string label, Panel? panel = null)
        {
            var button = new ToggleButton { Name = $"Tool_{tool}", Tag = tool, Content = label, Margin = new Thickness(0, 0, 6, 6) };
            button.Classes.Add("tool");
            if (panel is not null) AutomationProperties.SetName(button, tool == MapTool.Remove ? "Remove from tile" : $"Place {label}");
            if (ToolKeys.FirstOrDefault(pair => pair.Value == tool) is { Key: var key } shortcut && shortcut.Value == tool)
            {
                ToolTip.SetTip(button, $"{label} (press {key} on the map)");
                AutomationProperties.SetAcceleratorKey(button, key.ToString());
            }
            button.Click += (_, _) => Tool = tool;
            (panel ?? _tools).Children.Add(button);
        }

        ToolButton(MapTool.Inspect, "Inspect");
        ToolButton(MapTool.Brush, "Brush");
        ToolButton(MapTool.Rectangle, "Rectangle");
        ToolButton(MapTool.Fill, "Fill area");
        ToolButton(MapTool.Pick, "Pick");
        ToolButton(MapTool.Select, "Select");
        ToolButton(MapTool.Erase, "Erase layer");
        ToolButton(MapTool.Block, "Block");
        ToolButton(MapTool.Unblock, "Unblock");
        ToolButton(MapTool.Door, "Door");
        ToolButton(MapTool.PlayerStart, "Player start");
        ToolButton(MapTool.PlaceNpc, "NPC", _placeTools);
        ToolButton(MapTool.PlaceNode, "Node", _placeTools);
        ToolButton(MapTool.PlaceItem, "Item", _placeTools);
        ToolButton(MapTool.PlaceMachine, "Machine", _placeTools);
        ToolButton(MapTool.PlaceAnimal, "Animal", _placeTools);
        ToolButton(MapTool.Remove, "Remove", _placeTools);
        Tool = MapTool.Inspect;
        side.Children.Add(Ui.Text("MAP TOOLS", "section"));
        side.Children.Add(_tools);
        side.Children.Add(Ui.Wrapped("Drag with Brush, Erase, Block or Unblock. Drag a rectangle to paint or select an area. Pick takes a tile's type as the brush.", "muted", "small"));

        foreach (var (label, layer) in new[]
                 {
                     ("Background", Edits.LayerFor(TileTypes.Grass)),
                     ("Overlay", Edits.LayerFor(TileTypes.Path)),
                     ("Object", Edits.LayerFor(TileTypes.Wall)),
                 })
        {
            _layerSelector.Items.Add(new ComboBoxItem { Content = label, Tag = layer });
        }
        _layerSelector.SelectionChanged += (_, _) =>
        {
            if (_layerSelector.SelectedItem is ComboBoxItem { Tag: TileLayer layer })
            {
                _selectedLayer = layer;
            }
        };
        SyncLayerSelector();
        AutomationProperties.SetName(_layerSelector, "Layer");
        side.Children.Add(Ui.HStack(8, Ui.Text("Layer", "muted", "small"), _layerSelector));

        _copy.Click += (_, _) => CopySelection();
        _paste.Click += (_, _) => Tool = MapTool.Paste;
        side.Children.Add(Ui.HStack(8, _copy, _paste));

        side.Children.Add(Ui.Text("PLACE", "section"));
        side.Children.Add(_placeTools);
        _placeChoice.SelectionChanged += (_, _) =>
        {
            if (!_updatingPlaceChoice && _placeKind is not null && PlaceChoice is { } id) _placeChoices[_placeKind] = id;
        };
        AutomationProperties.SetName(_placeChoice, "What to place");
        side.Children.Add(_placeChoice);
        side.Children.Add(Ui.Wrapped("Choose what to place, then click a tile. Remove clears a tile's node, item, machine, crop and animals.", "muted", "small"));
        side.Children.Add(Ui.Text("Animals in this scene", "muted", "small"));
        side.Children.Add(_sceneAnimals);

        side.Children.Add(Ui.Text("SCENE", "section"));
        AutomationProperties.SetName(_sceneName, "Scene name");
        side.Children.Add(_sceneName);
        AutomationProperties.SetName(_sceneWidth, "Scene width");
        AutomationProperties.SetName(_sceneHeight, "Scene height");
        side.Children.Add(Ui.HStack(8, Ui.Text("Size", "muted", "small"), _sceneWidth, Ui.Text("×"), _sceneHeight));
        // Web SceneManager's size calculator, live as the size is typed.
        _sceneSizeInfo.Name = "SceneSizeInfo";
        _sceneWidth.TextChanged += (_, _) => RefreshSceneSizeInfo();
        _sceneHeight.TextChanged += (_, _) => RefreshSceneSizeInfo();
        side.Children.Add(_sceneSizeInfo);
        // Rain does not water an indoor scene's soil and storms do not reach its crops.
        ToolTip.SetTip(_sceneIndoor, "Greenhouses and interiors: rain does not water the soil and storms do not damage crops here.");
        _sceneIndoor.IsCheckedChanged += (_, _) =>
        {
            if (!_showingSceneIndoor) ApplyToScene(id => Edits.SetSceneIndoor(id, _sceneIndoor.IsChecked == true));
        };
        side.Children.Add(_sceneIndoor);
        foreach (var type in TileTypes.All)
        {
            _newSceneTile.Items.Add(new ComboBoxItem { Content = Ui.Capitalize(type), Tag = type });
        }
        _newSceneTile.SelectedItem = _newSceneTile.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, TileTypes.Grass));
        AutomationProperties.SetName(_newSceneTile, "New scene tile");
        side.Children.Add(Ui.HStack(8, Ui.Text("New scene tile", "muted", "small"), _newSceneTile));
        side.Children.Add(WrapButtons(
            NamedButton("AddSceneButton", "Add", AddScene),
            NamedButton("RenameSceneButton", "Rename", RenameScene),
            NamedButton("ResizeSceneButton", "Resize", ResizeScene)));
        side.Children.Add(WrapButtons(
            NamedButton("DuplicateSceneButton", "Duplicate", DuplicateScene), _startScene, _deleteScene));
        _startScene.Click += (_, _) => ApplyToScene(id => Edits.SetStartScene(id));
        _deleteScene.Click += (_, _) => DeleteScene();
        _fillScene.Click += (_, _) =>
        {
            if (_brush is not null) ApplyToScene(id => Edits.FillScene(id, _brush));
        };
        side.Children.Add(WrapButtons(_fillScene,
            NamedButton("ClearCropsItemsButton", "Clear crops/items", () => ApplyToScene(Edits.ClearCropsAndItems)),
            NamedButton("ResetSoilButton", "Reset soil", () => ApplyToScene(Edits.ResetSoil))));

        side.Children.Add(Ui.Text("TRANSITIONS", "section"));
        _transitionCount.Name = "TransitionCount";
        _clearTransitions.Classes.Add("tool");
        _clearTransitions.Click += (_, _) => ApplyToScene(Edits.ClearTransitions);
        side.Children.Add(Ui.HStack(8, _transitionCount, _clearTransitions));
        side.Children.Add(_transitionList);
        _doorFrom.Name = "DoorFrom";
        side.Children.Add(_doorFrom);
        AutomationProperties.SetName(_doorDestination, "To scene");
        AutomationProperties.SetName(_doorX, "At X (arrival column)");
        AutomationProperties.SetName(_doorY, "At Y (arrival row)");
        side.Children.Add(Ui.HStack(8, Ui.Text("To", "muted", "small"), _doorDestination));
        side.Children.Add(Ui.HStack(8, Ui.Text("At", "muted", "small"), _doorX, Ui.Text(","), _doorY));
        side.Children.Add(_doorReturn);
        side.Children.Add(Ui.HStack(8, NamedButton("SaveDoorButton", "Save door", SaveDoor), _removeDoor));
        _removeDoor.Click += (_, _) => RemoveDoor();
        // Tool results ("Door saved", "Corner marked") are announced politely.
        _editorMessage.Name = "MapEditorMessage";
        AutomationProperties.SetLiveSetting(_editorMessage, AutomationLiveSetting.Polite);
        side.Children.Add(_editorMessage);
    }

    /// <summary>The tool's button label ("Fill area"), for messages.</summary>
    private string ToolLabel(MapTool tool) =>
        _tools.Children.Concat(_placeTools.Children).OfType<ToggleButton>().FirstOrDefault(button => Equals(button.Tag, tool))?.Content as string ?? tool.ToString();

    private static Button NamedButton(string name, string label, Action action)
    {
        var button = Ui.Button(label, action, "tool");
        button.Name = name;
        return button;
    }

    private static WrapPanel WrapButtons(params Button[] buttons)
    {
        var panel = new WrapPanel();
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 0, 6, 6);
            panel.Children.Add(button);
        }
        return panel;
    }

    private void SyncLayerSelector()
    {
        _layerSelector.SelectedItem = _layerSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _selectedLayer));
    }

    private void RefreshEditorPanels(GameProject project, Scene? scene)
    {
        _copy.IsEnabled = _selection is not null;
        _paste.IsEnabled = _copiedTiles is not null;
        _fillScene.IsEnabled = _brush is not null;
        _deleteScene.IsEnabled = scene is not null && project.Scenes.Length > 1 && scene.Id != project.StartSceneId;
        _startScene.IsEnabled = scene is not null && scene.Id != project.StartSceneId;
        RefreshPlaceChoice(project);
        if (scene is null) return;
        RefreshSceneAnimals(project, scene);
        RefreshTransitionList(project, scene);

        if (_sceneControlsSceneId != scene.Id)
        {
            _sceneControlsSceneId = scene.Id;
            _sceneName.Text = scene.Name;
            _sceneWidth.Text = ((int)scene.Width).ToString(CultureInfo.InvariantCulture);
            _sceneHeight.Text = ((int)scene.Height).ToString(CultureInfo.InvariantCulture);
            _doorAt = null;
            _doorFrom.Text = "Choose Door, then click a departure tile.";
        }
        else
        {
            if (_sceneName.Text == _shownSceneName) _sceneName.Text = scene.Name;
            if (_sceneWidth.Text == _shownSceneWidth) _sceneWidth.Text = ((int)scene.Width).ToString(CultureInfo.InvariantCulture);
            if (_sceneHeight.Text == _shownSceneHeight) _sceneHeight.Text = ((int)scene.Height).ToString(CultureInfo.InvariantCulture);
        }
        _shownSceneName = scene.Name;
        _shownSceneWidth = ((int)scene.Width).ToString(CultureInfo.InvariantCulture);
        _shownSceneHeight = ((int)scene.Height).ToString(CultureInfo.InvariantCulture);
        _removeDoor.IsEnabled = _doorAt is { } at && scene.Transitions.Any(t => t.FromX == at.X && t.FromY == at.Y);
        _showingSceneIndoor = true;
        _sceneIndoor.IsChecked = scene.Indoor.OrNullable() == true;
        _showingSceneIndoor = false;
        RefreshSceneSizeInfo();

        var previous = (_doorDestination.SelectedItem as ComboBoxItem)?.Tag as string;
        var ids = _doorDestination.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string);
        if (!ids.SequenceEqual(project.Scenes.Select(s => s.Id)))
        {
            _doorDestination.Items.Clear();
            foreach (var destination in project.Scenes)
            {
                _doorDestination.Items.Add(new ComboBoxItem { Content = destination.Name, Tag = destination.Id });
            }
            _doorDestination.SelectedItem = _doorDestination.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previous))
                ?? _doorDestination.Items.OfType<ComboBoxItem>().FirstOrDefault(item => !Equals(item.Tag, scene.Id));
        }
        else
        {
            foreach (var item in _doorDestination.Items.OfType<ComboBoxItem>())
            {
                if (project.Scenes.FirstOrDefault(s => s.Id == item.Tag as string) is { } destination)
                    item.Content = destination.Name;
            }
        }
    }

    /// <summary>
    /// Fills the Place picker with what the current Place tool offers (from F#), keeping the
    /// creator's last choice for that tool.
    /// </summary>
    private void RefreshPlaceChoice(GameProject project)
    {
        var kind = PlacementKind(Tool);
        IReadOnlyList<PickerOption> options = kind is null ? [] : MapPlacement.Choices(kind, project);
        var shown = _placeChoice.Items.OfType<ComboBoxItem>().Select(item => ((string?)item.Tag, item.Content as string));
        if (kind == _placeKind && shown.SequenceEqual(options.Select(option => ((string?)option.Id, (string?)option.Label)))) return;

        _updatingPlaceChoice = true;
        try
        {
            _placeKind = kind;
            _placeChoice.Items.Clear();
            foreach (var option in options)
            {
                _placeChoice.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Id });
            }
            _placeChoice.IsEnabled = kind is not null;
            _placeChoice.PlaceholderText = kind is null ? "Choose a Place tool" : options.Count == 0 ? "Nothing to place yet" : "Choose what to place";
            PlaceChoice = kind is not null && _placeChoices.TryGetValue(kind, out var previous) ? previous : null;
            if (PlaceChoice is null && _placeChoice.ItemCount > 0) _placeChoice.SelectedIndex = 0;
        }
        finally
        {
            _updatingPlaceChoice = false;
        }
    }

    /// <summary>The animals placed in this scene, each with a button that removes it.</summary>
    private void RefreshSceneAnimals(GameProject project, Scene scene)
    {
        var animals = project.Animals.Where(a => a.SceneId == scene.Id).ToList();
        var labels = animals.Select(a => $"{a.Id}|{a.Name} ({Ui.Num(Math.Floor(a.X))}, {Ui.Num(Math.Floor(a.Y))})").ToList();
        if (labels.SequenceEqual(_shownAnimals)) return;
        _shownAnimals = labels;
        _sceneAnimals.Children.Clear();
        if (animals.Count == 0)
        {
            _sceneAnimals.Children.Add(Ui.Wrapped("None yet. Choose Animal and click a tile.", "muted", "small"));
            return;
        }
        foreach (var animal in animals)
        {
            var id = animal.Id;
            var remove = NamedButton($"RemoveAnimal_{id}", "Remove", () => _workspace.Apply(Edits.RemoveAnimal(id)));
            AutomationProperties.SetName(remove, $"Remove {animal.Name}");
            _sceneAnimals.Children.Add(Ui.Row(Ui.Wrapped($"{animal.Name} ({Ui.Num(Math.Floor(animal.X))}, {Ui.Num(Math.Floor(animal.Y))})", "small"), remove));
        }
    }

    /// <summary>
    /// The doors leaving this scene (web TransitionEditor list); a click edits one, Duplicate
    /// starts a new door with the same destination.
    /// </summary>
    private void RefreshTransitionList(GameProject project, Scene scene)
    {
        string SceneName(string id) => project.Scenes.FirstOrDefault(s => s.Id == id)?.Name ?? $"(missing: {id})";
        var labels = scene.Transitions
            .Select(t => $"({Ui.Num(t.FromX)}, {Ui.Num(t.FromY)}) → {SceneName(t.ToSceneId)} ({Ui.Num(t.ToX)}, {Ui.Num(t.ToY)})")
            .ToList();
        _transitionCount.Text = $"{labels.Count} transition(s) from this scene";
        _clearTransitions.IsEnabled = labels.Count > 0;
        if (labels.SequenceEqual(_shownTransitions)) return;
        _shownTransitions = labels;
        _transitionList.Children.Clear();
        for (var index = 0; index < labels.Count; index++)
        {
            var transition = scene.Transitions[index];
            var (x, y) = ((int)transition.FromX, (int)transition.FromY);
            var button = NamedButton($"Transition_{index}", labels[index], () =>
            {
                Tool = MapTool.Door;
                ChooseDoor(x, y);
            });
            button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            var duplicate = NamedButton($"DuplicateTransition_{index}", "Duplicate", () => DuplicateTransition(transition));
            AutomationProperties.SetName(duplicate, $"Duplicate transition {labels[index]}");
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(button);
            duplicate.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(duplicate, 1);
            row.Children.Add(duplicate);
            _transitionList.Children.Add(row);
        }
    }

    private bool BeginToolAt((int X, int Y) tile)
    {
        var scene = CurrentScene();
        if (scene is null) return false;
        switch (Tool)
        {
            case MapTool.Pick:
                PickAt(tile.X, tile.Y);
                return true;
            case MapTool.PlaceNpc:
            case MapTool.PlaceNode:
            case MapTool.PlaceItem:
            case MapTool.PlaceMachine:
            case MapTool.PlaceAnimal:
                PlaceAt(tile.X, tile.Y);
                return true;
            case MapTool.Remove:
                _workspace.Apply(Edits.ClearTile(scene.Id, tile.X, tile.Y));
                return true;
            case MapTool.Brush when _brush is not null:
            case MapTool.Erase:
            case MapTool.Block:
            case MapTool.Unblock:
                _strokeId = $"map-{++_strokeCount}";
                _lastPainted = tile;
                ApplyStrokeTile(tile.X, tile.Y);
                return true;
            case MapTool.Rectangle when _brush is not null:
            case MapTool.Select:
                _gestureStart = tile;
                _lastPainted = tile;
                return true;
            case MapTool.Fill when _brush is not null:
                _workspace.Apply(Edits.FloodFill(scene.Id, Layer, tile.X, tile.Y, _brush));
                return true;
            case MapTool.Paste:
                PasteAt(tile.X, tile.Y);
                return true;
            case MapTool.Door:
                ChooseDoor(tile.X, tile.Y);
                return true;
            case MapTool.PlayerStart:
                _workspace.Apply(Edits.SetPlayerStart(scene.Id, tile.X, tile.Y));
                return true;
            default:
                return false;
        }
    }

    private void ApplyStrokeTile(int x, int y)
    {
        var scene = CurrentScene();
        if (scene is null) return;
        switch (Tool)
        {
            case MapTool.Brush: PaintTile(x, y); break;
            case MapTool.Erase: _workspace.ApplyInStroke(_strokeId!, Edits.EraseLayer(scene.Id, Layer, new[] { new ValueTuple<int, int>(x, y) })); break;
            case MapTool.Block: _workspace.ApplyInStroke(_strokeId!, Edits.SetCollision(scene.Id, new[] { new ValueTuple<int, int>(x, y) }, true)); break;
            case MapTool.Unblock: _workspace.ApplyInStroke(_strokeId!, Edits.SetCollision(scene.Id, new[] { new ValueTuple<int, int>(x, y) }, false)); break;
        }
    }

    private void UpdateGesturePreview((int X, int Y) start, (int X, int Y) end)
    {
        _lastPainted = end;
        var first = _canvas.TileRect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y));
        var last = _canvas.TileRect(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
        _hover.Margin = new Thickness(first.X, first.Y, 0, 0);
        _hover.Width = last.Right - first.Left;
        _hover.Height = last.Bottom - first.Top;
        _hover.IsVisible = true;
    }

    private void CompleteGesture((int X, int Y) end)
    {
        if (_gestureStart is not { } start) return;
        _gestureStart = null;
        var scene = CurrentScene();
        if (scene is null) return;
        var x0 = Math.Min(start.X, end.X);
        var y0 = Math.Min(start.Y, end.Y);
        var x1 = Math.Max(start.X, end.X);
        var y1 = Math.Max(start.Y, end.Y);
        if (Tool == MapTool.Rectangle && _brush is not null)
        {
            _workspace.Apply(Edits.FillRect(scene.Id, Layer, x0, y0, x1, y1, _brush));
        }
        else if (Tool == MapTool.Select)
        {
            _selection = (x0, y0, x1, y1);
            _copy.IsEnabled = true;
            _editorMessage.Text = $"Selected {x1 - x0 + 1} × {y1 - y0 + 1} tiles.";
        }
        _hover.IsVisible = false;
    }

    private void ClearSelection()
    {
        _selection = null;
        _gestureStart = null;
        _copy.IsEnabled = false;
    }

    public void CopySelection()
    {
        var scene = CurrentScene();
        if (scene is null || _selection is not { } area) return;
        if (area.Y1 >= scene.Tiles.Length || scene.Tiles.Skip(area.Y0).Take(area.Y1 - area.Y0 + 1).Any(row => area.X1 >= row.Length))
        {
            ClearSelection();
            _editorMessage.Text = "The selection no longer fits this scene; select an area again.";
            return;
        }
        _copiedTiles = new List<List<Tile>>();
        for (var y = area.Y0; y <= area.Y1; y++)
        {
            _copiedTiles.Add(scene.Tiles[y].Skip(area.X0).Take(area.X1 - area.X0 + 1).ToList());
        }
        _paste.IsEnabled = true;
        _editorMessage.Text = $"Copied {_copiedTiles[0].Count} × {_copiedTiles.Count} tiles. Choose Paste on map.";
    }

    public void PasteAt(int x, int y)
    {
        var scene = CurrentScene();
        if (scene is null || _copiedTiles is null) return;
        _workspace.Apply(Edits.PasteTiles(scene.Id, x, y, _copiedTiles));
    }

    /// <summary>Eyedropper: the tile's type (and that layer's art) becomes the brush.</summary>
    public void PickAt(int x, int y)
    {
        var scene = CurrentScene();
        if (_workspace.Current is not { } project || scene is null || Edits.PickBrush(project, scene.Id, x, y) is not { } pick) return;
        var picked = Documents.Preview(project, pick);
        SetBrush(picked.SelectedTileType, picked.SelectedTileVisual.OrNull());
        _editorMessage.Text = $"Picked {picked.SelectedTileType}.";
    }

    /// <summary>The current Place tool puts its chosen NPC, node, item, machine or animal on the tile.</summary>
    public void PlaceAt(int x, int y)
    {
        var scene = CurrentScene();
        if (_workspace.Current is not { } project || scene is null || PlacementKind(Tool) is not { } kind) return;
        if (_placeChoice.SelectedItem is not ComboBoxItem { Tag: string id, Content: string label }
            || MapPlacement.Place(project, kind, id, scene.Id, x, y) is not { } edit)
        {
            _editorMessage.Text = "Choose what to place first.";
            return;
        }
        if (_workspace.Apply(edit)) _editorMessage.Text = $"Placed {label} at ({x}, {y}).";
    }

    private void ApplyToScene(Func<string, Edit> createEdit)
    {
        if (CurrentScene() is { } scene) _workspace.Apply(createEdit(scene.Id));
    }

    private bool SceneSize(out int width, out int height)
    {
        var ok = TypedSceneSize(out width, out height);
        if (!ok) _editorMessage.Text = $"Scene width and height must be whole numbers from 1 to {Edits.MaxSceneSize}.";
        return ok;
    }

    /// <summary>The size typed in the Size boxes, when both are whole numbers from 1 to 256.</summary>
    private bool TypedSceneSize(out int width, out int height)
    {
        var validWidth = int.TryParse(_sceneWidth.Text, NumberStyles.None, CultureInfo.InvariantCulture, out width);
        var validHeight = int.TryParse(_sceneHeight.Text, NumberStyles.None, CultureInfo.InvariantCulture, out height);
        return validWidth && validHeight && width >= 1 && width <= Edits.MaxSceneSize && height >= 1 && height <= Edits.MaxSceneSize;
    }

    /// <summary>
    /// Tile count and aspect ratio of the typed size, and what a resize to it would cut off: the
    /// tiles, and each thing standing on or pointing at them that moves inside (#44).
    /// </summary>
    private void RefreshSceneSizeInfo()
    {
        if (!TypedSceneSize(out var width, out var height))
        {
            _sceneSizeInfo.Text = $"Width and height must be whole numbers from 1 to {Edits.MaxSceneSize}.";
            return;
        }
        var info = $"Total tiles: {width * height} · Aspect ratio: {((double)width / height).ToString("0.00", CultureInfo.InvariantCulture)}";
        if (CurrentScene() is { } scene && _workspace.Current is { } project && (width < scene.Width || height < scene.Height))
        {
            info += $"\nResize removes the tiles outside {width}×{height}.";
            var impact = Edits.ResizeImpact(project, scene.Id, width, height);
            if (impact.Count > 0)
            {
                info += " It also changes: " + string.Join("; ", impact) + ".";
            }
        }
        _sceneSizeInfo.Text = info;
    }

    private void AddScene()
    {
        if (_workspace.Current is not { } project || !SceneSize(out var width, out var height)) return;
        var tile = (_newSceneTile.SelectedItem as ComboBoxItem)?.Tag as string ?? TileTypes.Grass;
        var scene = Defaults.NewScene(project, _sceneName.Text ?? "", width, height, tile);
        if (_workspace.Apply(Edits.AddScene(scene))) SelectScene(scene.Id);
    }

    private void RenameScene()
    {
        if (CurrentScene() is not { } scene) return;
        if (string.IsNullOrWhiteSpace(_sceneName.Text))
        {
            _editorMessage.Text = "Enter a scene name first.";
            return;
        }
        _workspace.Apply(Edits.RenameScene(scene.Id, _sceneName.Text.Trim()));
    }

    private void ResizeScene()
    {
        if (CurrentScene() is { } scene && _workspace.Current is { } project && SceneSize(out var width, out var height))
        {
            var impact = Edits.ResizeImpact(project, scene.Id, width, height);
            if (_workspace.Apply(Edits.ResizeScene(scene.Id, width, height)))
            {
                _editorMessage.Text = impact.Count == 0
                    ? $"Resized {scene.Name} to {width}×{height}."
                    : $"Resized {scene.Name} to {width}×{height}: {string.Join("; ", impact)}. Undo puts them back.";
            }
            ClearSelection();
            FitToView();
        }
    }

    private void DuplicateScene()
    {
        if (_workspace.Current is not { } project || CurrentScene() is not { } scene) return;
        var id = Defaults.NextId("scene", project.Scenes.Select(s => s.Id));
        if (_workspace.Apply(Edits.DuplicateScene(scene.Id, id))) SelectScene(id);
    }

    private void DeleteScene()
    {
        if (CurrentScene() is not { } scene) return;
        if (!_workspace.Apply(Edits.RemoveScene(scene.Id)))
        {
            _editorMessage.Text = "The start scene and the last scene cannot be deleted.";
        }
    }

    private void ChooseDoor(int x, int y)
    {
        var scene = CurrentScene();
        if (scene is null) return;
        _doorAt = (x, y);
        _doorFrom.Text = $"From {scene.Name} ({x}, {y})";
        if (scene.Transitions.FirstOrDefault(t => t.FromX == x && t.FromY == y) is { } existing)
        {
            _doorDestination.SelectedItem = _doorDestination.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, existing.ToSceneId));
            _doorX.Text = ((int)existing.ToX).ToString(CultureInfo.InvariantCulture);
            _doorY.Text = ((int)existing.ToY).ToString(CultureInfo.InvariantCulture);
            _doorReturn.IsChecked = false;
            _removeDoor.IsEnabled = true;
        }
        else
        {
            _removeDoor.IsEnabled = false;
        }
    }

    /// <summary>
    /// Web TransitionEditor "Duplicate": the door form gets the same destination and arrival tile,
    /// departing from the next free tile (one to the right, else onward to the right and below).
    /// Save door creates it.
    /// </summary>
    private void DuplicateTransition(SceneTransition transition)
    {
        if (CurrentScene() is not { } scene) return;
        var from = FreeDoorTile(scene, (int)transition.FromX + 1, (int)transition.FromY);
        Tool = MapTool.Door;
        ChooseDoor(from.X, from.Y);
        SetCursor(from);
        _doorDestination.SelectedItem = _doorDestination.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, transition.ToSceneId));
        _doorX.Text = ((int)transition.ToX).ToString(CultureInfo.InvariantCulture);
        _doorY.Text = ((int)transition.ToY).ToString(CultureInfo.InvariantCulture);
        _doorReturn.IsChecked = false;
        _editorMessage.Text = $"Copied the door to ({from.X}, {from.Y}). Adjust the departure tile (click the map), then choose Save door.";
    }

    /// <summary>The first tile without a door from (x, y), clamped into the scene, scanning right then down.</summary>
    private static (int X, int Y) FreeDoorTile(Scene scene, int x, int y)
    {
        var width = Math.Max(1, (int)scene.Width);
        var count = width * Math.Max(1, (int)scene.Height);
        var start = (Math.Clamp(y, 0, (count / width) - 1) * width) + Math.Clamp(x, 0, width - 1);
        for (var i = 0; i < count; i++)
        {
            var index = (start + i) % count;
            var (tileX, tileY) = (index % width, index / width);
            if (!scene.Transitions.Any(t => t.FromX == tileX && t.FromY == tileY)) return (tileX, tileY);
        }
        return (start % width, start / width);
    }

    private void SaveDoor()
    {
        var scene = CurrentScene();
        var project = _workspace.Current;
        if (scene is null || project is null || _doorAt is not { } from)
        {
            _editorMessage.Text = "Choose Door and click a departure tile first.";
            return;
        }
        if (_doorDestination.SelectedItem is not ComboBoxItem { Tag: string destinationId }
            || !int.TryParse(_doorX.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(_doorY.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            || project.Scenes.FirstOrDefault(s => s.Id == destinationId) is not { } destination
            || x < 0 || y < 0 || x >= destination.Width || y >= destination.Height)
        {
            _editorMessage.Text = "Choose a destination and coordinates inside that scene.";
            return;
        }
        var old = scene.Transitions.FirstOrDefault(t => t.FromX == from.X && t.FromY == from.Y);
        var transition = (old ?? SceneTransition.Default).WithFromX(from.X).WithFromY(from.Y).WithToSceneId(destinationId).WithToX(x).WithToY(y);
        _workspace.Apply(_doorReturn.IsChecked == true ? Edits.LinkScenes(scene.Id, transition) : Edits.SetTransition(scene.Id, transition));
        _editorMessage.Text = $"Door saved to {destination.Name} ({x}, {y}).";
    }

    private void RemoveDoor()
    {
        if (CurrentScene() is { } scene && _doorAt is { } from)
        {
            _workspace.Apply(Edits.RemoveTransition(scene.Id, from.X, from.Y));
        }
    }
}
