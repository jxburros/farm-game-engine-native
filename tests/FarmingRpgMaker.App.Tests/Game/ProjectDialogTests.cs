using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmEngine.Content;
using FarmingRpgMaker.App.Projects;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>The real Avalonia project dialogs (modal windows over the main window).</summary>
public sealed class ProjectDialogTests
{
    private static readonly string? OutputDir = Environment.GetEnvironmentVariable("FRM_SCREENSHOT_DIR");

    private static Window OpenedDialog(GameTestHost host, string name)
    {
        Window? found = null;
        PumpUntil(() => (found = host.Window.OwnedWindows.FirstOrDefault(w => w.Name == name)) is not null, name);
        return found!;
    }

    [AvaloniaFact]
    public void NewProjectDialog_CreatesFromTheChosenTemplate()
    {
        using var host = new GameTestHost();
        host.ViewModel.NewProjectCommand.Execute(null);
        var dialog = OpenedDialog(host, "NewProjectWindow");

        FindByName<TextBox>(dialog, "NewProjectName").Text = "Story Time";
        FindByName<RadioButton>(dialog, $"Template_{ProjectTemplates.Quest}").IsChecked = true;
        if (!string.IsNullOrEmpty(OutputDir))
        {
            GameTestHost.Capture(dialog).Save(Path.Combine(OutputDir, "new-project.png"));
        }

        Click(dialog, FindByName<Button>(dialog, "CreateProjectButton"));
        PumpUntil(() => host.Workspace.Current?.Name == "Story Time", "created");
        Assert.Contains(host.Workspace.Current!.Npcs, n => n.Id == "npc-elder");
    }

    [AvaloniaFact]
    public void OpenProjectDialog_ListsProjects_DeletesWithConfirmation_AndOpens()
    {
        using var host = new GameTestHost();
        var a = host.Workspace.CreateProject(ProjectTemplates.Blank, "Blank Slate");
        host.Workspace.Store.Save(a);
        var b = host.Workspace.CreateProject(ProjectTemplates.Cozy, "Cozy Corner");
        host.Workspace.Store.Save(b);

        host.ViewModel.OpenProjectCommand.Execute(null);
        var dialog = OpenedDialog(host, "OpenProjectWindow");
        var list = FindByName<ListBox>(dialog, "ProjectList");
        Assert.Equal(3, list.ItemCount);
        if (!string.IsNullOrEmpty(OutputDir))
        {
            GameTestHost.Capture(dialog).Save(Path.Combine(OutputDir, "open-project.png"));
        }

        list.SelectedItem = list.Items.OfType<ListBoxItem>().First(i => Equals(i.Tag, a.Id));
        Pump();
        Click(dialog, FindByName<Button>(dialog, "DeleteProjectButton"));
        Assert.True(host.Workspace.Store.Exists(a.Id), "first click only asks for confirmation");
        Click(dialog, FindByName<Button>(dialog, "DeleteProjectButton"));
        Assert.False(host.Workspace.Store.Exists(a.Id));
        Assert.Equal(2, list.ItemCount);

        list.SelectedItem = list.Items.OfType<ListBoxItem>().First(i => Equals(i.Tag, b.Id));
        Pump();
        Click(dialog, FindByName<Button>(dialog, "OpenSelectedProjectButton"));
        PumpUntil(() => host.Workspace.Current?.Id == b.Id, "opened");
        Assert.Equal("Cozy Corner", host.ViewModel.ProjectName);
    }
}
