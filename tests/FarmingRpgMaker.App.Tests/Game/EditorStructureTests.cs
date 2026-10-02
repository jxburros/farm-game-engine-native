using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// The pieces taken out of the large editor views (#147): named tabs, the editor's link
/// routing, the shared display formats and the content form's schema helpers.
/// </summary>
public sealed class EditorStructureTests
{
    [AvaloniaFact]
    public void EditorTabsMatchTheTabControlOrder()
    {
        using var host = new GameTestHost();
        var tabs = FindByName<TabControl>(host.Window, "EditorTabs");
        var view = FindByName<EditModeView>(host.Window, "EditModeView");

        var headers = tabs.Items.OfType<TabItem>().Select(item => item.Header as string).ToList();
        Assert.Equal(Enum.GetNames<EditorTab>(), headers);
        Assert.Equal(EditorTab.Map, view.SelectedTab);

        foreach (var tab in Enum.GetValues<EditorTab>())
        {
            view.SelectedTab = tab;
            Pump();
            Assert.Equal((int)tab, tabs.SelectedIndex);
            Assert.Equal(tab == EditorTab.Map, FindByName<Grid>(host.Window, "MapSidePanel").IsVisible);
        }
    }

    [Theory]
    [InlineData("scenes", EditorTab.Map, null)]
    [InlineData("problems", EditorTab.Problems, null)]
    [InlineData("assets", EditorTab.Art, null)]
    [InlineData("npcs", EditorTab.Content, "NPCs")]
    [InlineData("craft", EditorTab.Content, "Recipes")]
    [InlineData("wildlife", EditorTab.Content, "Animal species")]
    [InlineData("actions", EditorTab.Content, "Actions")]
    public void WorkshopLinksOpenTheirTab(string key, EditorTab tab, string? category) =>
        Assert.Equal((tab, category), EditorNavigation.WorkshopTarget(key));

    [Fact]
    public void UnknownWorkshopLinksGoNowhere() => Assert.Null(EditorNavigation.WorkshopTarget("nowhere"));

    [Theory]
    [InlineData("npc", "NPCs")]
    [InlineData("fishTable", "Fish tables")]
    [InlineData("minigame", "Minigames")]
    [InlineData("scene", null)]
    [InlineData("settings", null)]
    public void ProblemKindsMapToContentCategories(string kind, string? category) =>
        Assert.Equal(category, EditorNavigation.ContentCategory(kind));

    [Fact]
    public void SizesReadTheSameEverywhere()
    {
        Assert.Equal("12 B", DisplayFormat.FileSize(12));
        Assert.Equal("1.5 KB", DisplayFormat.FileSize(1536));
        Assert.Equal("850 bytes", DisplayFormat.DownloadSize(850));
        Assert.Equal("48.7 MB", DisplayFormat.DownloadSize(48_700_000));
        Assert.Equal("0.5", DisplayFormat.Number(0.5));
    }

    [Fact]
    public void RelativeTimesCountMinutesThenNameTheDay()
    {
        var now = new DateTimeOffset(2026, 9, 30, 15, 0, 0, TimeSpan.Zero).ToLocalTime();
        Assert.Equal("just now", DisplayFormat.RelativeTime(now.AddSeconds(-20), now));
        Assert.Equal("1 minute ago", DisplayFormat.RelativeTime(now.AddMinutes(-1), now));
        Assert.Equal("42 minutes ago", DisplayFormat.RelativeTime(now.AddMinutes(-42), now));
        var lastWeek = now.AddDays(-7);
        Assert.Equal(lastWeek.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) + " at " + lastWeek.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture),
            DisplayFormat.RelativeTime(lastWeek, now));
    }

    [Fact]
    public void ContentFormSchemaLabelsAndTypes()
    {
        Assert.Equal("Base Value", ContentFormSchema.Title("BaseValue"));
        Assert.Equal("Scene", ContentFormSchema.TrimId("Scene Id"));
        Assert.Equal("Items", ContentFormSchema.TrimId("Item Ids"));
        Assert.Equal("a_b", ContentFormSchema.Join("a", "b"));
        Assert.Equal("b", ContentFormSchema.Join("", "b"));
        Assert.True(ContentFormSchema.IsRecord(typeof(Item)));
        Assert.DoesNotContain(ContentFormSchema.FormProperties(typeof(Item)), property => property.Name == "Extra");
        Assert.Equal("x must be from 1 to 3.", ContentFormSchema.RangeText("x", 1, 3));

        var array = new JsonArray(1, 2, 3);
        ContentFormSchema.Move(array, 0, 1);
        Assert.Equal("[2,1,3]", array.ToJsonString());
        ContentFormSchema.Move(array, 0, -1);
        Assert.Equal("[2,1,3]", array.ToJsonString());
        Assert.Equal("text", ContentFormSchema.StringOf(ContentFormSchema.Literal("text")));
        Assert.Equal("4", ContentFormSchema.NumberText(ContentFormSchema.Literal("4")));
    }
}
