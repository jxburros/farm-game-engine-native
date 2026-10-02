using System.Text.Json.Nodes;
using FarmEngine.Authoring.Net;
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
        var project = FarmEngine.Authoring.ProjectCatalog.CreateNewProject(ProjectTemplates.Starter, "Sunny Acres", "proj-test", 0);

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
    public void RenameAndDuplicate_RewriteTheStoreAndIndex()
    {
        using var dir = new TempDir();
        var time = new TestTime(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var store = new ProjectStore(dir.Path, time);
        store.Save(FarmEngine.Authoring.ProjectCatalog.CreateNewProject(ProjectTemplates.Blank, "First", "proj-a", 0));

        var renamed = store.Rename("proj-a", " Meadow ");
        Assert.True(renamed.Ok, string.Join("; ", renamed.Errors));
        Assert.Equal("Meadow", store.Load("proj-a").Project!.Name);
        Assert.Equal("Meadow", Assert.Single(store.List()).Name);
        Assert.False(store.Rename("proj-a", "  ").Ok);
        Assert.False(store.Rename("proj-missing", "Name").Ok);

        var copy = store.Duplicate("proj-a");
        Assert.True(copy.Ok, string.Join("; ", copy.Errors));
        Assert.NotEqual("proj-a", copy.Project!.Id);
        Assert.StartsWith("proj-", copy.Project.Id, StringComparison.Ordinal);
        Assert.Equal("Meadow (copy)", store.Load(copy.Project.Id).Project!.Name);
        Assert.Equal(2, store.List().Count);
        Assert.False(store.Duplicate("proj-missing").Ok);
    }

    [Fact]
    public void Save_IsAtomic_ReplacesTheFileAndLeavesNoTempFiles()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);
        var project = FarmEngine.Authoring.ProjectCatalog.CreateNewProject(ProjectTemplates.Blank, "First", "proj-a", 0);
        store.Save(project);
        store.Save(project.WithName("Second"));

        var files = Directory.GetFiles(Path.Combine(dir.Path, "projects"));
        Assert.Equal(["index.json", "proj-a.json"], files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("Second", store.Load("proj-a").Project!.Name);
        Assert.Equal("Second", Assert.Single(store.List()).Name);
        // Web-compatible JSON: camelCase, indented, parseable by the migrations.
        var text = File.ReadAllText(Path.Combine(dir.Path, "projects", "proj-a.json"));
        Assert.Contains("\n  \"schemaVersion\": 9", text, StringComparison.Ordinal);
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
        var exported = JsonNode.Parse(ProjectStore.ToJson(FarmEngine.Authoring.ProjectCatalog.CreateInitialProject(0)))!.AsObject();
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
        store.Save(FarmEngine.Authoring.ProjectCatalog.CreateNewProject(ProjectTemplates.Blank, "A", "proj-a", 0));
        store.Save(FarmEngine.Authoring.ProjectCatalog.CreateNewProject(ProjectTemplates.Blank, "B", "proj-b", 0));

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

    [Fact]
    public void AppSettingsStore_DoesNotRewriteTheUpdateCentersSection()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var updates = new FarmingRpgMaker.Updates.JsonSettingsStore(path);
        updates.Save(new FarmingRpgMaker.Updates.UpdateSettings
        {
            Channel = FarmingRpgMaker.Updates.UpdateChannel.Prerelease,
            LastChecked = new DateTimeOffset(2026, 9, 27, 8, 30, 0, TimeSpan.FromHours(2)),
        });
        new AppSettingsStore(path).Save(new WorkspaceSettings { LastProjectId = "proj-x" });
        var afterWorkspace = File.ReadAllText(path);

        // Both stores serialize the whole file the same way: saving the updates section again
        // leaves the file byte-identical, and the timestamp keeps its readable "+02:00".
        updates.Save(updates.Load());
        Assert.Equal(afterWorkspace, File.ReadAllText(path));
        Assert.Contains("+02:00", afterWorkspace, StringComparison.Ordinal);
        Assert.Contains("\"prerelease\"", afterWorkspace, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("con", "%63on")]
    [InlineData("NUL", "%4EUL")]
    [InlineData("com1.backup", "%63om1.backup")]
    [InlineData("COM0", "%43OM0")]
    [InlineData("lpt¹", "%6Cpt¹")]
    [InlineData("CONOUT$", "%43ONOUT$")]
    [InlineData("a/b:c", "a%2Fb%3Ac")]
    [InlineData("a_b", "a_b")]
    [InlineData("100%", "100%25")]
    [InlineData("..", "%2E%2E")]
    [InlineData(".hidden", "%2Ehidden")]
    [InlineData("trailing. ", "trailing%2E%20")]
    [InlineData("project-1", "project-1")]
    [InlineData("proj-abc - Copy", "proj-abc - Copy")]
    public void SafeFileName_AvoidsReservedAndInvalidNames(string id, string expected)
    {
        Assert.Equal(expected, ProjectStore.SafeFileName(id));
        Assert.Equal(id, ProjectStore.IdForFileName(expected));
    }

    [Fact]
    public void SafeFileName_GivesDifferentIdsDifferentFiles()
    {
        string[] ids = ["a:b", "a_b", "a%3Ab", "con", "con_", "%63on", "x.", "x", "x%2E"];
        Assert.Equal(ids.Length, ids.Select(ProjectStore.SafeFileName).Distinct(StringComparer.Ordinal).Count());
        Assert.Null(ProjectStore.IdForFileName("100% farm"));
        Assert.Null(ProjectStore.IdForFileName("con"));
    }

    private static string WebExport(string name) =>
        ProjectStore.ToJson(FarmEngine.Authoring.ProjectCatalog.CreateNewProject(ProjectTemplates.Blank, name, "project-1", 0));

    [Fact]
    public void HandCopiedFiles_ThatShareAnId_EachSaveToTheirOwnFile()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);
        Directory.CreateDirectory(store.ProjectsDirectory);
        File.WriteAllText(Path.Combine(store.ProjectsDirectory, "farmA.json"), WebExport("Farm A"));
        File.WriteAllText(Path.Combine(store.ProjectsDirectory, "farmB.json"), WebExport("Farm B"));

        Assert.Equal(["farmA", "farmB"], store.List().Select(p => p.Id).Order(StringComparer.Ordinal));
        var a = store.Load("farmA").Project!;
        var b = store.Load("farmB").Project!;
        Assert.Equal(("farmA", "farmB"), (a.Id, b.Id));

        store.Save(a.WithName("Farm A edited"));
        store.Save(b.WithName("Farm B edited"));

        Assert.False(store.Exists("project-1"));
        Assert.Equal("Farm A edited", store.Load("farmA").Project!.Name);
        Assert.Equal("Farm B edited", store.Load("farmB").Project!.Name);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void AnExplorerCopy_SavesToTheCopy_NotTheOriginal()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);
        store.Save(FarmEngine.Authoring.ProjectCatalog.CreateNewProject(ProjectTemplates.Blank, "Original", "proj-abc", 0));
        File.Copy(store.PathFor("proj-abc"), Path.Combine(store.ProjectsDirectory, "proj-abc - Copy.json"));

        var copy = store.Load("proj-abc - Copy");
        Assert.True(copy.Ok, string.Join("; ", copy.Errors));
        Assert.Equal("proj-abc - Copy", copy.Project!.Id);
        store.Save(copy.Project.WithName("Edited copy"));

        Assert.Equal("Original", store.Load("proj-abc").Project!.Name);
        Assert.Equal("Edited copy", store.Load("proj-abc - Copy").Project!.Name);
        Assert.True(File.Exists(Path.Combine(store.ProjectsDirectory, "proj-abc - Copy.json")));
    }

    [Fact]
    public void AHandMadeFileName_NoIdMapsTo_IsRenamedWhenAdopted()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);
        Directory.CreateDirectory(store.ProjectsDirectory);
        File.WriteAllText(Path.Combine(store.ProjectsDirectory, "100% farm.json"), WebExport("Full Farm"));

        var summary = Assert.Single(store.List());
        Assert.Equal(("100% farm", "Full Farm"), (summary.Id, summary.Name));
        Assert.True(File.Exists(store.PathFor("100% farm")));
        Assert.Equal("Full Farm", store.Load("100% farm").Project!.Name);
    }

    [Fact]
    public void Save_KeepsThePreviousVersion_AndTheOriginalOfAMigratedProject()
    {
        using var dir = new TempDir();
        var store = new ProjectStore(dir.Path);
        Directory.CreateDirectory(store.ProjectsDirectory);
        File.Copy(FixturePath, store.PathFor("legacy"));

        var loaded = store.Load("legacy");
        Assert.Equal(1, loaded.MigratedFrom);
        store.Save(loaded.Project!);
        store.Save(loaded.Project!.WithName("Renamed"));

        Assert.Equal(File.ReadAllText(FixturePath), File.ReadAllText(store.MigrationBackupPath("legacy", 1)));
        Assert.Equal(Path.Combine(store.BackupsDirectory, "legacy.v1.json"), store.MigrationBackupPath("legacy", 1));
        Assert.Equal(loaded.Project!.Name, ProjectStore.Parse(File.ReadAllText(store.PreviousVersionPath("legacy"))).Project!.Name);
        Assert.Equal("Renamed", store.Load("legacy").Project!.Name);
        // Backups never show up as projects.
        Assert.Single(store.List());
    }

    [Fact]
    public void DeleteStaleTempFiles_CleansTheProjectsFolder()
    {
        using var dir = new TempDir();
        var time = new TestTime(DateTimeOffset.UtcNow.AddHours(1));
        var store = new ProjectStore(dir.Path, time);
        Directory.CreateDirectory(store.ProjectsDirectory);
        File.WriteAllText(Path.Combine(store.ProjectsDirectory, $".proj-a.json.{Guid.NewGuid():N}.tmp"), "half a project");

        Assert.Equal(1, store.DeleteStaleTempFiles());
        Assert.Empty(Directory.GetFiles(store.ProjectsDirectory));
    }
}
