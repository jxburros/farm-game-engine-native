using System.Text.Json.Nodes;
using FarmEngine.Content;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Tests.Game;

public sealed class ProjectStoreTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "project-v1.json");

    [Fact]
    public void SaveThenLoad_RoundTripsTheProject_AndIndexesIt()
    {
        using var dir = new TempDir();
        var time = new TestTime(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var store = new ProjectStore(dir.Path, time);
        var project = Templates.CreateNewProject(ProjectTemplates.Starter, "Sunny Acres", "proj-test", 0);

        store.Save(project);

        Assert.True(File.Exists(Path.Combine(dir.Path, "projects", "proj-test.json")));
        var loaded = store.Load("proj-test");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Errors));
        Assert.Null(loaded.MigratedFrom);
        Assert.Equal(ProjectStore.ToJson(project), ProjectStore.ToJson(loaded.Project!));

        var summary = Assert.Single(store.List());
        Assert.Equal(("proj-test", "Sunny Acres"), (summary.Id, summary.Name));
        Assert.Equal(time.Now, summary.UpdatedAt);
        Assert.True(summary.SizeBytes > 1000);
    }

    [Fact]
    public void Save_IsAtomic_ReplacesTheFileAndLeavesNoTempFiles()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);
        var project = Templates.CreateNewProject(ProjectTemplates.Blank, "First", "proj-a", 0);
        store.Save(project);
        store.Save(project with { Name = "Second" });

        var files = Directory.GetFiles(Path.Combine(dir.Path, "projects"));
        Assert.Equal(["index.json", "proj-a.json"], files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("Second", store.Load("proj-a").Project!.Name);
        Assert.Equal("Second", Assert.Single(store.List()).Name);
        // Web-compatible JSON: camelCase, indented, parseable by the migrations.
        var text = File.ReadAllText(Path.Combine(dir.Path, "projects", "proj-a.json"));
        Assert.Contains("\n  \"schemaVersion\": 8", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MigratesTheV1Fixture()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);
        Directory.CreateDirectory(store.ProjectsDirectory);
        File.Copy(FixturePath, store.PathFor("legacy"));

        // Files the index doesn't know are adopted (name read from the file).
        var summary = Assert.Single(store.List());
        Assert.Equal("legacy", summary.Id);

        var loaded = store.Load("legacy");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Errors));
        Assert.Equal(1, loaded.MigratedFrom);
        Assert.Equal(ProjectSchema.CurrentProjectSchemaVersion, loaded.Project!.SchemaVersion);
        Assert.NotEmpty(loaded.Project.Scenes);
    }

    [Fact]
    public void Load_ReportsErrorsForBrokenFiles()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);
        Directory.CreateDirectory(store.ProjectsDirectory);
        File.WriteAllText(store.PathFor("broken"), "{ not json");
        File.WriteAllText(store.PathFor("future"), """{ "schemaVersion": 999, "scenes": [] }""");

        var broken = store.Load("broken");
        Assert.False(broken.Ok);
        Assert.Contains("not valid JSON", Assert.Single(broken.Errors), StringComparison.Ordinal);
        var future = store.Load("future");
        Assert.False(future.Ok);
        Assert.Contains("newer than this engine supports", future.Errors[0], StringComparison.Ordinal);
        Assert.False(store.Load("missing").Ok);
    }

    [Fact]
    public void Import_CreatesANewProjectFromWebJson()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);

        var fromFixture = store.Import(File.ReadAllText(FixturePath));
        Assert.True(fromFixture.Ok, string.Join("; ", fromFixture.Errors));
        Assert.Equal(1, fromFixture.MigratedFrom);
        Assert.StartsWith("proj-", fromFixture.Project!.Id, StringComparison.Ordinal);
        Assert.Equal("tiles", fromFixture.Project.Mode);

        // An exported game (no editor fields) is layered over a blank project.
        var exported = JsonNode.Parse(ProjectStore.ToJson(DefaultContent.CreateInitialProject(0)))!.AsObject();
        foreach (var key in new[] { "id", "mode", "selectedTileType", "selectedNPCId", "selectedItemId", "eventFlags", "currentTime", "player" })
        {
            exported.Remove(key);
        }

        var fromExport = store.Import(exported.ToJsonString());
        Assert.True(fromExport.Ok, string.Join("; ", fromExport.Errors));
        Assert.Equal("My Farming Game", fromExport.Project!.Name);
        Assert.NotEmpty(fromExport.Project.Npcs);

        Assert.False(store.Import("[1, 2]").Ok);
        Assert.False(store.Import("nope").Ok);
    }

    [Fact]
    public void Delete_RemovesFileAndIndexEntry()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);
        store.Save(Templates.CreateNewProject(ProjectTemplates.Blank, "A", "proj-a", 0));
        store.Save(Templates.CreateNewProject(ProjectTemplates.Blank, "B", "proj-b", 0));

        store.Delete("proj-a");

        Assert.False(store.Exists("proj-a"));
        Assert.Equal(["proj-b"], store.List().Select(p => p.Id));
    }

    [Fact]
    public void AppSettings_PreserveOtherSections()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "updates": { "channel": "beta" } }""");
        var settings = new AppSettingsStore(path);

        settings.Save(new WorkspaceSettings { LastProjectId = "proj-x" });

        Assert.Equal("proj-x", settings.Load().LastProjectId);
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("beta", root["updates"]!["channel"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("con", "con_")]
    [InlineData("NUL", "NUL_")]
    [InlineData("com1.backup", "com1.backup_")]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("..", "project")]
    [InlineData("project-1", "project-1")]
    public void SafeFileName_AvoidsReservedAndInvalidNames(string id, string expected) =>
        Assert.Equal(expected, ProjectStore.SafeFileName(id));
}
