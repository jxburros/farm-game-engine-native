using FarmEngine.Authoring.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Projects;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

public sealed class WorkspaceTests
{
    [Fact]
    public void DesktopAppDoesNotReferenceTheLegacyCSharpEngine()
    {
        string[] legacy = ["FarmEngine.Core", "FarmEngine.Runtime", "FarmEngine.Rendering", "FarmEngine.Content"];
        Assert.DoesNotContain(typeof(ProjectWorkspace).Assembly.GetReferencedAssemblies(), assembly => legacy.Contains(assembly.Name));
        Assert.DoesNotContain(typeof(FarmEngine.Interop.RustPlayer).Assembly.GetReferencedAssemblies(), assembly => legacy.Contains(assembly.Name));
    }

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

    private static double Day(GameTestHost host) => host.Play.Use(player => player.State())["clock"]!["day"]!.GetValue<double>();

    [AvaloniaFact]
    public void Playtest_ExitWithoutKeep_RestoresTheSnapshot()
    {
        using var host = new GameTestHost();
        var before = ProjectStore.ToJson(host.Workspace.Current!);

        host.EnterPlay();
        host.Play.Use(player => player.RunCommands("""[{"type":"sleep"}]"""));
        host.Play.Use(player => player.Debug(new { type = "addMoney", amount = 899 }));
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
        host.Play.Use(player => player.RunCommands("""[{"type":"sleep"}]"""));
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
        host.Play.Use(player => player.RunCommands("""[{"type":"sleep"}]"""));
        Assert.Equal(2, Day(host));

        Click(host.Window, FindByName<Button>(host.Window, "RestartButton"));

        Assert.Equal(1, Day(host));
        Assert.True(host.Play.Surface.FrameCount > 0);
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
        // The Rust renderer (farm-render through RustPreview) draws the map.
        Assert.NotNull(edit.Canvas.Geometry);
        Assert.Null(edit.Canvas.Error);

        var description = edit.DescribeTile(0, 0);
        Assert.StartsWith("(0, 0) · Wall", description, StringComparison.Ordinal);
        Assert.Contains("object wall", description, StringComparison.Ordinal);
        Assert.Contains("NPC Old Farmer", edit.DescribeTile(3, 6), StringComparison.Ordinal);

        // Pointer hover over the canvas reports the tile under it.
        var rect = edit.Canvas.TileRect(2, 3);
        var point = edit.Canvas.TranslatePoint(rect.Center, host.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(host.Window, point);
        Pump();
        Assert.StartsWith("(2, 3)", edit.HoverText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void EditMode_DrawsTheVisibleMapWithTheRustRenderer_AndRedrawsAfterEdits()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(host.Window)?.Dispose();
        Pump();
        Assert.Null(edit.Canvas.Error);
        var before = edit.Canvas.RenderCount;
        Assert.True(before > 0, "the map was rasterized");
        var world = edit.Canvas.Geometry!.WorldSize;
        var region = edit.Canvas.RenderedRegion;
        Assert.True(region.Width > 0 && region.Width <= Math.Ceiling(world.Width), $"{region} within {world}");

        edit.Brush = "water";
        var point = edit.Canvas.TranslatePoint(edit.Canvas.TileRect(2, 2).Center, host.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(host.Window, point, Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(host.Window, point, Avalonia.Input.MouseButton.Left);
        Pump();
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(host.Window)?.Dispose();
        Assert.True(edit.Canvas.RenderCount > before, "an edit redraws the map");

        // The next tile sends only the scene it changed, not the project and its art (#53). (The
        // first one also selected the brush's tile type in the project, so it sent the project.)
        var preview = edit.Canvas.Preview!;
        var (scenes, projects) = (preview.SceneUpdates, preview.ProjectUpdates);
        point = edit.Canvas.TranslatePoint(edit.Canvas.TileRect(3, 2).Center, host.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(host.Window, point, Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(host.Window, point, Avalonia.Input.MouseButton.Left);
        Pump();
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(host.Window)?.Dispose();
        Assert.Equal((scenes + 1, projects), (preview.SceneUpdates, preview.ProjectUpdates));
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

        // A greenhouse or interior keeps the weather out.
        var indoor = FindByName<CheckBox>(host.Window, "SceneIndoor");
        Assert.False(indoor.IsChecked);
        indoor.IsChecked = true;
        Assert.True(host.Workspace.Current!.Scenes.First(s => s.Id == barnId).Indoor.OrNullable() == true);
        indoor.IsChecked = false;
        Assert.Null(host.Workspace.Current!.Scenes.First(s => s.Id == barnId).Indoor.OrNullable());

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
    private static void ClickTile(GameTestHost host, int x, int y)
    {
        var edit = host.Surface.EditView;
        var point = edit.Canvas.TranslatePoint(edit.Canvas.TileRect(x, y).Center, host.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(host.Window, point, MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(host.Window, point, MouseButton.Left);
        Pump();
    }

    [AvaloniaFact]
    public void EditMode_PlaceToolsPutContentOnTheMap_AndRemoveClearsIt()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var sceneId = edit.SceneId!;
        FarmEngine.Schemas.Tile TileAt(int x, int y) => host.Workspace.Current!.Scenes.First(s => s.Id == sceneId).Tiles[y][x];
        var picker = FindByName<ComboBox>(host.Window, "PlaceChoice");
        Assert.False(picker.IsEnabled);

        FindByName<ToggleButton>(host.Window, "Tool_PlaceNode").RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));
        Assert.Equal(MapTool.PlaceNode, edit.Tool);
        Assert.True(picker.IsEnabled);
        Assert.Contains(picker.Items.OfType<ComboBoxItem>(), item => Equals(item.Tag, "node-rock"));
        edit.PlaceChoice = "node-rock";
        ClickTile(host, 5, 5);
        Assert.Equal("node-rock", TileAt(5, 5).Node.OrNull()?.TypeId);
        Assert.Contains("node node-rock", edit.DescribeTile(5, 5), StringComparison.Ordinal);
        host.Workspace.Undo();
        Assert.Null(TileAt(5, 5).Node);
        host.Workspace.Redo();

        edit.Tool = MapTool.PlaceItem;
        edit.PlaceChoice = "material-wood";
        ClickTile(host, 6, 5);
        Assert.Equal("material-wood", TileAt(6, 5).Item.OrNull()?.Id);

        edit.Tool = MapTool.PlaceMachine;
        edit.PlaceChoice = "machine-kitchen";
        ClickTile(host, 7, 5);
        Assert.Equal("machine-kitchen", TileAt(7, 5).Machine.OrNull()?.TypeId);

        edit.Tool = MapTool.PlaceNpc;
        edit.PlaceChoice = "npc-farmer";
        ClickTile(host, 4, 4);
        var farmer = host.Workspace.Current!.Npcs.First(n => n.Id == "npc-farmer");
        Assert.Equal((sceneId, 4.0, 4.0), (farmer.SceneId, farmer.X, farmer.Y));

        // Each Place tool remembers its own choice.
        edit.Tool = MapTool.PlaceItem;
        Assert.Equal("material-wood", edit.PlaceChoice);

        var species = host.Workspace.Current.AnimalSpecies[0];
        var existing = host.Workspace.Current.Animals.Select(a => a.Id).ToHashSet();
        edit.Tool = MapTool.PlaceAnimal;
        edit.PlaceChoice = species.Id;
        ClickTile(host, 5, 5);
        ClickTile(host, 8, 5);
        var animals = host.Workspace.Current!.Animals.Where(a => !existing.Contains(a.Id)).ToList();
        Assert.Equal(2, animals.Count);
        Assert.All(animals, a => Assert.Equal((sceneId, species.Id), (a.SceneId, a.SpeciesId)));
        Assert.Contains($"animal {species.Name}", edit.DescribeTile(5, 5), StringComparison.Ordinal);
        Assert.Contains($"{species.Name} (8, 5)", AllVisibleText(FindByName<StackPanel>(host.Window, "SceneAnimals")), StringComparison.Ordinal);

        // Remove clears the tile: node and the animal on it; the NPC list is untouched.
        edit.Tool = MapTool.Remove;
        ClickTile(host, 5, 5);
        Assert.Null(TileAt(5, 5).Node);
        Assert.DoesNotContain(host.Workspace.Current!.Animals, a => a.Id == animals[0].Id);
        Assert.Contains(host.Workspace.Current.Animals, a => a.Id == animals[1].Id);
        host.Workspace.Undo();
        Assert.NotNull(TileAt(5, 5).Node);
        Assert.Contains(host.Workspace.Current!.Animals, a => a.Id == animals[0].Id);

        // Placed animals are listed for the scene and can be removed from the side panel.
        FindByName<Button>(host.Window, $"RemoveAnimal_{animals[1].Id}").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.DoesNotContain(host.Workspace.Current!.Animals, a => a.Id == animals[1].Id);
        Assert.Null(TryFindByName<Button>(host.Window, $"RemoveAnimal_{animals[1].Id}"));
    }

    [AvaloniaFact]
    public void EditMode_PickToolTakesTheTileTypeAsTheBrush()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        edit.Brush = "water";
        ClickTile(host, 2, 2);

        Click(host.Window, FindByName<ToggleButton>(host.Window, "Tool_Pick"));
        ClickTile(host, 0, 0);
        Assert.Equal("wall", edit.Brush);
        Assert.Equal(MapTool.Brush, edit.Tool);
        Assert.Equal(Edits.LayerFor("wall"), edit.Layer);
        Assert.Equal("wall", host.Workspace.Current!.SelectedTileType);
        Assert.True(FindByName<ToggleButton>(host.Window, "Brush_wall").IsChecked);

        edit.Tool = MapTool.Pick;
        ClickTile(host, 2, 2);
        Assert.Equal("water", edit.Brush);
        Assert.Equal("water", host.Workspace.Current!.SelectedTileType);
    }

    [AvaloniaFact]
    public void EditMode_ListsTransitions_AndClearsThemAll()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var farmId = edit.SceneId!;
        void Press(string name) => FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        FindByName<TextBox>(host.Window, "SceneName").Text = "Pond";
        FindByName<TextBox>(host.Window, "SceneWidth").Text = "6";
        FindByName<TextBox>(host.Window, "SceneHeight").Text = "5";
        var tile = FindByName<ComboBox>(host.Window, "NewSceneTile");
        Assert.Equal("grass", (tile.SelectedItem as ComboBoxItem)?.Tag);
        tile.SelectedItem = tile.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, "water"));
        Press("AddSceneButton");
        Pump();
        var pondId = edit.SceneId!;
        var pond = host.Workspace.Current!.Scenes.First(s => s.Id == pondId);
        Assert.All(pond.Tiles, row => Assert.All(row, t => Assert.Equal("water", t.Type)));

        edit.SelectScene(farmId);
        Assert.Equal("0 transition(s) from this scene", FindByName<TextBlock>(host.Window, "TransitionCount").Text);
        Assert.False(FindByName<Button>(host.Window, "ClearTransitionsButton").IsEnabled);
        host.Workspace.Apply(Edits.SetTransition(farmId, Defaults.NewTransition(pondId).WithFromX(3).WithFromY(4).WithToX(1).WithToY(2)));
        host.Workspace.Apply(Edits.SetTransition(farmId, Defaults.NewTransition(pondId).WithFromX(5).WithFromY(4)));
        Pump();
        Assert.Equal("2 transition(s) from this scene", FindByName<TextBlock>(host.Window, "TransitionCount").Text);
        Assert.Equal("(3, 4) → Pond (1, 2)", FindByName<Button>(host.Window, "Transition_0").Content);

        // A click on a listed transition opens it in the door form.
        Press("Transition_0");
        Assert.Equal(MapTool.Door, edit.Tool);
        Assert.Contains("(3, 4)", FindByName<TextBlock>(host.Window, "DoorFrom").Text, StringComparison.Ordinal);
        Assert.Equal("1", FindByName<TextBox>(host.Window, "DoorX").Text);
        Assert.Equal("2", FindByName<TextBox>(host.Window, "DoorY").Text);
        Assert.True(FindByName<Button>(host.Window, "RemoveDoorButton").IsEnabled);

        Press("ClearTransitionsButton");
        Pump();
        Assert.Empty(host.Workspace.Current!.Scenes.First(s => s.Id == farmId).Transitions);
        Assert.Null(TryFindByName<Button>(host.Window, "Transition_0"));
        host.Workspace.Undo();
        Pump();
        Assert.Equal(2, host.Workspace.Current!.Scenes.First(s => s.Id == farmId).Transitions.Length);
        Assert.NotNull(TryFindByName<Button>(host.Window, "Transition_1"));
    }

    [AvaloniaFact]
    public void ProblemsGoTo_OpensTheAssetInTheArtTab()
    {
        using var host = new GameTestHost();
        using var bitmap = new SkiaSharp.SKBitmap(16, 16);
        bitmap.Erase(SkiaSharp.SKColors.SeaGreen);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var asset = ArtImport.FromBytes(host.Workspace.Current!, "tiles.png", encoded.ToArray());
        host.Workspace.Apply(Edits.UpsertAsset(asset));
        // Two clips with one name: an error on the asset.
        var clip = FarmEngine.Schemas.AnimationClip.Default.WithName("idle");
        host.Workspace.Apply(Edits.UpsertAsset(host.Workspace.Current!.CustomAssets.First(a => a.Id == asset.Id).WithAnimations([clip, clip])));

        var tabs = FindByName<TabControl>(host.Window, "EditorTabs");
        tabs.SelectedIndex = 2;
        Pump();
        var problems = FindByName<ProblemsView>(host.Window, "ProblemsView");
        var index = problems.CurrentProblems.ToList().FindIndex(problem => problem.TargetKind == "asset" && problem.TargetId == asset.Id);
        Assert.True(index >= 0);

        FindByName<Button>(host.Window, $"ProblemGo_{index}").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal(5, tabs.SelectedIndex);
        Assert.Equal(asset.Id, FindByName<ArtEditorView>(host.Window, "ArtEditorView").SelectedAssetId);
        Assert.Equal(asset.Name, FindByName<TextBox>(host.Window, "ArtAssetName").Text);
        Assert.Equal(asset.Id, (FindByName<ListBox>(host.Window, "ArtAssets").SelectedItem as ListBoxItem)?.Tag);
    }
}
