using FarmEngine.Authoring.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmingRpgMaker.App.Game;
using SkiaSharp;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

public sealed class ContentEditorTests
{
    private static ContentEditorView OpenContent(GameTestHost host, string category)
    {
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 1;
        Pump();
        var view = FindByName<ContentEditorView>(host.Window, "ContentEditorView");
        view.SelectCategory(category);
        Pump();
        return view;
    }

    private static void Press(GameTestHost host, string name) =>
        FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void ItemEditorCreatesUpdatesDeletesAndUsesDocumentHistory()
    {
        using var host = new GameTestHost();
        OpenContent(host, "Items");
        var originalCount = host.Workspace.Current!.Items.Length;
        Press(host, "AddContentButton");
        Assert.Equal(originalCount + 1, host.Workspace.Current.Items.Length);
        var id = host.Workspace.Current.Items[^1].Id;

        FindByName<TextBox>(host.Window, "ContentField_Name").Text = "Moon Berry";
        FindByName<TextBox>(host.Window, "ContentField_Value").Text = "42";
        Press(host, "SaveContentButton");
        Assert.Equal("Moon Berry", host.Workspace.Current.Items[^1].Name);
        Assert.Equal(42, host.Workspace.Current.Items[^1].Value);

        host.Workspace.Undo();
        Assert.NotEqual("Moon Berry", host.Workspace.Current.Items[^1].Name);
        host.Workspace.Redo();
        Assert.Equal("Moon Berry", host.Workspace.Current.Items[^1].Name);
        host.Workspace.FlushPendingSave();
        var loaded = host.Workspace.Store.Load(host.Workspace.Current.Id);
        Assert.True(loaded.Ok, string.Join("; ", loaded.Errors));
        Assert.Equal("Moon Berry", loaded.Project!.Items[^1].Name);

        Press(host, "DeleteContentButton");
        Assert.DoesNotContain(host.Workspace.Current.Items, item => item.Id == id);
        host.Workspace.Undo();
        Assert.Contains(host.Workspace.Current.Items, item => item.Id == id);
    }

    [AvaloniaFact]
    public void DialogueEditorKeepsNpcCopyInSyncAndRejectsBrokenNestedJson()
    {
        using var host = new GameTestHost();
        OpenContent(host, "Dialogue");
        var list = FindByName<ListBox>(host.Window, "ContentEntities");
        list.SelectedIndex = 0;
        Pump();
        var id = host.Workspace.Current!.Dialogues[0].Id;
        var npcId = host.Workspace.Current.Dialogues[0].NpcId;
        FindByName<TextBox>(host.Window, "ContentField_Text").Text = "A native greeting.";
        Press(host, "SaveContentButton");
        Assert.Equal("A native greeting.", host.Workspace.Current.Dialogues.First(d => d.Id == id).Text);
        Assert.Equal("A native greeting.", host.Workspace.Current.Npcs.First(n => n.Id == npcId).Dialogue.First(d => d.Id == id).Text);

        FindByName<TextBox>(host.Window, "ContentJson_Options").Text = "not JSON";
        Press(host, "SaveContentButton");
        Assert.Contains("Could not save", FindByName<TextBlock>(host.Window, "ContentMessage").Text);
        Assert.Equal("A native greeting.", host.Workspace.Current.Dialogues.First(d => d.Id == id).Text);
    }

    [AvaloniaFact]
    public void ProblemsPanelOpensTheAffectedContentEntry()
    {
        using var host = new GameTestHost();
        var npc = host.Workspace.Current!.Npcs[0];
        host.Workspace.Apply(Edits.UpsertNpc(npc.WithSceneId("missing-scene")));
        var tabs = FindByName<TabControl>(host.Window, "EditorTabs");
        tabs.SelectedIndex = 2;
        Pump();
        var problems = FindByName<ProblemsView>(host.Window, "ProblemsView");
        var index = problems.CurrentProblems.ToList().FindIndex(problem => problem.TargetKind == "npc" && problem.TargetId == npc.Id);
        Assert.True(index >= 0);
        Assert.Contains("errors", FindByName<TextBlock>(host.Window, "ProblemsSummary").Text);

        Press(host, $"ProblemGo_{index}");
        Pump();
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.Equal(npc.Id, FindByName<TextBox>(host.Window, "ContentField_Id").Text);
    }

