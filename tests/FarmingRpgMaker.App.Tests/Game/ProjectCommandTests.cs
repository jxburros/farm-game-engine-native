using Avalonia.Headless.XUnit;
using FarmEngine.Content;
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

        public Task<NewProjectChoice?> ChooseNewProjectAsync(IShellHost shell, IReadOnlyList<TemplateInfo> templates)
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
        var reparsed = Migrations.MigrateProject(dialogs.ExportedJson!);
        Assert.True(reparsed.Ok, string.Join("; ", reparsed.Errors));
        Assert.False(reparsed.Migrated);
        Assert.Equal(ProjectStore.ToJson(imported), ProjectStore.ToJson(reparsed.Data!));
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
