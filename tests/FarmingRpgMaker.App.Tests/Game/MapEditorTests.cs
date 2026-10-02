using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>Keyboard and screen-reader map editing, transition duplicates, stats, size calculator and workshop links.</summary>
public sealed class MapEditorTests
{
    private static void Key(GameTestHost host, Key key, PhysicalKey physical, string? symbol = null, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(host.Window, key, modifiers, physical, symbol);
        Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(host.Window, key, modifiers, physical, symbol);
        Pump();
    }

    private static void Right(GameTestHost host, int times = 1)
    {
        for (var i = 0; i < times; i++) Key(host, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight);
    }

    private static void Down(GameTestHost host, int times = 1)
    {
        for (var i = 0; i < times; i++) Key(host, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown);
    }

    private static void Enter(GameTestHost host) => Key(host, Avalonia.Input.Key.Enter, PhysicalKey.Enter, "\r");

    /// <summary>Focuses the map and moves the editing cursor to (x, y).</summary>
    private static void CursorTo(GameTestHost host, int x, int y)
    {
        var edit = host.Surface.EditView;
        Assert.True(edit.Canvas.Focus());
        Pump();
        edit.MoveCursor(x - edit.CursorTile.X, y - edit.CursorTile.Y);
        Assert.Equal((x, y), edit.CursorTile);
    }

    private static void Press(GameTestHost host, string name) =>
        FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static FarmEngine.Schemas.Scene Scene(GameTestHost host, string id) => host.Workspace.Current!.Scenes.First(s => s.Id == id);

    [AvaloniaFact]
    public void ArrowKeysMoveTheEditingCursor_ClampedToTheScene()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var scene = Scene(host, edit.SceneId!);
        Assert.True(edit.Canvas.Focusable);
        Assert.NotNull(edit.Canvas.FocusAdorner);
        Assert.True(edit.Canvas.Focus());
        Pump();
        Assert.True(edit.Canvas.IsFocused);
        Assert.Equal((0, 0), edit.CursorTile);

        Right(host, 2);
        Down(host);
        Assert.Equal((2, 1), edit.CursorTile);
        var cursor = FindByName<Border>(host.Window, "EditCursor");
        var rect = edit.Canvas.TileRect(2, 1);
        Assert.True(cursor.IsVisible);
        Assert.Equal(new Thickness(rect.X, rect.Y, 0, 0), cursor.Margin);
        Assert.Equal(rect.Width, cursor.Width);

