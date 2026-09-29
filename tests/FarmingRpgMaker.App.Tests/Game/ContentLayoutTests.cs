using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;
using Microsoft.FSharp.Reflection;
using SkiaSharp;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>The content form's web layouts: crop tabs, schedule and waypoint rows, stock cards and art previews.</summary>
public sealed class ContentLayoutTests
{
    private static void OpenCategory(GameTestHost host, string category)
    {
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 1;
        Pump();
        FindByName<ContentEditorView>(host.Window, "ContentEditorView").SelectCategory(category);
        Pump();
    }

    private static void OpenEntry(GameTestHost host, string category, string id)
    {
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 1;
        Pump();
        FindByName<ContentEditorView>(host.Window, "ContentEditorView").SelectEntry(category, id);
        Pump();
    }

    private static void Press(GameTestHost host, string name)
    {
        FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private static TextBox Box(GameTestHost host, string name) => FindByName<TextBox>(host.Window, name);

    private static void Type(GameTestHost host, string name, string text)
    {
        Box(host, name).Text = text;
        Pump();
    }

    private static void Choose(GameTestHost host, string name, string id)
    {
        var picker = FindByName<ComboBox>(host.Window, name);
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag! == id);
        Pump();
    }

    private static string? Chosen(GameTestHost host, string name) => (FindByName<ComboBox>(host.Window, name).SelectedItem as ComboBoxItem)?.Tag as string;

    private static string? Message(GameTestHost host) => FindByName<TextBlock>(host.Window, "ContentMessage").Text;

    private static string? AccessibleName(GameTestHost host, string name) => AutomationProperties.GetName(FindByName<Control>(host.Window, name));

    private static CustomAsset AddArt(GameTestHost host, string fileName, SKColor color)
    {
        using var bitmap = new SKBitmap(32, 32);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var asset = ArtImport.FromBytes(host.Workspace.Current!, fileName, encoded.ToArray());
        host.Workspace.Apply(Edits.UpsertAsset(asset));
        return host.Workspace.Current!.CustomAssets.Single(existing => existing.Id == asset.Id);
    }

    // ---- Crop tabs ----

    private static bool InTab(GameTestHost host, string tab, string name) =>
        TryFindByName<Control>((Visual)FindByName<TabItem>(host.Window, tab).Content!, name) is not null;

    [AvaloniaFact]
    public void CropFieldsAreSplitIntoTheWebTabs()
    {
        using var host = new GameTestHost();
        OpenCategory(host, "Crops");
        Press(host, "AddContentButton");
        var tabs = FindByName<TabControl>(host.Window, "ContentTabs");
        Assert.Equal(["Basic", "Growth", "Asset"], tabs.Items.OfType<TabItem>().Select(tab => (string)tab.Header!));
        Assert.Equal("Growth", AccessibleName(host, "ContentTab_Growth"));
        Assert.Equal(0, tabs.SelectedIndex);

        Assert.True(InTab(host, "ContentTab_Basic", "ContentField_Name"));
        Assert.True(InTab(host, "ContentTab_Basic", "ContentField_SeedCost"));
        Assert.True(InTab(host, "ContentTab_Basic", "ContentGroup_Seasons"));
        Assert.True(InTab(host, "ContentTab_Growth", "ContentField_Stages"));
        Assert.True(InTab(host, "ContentTab_Growth", "ContentGroup_MultiTile"));
        Assert.True(InTab(host, "ContentTab_Asset", "ContentGroup_Visual"));
        Assert.True(InTab(host, "ContentTab_Asset", "ContentField_CustomAsset"));
        Assert.False(InTab(host, "ContentTab_Basic", "ContentField_Stages"));

        // Every field is on exactly one tab.
        foreach (var property in FSharpType.GetRecordFields(typeof(CustomCropDefinition), null).Where(property => property.Name != "Extra"))
        {
            var homes = new[] { "ContentTab_Basic", "ContentTab_Growth", "ContentTab_Asset" }
                .Count(tab => InTab(host, tab, "ContentField_" + property.Name) || InTab(host, tab, "ContentGroup_" + property.Name));
            Assert.True(homes == 1, $"{property.Name} is on {homes} tabs");
        }
    }