    [AvaloniaFact]
    public void SettingsEditorSavesCalendarAndGameplayAsOneUndoStep()
    {
        using var host = new GameTestHost();
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 3;
        Pump();
        FindByName<TextBox>(host.Window, "Setting_Name").Text = "A New Farm";
        FindByName<TextBox>(host.Window, "Setting_PlayerSpeed").Text = "5.25";
        FindByName<TextBox>(host.Window, "Season_Days").Text = "35";
        Press(host, "SaveSettingsButton");

        var project = host.Workspace.Current!;
        Assert.Equal("A New Farm", project.Name);
        Assert.Equal(5.25, project.Settings.Movement.PlayerSpeed);
        Assert.Equal(35, project.Settings.Calendar.Seasons[0].Days);
        host.Workspace.Undo();
        Assert.Equal("Starter Farm", host.Workspace.Current!.Name);
        Assert.Equal(28, host.Workspace.Current.Settings.Calendar.Seasons[0].Days);
        host.Workspace.Redo();
        Assert.Equal("A New Farm", host.Workspace.Current!.Name);
    }

    [AvaloniaFact]
    public void SettingsEditorRefusesADayThatEndsBeforeItStarts_AndTurnsThePauseOff()
    {
        using var host = new GameTestHost();
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 3;
        Pump();
        var before = host.Workspace.Current;
        Assert.True(FindByName<CheckBox>(host.Window, "Setting_PauseInModals").IsChecked);
        FindByName<TextBox>(host.Window, "Setting_DayStartMinute").Text = "1560";
        FindByName<TextBox>(host.Window, "Setting_DayEndMinute").Text = "1500";
        Press(host, "SaveSettingsButton");
        Assert.StartsWith("Could not save: The day must end at least 60 minutes after it starts", FindByName<TextBlock>(host.Window, "SettingsMessage").Text);
        Assert.Same(before, host.Workspace.Current);

        FindByName<TextBox>(host.Window, "Setting_DayStartMinute").Text = "360";
        FindByName<CheckBox>(host.Window, "Setting_PauseInModals").IsChecked = false;
        Press(host, "SaveSettingsButton");
        Assert.True(host.Workspace.Current!.Settings.Time.PauseInModals.OrNullable() == false);
        Assert.Equal(1500, host.Workspace.Current.Settings.Time.DayEndMinute);
    }