        for (var i = 0; i < 5; i++) Key(host, Avalonia.Input.Key.Left, PhysicalKey.ArrowLeft);
        for (var i = 0; i < 3; i++) Key(host, Avalonia.Input.Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal((0, 0), edit.CursorTile);
        edit.MoveCursor(1000, 1000);
        Assert.Equal(((int)scene.Width - 1, (int)scene.Height - 1), edit.CursorTile);
        Right(host);
        Assert.Equal(((int)scene.Width - 1, (int)scene.Height - 1), edit.CursorTile);

        // A text box keeps its arrow keys.
        var name = FindByName<TextBox>(host.Window, "SceneName");
        name.Focus();
        Pump();
        Key(host, Avalonia.Input.Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(((int)scene.Width - 1, (int)scene.Height - 1), edit.CursorTile);

        // A smaller scene pulls the cursor back inside it.
        name.Text = "Shed";
        FindByName<TextBox>(host.Window, "SceneWidth").Text = "8";
        FindByName<TextBox>(host.Window, "SceneHeight").Text = "6";
        Press(host, "AddSceneButton");
        Pump();
        Assert.Equal("Shed", Scene(host, edit.SceneId!).Name);
        Assert.Equal((7, 5), edit.CursorTile);

        // A click moves the cursor to the clicked tile.
        var point = edit.Canvas.TranslatePoint(edit.Canvas.TileRect(3, 2).Center, host.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(host.Window, point, MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(host.Window, point, MouseButton.Left);
        Pump();
        Assert.Equal((3, 2), edit.CursorTile);
    }

    [AvaloniaFact]
    public void EnterAndSpacePaintAtTheCursor_OneUndoStepEach()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var sceneId = edit.SceneId!;
        string TileType(int x, int y) => Scene(host, sceneId).Tiles[y][x].Type;
        Assert.Equal("grass", TileType(2, 2));
        Assert.Equal("grass", TileType(3, 2));

        edit.Brush = "water";
        Assert.True(edit.Canvas.Focus());
        Pump();
        Right(host, 2);
        Down(host, 2);
        Enter(host);
        Assert.Equal("water", TileType(2, 2));
        Right(host);
        Key(host, Avalonia.Input.Key.Space, PhysicalKey.Space, " ");
        Assert.Equal("water", TileType(3, 2));
        Assert.Contains("Water", FindByName<TextBlock>(host.Window, "EditCursorStatus").Text, StringComparison.Ordinal);

        // Ctrl+Z still undoes while the map has focus: one press per application.
        Key(host, Avalonia.Input.Key.Z, PhysicalKey.Z, "z", RawInputModifiers.Control);
        Assert.Equal("grass", TileType(3, 2));
        Assert.Equal("water", TileType(2, 2));
        Key(host, Avalonia.Input.Key.Z, PhysicalKey.Z, "z", RawInputModifiers.Control);
        Assert.Equal("grass", TileType(2, 2));
        Assert.Equal((3, 2), edit.CursorTile);
    }

    [AvaloniaFact]
    public void RectangleAndSelectTakeTwoPresses_AndEscapeCancelsTheFirstCorner()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var sceneId = edit.SceneId!;
        string TileType(int x, int y) => Scene(host, sceneId).Tiles[y][x].Type;
        var before = TileType(4, 4);
        var other = TileType(5, 5);
        var message = FindByName<TextBlock>(host.Window, "MapEditorMessage");

        edit.Brush = "water";
        edit.Tool = MapTool.Rectangle;
        CursorTo(host, 4, 4);
        Enter(host);
        Assert.Equal(before, TileType(4, 4));
        Assert.Contains("Corner marked at (4, 4)", message.Text, StringComparison.Ordinal);
        Assert.True(FindByName<Border>(host.Window, "HoverHighlight").IsVisible);
        Right(host);
        Down(host);
        Enter(host);
        Assert.Equal("water", TileType(4, 4));
        Assert.Equal("water", TileType(5, 4));
        Assert.Equal("water", TileType(5, 5));
        host.Workspace.Undo();
        Assert.Equal(before, TileType(4, 4));
        Assert.Equal(other, TileType(5, 5));

        // Escape drops the first corner; the next press starts over.
        CursorTo(host, 4, 4);
        Enter(host);
        Key(host, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Assert.Equal("Rectangle cancelled.", message.Text);
        Assert.False(FindByName<Border>(host.Window, "HoverHighlight").IsVisible);
        Right(host);
        Enter(host);
        Assert.Equal(before, TileType(4, 4));
        Assert.Contains("Corner marked at (5, 4)", message.Text, StringComparison.Ordinal);

        // Another tool drops the corner too.
        edit.Tool = MapTool.Select;
        CursorTo(host, 4, 4);
        Enter(host);
        Assert.Null(edit.Selection);
        CursorTo(host, 5, 5);
        Enter(host);
        Assert.Equal((4, 4, 5, 5), edit.Selection);
        Assert.Equal(before, TileType(4, 4));
    }

    [AvaloniaFact]
    public void TheMapAndItsControls_HaveAccessibleNames()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var scene = Scene(host, edit.SceneId!);
        var expected = $"Scene editor canvas: {scene.Name}, {scene.Width} by {scene.Height} tiles. Arrow keys move the editing cursor. Enter or Space applies the current tool.";
        Assert.Equal(expected, AutomationProperties.GetName(edit.Canvas));
        var peer = ControlAutomationPeer.CreatePeerForElement(edit.Canvas);
        Assert.Equal(expected, peer.GetName());
        Assert.True(peer.IsControlElement());
        Assert.True(peer.IsKeyboardFocusable());

        var status = FindByName<TextBlock>(host.Window, "EditCursorStatus");
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        Assert.StartsWith("Editing tile column 1, row 1. (0, 0) · Wall", status.Text, StringComparison.Ordinal);
        Assert.True(edit.Canvas.Focus());
        Pump();
        Right(host, 2);
        Down(host, 3);
        Assert.Equal($"Editing tile column 3, row 4. {edit.DescribeTile(2, 3)}", status.Text);

        Assert.Equal("Zoom out", AutomationProperties.GetName(FindByName<Button>(host.Window, "ZoomOutButton")));
        Assert.Equal("Zoom in", AutomationProperties.GetName(FindByName<Button>(host.Window, "ZoomInButton")));
        Assert.Equal("Brush: Grass", AutomationProperties.GetName(FindByName<ToggleButton>(host.Window, "Brush_grass")));
        Assert.Equal("Scene", AutomationProperties.GetName(FindByName<ComboBox>(host.Window, "SceneSelector")));
        Assert.Equal("Layer", AutomationProperties.GetName(FindByName<ComboBox>(host.Window, "LayerSelector")));
        Assert.Equal("Scene name", AutomationProperties.GetName(FindByName<TextBox>(host.Window, "SceneName")));
        Assert.Equal("Scene width", AutomationProperties.GetName(FindByName<TextBox>(host.Window, "SceneWidth")));
        Assert.Equal("Scene height", AutomationProperties.GetName(FindByName<TextBox>(host.Window, "SceneHeight")));
        Assert.Equal("New scene tile", AutomationProperties.GetName(FindByName<ComboBox>(host.Window, "NewSceneTile")));
        Assert.Equal("To scene", AutomationProperties.GetName(FindByName<ComboBox>(host.Window, "DoorDestination")));

        // Every button, box and picker in the toolbar and side panel has a name for screen readers.
        var roots = new Control[] { FindByName<Border>(host.Window, "MapToolbar"), FindByName<Border>(host.Window, "ProjectInfoPanel") };
        var controls = roots.SelectMany(root => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root))
            .OfType<Control>()
            .Where(c => c is Button or ToggleButton or TextBox or ComboBox)
            .ToList();
        Assert.True(controls.Count > 30, $"{controls.Count} controls");
        Assert.All(controls, control =>
        {
            var name = control is TextBox or ComboBox
                ? AutomationProperties.GetName(control)
                : ControlAutomationPeer.CreatePeerForElement(control).GetName();
            Assert.False(string.IsNullOrWhiteSpace(name), $"{control.GetType().Name} {control.Name} has no accessible name");
        });
    }