    [AvaloniaFact]
    public void CropTabsSaveFieldsFromEveryTabAsOneUndoStepAndKeepTheTabOnRebuild()
    {
        using var host = new GameTestHost();
        OpenCategory(host, "Crops");
        Press(host, "AddContentButton");
        var before = host.Workspace.Current!.CustomCrops.OrEmpty()[^1];
        CustomCropDefinition Crop() => host.Workspace.Current!.CustomCrops.OrEmpty().Single(crop => crop.Id == before.Id);

        Type(host, "ContentField_Name", "Moonmelon");
        FindByName<TabControl>(host.Window, "ContentTabs").SelectedIndex = 1;
        Pump();
        Type(host, "ContentField_Stages", "6");
        FindByName<CheckBox>(host.Window, "ContentInclude_MultiTile").IsChecked = true;
        Pump();
        // The rebuild keeps the Growth tab and what was typed on the other tabs.
        Assert.Equal(1, FindByName<TabControl>(host.Window, "ContentTabs").SelectedIndex);
        Assert.Equal("Moonmelon", Box(host, "ContentField_Name").Text);
        Assert.Equal("6", Box(host, "ContentField_Stages").Text);
        Type(host, "ContentField_MultiTile_Width", "2");
        Type(host, "ContentField_MultiTile_Height", "2");
        FindByName<TabControl>(host.Window, "ContentTabs").SelectedIndex = 2;
        Pump();
        Press(host, "SaveContentButton");

        var saved = Crop();
        Assert.Equal(("Moonmelon", 6.0), (saved.Name, saved.Stages));
        Assert.Equal((2.0, 2.0), (saved.MultiTile.OrNull()!.Width, saved.MultiTile.OrNull()!.Height));
        host.Workspace.Undo();
        Assert.Equal((before.Name, before.Stages), (Crop().Name, Crop().Stages));
        Assert.Null(Crop().MultiTile.OrNull());
    }

    // ---- NPC schedule and waypoints ----

    [AvaloniaFact]
    public void ScheduleRowsShowTheClockTimeAndSaveMinuteSceneAndTile()
    {
        using var host = new GameTestHost();
        host.Workspace.Apply(Edits.AddScene(AuthoringTiles.CreateEmptyScene("scene-town", "Town", 10, 10)));
        var npc = host.Workspace.Current!.Npcs[0];
        Npc Current() => host.Workspace.Current!.Npcs.Single(existing => existing.Id == npc.Id);
        OpenEntry(host, "NPCs", npc.Id);
        Assert.Equal("No schedule — the NPC stays put (or wanders/patrols).", FindByName<TextBlock>(host.Window, "ContentEmpty_Schedule").Text);

        Press(host, "ContentAdd_Schedule");
        Assert.Null(TryFindByName<TextBlock>(host.Window, "ContentEmpty_Schedule"));
        Assert.Equal("480", Box(host, "ContentField_Schedule_0_Minute").Text);
        Assert.Equal("8:00 AM", FindByName<TextBlock>(host.Window, "ContentClock_Schedule_0").Text);
        Assert.Equal(npc.SceneId, Chosen(host, "ContentField_Schedule_0_SceneId"));
        Assert.Equal((npc.X.ToString(), npc.Y.ToString()), (Box(host, "ContentField_Schedule_0_X").Text, Box(host, "ContentField_Schedule_0_Y").Text));
        Assert.Equal("Minute of day (480 = 8:00 AM)", ToolTip.GetTip(Box(host, "ContentField_Schedule_0_Minute")));
        Assert.Equal("Schedule entry 1 minute", AccessibleName(host, "ContentField_Schedule_0_Minute"));
        Assert.Equal("Schedule entry 1 scene", AccessibleName(host, "ContentField_Schedule_0_SceneId"));
        Assert.Equal("Schedule entry 1 y", AccessibleName(host, "ContentField_Schedule_0_Y"));
        Assert.Equal("Remove schedule entry 1", AccessibleName(host, "ContentRemove_Schedule_0"));

        Type(host, "ContentField_Schedule_0_Minute", "1020");
        Assert.Equal("5:00 PM", FindByName<TextBlock>(host.Window, "ContentClock_Schedule_0").Text);
        Choose(host, "ContentField_Schedule_0_SceneId", "scene-town");
        Type(host, "ContentField_Schedule_0_X", "4");
        Type(host, "ContentField_Schedule_0_Y", "5");
        Press(host, "ContentAdd_Schedule");
        // The add rebuilt the rows; the first keeps what was typed.
        Assert.Equal("5:00 PM", FindByName<TextBlock>(host.Window, "ContentClock_Schedule_0").Text);
        Assert.Equal("scene-town", Chosen(host, "ContentField_Schedule_0_SceneId"));
        Type(host, "ContentField_Schedule_1_Minute", "1500");
        Assert.Equal("1:00 AM (next day)", FindByName<TextBlock>(host.Window, "ContentClock_Schedule_1").Text);
        Press(host, "ContentUp_Schedule_1");
        Assert.Equal("1500", Box(host, "ContentField_Schedule_0_Minute").Text);
        Press(host, "SaveContentButton");

        var schedule = Current().Schedule.OrEmpty();
        Assert.Equal((1500.0, npc.SceneId, npc.X, npc.Y), (schedule[0].Minute, schedule[0].SceneId, schedule[0].X, schedule[0].Y));
        Assert.Equal((1020.0, "scene-town", 4.0, 5.0), (schedule[1].Minute, schedule[1].SceneId, schedule[1].X, schedule[1].Y));

        Type(host, "ContentField_Schedule_0_Minute", "8.5");
        Press(host, "SaveContentButton");
        Assert.Contains("Schedule entry 1 minute must be a whole number", Message(host));
        Type(host, "ContentField_Schedule_0_Minute", "2000");
        Press(host, "SaveContentButton");
        Assert.Equal(1560.0, Current().Schedule.OrEmpty()[0].Minute); // clamped to the web's 0-1560

        Press(host, "ContentRemove_Schedule_1");
        Press(host, "ContentRemove_Schedule_0");
        Assert.NotNull(TryFindByName<TextBlock>(host.Window, "ContentEmpty_Schedule"));
        Press(host, "SaveContentButton");
        Assert.Null(Current().Schedule.OrNull());
        host.Workspace.Undo();
        Assert.Equal(2, Current().Schedule.OrEmpty().Length);
    }

