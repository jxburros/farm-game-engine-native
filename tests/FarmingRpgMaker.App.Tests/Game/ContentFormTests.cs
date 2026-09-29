using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;
using SkiaSharp;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>The generated nested forms, reference pickers, export settings and art tools.</summary>
public sealed class ContentFormTests
{
    private static ContentEditorView OpenEntry(GameTestHost host, string category, string id)
    {
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 1;
        Pump();
        var view = FindByName<ContentEditorView>(host.Window, "ContentEditorView");
        view.SelectEntry(category, id);
        Pump();
        return view;
    }

    private static void Press(GameTestHost host, string name) =>
        FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static ComboBox Picker(GameTestHost host, string name) => FindByName<ComboBox>(host.Window, name);

    private static void Choose(GameTestHost host, string name, string id)
    {
        var picker = Picker(host, name);
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag! == id);
        Pump();
    }

    private static string? Chosen(GameTestHost host, string name) => (Picker(host, name).SelectedItem as ComboBoxItem)?.Tag as string;

    private static ShopDefinition Shop(GameTestHost host) => host.Workspace.Current!.Shops.First(shop => shop.Id == "shop-general");

    [AvaloniaFact]
    public void ShopStockIsEditedThroughTheListEditorAndPickersAsOneUndoStep()
    {
        using var host = new GameTestHost();
        var before = Shop(host);
        var count = before.Stock.Length;
        OpenEntry(host, "Shops", "shop-general");
        Assert.Equal(before.Stock[0].ItemId, Chosen(host, "ContentField_Stock_0_ItemId"));
        Assert.True(Picker(host, "ContentField_Stock_0_ItemId").IsEditable);
        Assert.Contains(Picker(host, "ContentField_Stock_0_ItemId").Items.OfType<ComboBoxItem>(), item => (string)item.Tag! == "tool-axe");

        Choose(host, "ContentField_Stock_0_ItemId", "tool-axe");
        FindByName<TextBox>(host.Window, "ContentField_Stock_0_Price").Text = "77";
        Press(host, "ContentAdd_Stock");
        Assert.NotNull(TryFindByName<ComboBox>(host.Window, $"ContentField_Stock_{count}_ItemId"));
        // Typed values survive the rebuild an add does.
        Assert.Equal("77", FindByName<TextBox>(host.Window, "ContentField_Stock_0_Price").Text);
        Assert.Equal("tool-axe", Chosen(host, "ContentField_Stock_0_ItemId"));
        Press(host, "ContentDown_Stock_0");
        Assert.Equal("tool-axe", Chosen(host, "ContentField_Stock_1_ItemId"));
        Press(host, "ContentUp_Stock_1");
        Press(host, "ContentRemove_Stock_2");
        Assert.Equal(before.Stock[0].ItemId, Shop(host).Stock[0].ItemId); // nothing saved yet

        Press(host, "SaveContentButton");
        var saved = Shop(host);
        Assert.Equal(count, saved.Stock.Length); // one added, one removed
        Assert.Equal("tool-axe", saved.Stock[0].ItemId);
        Assert.Equal(77, saved.Stock[0].Price);
        Assert.Equal(before.Stock[3].ItemId, saved.Stock[2].ItemId);
        Assert.Equal(host.Workspace.Current!.Items[0].Id, saved.Stock[^1].ItemId);

        host.Workspace.Undo();
        Assert.Equal(before.Stock.Select(entry => entry.ItemId), Shop(host).Stock.Select(entry => entry.ItemId));
        Assert.Null(Shop(host).Stock[0].Price);
    }

    [AvaloniaFact]
    public void ReferenceChipsAddAndRemoveSeasonsAndEmptyOptionalListsBecomeAbsent()
    {
        // Shop stock seasons are checkboxes now (ContentLayoutTests); quests keep the chips.
        using var host = new GameTestHost();
        Quest Current() => host.Workspace.Current!.Quests.First(quest => quest.Id == "quest-go-shopping");
        OpenEntry(host, "Quests", "quest-go-shopping");
        var prerequisites = Current().Prerequisites.OrEmpty();
        Assert.NotNull(TryFindByName<Border>(host.Window, "ContentChip_Prerequisites_0"));
        for (var i = prerequisites.Length - 1; i >= 0; i--) Press(host, $"ContentChipRemove_Prerequisites_{i}");
        Assert.Null(TryFindByName<Border>(host.Window, "ContentChip_Prerequisites_0"));
        Choose(host, "ContentChipAdd_AvailableSeasons", "winter");
        Press(host, "SaveContentButton");
        Assert.Null(Current().Prerequisites.OrNull());
        Assert.Contains("winter", Current().AvailableSeasons.OrEmpty());
    }

    [AvaloniaFact]
    public void MissingReferencesStayVisibleInThePicker()
    {
        using var host = new GameTestHost();
        var shop = Shop(host);
        var stock = shop.Stock.ToList();
        stock[0] = stock[0].WithItemId("ghost-item");
        host.Workspace.Apply(Edits.UpsertShop(shop.WithStock(stock)));
        OpenEntry(host, "Shops", "shop-general");
        var selected = Assert.IsType<ComboBoxItem>(Picker(host, "ContentField_Stock_0_ItemId").SelectedItem);
        Assert.Equal("(missing: ghost-item)", selected.Content);
        Assert.Contains("missing", selected.Classes);
        // Saving without touching it keeps the id.
        Press(host, "SaveContentButton");
        Assert.Equal("ghost-item", Shop(host).Stock[0].ItemId);
    }

    [AvaloniaFact]
    public void OutcomeAndConditionTypesShowOnlyTheirFieldsAndStartFromFSharpDefaults()
    {
        using var host = new GameTestHost();
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 1;
        Pump();
        var view = FindByName<ContentEditorView>(host.Window, "ContentEditorView");
        view.SelectCategory("Events");
        Pump();
        Press(host, "AddContentButton");
        Pump();
        var eventId = host.Workspace.Current!.Events[^1].Id;
        Assert.Equal("message", Chosen(host, "ContentField_Outcomes_0_Type"));
        Assert.NotNull(TryFindByName<TextBox>(host.Window, "ContentField_Outcomes_0_Message"));

        Choose(host, "ContentField_Outcomes_0_Type", "giveItem");
        Assert.Null(TryFindByName<TextBox>(host.Window, "ContentField_Outcomes_0_Message"));
        Assert.Null(Chosen(host, "ContentField_Outcomes_0_ItemId"));
        Choose(host, "ContentField_Outcomes_0_ItemId", "tool-hoe");
        FindByName<TextBox>(host.Window, "ContentField_Outcomes_0_ItemQuantity").Text = "3";

        Choose(host, "ContentAdd_Outcomes", "waterArea");
        FindByName<TextBox>(host.Window, "ContentField_Outcomes_1_Radius").Text = "40";

        Choose(host, "ContentField_Conditions_0_Type", "friendship");
        Assert.Equal("250", FindByName<TextBox>(host.Window, "ContentField_Conditions_0_Min").Text);
        Choose(host, "ContentField_Conditions_0_NpcId", "npc-farmer");
        Choose(host, "ContentAdd_Conditions", "season");
        Assert.NotNull(TryFindByName<Border>(host.Window, "ContentChip_Conditions_1_Seasons_0"));

        Press(host, "SaveContentButton");
        var saved = host.Workspace.Current.Events.First(e => e.Id == eventId);
        Assert.Equal("giveItem", saved.Outcomes[0].Type);
        Assert.Equal("tool-hoe", saved.Outcomes[0].ItemId);
        Assert.Equal(3, saved.Outcomes[0].ItemQuantity);
        Assert.Null(saved.Outcomes[0].Message);
        Assert.Equal(10, saved.Outcomes[1].Radius); // clamped to the web's 0-10
        var friendship = Assert.IsType<EventCondition.Friendship>(saved.Conditions[0]).Item;
        Assert.Equal(("npc-farmer", 250.0), (friendship.NpcId, friendship.Min));
        Assert.Equal(host.Workspace.Current.Settings.Calendar.Seasons[0].Id, Assert.IsType<EventCondition.Season>(saved.Conditions[1]).Item.Seasons.Single());

        host.Workspace.Undo();
        var restored = host.Workspace.Current.Events.First(e => e.Id == eventId);
        Assert.Equal("message", restored.Outcomes.Single().Type);
        Assert.IsType<EventCondition.EnterTile>(restored.Conditions.Single());
    }

    [AvaloniaFact]
    public void JsonEscapeHatchRoundTripsAndRejectsBrokenJson()
    {
        using var host = new GameTestHost();
        OpenEntry(host, "Shops", "shop-general");
        var json = FindByName<TextBox>(host.Window, "ContentJson_Stock");
        Assert.False(FindByName<Expander>(host.Window, "ContentJsonExpander_Stock").IsExpanded);
        Assert.Contains("\"itemId\"", json.Text);

        json.Text = """[{ "itemId": "tool-axe", "price": 5 }, { "itemId": "tool-hoe", "dailyLimit": 2 }]""";
        Press(host, "ContentJsonApply_Stock");
        Assert.Equal("tool-axe", Chosen(host, "ContentField_Stock_0_ItemId"));
        Assert.Equal("2", FindByName<TextBox>(host.Window, "ContentField_Stock_1_DailyLimit").Text);
        Assert.Null(TryFindByName<ComboBox>(host.Window, "ContentField_Stock_2_ItemId"));
        Press(host, "SaveContentButton");
        Assert.Equal(["tool-axe", "tool-hoe"], Shop(host).Stock.Select(entry => entry.ItemId));
        Assert.Equal(5, Shop(host).Stock[0].Price);

        FindByName<TextBox>(host.Window, "ContentJson_Stock").Text = "not JSON";
        Press(host, "SaveContentButton");
        Assert.Contains("Could not save", FindByName<TextBlock>(host.Window, "ContentMessage").Text);
        Assert.Equal(2, Shop(host).Stock.Length);
        Press(host, "ContentJsonApply_Stock");
        Assert.Contains("Fix these first", FindByName<TextBlock>(host.Window, "ContentMessage").Text);

        host.Workspace.Undo();
        Assert.True(Shop(host).Stock.Length > 2);
    }

    [AvaloniaFact]
    public void NestedRecordsCanBeAddedAndCleared()
    {
        using var host = new GameTestHost();
        var npc = host.Workspace.Current!.Npcs[0];
        OpenEntry(host, "NPCs", npc.Id);
        var include = FindByName<CheckBox>(host.Window, "ContentInclude_GiftTastes");
        Assert.Equal(npc.GiftTastes is not null, include.IsChecked);
        include.IsChecked = true;
        Pump();
        Choose(host, "ContentChipAdd_GiftTastes_Loved", "gift-flower");
        Press(host, "ContentAdd_Schedule");
        Assert.Equal(npc.SceneId, Chosen(host, "ContentField_Schedule_0_SceneId"));
        Press(host, "SaveContentButton");
        var saved = host.Workspace.Current.Npcs[0];
        Assert.Equal(["gift-flower"], saved.GiftTastes.OrNull()!.Loved);
        Assert.Equal(480, saved.Schedule.OrEmpty().Single().Minute);

        FindByName<CheckBox>(host.Window, "ContentInclude_GiftTastes").IsChecked = false;
        Pump();
        Press(host, "SaveContentButton");
        Assert.Null(host.Workspace.Current.Npcs[0].GiftTastes);
        host.Workspace.Undo();
        Assert.NotNull(host.Workspace.Current.Npcs[0].GiftTastes);
    }

    [AvaloniaFact]
    public void MinigameSettingsAndTierOutcomesAreEditable()
    {
        using var host = new GameTestHost();
        OpenEntry(host, "Minigames", "fishing");
        MinigameDef Fishing() => host.Workspace.Current!.Minigames.First(m => m.Id == "fishing");
        Assert.Equal("speed", FindByName<TextBox>(host.Window, "ContentKey_Config_0").Text);
        FindByName<TextBox>(host.Window, "ContentValue_Config_0").Text = "1.5";
        Press(host, "ContentAdd_Config");
        Assert.Equal("1.5", FindByName<TextBox>(host.Window, "ContentValue_Config_0").Text);
        FindByName<TextBox>(host.Window, "ContentKey_Config_3").Text = "hard";
        FindByName<TextBox>(host.Window, "ContentValue_Config_3").Text = "true";
        Press(host, "ContentAdd_ResultTiers");
        FindByName<TextBox>(host.Window, "ContentField_ResultTiers_0_MinScore").Text = "0.5";
        Choose(host, "ContentAdd_ResultTiers_0_Outcomes", "giveMoney");
        FindByName<TextBox>(host.Window, "ContentField_ResultTiers_0_Outcomes_0_Amount").Text = "25";
        Press(host, "SaveContentButton");

        var saved = Fishing();
        Assert.Equal(1.5, ((Json.JNumber)saved.Config.ToDictionary()["speed"]).Item);
        Assert.Equal(Json.NewJBool(true), saved.Config.ToDictionary()["hard"]);
        Assert.StartsWith("Hook the fish", ((Json.JString)saved.Config.ToDictionary()["prompt"]).Item, StringComparison.Ordinal);
        var tier = Assert.Single(saved.ResultTiers);
        Assert.Equal(0.5, tier.MinScore);
        Assert.Equal(("giveMoney", 25.0), (tier.Outcomes[0].Type, tier.Outcomes[0].Amount!.Value));

        Press(host, "ContentRemove_Config_1");
        Press(host, "SaveContentButton");
        Assert.Equal(["speed", "prompt", "hard"], Fishing().Config.Select(pair => pair.Item1));
    }

    [AvaloniaFact]
    public void QuestObjectivesShowTheTargetsOfTheirType()
    {
        using var host = new GameTestHost();
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 1;
        Pump();
        FindByName<ContentEditorView>(host.Window, "ContentEditorView").SelectCategory("Quests");
        Pump();
        Press(host, "AddContentButton");
        Pump();
        Press(host, "ContentAdd_Objectives");
        Assert.NotNull(TryFindByName<ComboBox>(host.Window, "ContentField_Objectives_0_TargetItemId"));
        Assert.Null(TryFindByName<ComboBox>(host.Window, "ContentField_Objectives_0_TargetNpcId"));
        Choose(host, "ContentField_Objectives_0_Type", "talk");
        Assert.Null(TryFindByName<ComboBox>(host.Window, "ContentField_Objectives_0_TargetItemId"));
        Choose(host, "ContentField_Objectives_0_TargetNpcId", "npc-farmer");
        Press(host, "SaveContentButton");
        var objective = host.Workspace.Current!.Quests[^1].Objectives.Single();
        Assert.Equal(("talk", "npc-farmer", "obj-1"), (objective.Type, objective.TargetNpcId, objective.Id));
    }

    [AvaloniaFact]
    public void ExportSettingsAreEditedInProjectSettingsWithInlineProblems()
    {
        using var host = new GameTestHost();
        using var bitmap = new SKBitmap(256, 256);
        bitmap.Erase(SKColors.SeaGreen);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var icon = ArtImport.FromBytes(host.Workspace.Current!, "icon.png", encoded.ToArray());
        host.Workspace.Apply(Edits.UpsertAsset(icon));
        Assert.Null(host.Workspace.Current!.Export);

        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 3;
        Pump();
        Assert.Contains("Not saved yet", FindByName<TextBlock>(host.Window, "ExportSettingsMessage").Text);
        Assert.Equal(1280.ToString(), FindByName<TextBox>(host.Window, "Export_WindowWidth").Text);
        FindByName<TextBox>(host.Window, "Export_Title").Text = "Willow Creek";
        FindByName<TextBox>(host.Window, "Export_Author").Text = "Jo";
        FindByName<TextBox>(host.Window, "Export_WindowWidth").Text = "100";
        FindByName<CheckBox>(host.Window, "Export_Fullscreen").IsChecked = true;
        Choose(host, "Export_Icon", icon.Id);
        Choose(host, "Export_PixelScale", "fit");
        Press(host, "SaveExportSettingsButton");

        var export = host.Workspace.Current.Export.OrNull()!;
        Assert.Equal(("Willow Creek", "Jo", icon.Id, "fit"), (export.Title.OrNull(), export.Author.OrNull(), export.IconAssetId.OrNull(), export.PixelScale));
        Assert.Equal((100, 800, true), (export.Window.Width, export.Window.Height, export.Window.Fullscreen));
        Assert.Contains("320×240", FindByName<TextBlock>(host.Window, "ExportSettingsProblem_0").Text);
        Assert.Contains(Problems.Collect(host.Workspace.Current), problem => problem.Code == "export.window");

        FindByName<TextBox>(host.Window, "Export_WindowWidth").Text = "1600";
        Press(host, "SaveExportSettingsButton");
        Assert.Equal(1600, host.Workspace.Current.Export.OrNull()!.Window.Width);
        Assert.Null(TryFindByName<TextBlock>(host.Window, "ExportSettingsProblem_0"));
        FindByName<TextBox>(host.Window, "Export_WindowWidth").Text = "wide";
        Press(host, "SaveExportSettingsButton");
        Assert.Contains("whole number", FindByName<TextBlock>(host.Window, "ExportSettingsMessage").Text);

        host.Workspace.Undo();
        Assert.Equal(100, host.Workspace.Current.Export.OrNull()!.Window.Width);
        host.Workspace.Undo();
        Assert.Null(host.Workspace.Current.Export);
    }

    private const string Svg = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
        <svg xmlns="http://www.w3.org/2000/svg" width="40" height="20" viewBox="0 0 40 20">
          <defs><linearGradient id="g"><stop offset="0" stop-color="#ff0000"/><stop offset="1" stop-color="#0000ff"/></linearGradient></defs>
          <rect x="0" y="0" width="40" height="20" fill="url(#g)"/>
        </svg>
        """;

    [AvaloniaFact]
    public void SvgImportRasterizesAtItsOwnOrAChosenSize()
    {
        using var host = new GameTestHost();
        var project = host.Workspace.Current!;
        var own = ArtImport.FromBytes(project, "banner.svg", System.Text.Encoding.UTF8.GetBytes(Svg));
        Assert.Equal((40.0, 20.0), (own.Width!.Value, own.Height!.Value));
        Assert.StartsWith("data:image/png;base64,", own.DataUrl, StringComparison.Ordinal);
        using var decoded = SKBitmap.Decode(Convert.FromBase64String(own.DataUrl["data:image/png;base64,".Length..]));
        Assert.Equal(40, decoded.Width);
        var left = decoded.GetPixel(1, 10);
        var right = decoded.GetPixel(38, 10);
        Assert.True(left.Red > 200 && left.Blue < 60, $"left pixel {left}");
        Assert.True(right.Blue > 200 && right.Red < 60, $"right pixel {right}");

        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 5;
        Pump();
        FindByName<TextBox>(host.Window, "ArtSvgSize").Text = "256";
        FindByName<ArtEditorView>(host.Window, "ArtEditorView").ImportBytes("banner.svg", System.Text.Encoding.UTF8.GetBytes(Svg));
        var scaled = Assert.Single(host.Workspace.Current!.CustomAssets);
        Assert.Equal((256.0, 128.0), (scaled.Width!.Value, scaled.Height!.Value));
        Assert.Equal((5792, 2896), ArtImport.SvgPixelSize(40, 20, 100_000)); // 16 megapixels at most
        Assert.Equal((4096, 4096), ArtImport.SvgPixelSize(1, 1, 8192));
        Assert.Equal((512, 512), ArtImport.SvgPixelSize(0, 0, null));
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""", "scripts")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"/>""", "scripts")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="https://example.com/a.png" width="4" height="4"/></svg>""", "external")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><use href="other.svg#icon"/></svg>""", "external")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>@import url(https://example.com/a.css);</style></svg>""", "external stylesheet")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect style="fill:url(file:///etc/passwd)" width="1" height="1"/></svg>""", "external")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div xmlns="http://www.w3.org/1999/xhtml">hi</div></foreignObject></svg>""", "HTML")]
    [InlineData("""<!DOCTYPE svg [<!ENTITY x SYSTEM "file:///etc/passwd">]><svg xmlns="http://www.w3.org/2000/svg"><text>&x;</text></svg>""", "SVG")]
    [InlineData("""<html><body/></html>""", "not an SVG")]
    public void SvgImportRejectsScriptsAndExternalReferences(string svg, string message)
    {
        var project = ProjectCatalog.CreateInitialProject(0);
        var error = Assert.Throws<ArgumentException>(() => ArtImport.FromBytes(project, "bad.svg", System.Text.Encoding.UTF8.GetBytes(svg)));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ArtStudioDuplicatesFramesAndEditsDurations()
    {
        using var bitmap = new SKBitmap(32, 16);
        bitmap.Erase(SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var host = new GameTestHost();
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 5;
        Pump();
        FindByName<ArtEditorView>(host.Window, "ArtEditorView").ImportBytes("sheet.png", encoded.ToArray());
        Press(host, "SliceArtButton");
        AnimationClip Clip() => host.Workspace.Current!.CustomAssets.Single().Animations.OrEmpty().Single();
        Assert.Equal(2, Clip().Frames.Length);

        Press(host, "ArtFrameDuplicate_0");
        Assert.Equal([0.0, 0.0, 16.0], Clip().Frames.Select(frame => frame.X));
        FindByName<TextBox>(host.Window, "ArtFrameTicks_2").Text = "12";
        Press(host, "ArtFrameTicksSet_2");
        Assert.Equal([6.0, 6.0, 12.0], Clip().Frames.Select(frame => frame.Ticks));
        FindByName<TextBox>(host.Window, "ArtFrameTicks").Text = "4";
        Press(host, "ArtAllFrameTicksButton");
        Assert.All(Clip().Frames, frame => Assert.Equal(4, frame.Ticks));
        host.Workspace.Undo();
        Assert.Equal([6.0, 6.0, 12.0], Clip().Frames.Select(frame => frame.Ticks));
        host.Workspace.Undo();
        host.Workspace.Undo();
        Assert.Equal(2, Clip().Frames.Length);
    }
}