    [AvaloniaFact]
    public void DuplicateTransition_PrefillsTheDoorForm_AndSaveDoorCreatesIt()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var farmId = edit.SceneId!;
        var farm = Scene(host, farmId);
        var pond = Defaults.NewScene(host.Workspace.Current!, "Pond", 6, 5, "water");
        host.Workspace.Apply(Edits.AddScene(pond));
        host.Workspace.Apply(Edits.SetTransition(farmId, Defaults.NewTransition(pond.Id).WithFromX(3).WithFromY(4).WithToX(1).WithToY(2)));
        Pump();
        Assert.Equal("(3, 4) → Pond (1, 2)", FindByName<Button>(host.Window, "Transition_0").Content);
        var duplicate = FindByName<Button>(host.Window, "DuplicateTransition_0");
        Assert.Equal("Duplicate transition (3, 4) → Pond (1, 2)", AutomationProperties.GetName(duplicate));

        duplicate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(MapTool.Door, edit.Tool);
        Assert.Contains("(4, 4)", FindByName<TextBlock>(host.Window, "DoorFrom").Text, StringComparison.Ordinal);
        Assert.Equal(pond.Id, (FindByName<ComboBox>(host.Window, "DoorDestination").SelectedItem as ComboBoxItem)?.Tag);
        Assert.Equal("1", FindByName<TextBox>(host.Window, "DoorX").Text);
        Assert.Equal("2", FindByName<TextBox>(host.Window, "DoorY").Text);
        Assert.Contains("Adjust the departure tile (click the map)", FindByName<TextBlock>(host.Window, "MapEditorMessage").Text, StringComparison.Ordinal);
        Assert.Equal((4, 4), edit.CursorTile);

        Press(host, "SaveDoorButton");
        Pump();
        var transitions = Scene(host, farmId).Transitions;
        Assert.Equal(2, transitions.Length);
        Assert.Contains(transitions, t => t.FromX == 4 && t.FromY == 4 && t.ToSceneId == pond.Id && t.ToX == 1 && t.ToY == 2);
        Assert.Empty(Scene(host, pond.Id).Transitions);
        host.Workspace.Undo();
        Assert.Single(Scene(host, farmId).Transitions);
        host.Workspace.Redo();
        Pump();

        // (4, 4) has a door now: the copy of (3, 4) departs from the next free tile.
        Press(host, "DuplicateTransition_0");
        Assert.Contains("(5, 4)", FindByName<TextBlock>(host.Window, "DoorFrom").Text, StringComparison.Ordinal);

        // At the right edge the copy wraps onto the next row.
        var edge = (int)farm.Width - 1;
        host.Workspace.Apply(Edits.SetTransition(farmId, Defaults.NewTransition(pond.Id).WithFromX(edge).WithFromY(2)));
        Pump();
        var index = Scene(host, farmId).Transitions.ToList().FindIndex(t => t.FromX == edge && t.FromY == 2);
        Press(host, $"DuplicateTransition_{index}");
        Assert.Contains("(0, 3)", FindByName<TextBlock>(host.Window, "DoorFrom").Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ProjectStats_CountDialogueAndAssets()
    {
        using var host = new GameTestHost();
        var project = host.Workspace.Current!;
        Assert.Equal(project.Dialogues.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), FindByName<TextBlock>(host.Window, "Stat_Dialogue").Text);
        Assert.Equal(project.CustomAssets.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), FindByName<TextBlock>(host.Window, "Stat_Assets").Text);