    [AvaloniaFact]
    public void AScheduleEntryAtAMissingSceneKeepsItsId()
    {
        using var host = new GameTestHost();
        var npc = host.Workspace.Current!.Npcs[0];
        host.Workspace.Apply(Edits.UpsertNpc(npc.WithSchedule([NpcScheduleEntry.Default.WithMinute(600).WithSceneId("ghost-scene")])));
        OpenEntry(host, "NPCs", npc.Id);
        var selected = Assert.IsType<ComboBoxItem>(FindByName<ComboBox>(host.Window, "ContentField_Schedule_0_SceneId").SelectedItem);
        Assert.Equal("(missing: ghost-scene)", selected.Content);
        Assert.Equal("10:00 AM", FindByName<TextBlock>(host.Window, "ContentClock_Schedule_0").Text);
        Type(host, "ContentField_Schedule_0_X", "2");
        Press(host, "SaveContentButton");
        var entry = host.Workspace.Current!.Npcs[0].Schedule.OrEmpty().Single();
        Assert.Equal(("ghost-scene", 2.0), (entry.SceneId, entry.X));
    }

    [AvaloniaFact]
    public void PatrolWaypointsAreAddedFromTheNpcTileEditedAndRemoved()
    {
        using var host = new GameTestHost();
        var npc = host.Workspace.Current!.Npcs[0];
        Npc Current() => host.Workspace.Current!.Npcs.Single(existing => existing.Id == npc.Id);
        OpenEntry(host, "NPCs", npc.Id);
        Assert.NotNull(TryFindByName<TextBlock>(host.Window, "ContentEmpty_PatrolPoints"));

        Press(host, "ContentAdd_PatrolPoints");
        Assert.Equal((npc.X.ToString(), npc.Y.ToString()), (Box(host, "ContentField_PatrolPoints_0_X").Text, Box(host, "ContentField_PatrolPoints_0_Y").Text));
        Assert.Equal("Waypoint 1 x", AccessibleName(host, "ContentField_PatrolPoints_0_X"));
        Assert.Equal("Move waypoint 1 down", AccessibleName(host, "ContentDown_PatrolPoints_0"));
        Type(host, "ContentField_PatrolPoints_0_X", "7");
        Press(host, "ContentAdd_PatrolPoints");
        Type(host, "ContentField_PatrolPoints_1_Y", "9");
        Press(host, "SaveContentButton");
        Assert.Equal([(7.0, npc.Y), (npc.X, 9.0)], Current().PatrolPoints.OrEmpty().Select(point => (point.X, point.Y)));

        Press(host, "ContentRemove_PatrolPoints_0");
        Assert.Equal("9", Box(host, "ContentField_PatrolPoints_0_Y").Text);
        Assert.Null(TryFindByName<TextBox>(host.Window, "ContentField_PatrolPoints_1_X"));
        Press(host, "SaveContentButton");
        Assert.Equal([(npc.X, 9.0)], Current().PatrolPoints.OrEmpty().Select(point => (point.X, point.Y)));

        Press(host, "ContentRemove_PatrolPoints_0");
        Press(host, "SaveContentButton");
        Assert.Null(Current().PatrolPoints.OrNull());
    }

    // ---- Shop stock ----

