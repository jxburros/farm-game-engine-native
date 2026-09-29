using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>Project Settings' skill levels: one XP row per level, saved with the other settings.</summary>
public sealed class SkillLevelCurveTests
{
    private static void OpenSettings(GameTestHost host)
    {
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 3;
        Pump();
    }

    private static void Press(GameTestHost host, string name) =>
        FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static List<string> Rows(GameTestHost host) =>
        [.. FindByName<StackPanel>(host.Window, "SkillLevels").Children.Select((_, i) => FindByName<TextBox>(host.Window, $"Setting_SkillLevel_{i}").Text ?? "")];

    private static string Message(GameTestHost host) => FindByName<TextBlock>(host.Window, "SettingsMessage").Text ?? "";

    [AvaloniaFact]
    public void LevelRowsAddRemoveAndSaveAsOneUndoStep()
    {
        using var host = new GameTestHost();
        OpenSettings(host);
        var before = host.Workspace.Current!;
        Assert.Equal(before.Settings.SkillLevelCurve.Select(xp => xp.ToString(System.Globalization.CultureInfo.InvariantCulture)), Rows(host));
        Assert.Equal(["0", "50", "150", "300", "500", "750", "1050", "1400", "1800", "2250"], Rows(host));
        Assert.Equal("Level 1 XP", AutomationProperties.GetName(FindByName<TextBox>(host.Window, "Setting_SkillLevel_1")));
        Assert.Contains("Level 1", VisibleTexts(FindByName<StackPanel>(host.Window, "SkillLevels")));

        // A new level continues the last step (2250 − 1800 = 450).
        Press(host, "AddSkillLevelButton");
        Assert.Equal("2700", FindByName<TextBox>(host.Window, "Setting_SkillLevel_10").Text);
        FindByName<TextBox>(host.Window, "Setting_SkillLevel_1").Text = "60";
        Press(host, "Setting_SkillLevelRemove_0");
        Assert.Equal(["60", "150", "300", "500", "750", "1050", "1400", "1800", "2250", "2700"], Rows(host));
        Assert.Equal("Remove level 0", AutomationProperties.GetName(FindByName<Button>(host.Window, "Setting_SkillLevelRemove_0")));
        Assert.Same(before, host.Workspace.Current);

        Press(host, "SaveSettingsButton");
        Assert.Equal("Settings saved.", Message(host));
        Assert.Equal([60.0, 150, 300, 500, 750, 1050, 1400, 1800, 2250, 2700], host.Workspace.Current!.Settings.SkillLevelCurve);
        host.Workspace.Undo();
        Assert.Same(before, host.Workspace.Current);
        Assert.Equal("0", FindByName<TextBox>(host.Window, "Setting_SkillLevel_0").Text);
    }

    [AvaloniaFact]
    public void InvalidLevelsAreRefused()
    {
        using var host = new GameTestHost();
        OpenSettings(host);
        var before = host.Workspace.Current;
        FindByName<TextBox>(host.Window, "Setting_SkillLevel_3").Text = "100";
        Press(host, "SaveSettingsButton");
        Assert.Equal("Could not save: Each level needs at least as much XP as the one before.", Message(host));
        foreach (var bad in new[] { "lots", "-5", "NaN", "Infinity" })
        {
            FindByName<TextBox>(host.Window, "Setting_SkillLevel_3").Text = bad;
            Press(host, "SaveSettingsButton");
            Assert.Equal("Could not save: Level 3 needs an XP amount of 0 or more.", Message(host));
        }

        Assert.Same(before, host.Workspace.Current);

        // Equal neighbours are fine; so is a curve that is whole again.
        FindByName<TextBox>(host.Window, "Setting_SkillLevel_3").Text = "150";
        Press(host, "SaveSettingsButton");
        Assert.Equal("Settings saved.", Message(host));
        Assert.Equal(150.0, host.Workspace.Current!.Settings.SkillLevelCurve[3]);
    }

    [AvaloniaFact]
    public void AnEmptyCurveCannotBeSavedAndNewLevelsStartAtZero()
    {
        using var host = new GameTestHost();
        OpenSettings(host);
        var before = host.Workspace.Current;
        while (FindByName<StackPanel>(host.Window, "SkillLevels").Children.Count > 0) Press(host, "Setting_SkillLevelRemove_0");
        Press(host, "SaveSettingsButton");
        Assert.Equal("Could not save: Add at least one skill level.", Message(host));
        Assert.Same(before, host.Workspace.Current);

        Press(host, "AddSkillLevelButton");
        Press(host, "AddSkillLevelButton");
        Press(host, "AddSkillLevelButton");
        Assert.Equal(["0", "100", "200"], Rows(host));
        Press(host, "SaveSettingsButton");
        Assert.Equal([0.0, 100, 200], host.Workspace.Current!.Settings.SkillLevelCurve);
    }
}