    [AvaloniaFact]
    public void ModsEditorReviewsInstallsDisablesAndRemovesPackWithUndo()
    {
        using var host = new GameTestHost();
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 4;
        Pump();
        var mods = FindByName<ModsEditorView>(host.Window, "ModsEditorView");
        mods.ReviewPackJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "demo-mod.json")));
        Assert.True(FindByName<Button>(host.Window, "InstallPackButton").IsEnabled);
        Assert.Contains("Permissions", AllVisibleText(host.Window));
        Press(host, "InstallPackButton");
        var installed = Assert.Single(host.Workspace.Current!.ContentPacks);
        var id = installed.Pack.Manifest.Id;

        var enabled = FindByName<CheckBox>(host.Window, $"PackEnabled_{id}");
        enabled.IsChecked = false;
        enabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(host.Workspace.Current.ContentPacks[0].Enabled);
        Press(host, $"RemovePack_{id}");
        Assert.Empty(host.Workspace.Current.ContentPacks);
        host.Workspace.Undo();
        Assert.Single(host.Workspace.Current.ContentPacks);
        Press(host, $"ImportPack_{id}");
        Assert.Empty(host.Workspace.Current.ContentPacks);
        Assert.Contains(host.Workspace.Current.Items, item => item.Id == "demo-glow-farm:glow-jelly");
        host.Workspace.Undo();
        Assert.Single(host.Workspace.Current.ContentPacks);
    }

    [AvaloniaFact]
    public void ArtEditorImportsSlicesBindsAndPaintsCustomArt()
    {
        using var bitmap = new SKBitmap(32, 16);
        bitmap.Erase(SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        using var host = new GameTestHost();
        var tabs = FindByName<TabControl>(host.Window, "EditorTabs");
        tabs.SelectedIndex = 5;
        Pump();
        var art = FindByName<ArtEditorView>(host.Window, "ArtEditorView");
        art.ImportBytes("sheet.png", encoded.ToArray());
        var asset = Assert.Single(host.Workspace.Current!.CustomAssets);
        Assert.Equal(32, asset.Width);
        Assert.StartsWith("data:image/png;base64,", asset.DataUrl, StringComparison.Ordinal);
        Press(host, "SliceArtButton");
        asset = Assert.Single(host.Workspace.Current.CustomAssets);
        Assert.Equal(2, Assert.Single(asset.Animations.OrEmpty()).Frames.Length);
        // The Rust renderer draws the preview frame (a 16×16 frame fitted into the 140px box).
        var preview = FindByName<Border>(host.Window, "ArtPreview");
        var shown = Assert.IsType<Image>(preview.Child);
        Assert.Equal(new Avalonia.PixelSize(140, 140), Assert.IsAssignableFrom<Avalonia.Media.Imaging.Bitmap>(shown.Source).PixelSize);

        Press(host, "BindArtButton");
        Assert.Equal(asset.Id, host.Workspace.Current.PlayerVisual.OrNull()?.AssetId);
        FindByName<ComboBox>(host.Window, "ArtTarget").SelectedIndex = 1;
        Press(host, "BindArtButton");
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.Equal(asset.Id, host.Workspace.Current.SelectedTileVisual.OrNull()?.AssetId);
        var edit = host.Surface.EditView;
        edit.PaintTile(4, 4);
        Assert.Equal(asset.Id, host.Workspace.Current.Scenes.First(scene => scene.Id == edit.SceneId).Tiles[4][4].Visuals.OrNull()?.Background.OrNull()?.AssetId);

        tabs.SelectedIndex = 5;
        Pump();
        Press(host, "RemoveArtButton");
        Assert.Empty(host.Workspace.Current.CustomAssets);
        Assert.Null(host.Workspace.Current.PlayerVisual);
        Assert.Null(host.Workspace.Current.Scenes.First(scene => scene.Id == edit.SceneId).Tiles[4][4].Visuals.OrNull()?.Background.OrNull());
    }

    [AvaloniaFact]
    public void WorkshopCreatesEditableContentAsOneUndoStep()
    {
        using var host = new GameTestHost();
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 6;
        Pump();
        var before = host.Workspace.Current!.Npcs.Length;
        FindByName<TextBox>(host.Window, "WorkshopName").Text = "Luna";
        FindByName<TextBox>(host.Window, "WorkshopX").Text = "4";
        FindByName<TextBox>(host.Window, "WorkshopY").Text = "4";
        Press(host, "CreatePatternButton");
        Assert.Equal(before + 1, host.Workspace.Current.Npcs.Length);
        Assert.Contains(host.Workspace.Current.Npcs, npc => npc.Name == "Luna" && npc.Dialogue.Length == 3);
        host.Workspace.Undo();
        Assert.Equal(before, host.Workspace.Current.Npcs.Length);
    }

    [AvaloniaFact]
    public void InterfaceEditorSavesPanelEntriesThroughFSharpHistory()
    {
        using var host = new GameTestHost();
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 7;
        Pump();
        var before = host.Workspace.Current!.GamePanels.OrEmpty().Length;
        Press(host, "AddInterfacePanelButton");
        Assert.Equal(before + 1, host.Workspace.Current.GamePanels.OrEmpty().Length);
        FindByName<TextBox>(host.Window, "InterfaceTitle").Text = "Farm Journal";
        Press(host, "AddInterfaceEntryButton");
        var newLabel = FindByName<TextBox>(host.Window, "InterfaceEntryLabel");
        newLabel.Text = "Mood";
        FindByName<TextBox>(host.Window, "InterfaceEntryValue").Text = "Calm";
        Press(host, "SaveInterfacePanelButton");
        var panel = host.Workspace.Current.GamePanels.OrEmpty().Last();
        Assert.Equal("Farm Journal", panel.Title);
        Assert.Contains(panel.Entries, entry => entry.Label == "Mood" && entry.Value == "Calm");
        host.Workspace.Undo();
        Assert.NotEqual("Farm Journal", host.Workspace.Current.GamePanels.OrEmpty().Last().Title);
    }
}
