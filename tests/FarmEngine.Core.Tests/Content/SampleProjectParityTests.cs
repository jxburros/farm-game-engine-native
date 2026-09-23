using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Content;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Content;

/// <summary>
/// The sample games must be byte-identical to the TS ones. The golden
/// content fixtures hold <c>migrateProject(clone(pinTimes(createXxx())))</c>
/// (tools/golden/golden.gen.test.ts <c>writeContentFixtures</c>) — the same
/// pipeline runs here over the C# factories.
/// </summary>
public class SampleProjectParityTests
{
    /// <summary>golden.gen.test.ts FIXED_TIME.</summary>
    private const double FixedTime = 1_700_000_000_000;

    private static JsonElement FixtureProject(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "content", $"{name}.json")))
            .RootElement.GetProperty("project");

    private static GameProject Migrate(GameProject project)
    {
        var result = Migrations.MigrateProject(JsonSerializer.SerializeToNode(project, JsonDefaults.Options));
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        return result.Data!;
    }

    public static TheoryData<string> Samples() => ["starter-farm", "cozy-garden", "quest-rpg", "blank"];

    private static GameProject Build(string name) => name switch
    {
        "starter-farm" => DefaultContent.CreateInitialProject(FixedTime),
        "cozy-garden" => Templates.CreateCozyFarmProject(FixedTime),
        "quest-rpg" => Templates.CreateQuestRpgProject(FixedTime),
        "blank" => DefaultContent.CreateBlankProject(FixedTime),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void SampleProjectMatchesTypeScript(string name)
    {
        var expected = StableJson.Stringify(FixtureProject(name));
        var actual = StableJson.Stringify(Migrate(Build(name)));
        if (expected != actual)
        {
            var i = 0;
            while (i < Math.Min(expected.Length, actual.Length) && expected[i] == actual[i]) i++;
            var from = Math.Max(0, i - 200);
            Assert.Fail($"{name} diverges at char {i}:\nTS: …{expected.Substring(from, Math.Min(400, expected.Length - from))}\nC#: …{actual.Substring(from, Math.Min(400, actual.Length - from))}");
        }
    }

    [Fact]
    public void FactoriesAreAlreadyInCurrentSchemaShape()
    {
        // Without the migration pass, the raw factory output already matches.
        foreach (var name in new[] { "starter-farm", "cozy-garden", "quest-rpg", "blank" })
        {
            Assert.Equal(StableJson.Stringify(FixtureProject(name)), StableJson.Stringify(Build(name)));
        }
    }

    [Fact]
    public void TemplateCatalogMirrorsTheWebApp()
    {
        Assert.Equal(["starter", "cozy", "quest", "blank"], Templates.TemplateInfo.Select(t => t.Id));
        Assert.Null(Templates.CreateProjectFromTemplate("starter"));
        Assert.Null(Templates.CreateProjectFromTemplate("blank"));
        Assert.Equal("Cozy Garden", Templates.CreateProjectFromTemplate("cozy", FixedTime)!.Name);
        Assert.Equal("Quest RPG", Templates.CreateProjectForTemplate("quest", FixedTime).Name);
        Assert.Equal("Untitled Game", Templates.CreateProjectForTemplate("blank", FixedTime).Name);
        Assert.Equal("My Farming Game", Templates.CreateProjectForTemplate("starter", FixedTime).Name);
        Assert.Equal("My Farming Game", Templates.CreateSampleProject("bogus", FixedTime).Name);
        Assert.Equal("Cozy Garden", Templates.CreateSampleProject("cozy", FixedTime).Name);

        var created = Templates.CreateNewProject("cozy", "Mine", now: FixedTime);
        Assert.Equal("proj-loyw3v28", created.Id); // (1_700_000_000_000).toString(36)
        Assert.Equal("Mine", created.Name);
        Assert.Equal(FixedTime, created.GameStartTime);
    }
}
