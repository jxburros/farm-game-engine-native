using Avalonia.Automation;
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

/// <summary>
/// The content editor's readouts: live recipe and crop profits, the summary cards, built-in crops
/// and node types (listed, customized, restored) and the art thumbnails in the lists.
/// </summary>
public sealed class ContentReadoutTests
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

    private static void Type(GameTestHost host, string field, string text)
    {
        FindByName<TextBox>(host.Window, field).Text = text;
        Pump();
    }

    private static string Text(GameTestHost host, string name) => FindByName<TextBlock>(host.Window, name).Text ?? "";

    private static ListBoxItem ListEntry(GameTestHost host, string id) =>
        FindByName<ListBox>(host.Window, "ContentEntities").Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, id));

    private static string? Note(GameTestHost host, string id) => AutomationProperties.GetHelpText(ListEntry(host, id));

    /// <summary>The card's label/value pairs, as shown.</summary>
    private static List<string> Card(GameTestHost host, string name) => VisibleTexts(FindByName<Border>(host.Window, name));

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Goldenrod);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [AvaloniaFact]
    public void RecipeProfitFollowsTheFieldsWhileTyping()
    {
        using var host = new GameTestHost();
        OpenEntry(host, "Recipes", "recipe-smelt-copper");
        // Three copper ore (12) and a wood (5) into a copper bar (45), two hours in the furnace.
        Assert.Equal("Furnace · 120min · profit +4", Note(host, "recipe-smelt-copper"));
        Assert.Equal("Copper Bar  ·  recipe-smelt-copper", AutomationProperties.GetName(ListEntry(host, "recipe-smelt-copper")));
        Assert.Equal("Profit per craft: 4 · time-adjusted: 2/hr", Text(host, "RecipeProfit"));

        Type(host, "ContentField_ProcessingMinutes", "30");
        Assert.Equal("Profit per craft: 4 · time-adjusted: 8/hr", Text(host, "RecipeProfit"));
        Type(host, "ContentField_Outputs_0_Quantity", "2");
        Assert.Equal("Profit per craft: 49 · time-adjusted: 98/hr", Text(host, "RecipeProfit"));
        Type(host, "ContentField_ProcessingMinutes", "0");
        Assert.Equal("Profit per craft: 49 · time-adjusted: instant", Text(host, "RecipeProfit"));
        // A half-typed value keeps the last readout.
        Type(host, "ContentField_ProcessingMinutes", "-");
        Assert.Equal("Profit per craft: 49 · time-adjusted: instant", Text(host, "RecipeProfit"));
        Assert.Equal(120, host.Workspace.Current!.Recipes.First(recipe => recipe.Id == "recipe-smelt-copper").ProcessingMinutes); // nothing saved

        Type(host, "ContentField_ProcessingMinutes", "60");
        Press(host, "SaveContentButton");
        Pump();
        Assert.Equal("Furnace · 60min · profit +49", Note(host, "recipe-smelt-copper"));
        Assert.Equal("Profit per craft: 49 · time-adjusted: 49/hr", Text(host, "RecipeProfit"));
    }

    [AvaloniaFact]
    public void CropSummaryCardAndProfitFollowTheForm()
    {
        using var host = new GameTestHost();
        OpenEntry(host, "Crops", "tomato");
        // The starter farm's crops replace the built-in ones.
        Assert.Equal("Replaces built-in · 5 stages", Note(host, "tomato"));
        Assert.Equal("Profit per harvest: $30", Text(host, "CropProfit"));
        var card = Card(host, "CropSummary");
        Assert.Equal(["Seed cost", "$20", "Harvest value", "$50", "Profit per harvest", "$30"], card.Take(6));
        Assert.Contains("Regrowth time", card);
        Assert.Contains("Summer", card);
        Assert.NotNull(TryFindByName<TextBox>(host.Window, "ContentField_SeedCost"));

        Type(host, "ContentField_SeedCost", "5");
        Assert.Equal("Profit per harvest: $45", Text(host, "CropProfit"));
        Assert.Equal("$45", Card(host, "CropSummary")[5]);
        Type(host, "ContentField_BaseHarvestValue", "1");
        Assert.Equal("Profit per harvest: -$4", Text(host, "CropProfit"));
        // Regrowth is on the Growth tab.
        var tabs = FindByName<TabControl>(host.Window, "ContentTabs");
        tabs.SelectedItem = FindByName<TabItem>(host.Window, "ContentTab_Growth");
        Pump();
        var regrow = FindByName<CheckBox>(host.Window, "ContentField_CanRegrow");
        regrow.IsChecked = false;
        Pump();
        Assert.DoesNotContain("Regrowth time", Card(host, "CropSummary"));

        Press(host, "RevertContentButton");
        Pump();
        Assert.Equal("Profit per harvest: $30", Text(host, "CropProfit"));
        Assert.Contains("Regrowth time", Card(host, "CropSummary"));
    }

    [AvaloniaFact]
    public void BuiltinCropsAreListedCustomizedAndRestored()
    {
        using var host = new GameTestHost();
        var view = OpenEntry(host, "Crops", "wheat");
        Assert.Equal("Replaces built-in · 4 stages", Note(host, "wheat"));

        // Deleting the starter's wheat brings the built-in back instead of removing wheat.
        Press(host, "DeleteContentButton");
        Pump();
        var project = host.Workspace.Current!;
        Assert.DoesNotContain(project.CustomCrops.OrEmpty(), crop => crop.Id == "wheat");
        Assert.Contains(project.Items, item => item.Id == "seed-wheat" && item.CropType.OrNull() == "wheat");
        Assert.Equal("Built-in · 4 stages", Note(host, "wheat"));
        Assert.Equal("wheat", FindByName<ListBox>(host.Window, "ContentEntities").SelectedItem is ListBoxItem { Tag: string tag } ? tag : null);
        Assert.Contains("built-in crop again", Text(host, "ContentMessage"));

        // Problems navigation reaches a built-in too: read-only, with its card and Customize.
        view.SelectEntry("Crops", "wheat");
        Pump();
        Assert.Equal("This is a built-in crop. Customize it to change it.", Text(host, "ContentMessage"));
        Assert.Contains("$15", Card(host, "CropSummary"));
        Assert.Null(TryFindByName<TextBox>(host.Window, "ContentField_SeedCost"));
        Assert.Null(TryFindByName<TextBlock>(host.Window, "CropProfit"));
        Assert.False(FindByName<Button>(host.Window, "SaveContentButton").IsEnabled);
        Assert.False(FindByName<Button>(host.Window, "DeleteContentButton").IsEnabled);
        Assert.False(FindByName<Button>(host.Window, "DuplicateContentButton").IsEnabled);
        var customize = FindByName<Button>(host.Window, "CustomizeBuiltinButton");
        Assert.True(customize.IsVisible);

        Press(host, "CustomizeBuiltinButton");
        Pump();
        var wheat = Assert.Single(host.Workspace.Current!.CustomCrops.OrEmpty(), crop => crop.Id == "wheat");
        Assert.Equal(10, wheat.SeedCost);
        Assert.Equal(25, wheat.BaseHarvestValue);
        Assert.Equal("Replaces built-in · 4 stages", Note(host, "wheat"));
        Assert.Equal("10", FindByName<TextBox>(host.Window, "ContentField_SeedCost").Text);
        Assert.False(customize.IsVisible);
        Assert.True(FindByName<Button>(host.Window, "SaveContentButton").IsEnabled);
        Assert.StartsWith("Customized Wheat", Text(host, "ContentMessage"));

        host.Workspace.Undo();
        Pump();
        Assert.DoesNotContain(host.Workspace.Current!.CustomCrops.OrEmpty(), crop => crop.Id == "wheat");
        Assert.Equal("Built-in · 4 stages", Note(host, "wheat"));
    }

    [AvaloniaFact]
    public void BuiltinNodeTypesAreListedInABlankProject()
    {
        using var host = new GameTestHost();
        host.Workspace.Open(host.Workspace.CreateProject("blank", "Blank"));
        OpenEntry(host, "Node types", "node-boulder");
        var list = FindByName<ListBox>(host.Window, "ContentEntities");
        Assert.Equal(9, list.ItemCount);
        Assert.Equal("Built-in · 6 hp · pickaxe t2 · no respawn", Note(host, "node-boulder"));
        Assert.Equal("This is a built-in node type. Customize it to change it.", Text(host, "ContentMessage"));
        var card = Card(host, "NodeTypeSummary");
        Assert.Contains("pickaxe (tier 2)", card);
        Assert.Contains("Stone ×4–8", card);

        Press(host, "CustomizeBuiltinButton");
        Pump();
        Assert.Equal("node-boulder", Assert.Single(host.Workspace.Current!.NodeTypes).Id);
        Assert.Equal(9, list.ItemCount);
        Assert.Equal("Replaces built-in · 6 hp · pickaxe t2 · no respawn", Note(host, "node-boulder"));
        Type(host, "ContentField_Health", "2");
        Assert.Contains("2 hits", Card(host, "NodeTypeSummary"));
        Press(host, "SaveContentButton");
        Pump();
        Assert.Equal(2, host.Workspace.Current!.NodeTypes[0].Health);

        Press(host, "DeleteContentButton");
        Pump();
        Assert.Empty(host.Workspace.Current!.NodeTypes);
        Assert.Equal("Built-in · 6 hp · pickaxe t2 · no respawn", Note(host, "node-boulder"));
    }

    [AvaloniaFact]
    public void ListEntriesShowTheirArt()
    {
        using var host = new GameTestHost();
        var npcs = host.Workspace.Current!.Npcs;
        Assert.True(npcs.Length >= 2);
        var asset = ArtImport.FromBytes(host.Workspace.Current, "portrait.png", Png(16, 16));
        host.Workspace.Apply(Edits.UpsertAsset(asset));
        host.Workspace.Apply(Edits.BindNpcVisual(npcs[0].Id, VisualRef.Default.WithAssetId(asset.Id)));
        OpenEntry(host, "NPCs", npcs[0].Id);

        // The Rust renderer draws the art into the row's thumbnail slot.
        var drawn = Assert.IsType<Image>(FindByName<Border>(host.Window, $"ContentThumb_{npcs[0].Id}").Child);
        Assert.Equal(new Avalonia.PixelSize(28, 28), Assert.IsAssignableFrom<Avalonia.Media.Imaging.Bitmap>(drawn.Source).PixelSize);
        // Entries without art keep an empty slot, so the names line up.
        var empty = FindByName<Border>(host.Window, $"ContentThumb_{npcs[1].Id}");
        Assert.Null(empty.Child);
        Assert.Equal(28, empty.Width);
        Assert.Equal($"{npcs[1].Name}  ·  {npcs[1].Id}", AutomationProperties.GetName(ListEntry(host, npcs[1].Id)));
        // Categories without art have no slot.
        FindByName<ContentEditorView>(host.Window, "ContentEditorView").SelectCategory("Quests");
        Pump();
        Assert.Null(TryFindByName<Border>(host.Window, $"ContentThumb_{host.Workspace.Current!.Quests[0].Id}"));
    }
}
