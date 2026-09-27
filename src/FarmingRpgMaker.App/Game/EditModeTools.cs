using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using FarmEngine.Authoring;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

public enum MapTool
{
    Inspect, Brush, Rectangle, Fill, Select, Paste, Erase, Block, Unblock, Door, PlayerStart,
}

/// <summary>The creator-facing map tools. All mutations go through the F# document.</summary>
public sealed partial class EditModeView
{
    private readonly WrapPanel _tools = new() { Name = "MapTools" };
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
    private readonly Button _removeDoor = new() { Name = "RemoveDoorButton", Content = "Remove door" };
    private readonly TextBlock _doorFrom = Ui.Wrapped("Choose Door, then click a departure tile.", "muted", "small");
    private readonly TextBlock _editorMessage = Ui.Wrapped("", "muted", "small");
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

    public MapTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            foreach (var button in _tools.Children.OfType<ToggleButton>())
            {
                button.IsChecked = Equals(button.Tag, value);
            }
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

    private void BuildEditorPanels(StackPanel side)
    {
        void ToolButton(MapTool tool, string label)
        {
            var button = new ToggleButton { Name = $"Tool_{tool}", Tag = tool, Content = label, Margin = new Thickness(0, 0, 6, 6) };
            button.Classes.Add("tool");
            button.Click += (_, _) => Tool = tool;
            _tools.Children.Add(button);
        }

        ToolButton(MapTool.Inspect, "Inspect");
        ToolButton(MapTool.Brush, "Brush");
        ToolButton(MapTool.Rectangle, "Rectangle");
        ToolButton(MapTool.Fill, "Fill area");
        ToolButton(MapTool.Select, "Select");
        ToolButton(MapTool.Erase, "Erase layer");
        ToolButton(MapTool.Block, "Block");
        ToolButton(MapTool.Unblock, "Unblock");
        ToolButton(MapTool.Door, "Door");
        ToolButton(MapTool.PlayerStart, "Player start");
        Tool = MapTool.Inspect;
        side.Children.Add(Ui.Text("MAP TOOLS", "section"));
        side.Children.Add(_tools);
        side.Children.Add(Ui.Wrapped("Drag with Brush, Erase, Block or Unblock. Drag a rectangle to paint or select an area.", "muted", "small"));

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
        side.Children.Add(Ui.HStack(8, Ui.Text("Layer", "muted", "small"), _layerSelector));

        _copy.Click += (_, _) => CopySelection();
        _paste.Click += (_, _) => Tool = MapTool.Paste;
        side.Children.Add(Ui.HStack(8, _copy, _paste));

        side.Children.Add(Ui.Text("SCENE", "section"));
        side.Children.Add(_sceneName);
        side.Children.Add(Ui.HStack(8, Ui.Text("Size", "muted", "small"), _sceneWidth, Ui.Text("×"), _sceneHeight));
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

        side.Children.Add(Ui.Text("TRANSITION", "section"));
        _doorFrom.Name = "DoorFrom";
        side.Children.Add(_doorFrom);
        side.Children.Add(Ui.HStack(8, Ui.Text("To", "muted", "small"), _doorDestination));
        side.Children.Add(Ui.HStack(8, Ui.Text("At", "muted", "small"), _doorX, Ui.Text(","), _doorY));
        side.Children.Add(_doorReturn);
        side.Children.Add(Ui.HStack(8, NamedButton("SaveDoorButton", "Save door", SaveDoor), _removeDoor));
        _removeDoor.Click += (_, _) => RemoveDoor();
        side.Children.Add(_editorMessage);
    }

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
        _deleteScene.IsEnabled = scene is not null && project.Scenes.Count > 1 && scene.Id != project.StartSceneId;
        _startScene.IsEnabled = scene is not null && scene.Id != project.StartSceneId;
        if (scene is null) return;

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

    private bool BeginToolAt((int X, int Y) tile)
    {
        var scene = CurrentScene();
        if (scene is null) return false;
        switch (Tool)
        {
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
        if (area.Y1 >= scene.Tiles.Count || scene.Tiles.Skip(area.Y0).Take(area.Y1 - area.Y0 + 1).Any(row => area.X1 >= row.Count))
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

    private void ApplyToScene(Func<string, Edit> createEdit)
    {
        if (CurrentScene() is { } scene) _workspace.Apply(createEdit(scene.Id));
    }

    private bool SceneSize(out int width, out int height)
    {
        var validWidth = int.TryParse(_sceneWidth.Text, NumberStyles.None, CultureInfo.InvariantCulture, out width);
        var validHeight = int.TryParse(_sceneHeight.Text, NumberStyles.None, CultureInfo.InvariantCulture, out height);
        var ok = validWidth && validHeight
            && width is >= 1 and <= 256 && height is >= 1 and <= 256;
        if (!ok) _editorMessage.Text = "Scene width and height must be whole numbers from 1 to 256.";
        return ok;
    }

    private void AddScene()
    {
        if (_workspace.Current is not { } project || !SceneSize(out var width, out var height)) return;
        var scene = Defaults.NewScene(project, _sceneName.Text ?? "", width, height);
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
        if (CurrentScene() is { } scene && SceneSize(out var width, out var height))
        {
            _workspace.Apply(Edits.ResizeScene(scene.Id, width, height));
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
        var transition = (old ?? new SceneTransition()) with
        {
            FromX = from.X, FromY = from.Y, ToSceneId = destinationId, ToX = x, ToY = y,
        };
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