        host.Workspace.Apply(Edits.UpsertDialogue(Defaults.NewDialogue(project, project.Npcs[0].Id)));
        Pump();
        Assert.Equal((project.Dialogues.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FindByName<TextBlock>(host.Window, "Stat_Dialogue").Text);
        var stats = AllVisibleText(FindByName<Avalonia.Controls.Primitives.UniformGrid>(host.Window, "ProjectStats"));
        Assert.Contains("Dialogue", stats, StringComparison.Ordinal);
        Assert.Contains("Assets", stats, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void SceneSizeCalculator_FollowsTheTypedSize()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var scene = Scene(host, edit.SceneId!);
        var info = FindByName<TextBlock>(host.Window, "SceneSizeInfo");
        var width = FindByName<TextBox>(host.Window, "SceneWidth");
        var height = FindByName<TextBox>(host.Window, "SceneHeight");
        Assert.Equal($"Total tiles: {scene.Width * scene.Height} · Aspect ratio: {(scene.Width / scene.Height).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}", info.Text);

        width.Text = "8";
        height.Text = "6";
        Pump();
        Assert.StartsWith("Total tiles: 48 · Aspect ratio: 1.33\nResize removes the tiles outside 8×6.", info.Text, StringComparison.Ordinal);

        height.Text = "abc";
        Pump();
        Assert.Equal("Width and height must be whole numbers from 1 to 256.", info.Text);
        height.Text = "300";
        Pump();
        Assert.Equal("Width and height must be whole numbers from 1 to 256.", info.Text);

        width.Text = "200";
        height.Text = "100";
        Pump();
        Assert.Equal("Total tiles: 20000 · Aspect ratio: 2.00", info.Text);
    }

    [AvaloniaFact]
    public void WorkshopBuildLinks_OpenTheirEditors()
    {
        using var host = new GameTestHost();
        var tabs = FindByName<TabControl>(host.Window, "EditorTabs");
        var category = FindByName<ComboBox>(host.Window, "ContentCategory");
        void Open(string tab)
        {
            tabs.SelectedIndex = 6;
            Pump();
            Press(host, $"WorkshopLink_{tab}");
            Pump();
        }

        foreach (var (tab, name) in new[]
                 {
                     ("crops", "Crops"), ("wildlife", "Animal species"), ("items", "Items"), ("craft", "Recipes"),
                     ("npcs", "NPCs"), ("quests", "Quests"), ("events", "Events"), ("actions", "Actions"),
                 })
        {
            Open(tab);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal(name, (category.SelectedItem as ComboBoxItem)?.Content);
        }

        Open("assets");
        Assert.Equal(5, tabs.SelectedIndex);
        Open("problems");
        Assert.Equal(2, tabs.SelectedIndex);
        Open("scenes");
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.Equal(11, FindByName<WrapPanel>(host.Window, "WorkshopLinks").Children.Count);
    }

    [AvaloniaFact]
    public void PickingABrushIsNotAnUndoStep_CtrlZUndoesThePaint()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var sceneId = edit.SceneId!;
        var before = host.Workspace.Document!.Past.Length;
        edit.Brush = "soil";
        Assert.Equal(before, host.Workspace.Document!.Past.Length);
        edit.PaintTile(3, 3);
        Assert.Equal("soil", Scene(host, sceneId).Tiles[3][3].Type);
        edit.Brush = "water";
        Assert.Equal(before + 1, host.Workspace.Document!.Past.Length);

        // Ctrl+Z right after picking the next brush undoes the paint, and the brush stays.
        Key(host, Avalonia.Input.Key.Z, PhysicalKey.Z, "z", RawInputModifiers.Control);
        Assert.NotEqual("soil", Scene(host, sceneId).Tiles[3][3].Type);
        Assert.Equal("water", edit.Brush);
        Assert.Equal("water", host.Workspace.Current!.SelectedTileType);
        Assert.True(FindByName<ToggleButton>(host.Window, "Brush_water").IsChecked);
    }

    [AvaloniaFact]
    public void ShrinkingAScene_ListsAndMovesWhatStoodOnTheCutTiles()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var sceneId = edit.SceneId!;
        host.Workspace.Apply(Edits.SetPlayerStart(sceneId, 14, 10));
        var info = FindByName<TextBlock>(host.Window, "SceneSizeInfo");
        FindByName<TextBox>(host.Window, "SceneWidth").Text = "6";
        FindByName<TextBox>(host.Window, "SceneHeight").Text = "5";
        Pump();
        Assert.Contains("the player start moves inside", info.Text, StringComparison.Ordinal);

        Press(host, "ResizeSceneButton");
        Pump();
        var project = host.Workspace.Current!;
        Assert.Equal((5.0, 4.0), (project.Player.X, project.Player.Y));
        var outside = Problems.Collect(project).Where(p => p.Code.Contains("utOfBounds", StringComparison.Ordinal)).ToList();
        Assert.Empty(outside);
        Assert.Contains("the player start moves inside", AllVisibleText(host.Window), StringComparison.Ordinal);
    }
}
