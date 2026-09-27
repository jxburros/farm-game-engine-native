using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FarmEngine.Authoring;
using FarmEngine.Core;
using FarmEngine.Rendering;
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

    public const string PortingNotice = "Map and content editing are available. Art, workshop and mod panels are still being ported.";

    private readonly ProjectWorkspace _workspace;
    private readonly GameCanvas _canvas = new() { Name = "EditCanvas", ZoomMode = CanvasZoomMode.Fixed, Cursor = new Cursor(StandardCursorType.Hand) };
    private readonly ScrollViewer _scroller;
    private readonly Border _hover = new() { Name = "HoverHighlight", BorderThickness = new Thickness(2), IsHitTestVisible = false, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ComboBox _sceneSelector = new() { Name = "SceneSelector", MinWidth = 200 };
    private readonly TextBlock _hoverInfo = new() { Name = "HoverInfo", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _zoomText = new() { Name = "ZoomText", VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _info = new() { Spacing = 12 };
    private readonly WrapPanel _palette = new() { Name = "TilePalette" };
    private readonly Button _undo;
    private readonly Button _redo;
    private GameProject? _projectForContent;
    private GameContent? _content;
    private string? _sceneId;
    private string? _brush;
    private string? _strokeId;
    private int _strokeCount;
    private (int X, int Y)? _lastPainted;
    private bool _fitPending = true;
    private double _zoom = 1;
    private bool _updatingScenes;
    private TopLevel? _topLevel;

    public EditModeView(ProjectWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Name = "EditModeView";
        _hover.BorderBrush = (IBrush?)Application.Current?.FindResource("FarmAccentBrush") ?? Brushes.Gold;
        _hover.Background = new SolidColorBrush(Color.Parse("#33FFFFFF"));

        // Canvas + hover highlight in a scroll viewer.
        var stage = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
        stage.Children.Add(_canvas);
        stage.Children.Add(_hover);
        _scroller = new ScrollViewer
        {
            Name = "EditScroller",
            Content = stage,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var frame = new Border { Child = _scroller, Background = new SolidColorBrush(Color.Parse("#4DE4DDCF")) }.WithClasses("game-frame");
        frame.Background = new SolidColorBrush(Color.Parse("#66E4DDCF"));

        _canvas.PointerMoved += OnCanvasPointerMoved;
        _canvas.PointerExited += (_, _) =>
        {
            _hover.IsVisible = false;
            _hoverInfo.Text = "Hover a tile to inspect it.";
        };
        _canvas.PointerPressed += OnCanvasPointerPressed;
        _canvas.PointerReleased += OnCanvasPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => EndStroke();
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
        var zoomOut = Ui.Button("−", () => SetZoom(_zoom - 0.25), "tool");
        zoomOut.Name = "ZoomOutButton";
        var zoomIn = Ui.Button("+", () => SetZoom(_zoom + 0.25), "tool");
        zoomIn.Name = "ZoomInButton";
        var fit = Ui.Button("Fit", FitToView, "tool");
        fit.Name = "ZoomFitButton";
        var toolbarLeft = Ui.HStack(8, Ui.Icon("IconMap", 18), Ui.Text("Scene", "hud-label"), _sceneSelector);
        var toolbarRight = Ui.HStack(6, zoomOut, _zoomText, zoomIn, fit);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(toolbarLeft);
        var hoverBox = new Border { Child = _hoverInfo, Margin = new Thickness(16, 0) };
        Grid.SetColumn(hoverBox, 1);
        toolbar.Children.Add(hoverBox);
        Grid.SetColumn(toolbarRight, 2);
        toolbar.Children.Add(toolbarRight);
        var toolbarBorder = new Border { Child = toolbar, Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 12) }.WithClasses("hud");

        var center = new DockPanel();
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        center.Children.Add(toolbarBorder);
        center.Children.Add(frame);

        // Side panel: map tools, scene and transition controls, history.
        _undo = Ui.Button(Ui.IconLabel("IconUndo", "Undo"), () => _workspace.Undo(), "tool");
        _undo.Name = "UndoButton";
        ToolTip.SetTip(_undo, "Undo (Ctrl+Z)");
        _redo = Ui.Button(Ui.IconLabel("IconRedo", "Redo"), () => _workspace.Redo(), "tool");
        _redo.Name = "RedoButton";
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
            if (problem.TargetKind == "pack" && problem.TargetId is { } packId)
            {
                tabs.SelectedIndex = 4;
                mods.SelectPack(packId);
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
        tabs.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.Source, tabs)) return;
            sidePanel.IsVisible = tabs.SelectedIndex == 0;
            if (tabs.SelectedIndex == 2) problems.Refresh();
            if (tabs.SelectedIndex == 1) contentEditor.Refresh();
            if (tabs.SelectedIndex == 3) settingsEditor.Refresh();
            if (tabs.SelectedIndex == 4) mods.Refresh();
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

    public GameCanvas Canvas => _canvas;

    /// <summary>Selected brush tile type (null = inspect only).</summary>
    public string? Brush
    {
        get => _brush;
        set
        {
            _brush = value;
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
    }

    /// <summary>Map zoom (1 = the web editor's 28px tiles).</summary>
    public double Zoom => _zoom;

    /// <summary>Last hover readout (tile coords + type).</summary>
    public string HoverText => _hoverInfo.Text ?? "";

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
        if (scene is null || y < 0 || y >= scene.Tiles.Count || x < 0 || x >= scene.Tiles[y].Count)
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

        if (tile.Overlay is not null)
        {
            layers.Add($"overlay {tile.Overlay}");
        }

        if (tile.Object is not null)
        {
            layers.Add($"object {tile.Object}");
        }

        if (layers.Count > 0)
        {
            parts.Add(string.Join(", ", layers));
        }

        if (tile.Crop is not null)
        {
            parts.Add($"crop {tile.Crop.Type} (stage {Ui.Num(tile.Crop.Stage)})");
        }

        if (tile.Node is not null)
        {
            parts.Add($"node {tile.Node.TypeId}");
        }

        if (tile.Machine is not null)
        {
            parts.Add($"machine {tile.Machine.TypeId}");
        }

        if (tile.Item is not null)
        {
            parts.Add($"item {tile.Item.Name}");
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
        if (brush is null || scene is null || y < 0 || y >= scene.Tiles.Count || x < 0 || x >= scene.Tiles[y].Count)
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
            _canvas.Snapshot = null;
            return;
        }

        if (!ReferenceEquals(project, _projectForContent))
        {
            _projectForContent = project;
            _content = EngineState.CreateContentFromProject(project);
        }

        var scene = CurrentScene();
        _sceneId = scene?.Id;
        RefreshSceneSelector(project);
        RefreshInfo(project);
        RefreshEditorPanels(project, scene);
        if (scene is null || _content is null)
        {
            _canvas.Snapshot = null;
            return;
        }

        // Zoom re-lays the map at a whole-pixel tile size (instead of scaling the canvas) so
        // the 1px grid seams stay exactly one pixel at every zoom level.
        var snapshot = ShellSnapshot.BuildEditorSnapshot(project, _content, scene, Math.Max(8, Math.Round(TileSize * _zoom)), Math.Round(12 * _zoom));
        _canvas.Snapshot = Graphics.ApplyGraphics(snapshot, GraphicsSource.FromProject(project), scene, 0, false);
        _zoomText.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void RefreshSceneSelector(GameProject project)
    {
        _updatingScenes = true;
        try
        {
            var scenes = project.Scenes.Where(s => s.Extra?.ContainsKey("generated") != true).ToList();
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
        _info.Children.Clear();
        var name = Ui.Wrapped(string.IsNullOrWhiteSpace(project.Name) ? "Untitled Game" : project.Name, "h2");
        name.Name = "ProjectInfoName";
        _info.Children.Add(Ui.VStack(2, name, Ui.Text($"Version {project.Version} · schema v{Ui.Num(project.SchemaVersion)}", "muted", "small")));

        var stats = new UniformGrid { Name = "ProjectStats", Columns = 2 };
        void Stat(string label, int count)
        {
            var value = Ui.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat-value");
            var box = new Border { Child = Ui.VStack(0, value, Ui.Text(label, "muted", "small")), Margin = new Thickness(0, 0, 6, 6) }.WithClasses("stat");
            stats.Children.Add(box);
        }

        Stat("Scenes", project.Scenes.Count(s => s.Extra?.ContainsKey("generated") != true));
        Stat("NPCs", project.Npcs.Count);
        Stat("Items", project.Items.Count);
        Stat("Quests", project.Quests.Count);
        Stat("Shops", project.Shops.Count);
        Stat("Recipes", project.Recipes.Count);
        _info.Children.Add(stats);

        var scene = CurrentScene();
        if (scene is not null)
        {
            var npcs = project.Npcs.Where(n => n.SceneId == scene.Id).Select(n => n.Name).ToList();
            var details = $"{scene.Name}: {Ui.Num(scene.Width)}×{Ui.Num(scene.Height)} tiles · {scene.Transitions.Count} exits";
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
                Background = type is null ? Brushes.Transparent : new SolidColorBrush(PlayOverlays.ToColor(Canvas2d.TileColors[type])),
            };
            if (type is null)
            {
                chip.Child = Ui.Icon("IconInfo", 14);
                chip.BorderThickness = default;
            }

            var toggle = new ToggleButton { Tag = type, Content = Ui.HStack(6, chip, Ui.Text(label, "small")), Margin = new Thickness(0, 0, 6, 6), Name = $"Brush_{type ?? "inspect"}" };
            toggle.Classes.Add("swatch");
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
            _hover.IsVisible = false;
            return;
        }

        var rect = _canvas.TileRect(tile.X, tile.Y);
        _hover.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        _hover.Width = rect.Width;
        _hover.Height = rect.Height;
        _hover.IsVisible = true;
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
        var snapshot = _canvas.Snapshot;
        var viewport = _scroller.Bounds.Size;
        if (snapshot is null || viewport.Width <= 0 || viewport.Height <= 0)
        {
            _fitPending = true;
            return;
        }

        _fitPending = false;
        // World size at 100%: 12px padding each side, 28px tiles with 1px seams.
        var width = 24 + (snapshot.Width * (TileSize + 1)) - 1;
        var height = 24 + (snapshot.Height * (TileSize + 1)) - 1;
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
