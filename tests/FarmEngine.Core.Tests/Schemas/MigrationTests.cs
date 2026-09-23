using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Schemas;

/// <summary>
/// Shared fixture helpers. <c>Fixtures/migrated/*.stable.json</c> are the TS
/// reference outputs (<c>stableStringify(result.data)</c>) generated from
/// <c>tests/fixtures/project-v*.json</c>; <c>*.input.json</c> are the raw inputs
/// the TS side fed to migrateExportedGame / migrateGameState.
/// </summary>
internal static class MigrationFixtures
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(PathOf(name)))!;

    public static string Expected(string name) => File.ReadAllText(PathOf(Path.Combine("migrated", name))).TrimEnd('\n');

    public static JsonNode LoadMigratedInput(string name) => Load(Path.Combine("migrated", name));
}

/// <summary>Byte-for-byte parity of the C# pipeline with the TS reference outputs.</summary>
public class MigrationParityTests
{
    public static TheoryData<int> ProjectVersions() => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(ProjectVersions))]
    public void MigrateProjectMatchesTsStableOutput(int version)
    {
        var result = Migrations.MigrateProject(MigrationFixtures.Load($"project-v{version}.json"));
        Assert.Equal([], result.Errors);
        Assert.True(result.Ok);
        Assert.Equal(version, result.FromVersion);
        Assert.Equal(version < 8, result.Migrated);
        Assert.Equal(MigrationFixtures.Expected($"project-v{version}.stable.json"), StableJson.Stringify(result.Data));
    }

    [Fact]
    public void MigrateExportedLegacyGameMatchesTsStableOutput()
    {
        var result = Migrations.MigrateExportedGame(MigrationFixtures.LoadMigratedInput("exported-legacy.input.json"));
        Assert.Equal([], result.Errors);
        Assert.True(result.Ok);
        Assert.Equal(1, result.FromVersion);
        Assert.True(result.Migrated);
        Assert.Equal(MigrationFixtures.Expected("exported-legacy.stable.json"), StableJson.Stringify(result.Data));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    public void MigrateProjectFixtureAsExportedGameMatchesTsStableOutput(int version)
    {
        var result = Migrations.MigrateExportedGame(MigrationFixtures.Load($"project-v{version}.json"));
        Assert.Equal([], result.Errors);
        Assert.True(result.Ok);
        Assert.Equal(version, result.FromVersion);
        Assert.Equal(MigrationFixtures.Expected($"exported-from-project-v{version}.stable.json"), StableJson.Stringify(result.Data));
    }

    [Theory]
    [InlineData("save-v1", 1, true)]
    [InlineData("save-v3", 3, true)]
    [InlineData("save-v3-frac", 3, true)]
    [InlineData("save-current", 4, false)]
    public void MigrateGameStateMatchesTsStableOutput(string name, int fromVersion, bool migrated)
    {
        var result = SaveMigrations.MigrateGameState(MigrationFixtures.LoadMigratedInput($"{name}.input.json"));
        Assert.Equal([], result.Errors);
        Assert.True(result.Ok);
        Assert.Equal(fromVersion, result.FromVersion);
        Assert.Equal(migrated, result.Migrated);
        Assert.Equal(MigrationFixtures.Expected($"{name}.stable.json"), StableJson.Stringify(result.Data));
    }

    /// <summary>
    /// Hand-built v1 project exercising the corners of every step: crop-def
    /// lookups (regrowth, explicit growthDays, JS half-up rounding, unknown
    /// crop types, crops already on day-based growth), every legacy event
    /// trigger type, JSON nulls vs missing keys, and passthrough extras.
    /// </summary>
    [Fact]
    public void MigrateEdgeCaseProjectMatchesTsStableOutput()
    {
        var input = MigrationFixtures.LoadMigratedInput("project-edge-v1.input.json");
        var project = Migrations.MigrateProject(input);
        Assert.Equal([], project.Errors);
        Assert.Equal(MigrationFixtures.Expected("project-edge-v1.stable.json"), StableJson.Stringify(project.Data));
        var exported = Migrations.MigrateExportedGame(input);
        Assert.Equal([], exported.Errors);
        Assert.Equal(MigrationFixtures.Expected("exported-edge-v1.stable.json"), StableJson.Stringify(exported.Data));
    }

    [Fact]
    public void MigrationDoesNotMutateTheCallersNode()
    {
        var raw = MigrationFixtures.Load("project-v1.json");
        var before = raw.ToJsonString();
        Migrations.MigrateProject(raw);
        Migrations.MigrateExportedGame(raw);
        Assert.Equal(before, raw.ToJsonString());
    }
}

