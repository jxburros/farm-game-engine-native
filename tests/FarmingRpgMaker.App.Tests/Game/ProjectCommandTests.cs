using FarmEngine.Authoring.Net;
using Avalonia.Headless.XUnit;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Projects;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

public sealed class ProjectCommandTests
{
    private sealed class FakeDialogs : IProjectDialogs
    {
        public NewProjectChoice? NewChoice { get; set; }

        public string? OpenChoice { get; set; }

        public (string FileName, string Json)? ImportFile { get; set; }

        public string? ExportedJson { get; private set; }

        public string? ExportedName { get; private set; }

        public List<(string Title, IReadOnlyList<string> Errors)> Errors { get; } = [];

        public Task<NewProjectChoice?> ChooseNewProjectAsync(IShellHost shell, IReadOnlyList<FarmEngine.Authoring.ProjectTemplateInfo> templates)
        {
            Assert.Equal(["starter", "cozy", "quest", "blank"], templates.Select(t => t.Id));
            return Task.FromResult(NewChoice);
        }

        public Task<string?> ChooseProjectAsync(IShellHost shell, ProjectStore store, string? currentId) => Task.FromResult(OpenChoice);

        public Task<(string FileName, string Json)?> PickImportFileAsync(IShellHost shell) => Task.FromResult(ImportFile);

        public Task<string?> SaveExportAsync(IShellHost shell, string suggestedFileName, string json)
        {
            ExportedName = suggestedFileName;
            ExportedJson = json;
            return Task.FromResult<string?>(suggestedFileName);
        }

        public Task ShowErrorsAsync(IShellHost shell, string title, IReadOnlyList<string> errors)
        {
            Errors.Add((title, errors));
            return Task.CompletedTask;
        }

        public Task<string?> PickFolderAsync(IShellHost shell, string title, string? startFolder) => Task.FromResult<string?>(null);

        public Task ShowExportGameAsync(IShellHost shell, FarmingRpgMaker.App.ViewModels.ExportGameViewModel viewModel) => Task.CompletedTask;

        /// <summary>Answers to the unsaved-changes prompt, in order (then Stay).</summary>
        public Queue<UnsavedChangesChoice> UnsavedAnswers { get; } = new();

        public List<(string Error, string DiscardText, string? Note)> UnsavedPrompts { get; } = [];

        public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(IShellHost shell, string error, string discardText, string? note)
        {
            UnsavedPrompts.Add((error, discardText, note));
            return Task.FromResult(UnsavedAnswers.Count > 0 ? UnsavedAnswers.Dequeue() : UnsavedChangesChoice.Stay);
        }
    }

    /// <summary>Makes the project file unwritable: a folder takes its place.</summary>
    private static string BlockProjectFile(GameTestHost host)
    {
        var path = host.Workspace.Store.PathFor(host.Workspace.Current!.Id);
        File.Delete(path);
        Directory.CreateDirectory(path);
        return path;
    }

