using FarmEngine.Json;
using FarmEngine.Schemas;
using static FarmEngine.Core.Tests.Core.CoreTestHelpers;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of m4-systems.test.ts. M4 system tests: crafting &amp; stations,
/// weather, animals, social, fishing, mining, skills, cross-system replay.
/// </summary>
public class M4SystemsTests
{
    /// <summary>M4 fixture: recipes, machines, animals, fishing, mining enabled.</summary>
    private static (EngineContext Ctx, GameState State, GameProject Project) MakeEngine(Func<GameProject, GameProject>? mutate = null)
    {
        var project = EngineTests.MakeProject() with
        {
            Recipes = ContentBuiltin.CreateDefaultRecipes(),
            MachineTypes = ContentBuiltin.CreateDefaultMachineTypes(),
            AnimalSpecies = ContentBuiltin.CreateDefaultAnimalSpecies(),
            FishTables = ContentBuiltin.CreateDefaultFishTables(),
            Mine = new MineConfig
            {
                Enabled = true,
                EntranceSceneId = "scene-test",
                EntranceX = 5,
                EntranceY = 5,
                Floors = 10,
                Bands = ContentBuiltin.CreateDefaultMineBands(),
            },
        };
        if (mutate is not null) project = mutate(project);
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "m4");
        return (ctx, state, project);
    }

    private static GameState Give(GameState state, string itemId, double quantity, EngineContext ctx)
    {
        var item = ctx.Content.Items.First(i => i.Id == itemId);
        return state with { Player = state.Player with { Inventory = [.. state.Player.Inventory, new InventorySlot { Item = item, Quantity = quantity }] } };
    }

    /// <summary>Directly stamp a machine instance onto a tile (bypasses placeMachine's item/facing rules).</summary>
    private static GameState PlaceMachineAt(GameState state, string sceneId, int x, int y, string typeId)
    {
        var scenes = state.World.Scenes.Select(scene =>
        {
            if (scene.Id != sceneId) return scene;
            var tiles = scene.Tiles.Select(row => row.ToList()).ToList();
            tiles[y][x] = tiles[y][x] with { Machine = new TileMachine { TypeId = typeId } };
            return scene with { Tiles = tiles };
        }).ToList();
        return state with { World = state.World with { Scenes = scenes } };
    }

    private static double? Quantity(GameState state, string itemId) =>
        state.Player.Inventory.FirstOrDefault(s => s.Item.Id == itemId)?.Quantity;

    private static GameState At(GameState state, double x, double y, string direction) =>
        state with { Player = state.Player with { X = x, Y = y, Direction = direction } };

    /// <summary>Water row across y=0 of the first scene.</summary>
    private static GameProject WithWaterRow(GameProject project)
    {
        var scene = project.Scenes[0];
        var tiles = scene.Tiles.Select(row => row.ToList()).ToList();
        for (var x = 0; x < 6; x++) tiles[0][x] = tiles[0][x] with { Type = "water", Background = "water" };
        return project with { Scenes = [scene with { Tiles = tiles }, .. project.Scenes.Skip(1)] };
    }

    private static GameProject WithGiftTastes(GameProject project, GiftTastes tastes) =>
        project with { Npcs = [project.Npcs[0] with { GiftTastes = tastes }, .. project.Npcs.Skip(1)] };

    private static List<WeatherTableEntry> Only(string weatherId) => [new WeatherTableEntry { WeatherId = weatherId, Weight = 1 }];

    private static WeatherConfig AlwaysWeather(string weatherId, params WeatherTypeDefinition[] types) => new()
    {
        Types = [.. types],
        Table = new OrderedDictionary<string, List<WeatherTableEntry>>
        {
            ["spring"] = Only(weatherId),
            ["summer"] = Only(weatherId),
            ["fall"] = Only(weatherId),
            ["winter"] = Only(weatherId),
        },
    };

    // --- crafting (M4a) ---

    [Fact]
    public void HandCraftsAnInstantRecipeConsumingInputs()
    {
        var (ctx, state, _) = MakeEngine();
        var withFiber = Give(state, "material-fiber", 6, ctx);
        var step = Engine.ApplyCommand(ctx, withFiber, new CraftCommand("recipe-craft-hay"));
        Assert.Equal(2, Quantity(step.State, "feed-hay"));
        Assert.Equal(3, Quantity(step.State, "material-fiber"));
    }

    [Fact]
    public void RejectsCraftingWithoutIngredientsOrBelowSkillUnlocks()
    {
        var (ctx, state, _) = MakeEngine();
        var noIngredients = Engine.ApplyCommand(ctx, state, new CraftCommand("recipe-craft-hay"));
        Assert.True(HasMessage(noIngredients.Effects, t => t == "Missing ingredients."));

        var withMaterials = Give(Give(state, "material-wood", 20, ctx), "material-stone", 20, ctx);
        var locked = Engine.ApplyCommand(ctx, withMaterials, new CraftCommand("recipe-craft-preserves-jar"));
        Assert.True(HasMessage(locked.Effects, t => t == "Recipe not unlocked yet."));
    }

    [Fact]
    public void PlacesAMachineLoadsAJobAndFinishesItAfterProcessingTime()
    {
        var (ctx, state, _) = MakeEngine();
        var current = Give(Give(Give(state, "machine-furnace", 1, ctx), "ore-copper", 3, ctx), "material-wood", 1, ctx);

        // facing up at (3,3): place the furnace there
        current = Engine.ApplyCommand(ctx, current, new PlaceMachineCommand("machine-furnace")).State;
        Assert.Equal("machine-furnace", current.World.Scenes[0].Tiles[3][3].Machine?.TypeId);
        Assert.Null(Quantity(current, "machine-furnace"));

        current = Engine.ApplyCommand(ctx, current, new MachineLoadCommand("recipe-smelt-copper")).State;
        Assert.Equal("recipe-smelt-copper", current.World.Scenes[0].Tiles[3][3].Machine?.Processing?.RecipeId);
        Assert.Null(Quantity(current, "ore-copper"));

        // Sleeping jumps time far past the 120-minute job → output ready.
        current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        Assert.Null(current.World.Scenes[0].Tiles[3][3].Machine?.Processing);
        AssertDeepEqual(
            new List<RecipeIngredient> { new() { ItemId = "bar-copper", Quantity = 1 } },
            current.World.Scenes[0].Tiles[3][3].Machine?.Output);

        // Interact collects.
        var collected = Engine.ApplyCommand(ctx, current, new InteractCommand());
        Assert.Equal(1, Quantity(collected.State, "bar-copper"));
        Assert.Null(collected.State.World.Scenes[0].Tiles[3][3].Machine?.Output);
    }

    [Fact]
    public void MachinesBlockMovement()
    {
        var (ctx, state, _) = MakeEngine();
        var current = Give(state, "machine-furnace", 1, ctx);
        current = Engine.ApplyCommand(ctx, current, new PlaceMachineCommand("machine-furnace")).State;
        var step = Engine.ApplyCommand(ctx, current, new MoveCommand("up"));
        Assert.Equal(4.5, step.State.Player.Y); // blocked by furnace at (3,3)
    }

    // --- crafting stations & categories ---

    /// <summary>Adds a hand-craftable recipe gated behind a 'cooking' station, plus the machine that provides it.</summary>
    private static (EngineContext Ctx, GameState State, GameProject Project) WithCookingStation() =>
        MakeEngine(project => project with
        {
            MachineTypes =
            [
                .. project.MachineTypes,
                new MachineTypeDefinition
                {
                    Id = "machine-test-kitchen", Name = "Test Kitchen", Description = "A test cooking station",
                    Color = "#c2703a", BlocksMovement = true, StationCategories = ["cooking"],
                },
            ],
            Recipes =
            [
                .. project.Recipes,
                new RecipeDefinition
                {
                    Id = "recipe-test-soup", Name = "Test Soup",
                    Inputs = [new RecipeIngredient { ItemId = "material-fiber", Quantity = 1 }],
                    Outputs = [new RecipeIngredient { ItemId = "feed-hay", Quantity = 1 }],
                    ProcessingMinutes = 0, Category = "cooking", RequiresStationCategory = "cooking",
                },
            ],
        });

    [Fact]
    public void FailsAStationGatedRecipeAwayFromTheStationNamingItInTheMessage()
    {
        var (ctx, state, _) = WithCookingStation();
        var withFiber = Give(state, "material-fiber", 5, ctx);

        var away = Engine.ApplyCommand(ctx, withFiber, new CraftCommand("recipe-test-soup"));
        Assert.Contains(new MessageEffect("error", "You need to be near a Test Kitchen to craft that."), away.Effects);
        // Nothing consumed on failure.
        Assert.Equal(5, Quantity(away.State, "material-fiber"));
        Assert.Null(Quantity(away.State, "feed-hay"));
    }

    [Fact]
    public void SucceedsOnceAMachineProvidingTheStationCategoryIsWithin1Tile()
    {
        var (ctx, state, _) = WithCookingStation();
        var withFiber = Give(state, "material-fiber", 5, ctx);

        // Player sits at (3,4); a kitchen at (2,4) is an 8-neighborhood tile away.
        var nearby = PlaceMachineAt(withFiber, "scene-test", 2, 4, "machine-test-kitchen");
        var crafted = Engine.ApplyCommand(ctx, nearby, new CraftCommand("recipe-test-soup"));
        Assert.Equal(1, Quantity(crafted.State, "feed-hay"));
        Assert.Equal(4, Quantity(crafted.State, "material-fiber"));
    }

    [Fact]
    public void NearbyStationCategoriesFloorsFractionalPlayerCoordinatesOntoTheRightTile()
    {
        var (ctx, state, _) = WithCookingStation();
        var nearby = PlaceMachineAt(state, "scene-test", 2, 4, "machine-test-kitchen");
        var fractional = nearby with { Player = nearby.Player with { X = 3.7, Y = 4.2 } };
        // floor(3.7)=3, floor(4.2)=4 → still adjacent to the kitchen at (2,4).
        Assert.Contains("cooking", Crafting.NearbyStationCategories(ctx, fractional));

        var farAway = nearby with { Player = nearby.Player with { X = 0.1, Y = 0.1 } };
        Assert.DoesNotContain("cooking", Crafting.NearbyStationCategories(ctx, farAway));
    }

    [Fact]
    public void CraftableStatusReportsIngredientsWhenShortOnInputs()
    {
        var (ctx, state, _) = MakeEngine();
        var status = Crafting.CraftableStatus(ctx, state, ctx.Content.Recipes.First(r => r.Id == "recipe-craft-hay"));
        Assert.Equal(new CraftableStatus(false, "ingredients", "Missing ingredients."), status);
    }

    [Fact]
    public void CraftableStatusReportsLockedWhenUnlockConditionsAreNotMet()
    {
        var (ctx, state, _) = MakeEngine();
        var withMaterials = Give(Give(state, "material-wood", 20, ctx), "material-stone", 20, ctx);
        var status = Crafting.CraftableStatus(ctx, withMaterials, ctx.Content.Recipes.First(r => r.Id == "recipe-craft-preserves-jar"));
        Assert.Equal(new CraftableStatus(false, "locked", "Recipe not unlocked yet."), status);
    }

    [Fact]
    public void CraftableStatusReportsStationWhenIngredientsAndUnlocksAreFineButNoStationIsNearby()
    {
        var (ctx, state, _) = WithCookingStation();
        var withFiber = Give(state, "material-fiber", 5, ctx);
        var status = Crafting.CraftableStatus(ctx, withFiber, ctx.Content.Recipes.First(r => r.Id == "recipe-test-soup"));
        Assert.Equal(new CraftableStatus(false, "station", "You need to be near a Test Kitchen to craft that."), status);
    }

    [Fact]
    public void CraftableStatusReportsCraftableTrueOnceEveryConditionIsSatisfied()
    {
        var (ctx, state, _) = WithCookingStation();
        var ready = PlaceMachineAt(Give(state, "material-fiber", 5, ctx), "scene-test", 2, 4, "machine-test-kitchen");
        var status = Crafting.CraftableStatus(ctx, ready, ctx.Content.Recipes.First(r => r.Id == "recipe-test-soup"));
        Assert.Equal(new CraftableStatus(true), status);
    }

    // --- weather (M4b) ---

    [Fact]
    public void RainWatersSoilAndCropsAtDayStart()
    {
        var (ctx, state, _) = MakeEngine(project => project with
        {
            Weather = AlwaysWeather("rain",
                new WeatherTypeDefinition { Id = "rain", Name = "Rain", WatersOutdoorSoil = true },
                new WeatherTypeDefinition { Id = "storm", Name = "Storm", WatersOutdoorSoil = true, CropDamageChance = 1 }),
        });
        var current = At(state, 3, 3, "up");
        current = Engine.ApplyCommand(ctx, current, new InteractCommand()).State; // plant wheat
        current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        Assert.Equal("rain", current.Clock.WeatherId);
        var tile = current.World.Scenes[0].Tiles[2][3];
        Assert.Equal("watered", tile.SoilState);
        Assert.True(tile.Crop?.Watered);
        // Rainy day 2: sleeping again grows the crop without manual watering.
        current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        Assert.Equal(1, current.World.Scenes[0].Tiles[2][3].Crop?.DaysGrown);
    }

    [Fact]
    public void StormsCanDestroyCropsOvernight()
    {
        var (ctx, state, _) = MakeEngine(project => project with
        {
            Weather = AlwaysWeather("storm", new WeatherTypeDefinition { Id = "storm", Name = "Storm", WatersOutdoorSoil = true, CropDamageChance = 1 }),
        });
        var current = At(state, 3, 3, "up");
        current = Engine.ApplyCommand(ctx, current, new InteractCommand()).State;
        // First sleep rolls storm for day 2; second sleep's overnight pass damages with p=1.
        current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        Assert.Null(current.World.Scenes[0].Tiles[2][3].Crop);
    }

    // --- animals (M4c) ---

    private static (EngineContext Ctx, GameState State, GameProject Project) RanchEngine() =>
        MakeEngine(project => project with
        {
            Animals =
            [
                new AnimalState
                {
                    Id = "animal-1", SpeciesId = "animal-chicken", Name = "Clucky",
                    SceneId = "scene-test", X = 3, Y = 3,
                    Mood = 70, FedToday = false, PettedToday = false,
                    AgeDays = 5, DaysSinceProduct = 0, ProductReady = false,
                },
            ],
        });

    [Fact]
    public void FeedingPettingAndDailyProductFlow()
    {
        var (ctx, state, _) = RanchEngine();
        var current = Give(state, "feed-hay", 2, ctx);

        // Interact 1: feeds (consumes hay)
        var step = Engine.ApplyCommand(ctx, current, new InteractCommand());
        Assert.True(HasMessage(step.Effects, t => t.Contains("Fed Clucky")));
        Assert.Equal(1, Quantity(step.State, "feed-hay"));

        // Interact 2: pets
        step = Engine.ApplyCommand(ctx, step.State, new InteractCommand());
        Assert.True(HasMessage(step.Effects, t => t.Contains("happy")));

        // Overnight: fed adult chicken with interval 1 → egg ready
        var next = Engine.ApplyCommand(ctx, step.State, new SleepCommand()).State;
        Assert.True(next.Animals[0].ProductReady);
        Assert.False(next.Animals[0].FedToday);

        // New day: feeding takes priority again (uses the last hay)…
        step = Engine.ApplyCommand(ctx, next, new InteractCommand());
        Assert.True(step.State.Animals[0].FedToday);
        // …then the next interact collects the egg.
        step = Engine.ApplyCommand(ctx, step.State, new InteractCommand());
        Assert.Contains(step.State.Player.Inventory, s => s.Item.Id == "product-egg");
        Assert.False(step.State.Animals[0].ProductReady);
    }

    [Fact]
    public void NeglectedAnimalsLoseMoodAndProduceNothing()
    {
        var (ctx, state, _) = RanchEngine();
        var next = Engine.ApplyCommand(ctx, state, new SleepCommand()).State;
        Assert.Equal(55, next.Animals[0].Mood); // -15 unfed
        Assert.False(next.Animals[0].ProductReady);
    }

    // --- social (M4d) ---

    [Fact]
    public void GiftingAppliesTasteDeltasAndDailyLimits()
    {
        var (ctx, state, _) = MakeEngine(project =>
            WithGiftTastes(project, new GiftTastes { Loved = ["gift-flower"], Liked = [], Disliked = [], Hated = ["junk-boot"] }));
        var current = At(Give(state, "gift-flower", 2, ctx), 1, 2, "up");

        var step = Engine.ApplyCommand(ctx, current, new GiveGiftCommand("gift-flower"));
        Assert.Equal(80, step.State.Social["npc-test"].Friendship);
        Assert.Equal(1, Quantity(step.State, "gift-flower"));

        // Second gift the same day is refused.
        step = Engine.ApplyCommand(ctx, step.State, new GiveGiftCommand("gift-flower"));
        Assert.Equal(80, step.State.Social["npc-test"].Friendship);
        Assert.True(HasMessage(step.Effects, t => t.Contains("already received")));
    }

    [Fact]
    public void FriendshipGatesDialogueOptionsForUiAndEngineAlike()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            var npc = project.Npcs[0];
            var first = npc.Dialogue[0];
            var dialogue = first with { Options = [.. first.Options, new DialogueOption { Text = "Secret", RequiresFriendship = 100, GiveMoney = 999 }] };
            List<Dialogue> dialogues = [dialogue, .. npc.Dialogue.Skip(1)];
            return project with { Npcs = [npc with { Dialogue = dialogues }, .. project.Npcs.Skip(1)], Dialogues = dialogues };
        });
        var inDialogue = state with { Dialogue = new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" } };
        // Below the gate: index 2 does not exist among visible options → dialogue closes, no money.
        var blocked = Engine.ApplyCommand(ctx, inDialogue, new ChooseDialogueOptionCommand(2));
        Assert.Equal(100, blocked.State.Player.Money);

        var friendly = inDialogue with
        {
            Social = new OrderedDictionary<string, NpcSocialState> { ["npc-test"] = new NpcSocialState { Friendship = 150, GiftsToday = 0 } },
        };
        var allowed = Engine.ApplyCommand(ctx, friendly, new ChooseDialogueOptionCommand(2));
        Assert.Equal(1099, allowed.State.Player.Money);
    }

    // --- fishing (M4e) ---

    [Fact]
    public void CastsResolveDeterministicallyThroughTheSeededRng()
    {
        var (ctx, state, _) = MakeEngine(WithWaterRow);
        var current = At(Give(state, "tool-fishing-rod", 1, ctx), 3, 1, "up");

        var a = current;
        var b = current;
        for (var i = 0; i < 10; i++)
        {
            a = Engine.ApplyCommand(ctx, a, new UseToolCommand("fishing-rod")).State;
            b = Engine.ApplyCommand(ctx, b, new UseToolCommand("fishing-rod")).State;
        }
        AssertDeepEqual(a.Player.Inventory, b.Player.Inventory);
        // Ten casts with the default table should land SOMETHING (fish or junk).
        var catchCount = a.Player.Inventory
            .Where(s => s.Item.Type == "fish" || s.Item.Id == "junk-boot")
            .Sum(s => s.Quantity);
        Assert.True(catchCount > 0);
        Assert.True(a.Player.Energy < 100); // rod costs energy
    }

    // --- mining (M4f) ---

    [Fact]
    public void FloorsGenerateDeterministicallyFromSeedPlusFloor()
    {
        var (ctx, _, _) = MakeEngine();
        var a = Mines.GenerateMineFloor(ctx, "seed-x", 3);
        var b = Mines.GenerateMineFloor(ctx, "seed-x", 3);
        var c = Mines.GenerateMineFloor(ctx, "seed-y", 3);
        AssertDeepEqual(a, b);
        Assert.NotEqual(StableJson.Stringify(a), StableJson.Stringify(c));
        // Has rocks from the band table
        var nodeCount = a.Tiles.SelectMany(row => row).Count(tile => tile.Node is not null);
        Assert.True(nodeCount > 5);
    }

    [Fact]
    public void EntranceInteractionDescendsExitReturnsToTheSurfaceAndDropsFloors()
    {
        var (ctx, state, _) = MakeEngine();
        // Player at (3,4); entrance at (5,5). Stand at (5,4) facing down → (5,5).
        var atEntrance = At(state, 5, 4, "down");
        var step = Engine.ApplyCommand(ctx, atEntrance, new InteractCommand());
        Assert.Equal("mine-floor-1", step.State.Player.SceneId);
        Assert.Equal(1, step.State.Mine.CurrentFloor);
        Assert.Contains(step.State.World.Scenes, s => s.Id == "mine-floor-1");

        // The exit check triggers when facing/at the entry — face up from (1,2).
        var inMine = At(step.State, 1, 2, "up");
        step = Engine.ApplyCommand(ctx, inMine, new ExitMineCommand());
        Assert.Equal("scene-test", step.State.Player.SceneId);
        Assert.DoesNotContain(step.State.World.Scenes, s => s.Id == "mine-floor-1");
    }

    [Fact]
    public void DescendCommandRespectsFloorBoundsAndRecordsDepth()
    {
        var (ctx, state, _) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, state, new DescendMineCommand(99));
        Assert.Equal(10, step.State.Mine.CurrentFloor); // clamped to config.floors
        Assert.Equal(10, step.State.Mine.DeepestFloor);
    }

    // --- skills (M4g) ---

    private static GameState GrowAndReady(EngineContext ctx, GameState start)
    {
        var current = Engine.ApplyCommand(ctx, start, new InteractCommand()).State; // plant
        for (var i = 0; i < 3; i++)
        {
            current = Engine.ApplyCommand(ctx, current, new UseToolCommand("watering-can")).State;
            current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        }
        return current;
    }

    [Fact]
    public void HarvestingGrantsFarmingXpAndLevelsUpAtThresholds()
    {
        var (ctx, state, _) = MakeEngine();
        var start = state with
        {
            Player = state.Player with
            {
                X = 3, Y = 3, Direction = "up",
                Skills = new OrderedDictionary<string, SkillState> { ["farming"] = new SkillState { Xp = 45, Level = 0 } },
            },
        };
        var current = GrowAndReady(ctx, start);
        var step = Engine.ApplyCommand(ctx, current, new InteractCommand()); // harvest: +8 xp → 53 ≥ 50
        Assert.Equal(1, step.State.Player.Skills["farming"].Level);
        Assert.True(HasMessage(step.Effects, t => t.Contains("Farming level 1")));
    }

    [Fact]
    public void SkillsCanBeDisabledPerProject()
    {
        var (ctx, state, _) = MakeEngine(project => project with { Settings = project.Settings with { SkillsEnabled = false } });
        var current = GrowAndReady(ctx, At(state, 3, 3, "up"));
        var step = Engine.ApplyCommand(ctx, current, new InteractCommand());
        Assert.False(step.State.Player.Skills.ContainsKey("farming"));
    }

    // --- cross-system replay (M4 exit criterion) ---

    [Fact]
    public void AScriptedInGameWeekUsingFarmingCraftingWeatherGiftingFishingMiningHashesDeterministically()
    {
        (EngineContext Ctx, GameState State, GameProject Project) Build() => MakeEngine(project =>
        {
            var withWater = WithGiftTastes(WithWaterRow(project), new GiftTastes { Loved = ["gift-flower"], Liked = [], Disliked = [], Hated = [] });
            var items = withWater.Items;
            return withWater with
            {
                Player = withWater.Player with
                {
                    Inventory =
                    [
                        .. withWater.Player.Inventory,
                        Slot(items, "tool-fishing-rod", 1),
                        Slot(items, "tool-pickaxe", 1),
                        Slot(items, "gift-flower", 3),
                        Slot(items, "material-fiber", 9),
                    ],
                },
            };
        });

        List<ReplayInput> week =
        [
            // Day 1: plant + water + craft hay + fish twice
            Replay.Cmd(new MoveCommand("up")),                   // (3,3)
            Replay.Cmd(new InteractCommand()),                   // plant on (3,2)
            Replay.Cmd(new UseToolCommand("watering-can")),
            Replay.Cmd(new CraftCommand("recipe-craft-hay")),
            Replay.Cmd(new MoveCommand("up")),                   // crops don't block: moves
            Replay.Cmd(new UseToolCommand("fishing-rod")),       // facing up
            Replay.Ticks(100),
            Replay.Cmd(new SleepCommand()),
            // Day 2: water, sleep
            Replay.Cmd(new UseToolCommand("watering-can")),
            Replay.Cmd(new SleepCommand()),
            // Day 3-4: water + sleep, then mine a bit
            Replay.Cmd(new UseToolCommand("watering-can")),
            Replay.Cmd(new SleepCommand()),
            Replay.Cmd(new DescendMineCommand(1)),
            Replay.Cmd(new MoveCommand("right")),
            Replay.Cmd(new UseToolCommand("pickaxe")),
            Replay.Cmd(new ExitMineCommand()),
            Replay.Cmd(new SleepCommand()),
            Replay.Cmd(new SleepCommand()),
            Replay.Cmd(new SleepCommand()),
            Replay.Cmd(new SleepCommand()),
        ];

        var (ctxA, stateA, _) = Build();
        var runA = Replay.RunReplay(ctxA, stateA, week);
        var (ctxB, stateB, _) = Build();
        var runB = Replay.RunReplay(ctxB, stateB, week);
        Assert.Equal(runA.Hash, runB.Hash);
        AssertDeepEqual(runA.State, runB.State);
        Assert.Equal(8, runA.State.Clock.Day);
    }
}
