using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Native editor: map tools and the content workspace, with F# document undo/redo.
/// </summary>
public sealed partial class EditModeView : UserControl
{
    /// <summary>Edit-mode tile size (web GameView: 28 outside play).</summary>
    public const double TileSize = 28;

    /// <summary>Palette chip colors per tile type (the renderer's fallback colors).</summary>
    private static readonly IReadOnlyDictionary<string, string> TileSwatches = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["grass"] = "#5b9a4a",
        ["soil"] = "#7a6545",
        ["water"] = "#3075b0",
        ["path"] = "#b5a48d",
        ["wall"] = "#5e5a68",
        ["door"] = "#8a6a3f",
        ["floor"] = "#b5a48d",
    };

    public const string PortingNotice = "Tip: click the map (or Tab to it), then use the arrow keys and Enter or Space to edit from the keyboard. Letter keys pick tools (B brush, R rectangle, G fill, E erase…).";

    private readonly ProjectWorkspace _workspace;
    private readonly MapCanvas _canvas = new() { Name = "EditCanvas", Cursor = new Cursor(StandardCursorType.Hand) };
    private readonly ScrollViewer _scroller;
    private readonly Border _hover = new() { Name = "HoverHighlight", BorderThickness = new Thickness(2), IsHitTestVisible = false, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    /// <summary>The keyboard editing cursor (web GameView edit cursor): a gold frame over one tile.</summary>
    private readonly Border _cursor = new()
    {
        Name = "EditCursor",
        BorderThickness = new Thickness(2),
        BorderBrush = new SolidColorBrush(Color.Parse("#E7BA4B")),
        BoxShadow = BoxShadows.Parse("0 0 0 1 #B3000000, 0 0 8 0 #99E7BA4B"),
        IsHitTestVisible = false,
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };
    /// <summary>One line (so the map never shifts as it changes); screen readers get the whole text.</summary>
    private readonly TextBlock _cursorStatus = new() { Name = "EditCursorStatus", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 8, 4, 0) };
    private readonly ComboBox _sceneSelector = new() { Name = "SceneSelector", MinWidth = 200 };
    private readonly TextBlock _hoverInfo = new() { Name = "HoverInfo", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _zoomText = new() { Name = "ZoomText", VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _info = new() { Spacing = 12 };
    private readonly WrapPanel _palette = new() { Name = "TilePalette" };
    private readonly Button _undo;
    private readonly Button _redo;
    private GameProject? _projectForCanvas;
    /// <summary>What the project info panel shows, to rebuild it only when that changed (not per painted tile).</summary>
    private string? _infoKey;
    private string? _sceneId;
    private string? _brush;
    /// <summary>The brush's art (null: none). With <see cref="_brush"/> the editor owns the brush, not the history.</summary>
    private VisualRef? _brushVisual;
    private string? _strokeId;
    private int _strokeCount;
    private (int X, int Y)? _lastPainted;
    private bool _fitPending = true;
    private double _zoom = 1;
    private (int X, int Y) _cursorTile;
    private bool _updatingScenes;
    private TopLevel? _topLevel;

    public EditModeView(ProjectWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Name = "EditModeView";
        _hover.BorderBrush = (IBrush?)Application.Current?.FindResource("FarmAccentBrush") ?? Brushes.Gold;
        _hover.Background = new SolidColorBrush(Color.Parse("#33FFFFFF"));

        // Canvas + hover highlight + editing cursor in a scroll viewer. Focusing the map must not
        // scroll it to its top-left corner; keyboard moves bring the cursor into view instead.
        var stage = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
        stage.Children.Add(_canvas);
        stage.Children.Add(_hover);
        stage.Children.Add(_cursor);
        _scroller = new ScrollViewer
        {
            Name = "EditScroller",
            Content = stage,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BringIntoViewOnFocusChange = false,
        };
        var frame = new Border { Child = _scroller, Background = new SolidColorBrush(Color.Parse("#4DE4DDCF")) }.WithClasses("game-frame");
        frame.Background = new SolidColorBrush(Color.Parse("#66E4DDCF"));

        _canvas.PointerMoved += OnCanvasPointerMoved;
        _canvas.PointerExited += (_, _) =>
        {
            // A rectangle started from the keyboard keeps its preview.
            _hover.IsVisible = _gestureStart is not null;
            _hoverInfo.Text = "Hover a tile to inspect it.";
        };
        _canvas.PointerPressed += OnCanvasPointerPressed;
        _canvas.PointerReleased += OnCanvasPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => EndStroke();
        _canvas.KeyDown += OnCanvasKeyDown;
        // The status line under the map is a polite live region: screen readers announce each cursor move.
        _cursorStatus.Classes.Add("muted");
        _cursorStatus.Classes.Add("small");
        AutomationProperties.SetLiveSetting(_cursorStatus, AutomationLiveSetting.Polite);
        _scroller.SizeChanged += (_, _) =>
        {
            if (_fitPending)
            {
                FitToView();
            }
        };

        // Toolbar above the canvas: scene selector, zoom, hover readout.
        _sceneSelector.SelectionChanged += (_, _) =>
        {
            if (!_updatingScenes && _sceneSelector.SelectedItem is ComboBoxItem { Tag: string id })
            {
                _sceneId = id;
                ClearSelection();
                _fitPending = true;
                Refresh();
                FitToView();
            }
        };
        _hoverInfo.Classes.Add("muted");
        _hoverInfo.Text = "Hover a tile to inspect it.";
        AutomationProperties.SetName(_sceneSelector, "Scene");
        var zoomOut = Ui.Button("−", () => SetZoom(_zoom - 0.25), "tool");
        zoomOut.Name = "ZoomOutButton";
        AutomationProperties.SetName(zoomOut, "Zoom out");
        var zoomIn = Ui.Button("+", () => SetZoom(_zoom + 0.25), "tool");
        zoomIn.Name = "ZoomInButton";
        AutomationProperties.SetName(zoomIn, "Zoom in");
        var fit = Ui.Button("Fit", FitToView, "tool");
        fit.Name = "ZoomFitButton";
        AutomationProperties.SetName(fit, "Fit map to view");
        var toolbarLeft = Ui.HStack(8, Ui.Icon("IconMap", 18), Ui.Text("Scene", "hud-label"), _sceneSelector);
        var toolbarRight = Ui.HStack(6, zoomOut, _zoomText, zoomIn, fit);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(toolbarLeft);
        var hoverBox = new Border { Child = _hoverInfo, Margin = new Thickness(16, 0) };
        Grid.SetColumn(hoverBox, 1);
        toolbar.Children.Add(hoverBox);
        Grid.SetColumn(toolbarRight, 2);
        toolbar.Children.Add(toolbarRight);
        var toolbarBorder = new Border { Name = "MapToolbar", Child = toolbar, Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 12) }.WithClasses("hud");

        var center = new DockPanel();
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        center.Children.Add(toolbarBorder);
        DockPanel.SetDock(_cursorStatus, Dock.Bottom);
        center.Children.Add(_cursorStatus);
        center.Children.Add(frame);

        // Side panel: map tools, scene and transition controls, history.
        _undo = Ui.Button(Ui.IconLabel("IconUndo", "Undo"), () => _workspace.Undo(), "tool");
        _undo.Name = "UndoButton";
        AutomationProperties.SetName(_undo, "Undo");
        ToolTip.SetTip(_undo, "Undo (Ctrl+Z)");
        _redo = Ui.Button(Ui.IconLabel("IconRedo", "Redo"), () => _workspace.Redo(), "tool");
        _redo.Name = "RedoButton";
        AutomationProperties.SetName(_redo, "Redo");
        ToolTip.SetTip(_redo, "Redo (Ctrl+Y)");
        BuildPalette();

        var notice = Ui.Wrapped(PortingNotice, "small");
        notice.Name = "EditorNotice";
        var noticeBox = new Border { Child = Ui.HStack(8, Ui.Icon("IconInfo", 16), notice) }.WithClasses("notice");
        ((StackPanel)noticeBox.Child!).Children[1].Width = 210;

        var side = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 10, 0) };
        side.Children.Add(noticeBox);
        side.Children.Add(_info);
        side.Children.Add(Ui.Text("TILE BRUSH", "section"));
        side.Children.Add(_palette);
        BuildEditorPanels(side);
        side.Children.Add(Ui.HStack(8, _undo, _redo));
        var sidePanel = new Border
        {
            Name = "ProjectInfoPanel",
            Width = 300,
            Margin = new Thickness(0, 0, 16, 0),
            Child = new ScrollViewer { Content = side, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        }.WithClasses("side-panel");
        DockPanel.SetDock(sidePanel, Dock.Left);

        var tabs = new TabControl { Name = "EditorTabs" };
        var contentEditor = new ContentEditorView(workspace);
        var settingsEditor = new SettingsEditorView(workspace);
        var mods = new ModsEditorView(workspace);
        var art = new ArtEditorView(workspace, (type, visual) =>
        {
            UseBrush(type, visual);
            tabs.SelectedIndex = 0;
        });
        var workshop = new WorkshopView(workspace, tab =>
        {
            // The web editor's tab keys: art, the map and the Problems panel have their own tabs.
            var editorTab = tab switch
            {
                "scenes" => 0,
                "problems" => 2,
                "assets" => 5,
                _ => -1,
            };
            if (editorTab >= 0)
            {
                tabs.SelectedIndex = editorTab;
                return;
            }

            var category = tab switch
            {
                "npcs" => "NPCs",
                "actions" => "Actions",
                "events" => "Events",
                "nodes" => "Node types",
                "craft" => "Recipes",
                "crops" => "Crops",
                "wildlife" => "Animal species",
                "items" => "Items",
                "quests" => "Quests",
                _ => null,
            };
            if (category is not null)
            {
                tabs.SelectedIndex = 1;
                contentEditor.SelectCategory(category);
            }
        });
        var interfaceEditor = new InterfaceEditorView(workspace);
        var problems = new ProblemsView(workspace, problem =>
        {
            if (problem.TargetKind == "scene" && problem.TargetId is { } sceneId)
            {
                tabs.SelectedIndex = 0;
                SelectScene(sceneId);
                return;
            }

            if (problem.TargetKind == "settings")
            {
                tabs.SelectedIndex = 3;
                return;
            }
            if (problem.TargetKind == "interface")
            {
                tabs.SelectedIndex = 7;
                return;
            }
            if (problem.TargetKind == "pack" && problem.TargetId is { } packId)
            {
                tabs.SelectedIndex = 4;
                mods.SelectPack(packId);
                return;
            }
            if (problem.TargetKind == "asset" && problem.TargetId is { } assetId)
            {
                tabs.SelectedIndex = 5;
                art.SelectAsset(assetId);
                return;
            }

            var category = problem.TargetKind switch
            {
                "npc" => "NPCs",
                "item" => "Items",
                "crop" => "Crops",
                "quest" => "Quests",
                "event" => "Events",
                "shop" => "Shops",
                "recipe" => "Recipes",
                "nodeType" => "Node types",
                "machineType" => "Machine types",
                "animalSpecies" => "Animal species",
                "fishTable" => "Fish tables",
                "action" => "Actions",
                "minigame" => "Minigames",
                _ => null,
            };
            if (category is not null && problem.TargetId is { } id)
            {
                tabs.SelectedIndex = 1;
                contentEditor.SelectEntry(category, id);
            }
        });
        tabs.Items.Add(new TabItem { Header = "Map", Content = center });
        tabs.Items.Add(new TabItem { Header = "Content", Content = contentEditor });
        tabs.Items.Add(new TabItem { Header = "Problems", Content = problems });
        tabs.Items.Add(new TabItem { Header = "Settings", Content = settingsEditor });
        tabs.Items.Add(new TabItem { Header = "Mods", Content = mods });
        tabs.Items.Add(new TabItem { Header = "Art", Content = art });
        tabs.Items.Add(new TabItem { Header = "Workshop", Content = workshop });
        tabs.Items.Add(new TabItem { Header = "Interface", Content = interfaceEditor });
        tabs.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.Source, tabs)) return;
            sidePanel.IsVisible = tabs.SelectedIndex == 0;
            if (tabs.SelectedIndex == 2) problems.Refresh();
            if (tabs.SelectedIndex == 1) contentEditor.Refresh();
            if (tabs.SelectedIndex == 3) settingsEditor.Refresh();
            if (tabs.SelectedIndex == 4) mods.Refresh();
            if (tabs.SelectedIndex == 5) art.Refresh();
            if (tabs.SelectedIndex == 6) workshop.Refresh();
            if (tabs.SelectedIndex == 7) interfaceEditor.Refresh();
        };
        tabs.SelectedIndex = 0;
        var root = new DockPanel();
        root.Children.Add(sidePanel);
        root.Children.Add(tabs);
        Content = root;

        _workspace.ProjectChanged += OnProjectChanged;
        Refresh();
    }

    /// <summary>The scene shown (defaults to the player's scene).</summary>
    public string? SceneId => _sceneId;

    public MapCanvas Canvas => _canvas;

    /// <summary>Selected brush tile type (null = inspect only).</summary>
    public string? Brush
    {
        get => _brush;
        set => SetBrush(value, null);
    }

    private void UseBrush(string type, VisualRef visual) => SetBrush(type, visual);

    private void SetBrush(string? value, VisualRef? visual)
    {
        _brush = value;
        _brushVisual = value is null ? null : visual;
        // The brush is editor state, not content: no undo step (#45).
        if (value is not null) _workspace.ApplyWithoutHistory(Edits.SelectBrush(value, visual));
        _fillScene.IsEnabled = value is not null;
        if (value is not null)
        {
            _selectedLayer = Edits.LayerFor(value);
            SyncLayerSelector();
            Tool = MapTool.Brush;
        }
        else
        {
            Tool = MapTool.Inspect;
        }
        foreach (var swatch in _palette.Children.OfType<ToggleButton>())
        {
            swatch.IsChecked = Equals(swatch.Tag, value);
        }
    }

    /// <summary>Map zoom (1 = the web editor's 28px tiles).</summary>
    public double Zoom => _zoom;

    /// <summary>Last hover readout (tile coords + type).</summary>
    public string HoverText => _hoverInfo.Text ?? "";

    /// <summary>The keyboard editing cursor: arrow keys move it, Enter or Space applies the tool there.</summary>
    public (int X, int Y) CursorTile => _cursorTile;

    /// <summary>Moves the editing cursor by whole tiles (clamped to the scene) and scrolls it into view.</summary>
    public void MoveCursor(int dx, int dy)
    {
        SetCursor((_cursorTile.X + dx, _cursorTile.Y + dy));
        if (_canvas.Geometry is not null)
        {
            _canvas.BringIntoView(_canvas.TileRect(_cursorTile.X, _cursorTile.Y).Inflate(4));
        }
    }

    /// <summary>Selects a scene by id.</summary>
    public void SelectScene(string sceneId)
    {
        _sceneId = sceneId;
        ClearSelection();
        Refresh();
        FitToView();
    }

    /// <summary>Hover readout for a tile (tests and pointer moves share it).</summary>
    public string DescribeTile(int x, int y)
    {
        var scene = CurrentScene();
        if (scene is null || y < 0 || y >= scene.Tiles.Length || x < 0 || x >= scene.Tiles[y].Length)
        {
            return "";
        }

        var tile = scene.Tiles[y][x];
        var parts = new List<string> { $"({x}, {y})", Ui.Capitalize(string.IsNullOrEmpty(tile.Type) ? tile.Background : tile.Type) };
        var layers = new List<string>();
        if (!string.IsNullOrEmpty(tile.Background))
        {
            layers.Add($"bg {tile.Background}");
        }

        if (tile.Overlay.OrNull() is { } overlay)
        {
            layers.Add($"overlay {overlay}");
        }

        if (tile.Object.OrNull() is { } tileObject)
        {
            layers.Add($"object {tileObject}");
        }

        if (layers.Count > 0)
        {
            parts.Add(string.Join(", ", layers));
        }

        if (tile.Crop is not null)
        {
            parts.Add($"crop {tile.Crop.Value.Type} (stage {Ui.Num(tile.Crop.Value.Stage)})");
        }

        if (tile.Node is not null)
        {
            parts.Add($"node {tile.Node.Value.TypeId}");
        }

        if (tile.Machine is not null)
        {
            parts.Add($"machine {tile.Machine.Value.TypeId}");
        }

        if (tile.Item is not null)
        {
            parts.Add($"item {tile.Item.Value.Name}");
        }

        if (tile.Collision)
        {
            parts.Add("blocked");
        }

        if (scene.Transitions.FirstOrDefault(t => t.FromX == x && t.FromY == y) is { } transition)
        {
            parts.Add($"→ {transition.ToSceneId}");
        }

        var npc = _workspace.Current?.Npcs.FirstOrDefault(n => n.SceneId == scene.Id && Math.Floor(n.X) == x && Math.Floor(n.Y) == y);
        if (npc is not null)
        {
            parts.Add($"NPC {npc.Name}");
        }

        foreach (var animal in _workspace.Current?.Animals.Where(a => a.SceneId == scene.Id && Math.Floor(a.X) == x && Math.Floor(a.Y) == y) ?? [])
        {
            parts.Add($"animal {animal.Name}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Paints one tile with the current brush and selected layer through the F# edit
    /// (clears crop and node, updates the selected tile type).
    /// Inside a drag stroke the paints merge into one undo entry.
    /// </summary>
    public void PaintTile(int x, int y)
    {
        var brush = _brush;
        var scene = CurrentScene();
        if (brush is null || scene is null || y < 0 || y >= scene.Tiles.Length || x < 0 || x >= scene.Tiles[y].Length)
        {
            return;
        }

        var edit = Edits.PaintTiles(scene.Id, _selectedLayer, new[] { new ValueTuple<int, int>(x, y) }, brush);
        if (_strokeId is { } strokeId)
        {
            _workspace.ApplyInStroke(strokeId, edit);
        }
        else
        {
            _workspace.Apply(edit);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Stops following the project for good (the error screen's Try Again replaced this editor
    /// with a fresh one) and frees the map renderer.
    /// </summary>
    public void Retire()
    {
        _workspace.ProjectChanged -= OnProjectChanged;
        _canvas.Dispose();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _topLevel?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _topLevel = null;
        EndStroke();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || e.Source is TextBox || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Z && !shift)
        {
            _workspace.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y || (e.Key == Key.Z && shift))
        {
            _workspace.Redo();
            e.Handled = true;
        }
    }

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        // Undo and redo bring back the brush an entry was recorded with; the brush on show
        // stays the one painting (#45), so its art keeps riding along with its type.
        if (e.Kind == ProjectChangeKind.Edited && _brush is not null && _workspace.Current is { } current
            && (current.SelectedTileType != _brush || !Equals(current.SelectedTileVisual.OrNull(), _brushVisual)))
        {
            _workspace.ApplyWithoutHistory(Edits.SelectBrush(_brush, _brushVisual));
            return;
        }

        if (e.Kind == ProjectChangeKind.Opened)
        {
            _sceneId = null;
            _sceneControlsSceneId = null;
            _copiedTiles = null;
            ClearSelection();
            _fitPending = true;
        }

        Refresh();
        if (e.Kind == ProjectChangeKind.Opened)
        {
            FitToView();
        }
    }

    private Scene? CurrentScene()
    {
        var project = _workspace.Current;
        if (project is null)
        {
            return null;
        }

        return project.Scenes.FirstOrDefault(s => s.Id == _sceneId)
            ?? project.Scenes.FirstOrDefault(s => s.Id == project.Player.SceneId)
            ?? project.Scenes.FirstOrDefault();
    }

    private void Refresh()
    {
        var project = _workspace.Current;
        _undo.IsEnabled = _workspace.CanUndo;
        _redo.IsEnabled = _workspace.CanRedo;
        if (project is null)
        {
            _canvas.Geometry = null;
            UpdateCursor(null);
            return;
        }

        if (!ReferenceEquals(project, _projectForCanvas))
        {
            _projectForCanvas = project;
            _canvas.SetProject(project);
        }

        var scene = CurrentScene();
        _sceneId = scene?.Id;
        RefreshSceneSelector(project);
        RefreshInfo(project);
        RefreshEditorPanels(project, scene);
        if (scene is null)
        {
            _canvas.Geometry = null;
            UpdateCursor(null);
            return;
        }

        // Zoom re-lays the map at a whole-pixel tile size (instead of scaling the canvas) so
        // the 1px grid seams stay exactly one pixel at every zoom level. The Rust renderer draws it.
        _canvas.Geometry = new MapGeometry(scene.Id, (int)scene.Width, (int)scene.Height, Math.Max(8, Math.Round(TileSize * _zoom)), Math.Round(12 * _zoom));
        _zoomText.Text = $"{Math.Round(_zoom * 100)}%";
        AutomationProperties.SetName(_canvas, $"Scene editor canvas: {scene.Name}, {Ui.Num(scene.Width)} by {Ui.Num(scene.Height)} tiles. Arrow keys move the editing cursor. Enter or Space applies the current tool.");
        UpdateCursor(scene);
    }

    /// <summary>
    /// Clamps the editing cursor to the scene (after a scene switch or resize) and moves its frame,
    /// the status line and a rectangle preview that is waiting for its second corner.
    /// </summary>
    private void UpdateCursor(Scene? scene)
    {
        if (scene is null || _canvas.Geometry is null)
        {
            _cursor.IsVisible = false;
            _cursorStatus.Text = "";
            return;
        }

        _cursorTile = (Math.Clamp(_cursorTile.X, 0, Math.Max(0, (int)scene.Width - 1)), Math.Clamp(_cursorTile.Y, 0, Math.Max(0, (int)scene.Height - 1)));
        var rect = _canvas.TileRect(_cursorTile.X, _cursorTile.Y);
        _cursor.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        _cursor.Width = rect.Width;
        _cursor.Height = rect.Height;
        _cursor.IsVisible = true;
        _cursorStatus.Text = $"Editing tile column {_cursorTile.X + 1}, row {_cursorTile.Y + 1}. {DescribeTile(_cursorTile.X, _cursorTile.Y)}";
        if (_gestureStart is { } start)
        {
            UpdateGesturePreview(start, _lastPainted ?? start);
        }
    }

    private void SetCursor((int X, int Y) tile)
    {
        _cursorTile = tile;
        if (_gestureStart is not null)
        {
            // A rectangle started from the keyboard stretches to the cursor.
            _lastPainted = tile;
        }

        UpdateCursor(CurrentScene());
    }

    private void RefreshSceneSelector(GameProject project)
    {
        _updatingScenes = true;
        try
        {
            var scenes = project.Scenes.Where(s => !s.Extra.ContainsKey("generated")).ToList();
            var existing = _sceneSelector.Items.OfType<ComboBoxItem>().Select(i => (string?)i.Tag).ToList();
            var ids = scenes.Select(s => (string?)s.Id).ToList();
            if (!existing.SequenceEqual(ids) || _sceneSelector.Items.OfType<ComboBoxItem>().Select(i => i.Content as string).Where((name, i) => name != $"{scenes[i].Name}  ({Ui.Num(scenes[i].Width)}×{Ui.Num(scenes[i].Height)})").Any())
            {
                _sceneSelector.Items.Clear();
                foreach (var scene in scenes)
                {
                    _sceneSelector.Items.Add(new ComboBoxItem { Content = $"{scene.Name}  ({Ui.Num(scene.Width)}×{Ui.Num(scene.Height)})", Tag = scene.Id });
                }
            }

            _sceneSelector.SelectedItem = _sceneSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, _sceneId));
        }
        finally
        {
            _updatingScenes = false;
        }
    }

    private void RefreshInfo(GameProject project)
    {
        var scene = CurrentScene();
        var key = string.Join(
            '\u001f',
            project.Name,
            project.Version,
            project.SchemaVersion,
            project.Scenes.Count(s => !s.Extra.ContainsKey("generated")),
            project.Npcs.Length,
            project.Items.Length,
            project.Quests.Length,
            project.Shops.Length,
            project.Recipes.Length,
            project.Dialogues.Length,
            project.CustomAssets.Length,
            scene is null ? "" : $"{scene.Id}\u001f{scene.Name}\u001f{scene.Width}\u001f{scene.Height}\u001f{scene.Transitions.Length}",
            scene is null ? "" : string.Join(",", project.Npcs.Where(n => n.SceneId == scene.Id).Select(n => n.Name)));
        if (key == _infoKey)
        {
            return;
        }

        _infoKey = key;
        _info.Children.Clear();
        var name = Ui.Wrapped(string.IsNullOrWhiteSpace(project.Name) ? "Untitled Game" : project.Name, "h2");
        name.Name = "ProjectInfoName";
        _info.Children.Add(Ui.VStack(2, name, Ui.Text($"Version {project.Version} · schema v{Ui.Num(project.SchemaVersion)}", "muted", "small")));

        // Three columns keep the eight counts to three rows, so the map tools below stay in view.
        var stats = new UniformGrid { Name = "ProjectStats", Columns = 3 };
        void Stat(string label, int count)
        {
            var value = Ui.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat-value");
            value.Name = $"Stat_{label}";
            var box = new Border { Child = Ui.VStack(0, value, Ui.Text(label, "muted", "small")), Margin = new Thickness(0, 0, 6, 6) }.WithClasses("stat");
            stats.Children.Add(box);
        }

        Stat("Scenes", project.Scenes.Count(s => !s.Extra.ContainsKey("generated")));
        Stat("NPCs", project.Npcs.Length);
        Stat("Items", project.Items.Length);
        Stat("Quests", project.Quests.Length);
        Stat("Shops", project.Shops.Length);
        Stat("Recipes", project.Recipes.Length);
        Stat("Dialogue", project.Dialogues.Length);
        Stat("Assets", project.CustomAssets.Length);
        _info.Children.Add(stats);

        if (scene is not null)
        {
            var npcs = project.Npcs.Where(n => n.SceneId == scene.Id).Select(n => n.Name).ToList();
            var details = $"{scene.Name}: {Ui.Num(scene.Width)}×{Ui.Num(scene.Height)} tiles · {scene.Transitions.Length} exits";
            if (npcs.Count > 0)
            {
                details += $" · {string.Join(", ", npcs)}";
            }

            _info.Children.Add(Ui.Wrapped(details, "muted", "small"));
        }
    }

    private void BuildPalette()
    {
        void Swatch(string? type, string label)
        {
            var chip = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#40000000")),
                Background = type is null ? Brushes.Transparent : new SolidColorBrush(Color.Parse(TileSwatches[type])),
            };
            if (type is null)
            {
                chip.Child = Ui.Icon("IconInfo", 14);
                chip.BorderThickness = default;
            }

            var toggle = new ToggleButton { Tag = type, Content = Ui.HStack(6, chip, Ui.Text(label, "small")), Margin = new Thickness(0, 0, 6, 6), Name = $"Brush_{type ?? "inspect"}" };
            toggle.Classes.Add("swatch");
            AutomationProperties.SetName(toggle, type is null ? "Inspect (no brush)" : $"Brush: {label}");
            toggle.IsChecked = type is null;
            toggle.Click += (_, _) => Brush = type;
            _palette.Children.Add(toggle);
        }

        Swatch(null, "Inspect");
        foreach (var type in TileTypes.All)
        {
            Swatch(type, Ui.Capitalize(type));
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(_canvas);
        if (_canvas.TileAt(point) is not { } tile)
        {
            _hover.IsVisible = _gestureStart is not null;
            return;
        }

        // While a rectangle is open the highlight is its preview, not the hovered tile.
        if (_gestureStart is null)
        {
            var rect = _canvas.TileRect(tile.X, tile.Y);
            _hover.Margin = new Thickness(rect.X, rect.Y, 0, 0);
            _hover.Width = rect.Width;
            _hover.Height = rect.Height;
            _hover.IsVisible = true;
        }

        _hoverInfo.Text = DescribeTile(tile.X, tile.Y);

        if (_gestureStart is { } start && e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed)
        {
            UpdateGesturePreview(start, tile);
        }
        else if (_strokeId is not null && e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed && _lastPainted != tile)
        {
            _lastPainted = tile;
            ApplyStrokeTile(tile.X, tile.Y);
        }
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed || _canvas.TileAt(e.GetPosition(_canvas)) is not { } tile)
        {
            return;
        }

        SetCursor(tile);
        if (BeginToolAt(tile))
        {
            e.Pointer.Capture(_canvas);
            e.Handled = true;
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var tile = _canvas.TileAt(e.GetPosition(_canvas)) ?? _lastPainted;
        if (tile is { } end)
        {
            CompleteGesture(end);
        }
        EndStroke();
    }

    /// <summary>
    /// Keyboard map editing (web GameView): arrow keys move the editing cursor, Enter or Space
    /// applies the current tool there like a click, and Escape drops a rectangle's first corner.
    /// Only the focused map sees these keys, so text boxes keep theirs.
    /// </summary>
    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (CurrentScene() is null || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                MoveCursor(-1, 0);
                break;
            case Key.Right:
                MoveCursor(1, 0);
                break;
            case Key.Up:
                MoveCursor(0, -1);
                break;
            case Key.Down:
                MoveCursor(0, 1);
                break;
            case Key.Enter:
            case Key.Space:
                ApplyToolAtCursor();
                break;
            case Key.Escape when _gestureStart is not null:
                CancelCorner();
                break;
            case var key when (e.KeyModifiers & KeyModifiers.Shift) == 0 && ToolKeys.TryGetValue(key, out var tool):
                Tool = tool;
                _editorMessage.Text = $"{ToolLabel(tool)} tool.";
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Single-key map tools while the map has focus (listed in Help → Keyboard Shortcuts).</summary>
    private static readonly IReadOnlyDictionary<Key, MapTool> ToolKeys = new Dictionary<Key, MapTool>
    {
        [Key.V] = MapTool.Inspect,
        [Key.B] = MapTool.Brush,
        [Key.R] = MapTool.Rectangle,
        [Key.G] = MapTool.Fill,
        [Key.I] = MapTool.Pick,
        [Key.M] = MapTool.Select,
        [Key.E] = MapTool.Erase,
        [Key.X] = MapTool.Block,
        [Key.U] = MapTool.Unblock,
        [Key.D] = MapTool.Door,
        [Key.P] = MapTool.PlayerStart,
    };

    /// <summary>
    /// The current tool at the cursor, exactly as a click there (one undo step). Rectangle and
    /// Select take two presses: the first marks a corner, the second the opposite corner.
    /// </summary>
    private void ApplyToolAtCursor()
    {
        var tile = _cursorTile;
        if (_gestureStart is not null)
        {
            CompleteGesture(tile);
            EndStroke();
            return;
        }

        if (!BeginToolAt(tile))
        {
            return;
        }

        if (_gestureStart is not null)
        {
            UpdateGesturePreview(tile, tile);
            _editorMessage.Text = $"Corner marked at ({tile.X}, {tile.Y}). Move to the opposite corner and press Enter; Escape cancels.";
            return;
        }

        EndStroke();
    }

    private void CancelCorner()
    {
        _gestureStart = null;
        _lastPainted = null;
        _hover.IsVisible = false;
        _editorMessage.Text = Tool == MapTool.Select ? "Selection cancelled." : "Rectangle cancelled.";
    }

    private void EndStroke()
    {
        _gestureStart = null;
        if (_strokeId is not null)
        {
            _strokeId = null;
            _workspace.EndStroke();
        }

        _lastPainted = null;
        _undo.IsEnabled = _workspace.CanUndo;
        _redo.IsEnabled = _workspace.CanRedo;
    }

    private void FitToView()
    {
        var geometry = _canvas.Geometry;
        var viewport = _scroller.Bounds.Size;
        if (geometry is null || viewport.Width <= 0 || viewport.Height <= 0)
        {
            _fitPending = true;
            return;
        }

        _fitPending = false;
        // World size at 100%: 12px padding each side, 28px tiles with 1px seams.
        var width = 24 + (geometry.Width * (TileSize + 1)) - 1;
        var height = 24 + (geometry.Height * (TileSize + 1)) - 1;
        var zoom = Math.Min((viewport.Width - 28) / width, (viewport.Height - 28) / height);
        SetZoom(Math.Floor(zoom * 20) / 20);
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.5, 4);
        _hover.IsVisible = false;
        Refresh();
    }
}
