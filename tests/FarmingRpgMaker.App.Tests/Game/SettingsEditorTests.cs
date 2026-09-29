using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>Project Settings: weather odds, the mine card and the season arrows.</summary>
public sealed class SettingsEditorTests
{
    private static void OpenSettings(GameTestHost host)
    {
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 3;
        Pump();
    }

    private static void Press(GameTestHost host, string name) =>
        FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void WeatherOddsSaveAsOneUndoStep()
    {
        using var host = new GameTestHost();
        OpenSettings(host);
        var before = host.Workspace.Current!;
        Assert.Equal("1", FindByName<TextBox>(host.Window, "Weather_spring_storm").Text);
        Assert.Equal("0", FindByName<TextBox>(host.Window, "Weather_spring_snow").Text);

        FindByName<TextBox>(host.Window, "Weather_spring_storm").Text = "4";
        FindByName<TextBox>(host.Window, "Weather_summer_rain").Text = "0";
        Press(host, "SaveWeatherButton");
        var project = host.Workspace.Current!;
        Assert.Equal(4, SettingsForm.WeatherWeight(project, "spring", "storm"));
        Assert.Equal(0, SettingsForm.WeatherWeight(project, "summer", "rain"));
        Assert.Equal("Weather odds saved.", FindByName<TextBlock>(host.Window, "WeatherMessage").Text);

        Press(host, "SaveWeatherButton");
        Assert.Equal("No changes were made.", FindByName<TextBlock>(host.Window, "WeatherMessage").Text);
        FindByName<TextBox>(host.Window, "Weather_fall_sun").Text = "lots";
        Press(host, "SaveWeatherButton");
        Assert.Contains("Could not save", FindByName<TextBlock>(host.Window, "WeatherMessage").Text);
        Assert.Same(project, host.Workspace.Current);

        host.Workspace.Undo();
        Assert.Same(before, host.Workspace.Current);
    }

    [AvaloniaFact]
    public void MineCardEnablesAndSavesClampedSettings()
    {
        using var host = new GameTestHost();
        OpenSettings(host);
        Assert.False(FindByName<StackPanel>(host.Window, "MineFields").IsVisible);
        var toggle = FindByName<CheckBox>(host.Window, "Mine_Enabled");
        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var project = host.Workspace.Current!;
        Assert.True(project.Mine.Enabled);
        Assert.Equal(project.StartSceneId, project.Mine.EntranceSceneId.OrNull());
        Assert.NotEmpty(project.Mine.Bands);
        Assert.True(FindByName<StackPanel>(host.Window, "MineFields").IsVisible);

        FindByName<TextBox>(host.Window, "Mine_EntranceX").Text = "6";
        FindByName<TextBox>(host.Window, "Mine_EntranceY").Text = "4";
        FindByName<TextBox>(host.Window, "Mine_Floors").Text = "0";
        FindByName<TextBox>(host.Window, "Mine_LadderChance").Text = "3";
        Press(host, "SaveMineButton");
        var mine = host.Workspace.Current!.Mine;
        Assert.Equal(6, mine.EntranceX.OrNullable());
        Assert.Equal(4, mine.EntranceY.OrNullable());
        Assert.Equal(1, mine.Floors);
        Assert.Equal(1, mine.LadderChance);
        Assert.Equal("1", FindByName<TextBox>(host.Window, "Mine_Floors").Text);

        toggle.IsChecked = false;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(host.Workspace.Current!.Mine.Enabled);
        Assert.Equal(6, host.Workspace.Current.Mine.EntranceX.OrNullable());
        host.Workspace.Undo();
        Assert.True(host.Workspace.Current!.Mine.Enabled);
    }

    [AvaloniaFact]
    public void SeasonArrowsReorderTheYear()
    {
        using var host = new GameTestHost();
        OpenSettings(host);
        Assert.False(FindByName<Button>(host.Window, "Season_Up_spring").IsEnabled);
        Assert.False(FindByName<Button>(host.Window, "Season_Down_winter").IsEnabled);
        Press(host, "Season_Up_summer");
        Assert.Equal(["summer", "spring", "fall", "winter"], host.Workspace.Current!.Settings.Calendar.Seasons.Select(s => s.Id));
        Assert.False(FindByName<Button>(host.Window, "Season_Up_summer").IsEnabled);
        Press(host, "Season_Down_fall");
        Assert.Equal(["summer", "spring", "winter", "fall"], host.Workspace.Current!.Settings.Calendar.Seasons.Select(s => s.Id));
        host.Workspace.Undo();
        host.Workspace.Undo();
        Assert.Equal(["spring", "summer", "fall", "winter"], host.Workspace.Current!.Settings.Calendar.Seasons.Select(s => s.Id));
    }
}