    [AvaloniaFact]
    public void NewProject_WhenItCannotBeSaved_SaysSoInsteadOfCreated()
    {
        var dialogs = new FakeDialogs { NewChoice = new NewProjectChoice(ProjectTemplates.Blank, "Unsaved Farm") };
        using var host = new GameTestHost(dialogs: dialogs);
        // The index can't be written: every save fails.
        var index = host.Workspace.Store.IndexPath;
        File.Delete(index);
        Directory.CreateDirectory(index);

        host.ViewModel.NewProjectCommand.Execute(null);
        PumpUntil(() => host.Workspace.Current?.Name == "Unsaved Farm", "new project");

        Assert.NotNull(host.Workspace.SaveError);
        Assert.Contains("is open, but it could not be saved", host.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Created", host.ViewModel.StatusMessage, StringComparison.Ordinal);
        Directory.Delete(index);
    }

    [AvaloniaFact]
    public void Open_WithASettingsFileThatCannotBeWritten_StillOpensTheProjectEverywhere()
    {
        var dialogs = new FakeDialogs();
        using var host = new GameTestHost(dialogs: dialogs);
        var other = host.Workspace.CreateProject(ProjectTemplates.Quest, "Quest Land");
        host.Workspace.Store.Save(other);
        var settings = host.Workspace.Settings.FilePath;
        File.Delete(settings);
        Directory.CreateDirectory(settings);
        dialogs.OpenChoice = other.Id;

        host.ViewModel.OpenProjectCommand.Execute(null);
        PumpUntil(() => host.Workspace.Current?.Id == other.Id, "open");

        // The views follow the new project although remembering it failed.
        Assert.Equal("Quest Land", host.ViewModel.ProjectName);
        Assert.Equal("Opened \"Quest Land\".", host.ViewModel.StatusMessage);
        Directory.Delete(settings);
    }

    [AvaloniaFact]
    public void SwitchingProjects_WithEditsThatCannotBeSaved_AsksFirst()
    {
        var dialogs = new FakeDialogs { NewChoice = new NewProjectChoice(ProjectTemplates.Blank, "Next Farm") };
        using var host = new GameTestHost(dialogs: dialogs);
        var path = BlockProjectFile(host);
        host.Workspace.Apply(FarmEngine.Authoring.Edits.SetProjectInfo("Unsaved Farm", host.Workspace.Current!.Version));

        // Stay: nothing is switched and the edits stay open.
        host.ViewModel.NewProjectCommand.Execute(null);
        PumpUntil(() => dialogs.UnsavedPrompts.Count == 1, "prompt");
        Pump();
        Assert.Equal("Unsaved Farm", host.Workspace.Current!.Name);

        // Retry (still failing), Export a copy, then go on without them.
        dialogs.UnsavedAnswers.Enqueue(UnsavedChangesChoice.Retry);
        dialogs.UnsavedAnswers.Enqueue(UnsavedChangesChoice.Export);
        dialogs.UnsavedAnswers.Enqueue(UnsavedChangesChoice.Discard);
        host.ViewModel.NewProjectCommand.Execute(null);
        PumpUntil(() => host.Workspace.Current?.Name == "Next Farm", "switched");

        Assert.Equal(4, dialogs.UnsavedPrompts.Count);
        Assert.StartsWith("Still can't save", dialogs.UnsavedPrompts[2].Note, StringComparison.Ordinal);
        Assert.StartsWith("A copy was exported", dialogs.UnsavedPrompts[3].Note, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Unsaved Farm\"", dialogs.ExportedJson, StringComparison.Ordinal);
        Directory.Delete(path);
    }

    [AvaloniaFact]
    public void NewProject_FromTemplate_LeavesPlayModeAndOpensIt()
    {
        var dialogs = new FakeDialogs { NewChoice = new NewProjectChoice(ProjectTemplates.Cozy, "My Cozy Farm") };
        using var host = new GameTestHost(dialogs: dialogs);
        host.EnterPlay();

        host.ViewModel.NewProjectCommand.Execute(null);
        PumpUntil(() => host.Workspace.Current?.Name == "My Cozy Farm", "new project");

        Assert.Equal(EditorMode.Edit, host.ViewModel.Mode);
        Assert.Equal("Editing: My Cozy Farm", FindByName<Avalonia.Controls.TextBlock>(host.Window, "HeaderSubtitle").Text);
        Assert.False(host.Workspace.Current!.Settings.EnergyEnabled); // cozy template
        Assert.Equal(2, host.Workspace.Store.List().Count);
        Assert.Contains("Cozy Garden template", host.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void OpenProject_SwitchesToTheChosenProject()
    {
        var dialogs = new FakeDialogs();
        using var host = new GameTestHost(dialogs: dialogs);
        var starterId = host.Workspace.Current!.Id;
        var other = host.Workspace.CreateProject(ProjectTemplates.Quest, "Quest Land");
        host.Workspace.Store.Save(other);
        dialogs.OpenChoice = other.Id;

        host.ViewModel.OpenProjectCommand.Execute(null);
        PumpUntil(() => host.Workspace.Current?.Id == other.Id, "open");

        Assert.Equal("Quest Land", host.ViewModel.ProjectName);
        Assert.Equal(other.Id, host.Workspace.Settings.Load().LastProjectId);
        Assert.NotEqual(starterId, host.Workspace.Current!.Id);
    }

    [AvaloniaFact]
    public void ImportAndExport_RoundTripWebJson()
    {
        var dialogs = new FakeDialogs
        {
            ImportFile = ("project-v1.json", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "project-v1.json"))),
        };
        using var host = new GameTestHost(dialogs: dialogs);

        host.ViewModel.ImportProjectJsonCommand.Execute(null);
        PumpUntil(() => host.ViewModel.StatusMessage.StartsWith("Imported", StringComparison.Ordinal), "import");
        Assert.Contains("upgraded from schema v1", host.ViewModel.StatusMessage, StringComparison.Ordinal);
        var imported = host.Workspace.Current!;

        host.ViewModel.ExportProjectJsonCommand.Execute(null);
        PumpUntil(() => dialogs.ExportedJson is not null, "export");
        Assert.EndsWith(".json", dialogs.ExportedName, StringComparison.Ordinal);
        var reparsed = ProjectMigrations.migrateProjectText(dialogs.ExportedJson!);
        Assert.True(reparsed.Ok, string.Join("; ", reparsed.Errors));
        Assert.False(reparsed.Migrated);
        Assert.Equal(ProjectStore.ToJson(imported), ProjectStore.ToJson(reparsed.Data!.Value));
        Assert.Contains("\n  \"scenes\": [", dialogs.ExportedJson, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Import_ShowsMigrationErrors()
    {
        var dialogs = new FakeDialogs { ImportFile = ("future.json", """{ "schemaVersion": 99, "scenes": [] }""") };
        using var host = new GameTestHost(dialogs: dialogs);
        var before = host.Workspace.Current!.Id;

        host.ViewModel.ImportProjectJsonCommand.Execute(null);
        PumpUntil(() => dialogs.Errors.Count > 0, "error dialog");

        Assert.Contains("could not be imported", dialogs.Errors[0].Title, StringComparison.Ordinal);
        Assert.Contains("newer than this engine supports", dialogs.Errors[0].Errors[0], StringComparison.Ordinal);
        Assert.Equal(before, host.Workspace.Current!.Id);
    }

    [Theory]
    [InlineData("Sunny Acres", "sunny-acres.json")]
    [InlineData("  ", "farming-game.json")]
    [InlineData("Émile's Farm!!", "mile-s-farm.json")]
    public void SuggestedFileName_IsASlug(string name, string expected) =>
        Assert.Equal(expected, ProjectCommandHandler.SuggestedFileName(name));
}
