using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>The Mods view's curated registry and "Export selection as pack".</summary>
public sealed class ModsRegistryTests
{
    private static ModsEditorView OpenMods(GameTestHost host)
    {
        FindByName<TabControl>(host.Window, "EditorTabs").SelectedIndex = 4;
        Pump();
        return FindByName<ModsEditorView>(host.Window, "ModsEditorView");
    }

    private static void Press(GameTestHost host, string name) =>
        FindByName<Button>(host.Window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Toggle(GameTestHost host, string name, bool value)
    {
        var box = FindByName<CheckBox>(host.Window, name);
        box.IsChecked = value;
        box.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaFact]
    public void RegistryInstallGoesThroughTheReviewAndThenShowsInstalled()
    {
        using var host = new GameTestHost();
        OpenMods(host);
        var install = FindByName<Button>(host.Window, "RegistryInstall_demo-glow-farm");
        Assert.True(install.IsEnabled);
        Assert.True(FindByName<Button>(host.Window, "RegistryInstall_my-first-mod").IsEnabled);
        Assert.Contains("Glow Farm Demo Mod · by farm-game-engine", AllVisibleText(host.Window));

        Press(host, "RegistryInstall_demo-glow-farm");
        Assert.Empty(host.Workspace.Current!.ContentPacks);
        Assert.True(FindByName<Button>(host.Window, "InstallPackButton").IsEnabled);
        Assert.Contains("Permissions", AllVisibleText(host.Window));
        Press(host, "InstallPackButton");
        Assert.Equal("demo-glow-farm", Assert.Single(host.Workspace.Current!.ContentPacks).Pack.Manifest.Id);
        install = FindByName<Button>(host.Window, "RegistryInstall_demo-glow-farm");
        Assert.False(install.IsEnabled);
        Assert.Equal("Installed", install.Content);
        host.Workspace.Undo();
        Assert.Empty(host.Workspace.Current!.ContentPacks);
    }

    [AvaloniaFact]
    public void ExportSelectionBuildsAValidPackOfTheTickedEntries()
    {
        using var host = new GameTestHost();
        var mods = OpenMods(host);
        var project = host.Workspace.Current!;
        // The web defaults: items and recipes.
        Assert.True(FindByName<CheckBox>(host.Window, "ExportCategory_items").IsChecked);
        Assert.True(FindByName<CheckBox>(host.Window, "ExportCategory_recipes").IsChecked);
        Assert.False(FindByName<CheckBox>(host.Window, "ExportCategory_npcs").IsChecked);

        FindByName<TextBox>(host.Window, "ExportPackName").Text = "Starter Goods";
        Toggle(host, "ExportCategory_recipes", false);
        Toggle(host, "ExportEntry_items_material-wood", false);
        Toggle(host, "ExportCategory_npcs", true);
        var result = mods.BuildSelectedPack();
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var pack = result.Pack!;
        Assert.Equal("starter-goods", pack.Manifest.Id);
        Assert.Equal("starter-goods.json", result.FileName);
        Assert.Equal(project.Items.Length - 1, pack.Content.Items.Length);
        Assert.DoesNotContain(pack.Content.Items, item => item.Id == "material-wood");
        Assert.Empty(pack.Content.Recipes);
        Assert.Equal(project.Npcs.Length, pack.Content.Npcs.Length);

        // The saved text installs back through the review.
        mods.ReviewPackJson(result.Text);
        Assert.True(FindByName<Button>(host.Window, "InstallPackButton").IsEnabled);

        foreach (var category in Mods.ExportCategories(project)) Toggle(host, $"ExportCategory_{category.Key}", false);
        Assert.False(mods.BuildSelectedPack().Ok);
        Assert.Same(project, host.Workspace.Current);
    }
}