/// <summary>Port of tests/unit/migrations.test.ts.</summary>
public class MigrationsTests
{
    private static JsonNode Load(string name) => MigrationFixtures.Load(name);

    [Fact]
    public void DetectsVersionsCorrectly()
    {
        for (var v = 1; v <= 7; v++)
        {
            Assert.Equal(v, Migrations.DetectProjectVersion(Load($"project-v{v}.json")));
        }
    }

    [Theory]
    [InlineData("project-v1.json")]
    [InlineData("project-v2.json")]
    [InlineData("project-v3.json")]
    [InlineData("project-v4.json")]
    [InlineData("project-v5.json")]
    [InlineData("project-v6.json")]
    [InlineData("project-v7.json")]
    [InlineData("project-v8.json")]
    public void MigratesFixtureToTheCurrentSchemaVersionAndValidates(string fixture)
    {
        var result = Migrations.MigrateProject(Load(fixture));
        Assert.Equal([], result.Errors);
        Assert.True(result.Ok);
        Assert.Equal(ProjectSchema.CurrentProjectSchemaVersion, result.Data!.SchemaVersion);
        Assert.Empty(SchemaValidation.ValidateProject(result.Data));
        // Re-parsing the output succeeds (GameProjectSchema.safeParse(result.data)).
        var reparsed = JsonDefaults.Deserialize<GameProject>(JsonDefaults.Serialize(result.Data));
        Assert.NotNull(reparsed);
        Assert.Empty(SchemaValidation.ValidateProject(reparsed!));
    }

    [Fact]
    public void V1MigrationAddsTileLayers()
    {
        var result = Migrations.MigrateProject(Load("project-v1.json"));
        Assert.True(result.Ok);
        var tiles = result.Data!.Scenes[0].Tiles;
        Assert.Equal(("wall", "grass", (string?)null, (string?)"wall"), (tiles[0][0].Type, tiles[0][0].Background, tiles[0][0].Overlay, tiles[0][0].Object));
        Assert.Equal(("path", "grass", (string?)"path", (string?)null), (tiles[0][2].Type, tiles[0][2].Background, tiles[0][2].Overlay, tiles[0][2].Object));
        Assert.Equal(("soil", "soil", 0.0, 0.0), (tiles[1][0].Type, tiles[1][0].Background, tiles[1][0].SoilMoisture, tiles[1][0].SoilFertility));
        // Crop data survives migration
        Assert.Equal("wheat", tiles[1][0].Crop!.Type);
        Assert.Equal(1000, tiles[1][0].Crop!.PlantedAt);
    }

    [Fact]
    public void V2MigrationBackfillsFieldsThatAppTsxUsedToPatchAdHoc()
    {
        var result = Migrations.MigrateProject(Load("project-v2.json"));
        Assert.True(result.Ok);
        var project = result.Data!;
        Assert.Empty(project.CustomAssets);
        Assert.Empty(project.Quests);
        Assert.Equal("spring", project.CurrentSeason);
        Assert.Equal(1, project.CurrentDay);
        Assert.Equal(0, project.Player.PixelX);
        Assert.Empty(project.Player.ActiveQuests);
        // Existing data is preserved
        Assert.Equal(250, project.Player.Money);
        Assert.True(project.EventFlags["met-farmer"]);
        Assert.Equal("soil", project.SelectedTileType);
    }

