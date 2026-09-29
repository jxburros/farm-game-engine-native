using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmEngine.Authoring.Net;
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

    [AvaloniaFact]
    public void OpenProjectDialog_RenamesAndDuplicatesProjects()
    {
        using var host = new GameTestHost();
        var other = host.Workspace.CreateProject(ProjectTemplates.Cozy, "Cozy Corner");
        other = other.WithExport(FarmEngine.Authoring.ExportSettingsForm.Current(other));
        host.Workspace.Store.Save(other);
        var openId = host.Workspace.Current!.Id;

        host.ViewModel.OpenProjectCommand.Execute(null);
        var dialog = OpenedDialog(host, "OpenProjectWindow");
        var list = FindByName<ListBox>(dialog, "ProjectList");
        list.SelectedItem = list.Items.OfType<ListBoxItem>().First(i => Equals(i.Tag, other.Id));
        Pump();
        var name = FindByName<TextBox>(dialog, "RenameProjectName");
        Assert.Equal("Cozy Corner", name.Text);

        // Duplicate: a new id and "(copy)" name, selected in the list; the original is untouched.
        Click(dialog, FindByName<Button>(dialog, "DuplicateProjectButton"));
        Assert.Equal(3, list.ItemCount);
        var copyId = (string)((ListBoxItem)list.SelectedItem!).Tag!;
        Assert.NotEqual(other.Id, copyId);
        var copy = host.Workspace.Store.Load(copyId).Project!;
        Assert.Equal("Cozy Corner (copy)", copy.Name);
        Assert.NotEqual(other.Export.OrNull()!.GameId, copy.Export.OrNull()!.GameId);
        Assert.Equal("Cozy Corner", host.Workspace.Store.Load(other.Id).Project!.Name);
        Assert.Contains("Created", FindByName<TextBlock>(dialog, "ProjectListHint").Text);

        // Rename a stored project.
        name.Text = "  Copy Farm ";
        Click(dialog, FindByName<Button>(dialog, "RenameProjectButton"));
        Assert.Equal("Copy Farm", host.Workspace.Store.Load(copyId).Project!.Name);
        Assert.Equal(copyId, ((ListBoxItem)list.SelectedItem!).Tag);
        name.Text = " ";
        Pump();
        Assert.False(FindByName<Button>(dialog, "RenameProjectButton").IsEnabled);

        // Rename the open project: the open document takes the name when the dialog closes.
        list.SelectedItem = list.Items.OfType<ListBoxItem>().First(i => Equals(i.Tag, openId));
        Pump();
        name.Text = "Renamed Farm";
        Click(dialog, FindByName<Button>(dialog, "RenameProjectButton"));
        dialog.Close();
        PumpUntil(() => host.Workspace.Current?.Name == "Renamed Farm", "open project renamed");
        Assert.Equal(openId, host.Workspace.Current!.Id);
        host.Workspace.FlushPendingSave();
        Assert.Equal("Renamed Farm", host.Workspace.Store.Load(openId).Project!.Name);
        Assert.True(host.Workspace.Undo());
        Assert.Equal("Starter Farm", host.Workspace.Current!.Name);
    }
}
