using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>Duplicate (crops, actions, minigames) and Add to inventory (items) in the content editor.</summary>
public sealed class ContentActionTests
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

    [AvaloniaTheory]
    [InlineData("Actions")]
    [InlineData("Minigames")]
    [InlineData("Crops")]
    public void DuplicateCopiesTheSelectedEntryAsOneUndoStep(string category)
    {
        using var host = new GameTestHost();
        OpenContent(host, category);
        var duplicate = FindByName<Button>(host.Window, "DuplicateContentButton");
        Assert.True(duplicate.IsVisible);
        Assert.False(duplicate.IsEnabled);
        Assert.False(FindByName<Button>(host.Window, "AddToInventoryButton").IsVisible);
        Press(host, "AddContentButton");
        var list = FindByName<ListBox>(host.Window, "ContentEntities");
        var count = list.ItemCount;
        var original = (string)((ListBoxItem)list.SelectedItem!).Tag!;
        Assert.True(duplicate.IsEnabled);

        Press(host, "DuplicateContentButton");
        Assert.Equal(count + 1, list.ItemCount);
        var copy = (string)((ListBoxItem)list.SelectedItem!).Tag!;
        Assert.NotEqual(original, copy);
        Assert.StartsWith("Duplicated as", FindByName<TextBlock>(host.Window, "ContentMessage").Text);
        host.Workspace.Undo();
        Assert.Equal(count, list.ItemCount);
    }

    [AvaloniaFact]
    public void DuplicateIsHiddenWhereTheWebHasNone()
    {
        using var host = new GameTestHost();
        OpenContent(host, "Quests");
        Assert.False(FindByName<Button>(host.Window, "DuplicateContentButton").IsVisible);
    }

    [AvaloniaFact]
    public void AddToInventoryStacksTheSelectedItem()
    {
        using var host = new GameTestHost();
        OpenContent(host, "Items");
        var button = FindByName<Button>(host.Window, "AddToInventoryButton");
        Assert.True(button.IsVisible);
        Assert.False(button.IsEnabled);
        var list = FindByName<ListBox>(host.Window, "ContentEntities");
        list.SelectedItem = list.Items.OfType<ListBoxItem>().First(item => Equals(item.Tag, "seed-wheat"));
        Pump();
        double Wheat() => host.Workspace.Current!.Player.Inventory.First(slot => slot.Item.Id == "seed-wheat").Quantity;
        var before = Wheat();
        Press(host, "AddToInventoryButton");
        Assert.Equal(before + 1, Wheat());
        Assert.StartsWith("Added ", FindByName<TextBlock>(host.Window, "ContentMessage").Text);
        host.Workspace.Undo();
        Assert.Equal(before, Wheat());
    }
}