    [Fact]
    public void V3ToV4ConvertsInProgressCropsToDayBasedGrowth()
    {
        var result = Migrations.MigrateProject(Load("project-v1.json"));
        Assert.True(result.Ok);
        var crop = result.Data!.Scenes[0].Tiles[1][0].Crop!;
        // legacy: planted at 1000, now 5000 → wall-clock stage 1 of 4 → 1 of 3 growth days
        Assert.Equal(1, crop.PlantedOnDay);
        Assert.Equal(1, crop.DaysGrown);
        Assert.Equal(1, crop.Stage);
        // v4 shape essentials
        Assert.True(result.Data!.Settings.EnergyEnabled);
        Assert.Equal(360, result.Data!.CurrentTimeMinutes);
        Assert.Equal(1, result.Data!.CurrentYear);
        Assert.Empty(result.Data!.Shops);
        Assert.Equal(100, result.Data!.Player.Energy);
    }

    [Fact]
    public void V4ToV5ConvertsLegacyEventsToTheTriggerConditionsModel()
    {
        var result = Migrations.MigrateProject(Load("project-v4.json"));
        Assert.True(result.Ok);
        var ev = result.Data!.Events[0];
        Assert.Equal("enter", ev.Trigger);
        var condition = Assert.IsType<EnterTileCondition>(Assert.Single(ev.Conditions));
        Assert.Equal("[{\"type\":\"enterTile\",\"x\":1,\"y\":0}]", JsonDefaults.Serialize(ev.Conditions));
        Assert.Equal((1.0, 0.0), (condition.X, condition.Y));
        Assert.Equal("message", ev.Outcomes[0].Type);
        Assert.Equal("Old!", ev.Outcomes[0].Message);
    }

    [Fact]
    public void V5ToV6BackfillsM4SystemsWithDefaults()
    {
        var result = Migrations.MigrateProject(Load("project-v5.json"));
        Assert.True(result.Ok);
        var project = result.Data!;
        Assert.Empty(project.Recipes);
        Assert.Empty(project.MachineTypes);
        Assert.Empty(project.AnimalSpecies);
        Assert.Empty(project.Animals);
        Assert.Empty(project.FishTables);
        Assert.False(project.Mine.Enabled);
        // Every pre-v6 project gets the tunable default weather set
        Assert.Equal(["sun", "rain", "storm", "snow"], project.Weather.Types.Select(t => t.Id));
        Assert.Contains(project.Weather.Table["winter"], e => e.WeatherId == "snow");
        // Pre-existing v5 content is untouched
        Assert.Equal("shop-x", project.Shops[0].Id);
        Assert.Equal("enter", project.Events[0].Trigger);
    }

    [Fact]
    public void V6ToV7KeepsAuthoredM4ContentIntactAndBackfillsContentPacks()
    {
        var result = Migrations.MigrateProject(Load("project-v6.json"));
        Assert.True(result.Ok);
        var project = result.Data!;
        Assert.Equal(("recipe-flour", "machine-mill"), (project.Recipes[0].Id, project.Recipes[0].MachineTypeId));
        Assert.Single(project.Weather.Types);
        Assert.Equal("Clucky", project.Animals[0].Name);
        var farming = project.Player.Skills!["farming"];
        Assert.Equal((2.0, 40.0), (farming.Level, farming.Xp));
        // v7 backfill
        Assert.Empty(project.ContentPacks);
    }

    [Fact]
    public void V7ProjectsKeepInstalledContentPacksIntact()
    {
        var result = Migrations.MigrateProject(Load("project-v7.json"));
        Assert.True(result.Ok);
        Assert.True(result.Migrated);
        Assert.Equal("{\"pixelArt\":true}", JsonDefaults.Serialize(result.Data!.Graphics));
        var install = result.Data!.ContentPacks[0];
        Assert.True(install.Enabled);
        Assert.Equal(("glow-farm", "1.0.0"), (install.Pack.Manifest.Id, install.Pack.Manifest.Version));
        Assert.Equal("glowshroom", install.Pack.Content.Items[0].Id);
        Assert.Equal("daily-hum", install.Pack.Plugins[0].Id);
    }

