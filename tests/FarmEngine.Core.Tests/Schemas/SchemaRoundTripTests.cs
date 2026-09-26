using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Schemas;

public class SchemaRoundTripTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static GameProject LoadV8(out string json)
    {
        json = File.ReadAllText(FixturePath("project-v8.json"));
        var project = JsonDefaults.Deserialize<GameProject>(json);
        Assert.NotNull(project);
        return project!;
    }

    /// <summary>
    /// Every key/value in <paramref name="expected"/> must be present (and equal)
    /// in <paramref name="actual"/>; <paramref name="actual"/> may carry extra
    /// keys (zod defaults filled in on parse).
    /// </summary>
    private static void AssertSubset(JsonElement expected, JsonElement actual, string path = "$")
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.True(actual.ValueKind == JsonValueKind.Object, $"{path}: expected object, got {actual.ValueKind}");
                foreach (var prop in expected.EnumerateObject())
                {
                    Assert.True(actual.TryGetProperty(prop.Name, out var value), $"{path}.{prop.Name}: missing after round trip");
                    AssertSubset(prop.Value, value, $"{path}.{prop.Name}");
                }
                break;
            case JsonValueKind.Array:
                Assert.True(actual.ValueKind == JsonValueKind.Array, $"{path}: expected array, got {actual.ValueKind}");
                Assert.True(expected.GetArrayLength() == actual.GetArrayLength(), $"{path}: array length differs");
                var i = 0;
                using (var a = actual.EnumerateArray().GetEnumerator())
                {
                    foreach (var item in expected.EnumerateArray())
                    {
                        a.MoveNext();
                        AssertSubset(item, a.Current, $"{path}[{i++}]");
                    }
                }
                break;
            case JsonValueKind.Number:
                Assert.True(actual.ValueKind == JsonValueKind.Number, $"{path}: expected number, got {actual.ValueKind}");
                Assert.True(expected.GetDouble() == actual.GetDouble(), $"{path}: {expected.GetDouble()} != {actual.GetDouble()}");
                break;
            case JsonValueKind.String:
                Assert.True(actual.ValueKind == JsonValueKind.String, $"{path}: expected string, got {actual.ValueKind}");
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            default:
                Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: expected {expected.ValueKind}, got {actual.ValueKind}");
                break;
        }
    }

    [Fact]
    public void ProjectV8FixtureDeserializes()
    {
        var project = LoadV8(out _);

        Assert.Equal(8, project.SchemaVersion);
        Assert.Equal(ProjectSchema.CurrentProjectSchemaVersion, project.SchemaVersion);
        Assert.Equal("project-1", project.Id);
        Assert.NotEmpty(project.Scenes);
        var scene = project.Scenes[0];
        Assert.Equal("scene-farm", scene.Id);
        Assert.Equal(2, scene.Width);
        Assert.Equal(1, scene.Height);
        Assert.Single(scene.Tiles);
        Assert.Equal(2, scene.Tiles[0].Count);
        Assert.Equal(TileTypes.Soil, scene.Tiles[0][1].Background);
        Assert.Null(scene.Tiles[0][1].Overlay);
        Assert.Equal(SoilStates.Fertilized, scene.Tiles[0][1].SoilState);
        Assert.Equal(50, scene.Tiles[0][1].SoilMoisture);
        Assert.NotNull(project.Npcs);
        Assert.Equal(Directions.Down, project.Player.Direction);
        Assert.Equal("scene-farm", project.Player.SceneId);
        Assert.Equal(500, project.Player.Money);
        Assert.Equal(2, project.Player.Skills!["farming"].Level);
        Assert.Null(project.SelectedNpcId);
        Assert.Equal(EditorModes.Play, project.Mode);

        // Defaults zod fills in on parse.
        Assert.Equal(4.5, project.Settings.Movement.PlayerSpeed);
        Assert.Equal(4, project.Settings.Calendar.Seasons.Count);
        Assert.Equal("Spring", project.Settings.Calendar.Seasons[0].Name);
        Assert.Equal("en", project.Settings.Locale);
        Assert.Equal("crafting", project.Recipes[0].Category);

        // Passthrough on items: `icon` is not in the schema.
        Assert.True(project.Items[0].Extra!.ContainsKey("icon"));

        Assert.Empty(SchemaValidation.ValidateProject(project));
    }

    [Fact]
    public void ProjectV8RoundTripIsStableAndLossless()
    {
        var project = LoadV8(out var json);

        var first = JsonDefaults.Serialize(project);
        var again = JsonDefaults.Deserialize<GameProject>(first)!;
        var second = JsonDefaults.Serialize(again);
        Assert.Equal(first, second);
        Assert.Equal(StableJson.Stringify(project), StableJson.Stringify(again));

        // Nothing in the source document is lost or altered.
        using var source = JsonDocument.Parse(json);
        using var output = JsonDocument.Parse(first);
        AssertSubset(source.RootElement, output.RootElement);

        // Present-as-null keys stay present.
        Assert.Contains("\"selectedNPCId\":null", first);
        Assert.Contains("\"overlay\":null", first);
    }

    [Fact]
    public void UnknownTopLevelAndTilePropertiesSurviveRoundTrip()
    {
        _ = LoadV8(out var json);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["futureField"] = new System.Text.Json.Nodes.JsonObject { ["nested"] = 42 };
        node["scenes"]![0]!["tiles"]![0]![0]!["glow"] = "blue";
        var modified = node.ToJsonString();

        var parsed = JsonDefaults.Deserialize<GameProject>(modified)!;
        Assert.Equal(42, parsed.Extra!["futureField"].GetProperty("nested").GetDouble());
        Assert.Equal("blue", parsed.Scenes[0].Tiles[0][0].Extra!["glow"].GetString());

        var reparsed = JsonDefaults.Deserialize<GameProject>(JsonDefaults.Serialize(parsed))!;
        Assert.Equal(42, reparsed.Extra!["futureField"].GetProperty("nested").GetDouble());
        Assert.Equal("blue", reparsed.Scenes[0].Tiles[0][0].Extra!["glow"].GetString());
        Assert.Equal(JsonDefaults.Serialize(parsed), JsonDefaults.Serialize(reparsed));
    }

    [Fact]
    public void NonPassthroughTypesDropUnknownKeys()
    {
        var graphics = JsonDefaults.Deserialize<GraphicsSettings>("""{"pixelArt":false,"bogus":1}""")!;
        Assert.Equal("""{"pixelArt":false}""", JsonDefaults.Serialize(graphics));

        var slot = JsonDefaults.Deserialize<InventorySlot>(
            """{"item":{"id":"a","name":"A","description":"","type":"crop","stackable":true,"maxStack":9,"value":1},"quantity":2,"extra":true}""")!;
        Assert.DoesNotContain("\"extra\"", JsonDefaults.Serialize(slot));

        var condition = JsonDefaults.Deserialize<EventCondition>("""{"type":"flag","flag":"f","junk":"x"}""")!;
        Assert.Equal("""{"type":"flag","flag":"f","value":true}""", JsonDefaults.Serialize(condition));

        // Passthrough objects nested in a non-passthrough parent keep theirs.
        Assert.Contains("\"extra\":true", JsonDefaults.Serialize(
            JsonDefaults.Deserialize<InventorySlot>("""{"item":{"id":"a","extra":true},"quantity":1}""")!));
    }

    private static GameState SampleState()
    {
        var scene = new Scene
        {
            Id = "farm",
            Name = "Farm",
            Width = 2,
            Height = 1,
            Tiles =
            [
                [
                    new Tile { X = 0, Y = 0, Type = TileTypes.Grass, Background = TileTypes.Grass, SoilMoisture = 0, SoilFertility = 0 },
                    new Tile
                    {
                        X = 1, Y = 0, Type = TileTypes.Soil, Background = TileTypes.Soil, Overlay = TileTypes.Path,
                        SoilState = SoilStates.Watered, SoilMoisture = 0.5, SoilFertility = 1,
                        Crop = new Crop { Type = "wheat", PlantedAt = 0, PlantedOnDay = 3, Stage = 1, Watered = true, Quality = CropQualities.Normal, HarvestCount = 0, DaysWithoutWater = 0 },
                        Node = new TileNode { TypeId = "tree", RemainingHealth = 3 },
                        Machine = new TileMachine { TypeId = "mill", Processing = new MachineProcessing { RecipeId = "flour", CompletesAtMinute = 1234.5 } },
                    },
                ],
            ],
        };
        return new GameState
        {
            Meta = new GameStateMeta { SaveVersion = SaveSchema.CurrentSaveVersion, EngineSeed = "seed-1", Packs = [new SavePackRef { Id = "base", Version = "1.0.0" }] },
            Clock = new ClockState { Tick = 120, TimeMinutes = 390.25, Day = 3, Season = "spring", Year = 1 },
            World = new WorldState { Scenes = [scene] },
            Player = new PlayerState
            {
                X = 1.5, Y = 0.5, MoveIntent = new MoveIntent { Dx = -1, Dy = 0 }, Direction = Directions.Left, SceneId = "farm",
                Inventory = [new InventorySlot { Item = new Item { Id = "wheat", Name = "Wheat", Description = "", Type = ItemTypes.Crop, Stackable = true, MaxStack = 99, Value = 25 }, Quantity = 3 }],
                MaxInventorySize = 20, Money = 500, Energy = 97.5, MaxEnergy = 100,
                Skills = new OrderedDictionary<string, SkillState> { ["farming"] = new SkillState { Xp = 12, Level = 0 } },
            },
            Npcs = new OrderedDictionary<string, NpcState> { ["bob"] = new NpcState { X = 1, Y = 0, SceneId = "farm", Path = [new GridPoint { X = 0, Y = 0 }], PatrolIndex = 0 } },
            Quests = new OrderedDictionary<string, QuestProgress>
            {
                ["q1"] = new QuestProgress { Status = QuestStatuses.Active, Objectives = new OrderedDictionary<string, QuestObjectiveProgress> { ["o1"] = new QuestObjectiveProgress { Progress = 1, Completed = false } } },
            },
            ShopPurchasesToday = new OrderedDictionary<string, OrderedDictionary<string, double>> { ["shop"] = new OrderedDictionary<string, double> { ["seed"] = 2 } },
            Social = new OrderedDictionary<string, NpcSocialState> { ["bob"] = new NpcSocialState { Friendship = 45, GiftsToday = 1, LastGiftDay = 3 } },
            Animals = [new AnimalState { Id = "a1", SpeciesId = "cow", Name = "Bess", SceneId = "farm", X = 0, Y = 0 }],
            Mine = new MineProgress { DeepestFloor = 4, CurrentFloor = 0 },
            Flags = new OrderedDictionary<string, JsonElement>
            {
                [EventsSchema.EventFiredFlag("intro")] = Js.Value(true),
                ["count"] = Js.Value(3),
                ["name"] = Js.Value("x"),
            },
            Rng = new RngState { S = [1u, 0xFFFFFFFFu, 123456789u, 0x80000000u] },
        };
    }

    [Fact]
    public void GameStateRoundTripIsStable()
    {
        var state = SampleState();
        var first = JsonDefaults.Serialize(state);
        var parsed = JsonDefaults.Deserialize<GameState>(first)!;
        var second = JsonDefaults.Serialize(parsed);
        Assert.Equal(first, second);
        Assert.Equal(StableJson.Stringify(state), StableJson.Stringify(parsed));

        Assert.Contains("\"dialogue\":null", first);
        Assert.Contains("\"shop\":null", first);
        Assert.Contains("\"minigame\":null", first);
        Assert.Contains("\"mutation\":null", first);
        Assert.Contains("\"s\":[1,4294967295,123456789,2147483648]", first);
        Assert.Contains("\"algorithm\":\"xoshiro128ss\"", first);
        Assert.Contains("\"event:intro:fired\":true", first);
        Assert.Equal(new uint[] { 1u, 0xFFFFFFFFu, 123456789u, 0x80000000u }, parsed.Rng.S);
        Assert.True(Js.Truthy(parsed.Flags["count"]));
        Assert.Equal("sun", parsed.Clock.WeatherId);
        Assert.Equal(TileTypes.Path, parsed.World.Scenes[0].Tiles[0][1].Overlay);
        Assert.Equal(1234.5, parsed.World.Scenes[0].Tiles[0][1].Machine!.Processing!.CompletesAtMinute);

        var withDialogue = state with { Dialogue = new DialogueState { NpcId = "bob", DialogueId = "d1" }, Minigame = new MinigameSession { MinigameId = "fishing", Context = new OrderedDictionary<string, JsonElement> { ["rodTier"] = Js.Value(2) } } };
        var reparsed = JsonDefaults.Deserialize<GameState>(JsonDefaults.Serialize(withDialogue))!;
        Assert.Equal("d1", reparsed.Dialogue!.DialogueId);
        Assert.Equal(2, reparsed.Minigame!.Context["rodTier"].GetDouble());
    }

    [Fact]
    public void GameStateDefaultsApplyWhenKeysMissing()
    {
        const string json = """
            {"meta":{"saveVersion":4,"engineSeed":"s"},
             "clock":{"tick":0,"timeMinutes":360,"day":1,"season":"spring","year":1},
             "world":{"scenes":[]},
             "player":{"x":0.5,"y":0.5,"direction":"down","sceneId":"farm","inventory":[],"maxInventorySize":20,"money":0,"energy":100,"maxEnergy":100,"activeQuests":[],"completedQuests":[]},
             "npcs":{},"quests":{},"dialogue":null,"shop":null,"shopPurchasesToday":{},"social":{},"animals":[],"mine":{},"flags":{},
             "rng":{"algorithm":"xoshiro128ss","s":[1,2,3,4]}}
            """;
        var state = JsonDefaults.Deserialize<GameState>(json)!;
        Assert.Empty(state.Meta.Packs);
        Assert.Equal("sun", state.Clock.WeatherId);
        Assert.Equal(0, state.Player.MoveIntent.Dx);
        Assert.Empty(state.Player.Skills);
        Assert.Null(state.Minigame);
        Assert.Equal(0, state.Mine.DeepestFloor);
        Assert.Empty(state.QuarantinedItems);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, state.Rng.S);
    }

    public static TheoryData<string, Type> ConditionCases => new()
    {
        { """{"type":"enterTile","x":1,"y":2,"x2":3,"y2":4}""", typeof(EnterTileCondition) },
        { """{"type":"interactTile","x":1,"y":2}""", typeof(InteractTileCondition) },
        { """{"type":"hasItem","itemId":"wheat"}""", typeof(HasItemCondition) },
        { """{"type":"inventorySpace","itemId":"wheat","quantity":2}""", typeof(InventorySpaceCondition) },
        { """{"type":"flag","flag":"met-bob","value":false}""", typeof(FlagCondition) },
        { """{"type":"dayRange","minDay":1,"maxDay":7}""", typeof(DayRangeCondition) },
        { """{"type":"season","seasons":["spring","fall"]}""", typeof(SeasonCondition) },
        { """{"type":"yearRange","minYear":2}""", typeof(YearRangeCondition) },
        { """{"type":"timeOfDay","minMinute":360,"maxMinute":720}""", typeof(TimeOfDayCondition) },
        { """{"type":"questStatus","questId":"q1","status":"completed"}""", typeof(QuestStatusCondition) },
        { """{"type":"friendship","npcId":"bob","min":250}""", typeof(FriendshipCondition) },
        { """{"type":"weather","weatherIds":["rain","storm"]}""", typeof(WeatherCondition) },
        { """{"type":"festivalId","festivalId":"egg-festival"}""", typeof(FestivalIdCondition) },
    };

    [Theory]
    [MemberData(nameof(ConditionCases))]
    public void EventConditionDeserializesPolymorphically(string json, Type expected)
    {
        var condition = JsonDefaults.Deserialize<EventCondition>(json)!;
        Assert.IsType(expected, condition);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(doc.RootElement.GetProperty("type").GetString(), condition.Type);

        // Re-serializing through the base type writes the discriminator first
        // and keeps every authored field.
        var output = JsonDefaults.Serialize(condition);
        Assert.StartsWith($"{{\"type\":\"{condition.Type}\"", output);
        using var outDoc = JsonDocument.Parse(output);
        AssertSubset(doc.RootElement, outDoc.RootElement);
        Assert.Equal(output, JsonDefaults.Serialize(JsonDefaults.Deserialize<EventCondition>(output)));
    }

    [Fact]
    public void EventConditionFieldsAndDefaults()
    {
        var hasItem = Assert.IsType<HasItemCondition>(JsonDefaults.Deserialize<EventCondition>("""{"type":"hasItem","itemId":"wheat"}"""));
        Assert.Equal(1, hasItem.Quantity);
        var flag = Assert.IsType<FlagCondition>(JsonDefaults.Deserialize<EventCondition>("""{"flag":"f","type":"flag"}"""));
        Assert.True(flag.Value);
        var enter = Assert.IsType<EnterTileCondition>(JsonDefaults.Deserialize<EventCondition>("""{"type":"enterTile","x":1,"y":2}"""));
        Assert.Null(enter.X2);
        Assert.Equal("""{"type":"enterTile","x":1,"y":2}""", JsonDefaults.Serialize<EventCondition>(enter));

        var ev = JsonDefaults.Deserialize<GameEvent>(
            """{"id":"e","name":"E","sceneId":"","trigger":"enter","conditions":[{"type":"season","seasons":["winter"]},{"type":"friendship","npcId":"bob","min":10}],"outcomes":[{"type":"message","message":"hi"}],"active":true,"repeatable":false}""")!;
        Assert.IsType<SeasonCondition>(ev.Conditions[0]);
        Assert.Equal(10, Assert.IsType<FriendshipCondition>(ev.Conditions[1]).Min);
        Assert.Equal(EventOutcomeTypes.Message, ev.Outcomes[0].Type);
    }

    [Fact]
    public void UnknownEventConditionTypeIsRejected()
    {
        Assert.ThrowsAny<JsonException>(() => JsonDefaults.Deserialize<EventCondition>("""{"type":"nope"}"""));
    }

    [Fact]
    public void PluginMutationDeserializesPolymorphically()
    {
        var list = JsonDefaults.Deserialize<List<PluginMutation>>(
            """[{"type":"giveItem","itemId":"a","quantity":2},{"type":"setFlag","flag":"f","value":"x"},{"type":"warpPlayer","sceneId":"s","x":1,"y":2}]""")!;
        Assert.IsType<GiveItemMutation>(list[0]);
        Assert.Equal("x", Assert.IsType<SetFlagMutation>(list[1]).Value.GetString());
        Assert.Equal(2, Assert.IsType<WarpPlayerMutation>(list[2]).Y);
        Assert.Equal(
            """[{"type":"giveItem","itemId":"a","quantity":2},{"type":"setFlag","flag":"f","value":"x"},{"type":"warpPlayer","sceneId":"s","x":1,"y":2}]""",
            JsonDefaults.Serialize(list));
    }

    [Fact]
    public void ValidateProjectReportsConstraintViolations()
    {
        var project = LoadV8(out _);
        var scene = project.Scenes[0];
        var broken = project with
        {
            Mode = "bogus",
            Scenes = [scene with { Width = 3, Height = 1.5, Tiles = [[scene.Tiles[0][0] with { Background = "lava" }]] }],
            Quests = [new Quest { Id = "q", Name = "Q", Description = "", Status = "pending" }],
            Events =
            [
                new GameEvent
                {
                    Id = "", Name = "E", Trigger = "enter",
                    Conditions = [new InventorySpaceCondition { ItemId = "a", Quantity = 0 }, new TimeOfDayCondition { MinMinute = 800, MaxMinute = 300 }],
                    Outcomes = [new EventOutcome { Type = EventOutcomeTypes.WaterArea, Radius = 11 }],
                },
            ],
        };
        // Parse-level: what zod rejects.
        var errors = SchemaValidation.ValidateProject(broken);
        Assert.Contains(errors, e => e.StartsWith("mode:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("scenes.0.height:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("scenes.0.tiles.0.0.background:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("quests.0.status:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("events.0.conditions.0.quantity:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("events.0.outcomes.0.radius:", StringComparison.Ordinal));
        // Lint-level: what the web editor lets a creator save. Never a parse error.
        Assert.DoesNotContain(errors, e => e.StartsWith("scenes.0.tiles.0:", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.StartsWith("events.0.id:", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.StartsWith("events.0.conditions.1:", StringComparison.Ordinal));
        var lint = SchemaValidation.LintProject(broken);
        Assert.Contains(lint, e => e.StartsWith("scenes.0.tiles.0:", StringComparison.Ordinal));
        Assert.Contains(lint, e => e.StartsWith("events.0.id:", StringComparison.Ordinal));
        Assert.Contains(lint, e => e.StartsWith("events.0.conditions.1:", StringComparison.Ordinal));
        Assert.DoesNotContain(lint, e => e.StartsWith("mode:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Shapes the web editor produces and zod accepts (no refinements exist for them) must
    /// load here: inverted regions, inverted day ranges, a start scene that was deleted, an
    /// empty id. Import parity, not lint cleanliness.
    /// </summary>
    [Fact]
    public void ValidateProjectAcceptsWhatZodAccepts()
    {
        var project = LoadV8(out _);
        var webShaped = project with
        {
            StartSceneId = "scene-that-was-deleted",
            Items = [.. project.Items, new Item { Id = "", Name = "Unnamed", Description = "", Type = "material", Stackable = true, MaxStack = 1, Value = 0 }],
            Events =
            [
                new GameEvent
                {
                    Id = "e", Name = "E", Trigger = "enter",
                    Conditions =
                    [
                        new EnterTileCondition { X = 5, Y = 5, X2 = 2, Y2 = 1 },
                        new DayRangeCondition { MinDay = 20, MaxDay = 3 },
                        new TimeOfDayCondition { MinMinute = 800, MaxMinute = 300 },
                    ],
                    Outcomes = [],
                },
            ],
        };
        Assert.Empty(SchemaValidation.ValidateProject(webShaped));
        Assert.True(Migrations.MigrateProject(JsonNode.Parse(JsonDefaults.Serialize(webShaped))).Ok);
        Assert.NotEmpty(SchemaValidation.LintProject(webShaped));
    }

    [Fact]
    public void SchemaHelpers()
    {
        Assert.Equal("event:abc:fired", EventsSchema.EventFiredFlag("abc"));
        Assert.Equal(3.5, SaveSchema.CenterCoordinate(3));
        Assert.Equal(3.25, SaveSchema.CenterCoordinate(3.25));
        Assert.Equal(["Spring", "Summer", "Fall", "Winter"], SettingsSchema.ClassicCalendarSeasons().Select(s => s.Name));
        Assert.Equal(100, SettingsSchema.DefaultProjectSettings.MaxEnergy);
        Assert.Equal(1560, SettingsSchema.DefaultProjectSettings.Time.DayEndMinute);
        Assert.Equal(4, MigrationsSchema.DefaultWeatherConfig().Types.Count);
        Assert.Contains("\"overlay\":null", JsonDefaults.Serialize(MigrationsSchema.DefaultWeatherConfig()));
        Assert.True(PacksSchema.IsEngineCompatible("*"));
        Assert.True(PacksSchema.IsEngineCompatible("^0.5.0"));
        Assert.False(PacksSchema.IsEngineCompatible("^0.6.0"));
        Assert.True(PacksSchema.IsEngineCompatible(">=0.4.9"));
        Assert.False(PacksSchema.IsEngineCompatible("0.5.1"));
        Assert.Equal(1250, SocialSchema.MaxFriendship);
    }

    [Fact]
    public void ContentPackDefaultsAndValidation()
    {
        using var doc = JsonDocument.Parse("""{"manifest":{"id":"my-pack","name":"My Pack","version":"1.0.0"}}""");
        var result = PacksSchema.ValidateContentPack(doc.RootElement);
        Assert.True(result.Ok);
        Assert.Equal("*", result.Pack!.Manifest.EngineCompatibility);
        Assert.True(result.Pack.Manifest.Permissions.ContentInject);
        Assert.Empty(result.Pack.Content.Items);

        using var bad = JsonDocument.Parse("""{"manifest":{"id":"My Pack","name":"x","version":"1"}}""");
        var badResult = PacksSchema.ValidateContentPack(bad.RootElement);
        Assert.False(badResult.Ok);
        Assert.Contains(badResult.Errors, e => e.StartsWith("manifest.id:", StringComparison.Ordinal));
    }
}
