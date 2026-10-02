using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// Unsaved form fields (#87), quick open, the entry filter and the splitters (#56), and the map's
/// markers and "Pick on map" (#47).
/// </summary>
public sealed class EditorDraftAndFindTests
{
    private static TabControl Tabs(GameTestHost host) => FindByName<TabControl>(host.Window, "EditorTabs");

    private static ContentEditorView OpenEntry(GameTestHost host, string category, string id)
    {
        Tabs(host).SelectedIndex = 1;
        Pump();
        var view = FindByName<ContentEditorView>(host.Window, "ContentEditorView");
        view.SelectEntry(category, id);
        Pump();
        return view;
    }

    private static void Press(GameTestHost host, string name)
    {
        FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
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
    public void AnUnsavedNpcNameSurvivesTabSwitchesAndUndoElsewhere()
    {
        using var host = new GameTestHost();
        var npc = host.Workspace.Current!.Npcs[0];
        var view = OpenEntry(host, "NPCs", npc.Id);
        FindByName<TextBox>(host.Window, "ContentField_Name").Text = "Draft Name";
        Assert.True(view.HasUnsavedChanges);

        // Another tab and a map edit undone meanwhile: the draft stays.
        Tabs(host).SelectedIndex = 0;
        Pump();
        host.Surface.EditView.Brush = "soil";
        host.Surface.EditView.PaintTile(3, 3);
        host.Workspace.Undo();
        Tabs(host).SelectedIndex = 1;
        Pump();
        Assert.Equal("Draft Name", FindByName<TextBox>(host.Window, "ContentField_Name").Text);

        // Another entry asks first; Keep editing stays, Save and continue saves then moves on.
        var other = host.Workspace.Current!.Npcs[1];
        view.SelectEntry("NPCs", other.Id);
        Pump();
        Assert.True(FindByName<Border>(host.Window, "ContentDraftBar").IsVisible);
        Assert.Equal(npc.Id, view.SelectedId);
        Press(host, "ContentDraftKeep");
        Assert.False(FindByName<Border>(host.Window, "ContentDraftBar").IsVisible);
        Assert.Equal("Draft Name", FindByName<TextBox>(host.Window, "ContentField_Name").Text);
        view.SelectEntry("NPCs", other.Id);
        Pump();
        Press(host, "ContentDraftSave");
        Assert.Equal("Draft Name", host.Workspace.Current!.Npcs.First(n => n.Id == npc.Id).Name);
        Assert.Equal(other.Id, view.SelectedId);
        Assert.False(view.HasUnsavedChanges);

        // Discard drops the fields.
        FindByName<TextBox>(host.Window, "ContentField_Name").Text = "Thrown away";
        view.SelectEntry("NPCs", npc.Id);
        Pump();
        Press(host, "ContentDraftDiscard");
        Assert.Equal(npc.Id, view.SelectedId);
        Assert.Equal(other.Name, host.Workspace.Current!.Npcs.First(n => n.Id == other.Id).Name);
    }

    [AvaloniaFact]
    public void UnsavedFieldsAreAppliedWhenTheProjectIsLeft()
    {
        using var host = new GameTestHost();
        var npc = host.Workspace.Current!.Npcs[0];
        OpenEntry(host, "NPCs", npc.Id);
        FindByName<TextBox>(host.Window, "ContentField_Name").Text = "Kept on exit";
        Tabs(host).SelectedIndex = 3;
        Pump();
        FindByName<TextBox>(host.Window, "Setting_PlayerSpeed").Text = "6";
        host.Surface.PrepareForShutdown();
        var project = host.Workspace.Current!;
        Assert.Equal("Kept on exit", project.Npcs.First(n => n.Id == npc.Id).Name);
        Assert.Equal(6, project.Settings.Movement.PlayerSpeed);
        Assert.False(host.Surface.EditView.HasUnsavedDrafts);
    }

    [AvaloniaFact]
    public void InterfacePanelDraftsAskBeforeAnotherPanel()
    {
        using var host = new GameTestHost();
        Tabs(host).SelectedIndex = 7;
        Pump();
        Press(host, "AddInterfacePanelButton");
        Press(host, "AddInterfacePanelButton");
        var view = FindByName<InterfaceEditorView>(host.Window, "InterfaceEditorView");
        var panels = FindByName<ListBox>(host.Window, "InterfacePanels");
        FindByName<TextBox>(host.Window, "InterfaceTitle").Text = "Draft panel";
        Assert.True(view.HasUnsavedChanges);
        Tabs(host).SelectedIndex = 0;
        Pump();
        Tabs(host).SelectedIndex = 7;
        Pump();
        Assert.Equal("Draft panel", FindByName<TextBox>(host.Window, "InterfaceTitle").Text);
        panels.SelectedIndex = 0;
        Pump();
        Assert.True(FindByName<Border>(host.Window, "InterfaceDraftBar").IsVisible);
        Press(host, "InterfaceDraftSave");
        Assert.Contains(host.Workspace.Current!.GamePanels.OrEmpty(), panel => panel.Title == "Draft panel");
        Assert.False(view.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void QuickOpenFindsContentScenesAndOpensThem()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var npc = host.Workspace.Current!.Npcs[0];
        var results = edit.Find(npc.Name);
        Assert.Contains(results, r => r.Kind == "NPCs" && r.Label.Contains(npc.Id, StringComparison.Ordinal));
        var scene = host.Workspace.Current.Scenes[0];
        Assert.Contains(edit.Find(scene.Name), r => r.Kind == "Scene");
        Assert.Empty(edit.Find(""));

        // Ctrl+K opens it; Enter opens the first result.
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(host.Window, Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
        Pump();
        Assert.True(FindByName<Border>(host.Window, "QuickOpen").IsVisible);
        var box = FindByName<TextBox>(host.Window, "QuickOpenBox");
        box.Text = npc.Id;
        Pump();
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(host.Window, Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Pump();
        Assert.False(FindByName<Border>(host.Window, "QuickOpen").IsVisible);
        Assert.Equal(1, Tabs(host).SelectedIndex);
        Assert.Equal(npc.Id, FindByName<ContentEditorView>(host.Window, "ContentEditorView").SelectedId);
    }

    [AvaloniaFact]
    public void TheEntryFilterNarrowsTheListAndThePanelsHaveSplitters()
    {
        using var host = new GameTestHost();
        var item = host.Workspace.Current!.Items[0];
        OpenEntry(host, "Items", item.Id);
        var list = FindByName<ListBox>(host.Window, "ContentEntities");
        var all = list.ItemCount;
        FindByName<TextBox>(host.Window, "ContentFilter").Text = item.Name;
        Pump();
        Assert.InRange(list.ItemCount, 1, all - 1);
        Assert.All(list.Items.OfType<ListBoxItem>(), row => Assert.True(
            (Avalonia.Automation.AutomationProperties.GetName(row) ?? "").Contains(item.Name, StringComparison.OrdinalIgnoreCase) || Equals(row.Tag, item.Id)));
        Assert.NotNull(FindByName<GridSplitter>(host.Window, "ContentSplitter"));
        Assert.NotNull(FindByName<GridSplitter>(host.Window, "MapPanelSplitter"));
    }

    [AvaloniaFact]
    public void MapMarkersShowDoorsAndTheStartAndInspectOpensADoor()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var sceneId = edit.SceneId!;
        var barn = Defaults.NewScene(host.Workspace.Current!, "Barn", 8, 6);
        host.Workspace.Apply(Edits.AddScene(barn));
        host.Workspace.Apply(Edits.SetTransition(sceneId, FarmEngine.Schemas.SceneTransition.Default.WithFromX(4).WithFromY(4).WithToSceneId(barn.Id).WithToX(2).WithToY(2)));
        Pump();
        Assert.Contains(edit.Markers, m => m.Kind == "door" && m.X == 4 && m.Y == 4);
        Assert.Contains(edit.Markers, m => m.Kind == "start");
        Assert.NotNull(FindByName<Border>(host.Window, "Marker_door_4_4"));
        FindByName<CheckBox>(host.Window, "ShowMarkers").IsChecked = false;
        Pump();
        Assert.False(FindByName<Canvas>(host.Window, "MapMarkers").IsVisible);

        edit.Tool = MapTool.Inspect;
        ClickTile(host, 4, 4);
        Assert.Equal(MapTool.Door, edit.Tool);
        Assert.Contains("(4, 4)", FindByName<TextBlock>(host.Window, "DoorFrom").Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void PickOnMapFillsTheDoorArrivalInTheDestinationScene()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        var sceneId = edit.SceneId!;
        var barn = Defaults.NewScene(host.Workspace.Current!, "Barn", 8, 6);
        host.Workspace.Apply(Edits.AddScene(barn));
        Pump();
        edit.Tool = MapTool.Door;
        ClickTile(host, 1, 1);
        var destination = FindByName<ComboBox>(host.Window, "DoorDestination");
        destination.SelectedItem = destination.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, barn.Id));
        Press(host, "PickDoorArrivalButton");
        Assert.True(edit.IsPicking);
        Assert.Equal(barn.Id, edit.SceneId);
        ClickTile(host, 3, 2);
        Assert.False(edit.IsPicking);
        Assert.Equal(sceneId, edit.SceneId);
        Assert.Equal("3", FindByName<TextBox>(host.Window, "DoorX").Text);
        Assert.Equal("2", FindByName<TextBox>(host.Window, "DoorY").Text);
        Press(host, "SaveDoorButton");
        Assert.Contains(host.Workspace.Current!.Scenes.First(s => s.Id == sceneId).Transitions,
            t => t.FromX == 1 && t.FromY == 1 && t.ToSceneId == barn.Id && t.ToX == 3 && t.ToY == 2);
    }

    [AvaloniaFact]
    public void PickOnMapFillsAnEventTileAndKeepsTheForm()
    {
        using var host = new GameTestHost();
        Tabs(host).SelectedIndex = 1;
        Pump();
        var view = FindByName<ContentEditorView>(host.Window, "ContentEditorView");
        view.SelectCategory("Events");
        Pump();
        Press(host, "AddContentButton");
        var eventId = host.Workspace.Current!.Events[^1].Id;
        Press(host, "ContentPick_Conditions_0_X");
        Assert.Equal(0, Tabs(host).SelectedIndex);
        ClickTile(host, 5, 3);
        Assert.Equal(1, Tabs(host).SelectedIndex);
        Assert.Equal("5", FindByName<TextBox>(host.Window, "ContentField_Conditions_0_X").Text);
        Assert.Equal("3", FindByName<TextBox>(host.Window, "ContentField_Conditions_0_Y").Text);
        Press(host, "SaveContentButton");
        var condition = Assert.IsType<FarmEngine.Schemas.EventCondition.EnterTile>(host.Workspace.Current!.Events.First(e => e.Id == eventId).Conditions[0]).Item;
        Assert.Equal((5.0, 3.0), (condition.X, condition.Y));
        // The map marks the new trigger.
        Tabs(host).SelectedIndex = 0;
        Pump();
        Assert.Contains(host.Surface.EditView.Markers, m => m.Kind == "event" && m.TargetId == eventId && m.X == 5 && m.Y == 3);
        Assert.Contains("Event", host.Surface.EditView.DescribeTile(5, 3), StringComparison.Ordinal);
    }
}