    [Fact]
    public void BackfillsTheClassicCalendarForEveryPreM9Fixture()
    {
        // settings.calendar is a defaulted field, no version bump needed.
        foreach (var fixture in new[] { "project-v4.json", "project-v5.json", "project-v6.json", "project-v7.json", "project-v8.json" })
        {
            var result = Migrations.MigrateProject(Load(fixture));
            Assert.True(result.Ok);
            var calendar = result.Data!.Settings.Calendar;
            Assert.Equal(["spring", "summer", "fall", "winter"], calendar.Seasons.Select(s => s.Id));
            Assert.All(calendar.Seasons, s => Assert.Equal(28, s.Days));
            Assert.Empty(calendar.Festivals);
        }
    }

    [Fact]
    public void IsIdempotentMigratingAnAlreadyMigratedProjectChangesNothing()
    {
        var once = Migrations.MigrateProject(Load("project-v1.json"));
        var twice = Migrations.MigrateProject(JsonSerializer.SerializeToNode(once.Data, JsonDefaults.Options));
        Assert.True(twice.Ok);
        Assert.False(twice.Migrated);
        Assert.Equal(StableJson.Stringify(once.Data), StableJson.Stringify(twice.Data));
    }

    [Fact]
    public void RejectsProjectsFromANewerEngine()
    {
        var result = Migrations.MigrateProject(new JsonObject { ["schemaVersion"] = ProjectSchema.CurrentProjectSchemaVersion + 100 });
        Assert.False(result.Ok);
        Assert.Contains("newer than this engine supports", result.Errors[0]);
    }

    [Fact]
    public void RejectsNonObjectPayloadsWithoutThrowing()
    {
        Assert.False(Migrations.MigrateProject((JsonNode?)null).Ok);
        Assert.False(Migrations.MigrateProject(JsonValue.Create("garbage")).Ok);
        Assert.False(Migrations.MigrateProject(JsonValue.Create(42)).Ok);
        // JSON-text overload: invalid JSON and non-object JSON.
        Assert.False(Migrations.MigrateProject("garbage").Ok);
        Assert.False(Migrations.MigrateProject("42").Ok);
        Assert.False(Migrations.MigrateProject("null").Ok);
        Assert.Equal(["Project data is not an object"], Migrations.MigrateProject("\"garbage\"").Errors);
    }

    [Fact]
    public void ReportsValidationErrorsInsteadOfCrashingOnMalformedData()
    {
        var broken = Load("project-v3.json");
        broken["player"]!["money"] = "lots";
        var result = Migrations.MigrateProject(broken);
        Assert.False(result.Ok);
        Assert.True(result.Migrated);
        // Same text as zod's issue for this input.
        Assert.Equal(["player.money: Expected number, received string"], result.Errors);
    }

    [Fact]
    public void ReportsStructuralErrorsInsteadOfThrowing()
    {
        var broken = Load("project-v1.json");
        broken["scenes"] = "not-a-list";
        var result = Migrations.MigrateProject(broken);
        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void MigratesALegacyExportedGame()
    {
        // No layers, missing arrays.
        var legacy = new JsonObject
        {
            ["version"] = "1.0",
            ["name"] = "Shared Game",
            ["scenes"] = Load("project-v1.json")["scenes"]!.DeepClone(),
            ["startSceneId"] = "scene-farm",
            ["currentSeason"] = "summer",
            ["currentDay"] = 3,
            ["gameStartTime"] = 0,
        };
        var result = Migrations.MigrateExportedGame(legacy);
        Assert.Equal([], result.Errors);
        Assert.True(result.Ok);
        Assert.Empty(result.Data!.Npcs);
        Assert.Empty(result.Data!.CustomAssets);
        Assert.False(string.IsNullOrEmpty(result.Data!.Scenes[0].Tiles[0][0].Background));
        Assert.Null(result.Data!.Player);
        // Editor-only fields never appear in an exported game.
        Assert.Null(result.Data!.Extra);
    }

    [Fact]
    public void RejectsMalformedExportedGamesWithActionableErrors()
    {
        var result = Migrations.MigrateExportedGame(new JsonObject { ["name"] = 42 });
        Assert.False(result.Ok);
        Assert.Equal(["name: Expected string, received number"], result.Errors);
    }
}

/// <summary>
/// Port of tests/unit/save-migrations.test.ts. The raw states come from the TS
/// <c>createGameState(createInitialProject(), { seed })</c> (with the same field
/// deletions the TS test applies), stored as <c>Fixtures/migrated/save-*.input.json</c>.
/// </summary>
public class SaveMigrationsTests
{
    [Fact]
    public void MigratesAV1SimulationSaveThroughEveryRegisteredVersion()
    {
        var raw = MigrationFixtures.LoadMigratedInput("save-v1.input.json");
        Assert.Equal(1, raw["meta"]!["saveVersion"]!.GetValue<double>());
        Assert.False(raw["clock"]!.AsObject().ContainsKey("timeMinutes"));

        var result = SaveMigrations.MigrateGameState(raw);
        Assert.True(result.Ok);
        Assert.Equal(1, result.FromVersion);
        Assert.True(result.Migrated);
        var data = result.Data!;
        Assert.Equal(SaveSchema.CurrentSaveVersion, data.Meta.SaveVersion);
        Assert.Equal((360.0, 1.0, "spring", 1.0, "sun"), (data.Clock.TimeMinutes, data.Clock.Day, data.Clock.Season, data.Clock.Year, data.Clock.WeatherId));
        Assert.Equal((100.0, 100.0), (data.Player.Energy, data.Player.MaxEnergy));
        Assert.Empty(data.Player.Skills);
        Assert.Empty(data.Social);
        Assert.Empty(data.Animals);
        Assert.Equal(new MineProgress { DeepestFloor = 0, CurrentFloor = 0 }, data.Mine);
    }

