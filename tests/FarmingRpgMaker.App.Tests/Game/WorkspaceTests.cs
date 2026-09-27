using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmEngine.Core;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Projects;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

public sealed class WorkspaceTests
{
    [AvaloniaFact]
    public void FirstLaunch_CreatesAndOpensTheStarterFarm_AndRemembersIt()
    {
        using var host = new GameTestHost();

        var project = host.Workspace.Current!;
        Assert.Equal("Starter Farm", project.Name);
        Assert.Equal("Starter Farm", host.ViewModel.ProjectName);
        Assert.True(host.Workspace.Store.Exists(project.Id));
        Assert.Equal(project.Id, host.Workspace.Settings.Load().LastProjectId);

        // A second launch over the same data reopens it instead of creating another.
        var again = new ProjectWorkspace(host.Workspace.Store, host.Workspace.Settings);
        Assert.Empty(again.OpenStartupProject());
        Assert.Equal(project.Id, again.Current!.Id);
        Assert.Single(host.Workspace.Store.List());
    }

    [AvaloniaFact]
    public void Playtest_ExitWithoutKeep_RestoresTheSnapshot()
    {
        using var host = new GameTestHost();
        var before = ProjectStore.ToJson(host.Workspace.Current!);

        host.EnterPlay();
        host.Play.Session.RunCommand(new SleepCommand());
        host.Play.Session.DebugMutate((s, _) => s with { Player = s.Player with { Money = 999 } });
        host.ViewModel.Mode = EditorMode.Edit;
        Pump();

        Assert.Null(host.Surface.PlayView);
        Assert.Equal(before, ProjectStore.ToJson(host.Workspace.Current!));
        Assert.Equal(before, ProjectStore.ToJson(host.Workspace.Store.Load(host.Workspace.Current!.Id).Project!));
        Assert.Contains("discarded", host.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Playtest_KeepChanges_WritesTheFinalStateBack()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        host.Play.Session.RunCommand(new SleepCommand());
        Click(host.Window, FindByName<Avalonia.Controls.Primitives.ToggleButton>(host.Window, "KeepChangesButton"));
        Assert.True(host.Play.KeepChanges);
        host.ViewModel.Mode = EditorMode.Edit;
        Pump();

        var project = host.Workspace.Current!;
        Assert.Equal(2, project.CurrentDay);
        Assert.Equal(2, host.Workspace.Store.Load(project.Id).Project!.CurrentDay);
        Assert.Contains("kept", host.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Playtest_Restart_RebuildsFromTheSnapshot()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        host.Play.Session.RunCommand(new SleepCommand());
        Assert.Equal(2, host.Play.Session.State.Clock.Day);

        Click(host.Window, FindByName<Button>(host.Window, "RestartButton"));

        Assert.Equal(1, host.Play.Session.State.Clock.Day);
        Assert.Contains("Playtest restarted", host.Play.Toasts.History.Select(t => t.Text));
    }

    [AvaloniaFact]
    public void EditMode_ShowsSceneInfoAndInspectsTiles()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;

        Assert.Contains(EditModeView.PortingNotice, AllVisibleText(host.Window), StringComparison.Ordinal);
        Assert.Equal("Starter Farm", FindByName<TextBlock>(host.Window, "ProjectInfoName").Text);
        var selector = FindByName<ComboBox>(host.Window, "SceneSelector");
        Assert.True(selector.ItemCount >= 1);
        Assert.NotNull(edit.Canvas.Snapshot);
        Assert.True(edit.Canvas.Snapshot!.GridOverlay);

        var description = edit.DescribeTile(0, 0);
        Assert.StartsWith("(0, 0) · Wall", description, StringComparison.Ordinal);
        Assert.Contains("NPC Old Farmer", edit.DescribeTile(3, 6), StringComparison.Ordinal);

        // Pointer hover over the canvas reports the tile under it.
        var rect = edit.Canvas.TileRect(2, 3);
        var point = edit.Canvas.TranslatePoint(rect.Center, host.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(host.Window, point);
        Pump();
        Assert.StartsWith("(2, 3)", edit.HoverText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void EditMode_PaintsTiles_WithUndoRedo()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var sceneId = edit.SceneId!;
        string TileType(int x, int y) => host.Workspace.Current!.Scenes.First(s => s.Id == sceneId).Tiles[y][x].Type;
        Assert.Equal("grass", TileType(2, 2));

        edit.Brush = "water";
        var rect = edit.Canvas.TileRect(2, 2);
        var point = edit.Canvas.TranslatePoint(rect.Center, host.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(host.Window, point, Avalonia.Input.MouseButton.Left);
        var next = edit.Canvas.TranslatePoint(edit.Canvas.TileRect(3, 2).Center, host.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(host.Window, next, Avalonia.Input.RawInputModifiers.LeftMouseButton);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(host.Window, next, Avalonia.Input.MouseButton.Left);
        Pump();

        Assert.Equal("water", TileType(2, 2));
        Assert.Equal("water", TileType(3, 2));
        // Saved (autosave delay is zero in tests).
        Assert.Equal("water", host.Workspace.Store.Load(host.Workspace.Current!.Id).Project!.Scenes.First(s => s.Id == sceneId).Tiles[2][2].Type);

        // The drag stroke is one undo step.
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(host.Window, Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Z, "z");
        Pump();
        Assert.Equal("grass", TileType(2, 2));
        Assert.Equal("grass", TileType(3, 2));
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(host.Window, Avalonia.Input.Key.Y, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Y, "y");
        Pump();
        Assert.Equal("water", TileType(3, 2));
    }

    [AvaloniaFact]
    public void EditMode_RectangleSelectionPasteAndLayerErase_AreUndoable()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var sceneId = edit.SceneId!;
        FarmEngine.Schemas.Tile TileAt(int x, int y) => host.Workspace.Current!.Scenes.First(s => s.Id == sceneId).Tiles[y][x];
        var before = TileAt(4, 4).Type;
        var other = TileAt(5, 5).Type;

        void Drag(int x0, int y0, int x1, int y1)
        {
            var from = edit.Canvas.TranslatePoint(edit.Canvas.TileRect(x0, y0).Center, host.Window)!.Value;
            var to = edit.Canvas.TranslatePoint(edit.Canvas.TileRect(x1, y1).Center, host.Window)!.Value;
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(host.Window, from, MouseButton.Left);
            Avalonia.Headless.HeadlessWindowExtensions.MouseMove(host.Window, to, RawInputModifiers.LeftMouseButton);
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(host.Window, to, MouseButton.Left);
            Pump();
        }

        edit.Brush = "water";
        edit.Tool = MapTool.Rectangle;
        Drag(4, 4, 5, 5);
        Assert.Equal("water", TileAt(4, 4).Type);
        Assert.Equal("water", TileAt(5, 5).Type);
        host.Workspace.Undo();
        Assert.Equal(before, TileAt(4, 4).Type);
        Assert.Equal(other, TileAt(5, 5).Type);
        host.Workspace.Redo();

        edit.Tool = MapTool.Select;
        Drag(4, 4, 5, 5);
        Assert.Equal((4, 4, 5, 5), edit.Selection);
        edit.CopySelection();
        edit.PasteAt(10, 8);
        Assert.Equal("water", TileAt(10, 8).Type);
        Assert.Equal("water", TileAt(11, 9).Type);
        Assert.Equal(10, TileAt(10, 8).X);
        host.Workspace.Undo();
        Assert.NotEqual("water", TileAt(10, 8).Type);

        edit.Layer = Edits.LayerFor("water");
        edit.Tool = MapTool.Erase;
        Drag(4, 4, 5, 4);
        Assert.Equal("grass", TileAt(4, 4).Type);
        Assert.Equal("grass", TileAt(5, 4).Type);
        host.Workspace.Undo();
        Assert.Equal("water", TileAt(4, 4).Type);
        Assert.Equal("water", TileAt(5, 4).Type);
    }

    [AvaloniaFact]
    public void EditMode_ManagesScenesAndBidirectionalDoors()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var farmId = edit.SceneId!;
        void Press(string name) => FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        FindByName<TextBox>(host.Window, "SceneName").Text = "Barn";
        FindByName<TextBox>(host.Window, "SceneWidth").Text = "8";
        FindByName<TextBox>(host.Window, "SceneHeight").Text = "6";
        Press("AddSceneButton");
        Pump();
        var barnId = edit.SceneId!;
        Assert.NotEqual(farmId, barnId);
        Assert.Equal("Barn", host.Workspace.Current!.Scenes.First(s => s.Id == barnId).Name);

        FindByName<TextBox>(host.Window, "SceneName").Text = "Big Barn";
        Press("RenameSceneButton");
        FindByName<TextBox>(host.Window, "SceneWidth").Text = "10";
        Press("ResizeSceneButton");
        Pump();
        Assert.Equal(10, host.Workspace.Current!.Scenes.First(s => s.Id == barnId).Width);
        Assert.Equal("Big Barn", host.Workspace.Current.Scenes.First(s => s.Id == barnId).Name);

        edit.Tool = MapTool.Door;
        var point = edit.Canvas.TranslatePoint(edit.Canvas.TileRect(1, 1).Center, host.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(host.Window, point, MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(host.Window, point, MouseButton.Left);
        var destination = FindByName<ComboBox>(host.Window, "DoorDestination");
        destination.SelectedItem = destination.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, farmId));
        FindByName<TextBox>(host.Window, "DoorX").Text = "2";
        FindByName<TextBox>(host.Window, "DoorY").Text = "2";
        FindByName<CheckBox>(host.Window, "DoorReturn").IsChecked = true;
        Press("SaveDoorButton");
        Assert.Contains(host.Workspace.Current.Scenes.First(s => s.Id == barnId).Transitions,
            t => t.FromX == 1 && t.FromY == 1 && t.ToSceneId == farmId && t.ToX == 2 && t.ToY == 2);
        Assert.Contains(host.Workspace.Current.Scenes.First(s => s.Id == farmId).Transitions,
            t => t.ToSceneId == barnId && t.FromX == 2 && t.FromY == 2);
        host.Workspace.Undo();
        Assert.Empty(host.Workspace.Current.Scenes.First(s => s.Id == barnId).Transitions);
        Assert.Empty(host.Workspace.Current.Scenes.First(s => s.Id == farmId).Transitions);
        host.Workspace.Redo();

        Press("DuplicateSceneButton");
        var copyId = edit.SceneId!;
        Assert.Equal("Big Barn (Copy)", host.Workspace.Current.Scenes.First(s => s.Id == copyId).Name);
        edit.Brush = "floor";
        Press("FillSceneButton");
        Assert.All(host.Workspace.Current.Scenes.First(s => s.Id == copyId).Tiles,
            row => Assert.All(row, tile => Assert.Equal("floor", tile.Type)));
        host.Workspace.Undo();
        Press("DeleteSceneButton");
        Assert.DoesNotContain(host.Workspace.Current.Scenes, s => s.Id == copyId);
    }
}