    private static ShopDefinition Shop(GameTestHost host) => host.Workspace.Current!.Shops.First(shop => shop.Id == "shop-general");

    [AvaloniaFact]
    public void StockCardsEditPriceLimitAndSeasonsAndClearThemToAbsent()
    {
        using var host = new GameTestHost();
        var before = Shop(host);
        var seasons = host.Workspace.Current!.Settings.Calendar.Seasons;
        Item ItemOf(string id) => host.Workspace.Current!.Items.First(item => item.Id == id);
        OpenEntry(host, "Shops", "shop-general");

        var first = ItemOf(before.Stock[0].ItemId);
        var selected = Assert.IsType<ComboBoxItem>(FindByName<ComboBox>(host.Window, "ContentField_Stock_0_ItemId").SelectedItem);
        Assert.Equal($"{first.Name} ({ContentReadouts.Money(first.Value)})", selected.Content);
        Assert.Equal($"{JsNumber.format(first.Value)} (base)", Box(host, "ContentField_Stock_0_Price").Watermark);
        Assert.Equal("Unlimited", Box(host, "ContentField_Stock_0_DailyLimit").Watermark);
        for (var i = 0; i < seasons.Length; i++)
        {
            var check = FindByName<CheckBox>(host.Window, $"ContentSeason_Stock_0_{i}");
            Assert.Equal(seasons[i].Name, check.Content);
            Assert.Equal(before.Stock[0].Seasons.OrEmpty().Contains(seasons[i].Id), check.IsChecked);
            Assert.Equal($"Stock 1 season {seasons[i].Name}", AutomationProperties.GetName(check));
        }

        Assert.Equal("Stock 1 price override", AccessibleName(host, "ContentField_Stock_0_Price"));
        Assert.Equal("Stock 1 daily limit", AccessibleName(host, "ContentField_Stock_0_DailyLimit"));
        Assert.Equal("Remove stock entry 1", AccessibleName(host, "ContentRemove_Stock_0"));

        // Another item: the placeholder follows it.
        Choose(host, "ContentField_Stock_1_ItemId", "tool-axe");
        Assert.Equal($"{JsNumber.format(ItemOf("tool-axe").Value)} (base)", Box(host, "ContentField_Stock_1_Price").Watermark);

        Type(host, "ContentField_Stock_0_Price", "40");
        Type(host, "ContentField_Stock_0_DailyLimit", "3");
        for (var i = 0; i < seasons.Length; i++) FindByName<CheckBox>(host.Window, $"ContentSeason_Stock_0_{i}").IsChecked = i < 2;
        Press(host, "SaveContentButton");
        var saved = Shop(host).Stock[0];
        Assert.Equal((40.0, 3.0), (saved.Price!.Value, saved.DailyLimit!.Value));
        Assert.Equal([seasons[0].Id, seasons[1].Id], saved.Seasons.OrEmpty());
        Assert.Equal("tool-axe", Shop(host).Stock[1].ItemId);

        Type(host, "ContentField_Stock_0_DailyLimit", "2.5");
        Press(host, "SaveContentButton");
        Assert.Contains("Stock 1 daily limit must be a whole number", Message(host));

        Type(host, "ContentField_Stock_0_Price", "");
        Type(host, "ContentField_Stock_0_DailyLimit", "0");
        for (var i = 0; i < seasons.Length; i++) FindByName<CheckBox>(host.Window, $"ContentSeason_Stock_0_{i}").IsChecked = false;
        Press(host, "SaveContentButton");
        saved = Shop(host).Stock[0];
        Assert.Null(saved.Price);
        Assert.Null(saved.DailyLimit);
        Assert.Null(saved.Seasons);

        host.Workspace.Undo();
        host.Workspace.Undo();
        Assert.Equal(before.Stock[0].Seasons.OrEmpty(), Shop(host).Stock[0].Seasons.OrEmpty());
        Assert.Null(Shop(host).Stock[0].Price);
        Assert.Equal(before.Stock[1].ItemId, Shop(host).Stock[1].ItemId);
    }