    [Fact]
    public void MigratesV3GridPositionsToFreeMovementTileCenters()
    {
        var raw = MigrationFixtures.LoadMigratedInput("save-v3.input.json");
        var result = SaveMigrations.MigrateGameState(raw);
        Assert.True(result.Ok);
        Assert.Equal(3, result.FromVersion);
        Assert.True(result.Migrated);
        // Grid saves stored the occupied tile; the free-movement position is
        // that tile's center. A held intent never survives a migration.
        Assert.Equal(3.5, result.Data!.Player.X);
        Assert.Equal(4.5, result.Data!.Player.Y);
        Assert.Equal(new MoveIntent { Dx = 0, Dy = 0 }, result.Data!.Player.MoveIntent);
    }

    [Fact]
    public void PassesFractionalFreeMovementPositionsThroughUnchanged()
    {
        var result = SaveMigrations.MigrateGameState(MigrationFixtures.LoadMigratedInput("save-v3-frac.input.json"));
        Assert.True(result.Ok);
        Assert.Equal(2.25, result.Data!.Player.X);
        Assert.Equal(6.75, result.Data!.Player.Y);
    }

    [Fact]
    public void ValidatesCurrentSavesWithoutMarkingThemMigrated()
    {
        var raw = MigrationFixtures.LoadMigratedInput("save-current.input.json");
        var result = SaveMigrations.MigrateGameState(raw);
        Assert.True(result.Ok);
        Assert.False(result.Migrated);
        Assert.True(JsonNode.DeepEquals(raw, JsonSerializer.SerializeToNode(result.Data, JsonDefaults.Options)));
        // Typed overload round-trips too.
        var again = SaveMigrations.MigrateGameState(result.Data!);
        Assert.True(again.Ok);
        Assert.Equal(StableJson.Stringify(result.Data), StableJson.Stringify(again.Data));
    }

    [Fact]
    public void RejectsFutureSaveVersionsWithoutThrowing()
    {
        var raw = MigrationFixtures.LoadMigratedInput("save-current.input.json");
        raw["meta"]!["saveVersion"] = SaveSchema.CurrentSaveVersion + 1;
        var result = SaveMigrations.MigrateGameState(raw);
        Assert.False(result.Ok);
        Assert.Contains("newer than this engine supports", result.Errors[0]);
    }

    [Fact]
    public void RejectsNonObjectPayloadsWithoutThrowing()
    {
        Assert.Equal(["Save state is not an object"], SaveMigrations.MigrateGameState((JsonNode?)null).Errors);
        Assert.False(SaveMigrations.MigrateGameState("not json").Ok);
        Assert.False(SaveMigrations.MigrateGameState("[1,2]").Ok);
    }
}