    [AvaloniaFact]
    public void StockCardsKeepUnknownKeysAndMissingSeasons()
    {
        using var host = new GameTestHost();
        OpenEntry(host, "Shops", "shop-general");
        var seasons = host.Workspace.Current!.Settings.Calendar.Seasons;
        Type(host, "ContentJson_Stock", """[{ "itemId": "tool-axe", "seasons": ["monsoon"], "note": "keep me" }]""");
        Press(host, "ContentJsonApply_Stock");
        var missing = FindByName<CheckBox>(host.Window, $"ContentSeason_Stock_0_{seasons.Length}");
        Assert.Equal("(missing: monsoon)", missing.Content);
        Assert.True(missing.IsChecked);
        Type(host, "ContentField_Stock_0_Price", "6");
        Press(host, "SaveContentButton");
        var entry = Shop(host).Stock.Single();
        Assert.Equal(6.0, entry.Price!.Value);
        Assert.Equal(["monsoon"], entry.Seasons.OrEmpty());
        Assert.Contains(entry.Extra, pair => pair.Item1 == "note");

        FindByName<CheckBox>(host.Window, $"ContentSeason_Stock_0_{seasons.Length}").IsChecked = false;
        Press(host, "SaveContentButton");
        Assert.Null(Shop(host).Stock.Single().Seasons);

        Press(host, "ContentRemove_Stock_0");
        Assert.Equal("No stock entries. Add one to sell items.", FindByName<TextBlock>(host.Window, "ContentEmpty_Stock").Text);
        Press(host, "ContentAdd_Stock");
        Assert.NotNull(TryFindByName<Border>(host.Window, "ContentCard_Stock_0"));
    }

    // ---- Art previews ----

    [AvaloniaFact]
    public void VisualGroupsShowAPreviewOfTheChosenArt()
    {
        using var host = new GameTestHost();
        var coral = AddArt(host, "coral.png", SKColors.Coral);
        var teal = AddArt(host, "teal.png", SKColors.Teal);
        var npc = host.Workspace.Current!.Npcs[0];
        host.Workspace.Apply(Edits.UpsertNpc(npc.WithVisual(VisualRef.Default.WithAssetId(coral.Id))));
        OpenEntry(host, "NPCs", npc.Id);

        var frame = FindByName<Border>(host.Window, "ContentArt_Visual");
        Assert.Equal("Art preview", AutomationProperties.GetName(frame));
        var image = Assert.IsType<Image>(frame.Child);
        Assert.NotNull(image.Source);
        Choose(host, "ContentField_Visual_AssetId", teal.Id);
        var redrawn = Assert.IsType<Image>(FindByName<Border>(host.Window, "ContentArt_Visual").Child);
        Assert.NotSame(image, redrawn);

        // Without art the group has no preview; the other NPC has no visual at all.
        OpenEntry(host, "NPCs", host.Workspace.Current!.Npcs[1].Id);
        Assert.Null(TryFindByName<Border>(host.Window, "ContentArt_Visual"));
    }

    [AvaloniaFact]
    public void LegacyImageFieldsShowAThumbnailOfADataUrlOrAnAsset()
    {
        using var host = new GameTestHost();
        var art = AddArt(host, "coral.png", SKColors.Coral);
        var item = host.Workspace.Current!.Items[0];
        host.Workspace.Apply(Edits.UpsertItem(item.WithCustomImage(art.DataUrl)));
        OpenEntry(host, "Items", item.Id);

        Border Thumbnail() => FindByName<Border>(host.Window, "ContentThumb_CustomImage");
        Assert.IsType<Image>(Thumbnail().Child);
        Assert.Equal("Custom Image preview", AutomationProperties.GetName(Thumbnail()));
        Type(host, "ContentField_CustomImage", art.Id);
        Assert.IsType<Image>(Thumbnail().Child);
        Type(host, "ContentField_CustomImage", "data:image/png;base64,AAAA");
        Assert.Null(Thumbnail().Child);
        Type(host, "ContentField_CustomImage", "data:image/png;base64,not base64!");
        Assert.Null(Thumbnail().Child);
        Type(host, "ContentField_CustomImage", "no-such-asset");
        Assert.Null(Thumbnail().Child);
        Type(host, "ContentField_CustomImage", "");
        Assert.Null(Thumbnail().Child);
    }

    // ---- Accessible names ----

    [AvaloniaFact]
    public void GenericFieldsAndRowButtonsHaveAccessibleNames()
    {
        using var host = new GameTestHost();
        OpenEntry(host, "Shops", "shop-general");
        Assert.Equal("Name", AccessibleName(host, "ContentField_Name"));
        Assert.Equal("Sell Price Multiplier", AccessibleName(host, "ContentField_SellPriceMultiplier"));
        Assert.Equal("Stock as JSON", AccessibleName(host, "ContentJson_Stock"));

        OpenEntry(host, "Quests", "quest-first-harvest");
        Assert.Equal("Move quest objective down", AccessibleName(host, "ContentDown_Objectives_0"));
        Assert.Equal("Remove quest objective", AccessibleName(host, "ContentRemove_Objectives_0"));
        Assert.Equal("Giver", AccessibleName(host, "ContentField_Giver"));
    }
}
