using FarmEngine.Json;
using FarmEngine.Schemas;
using static FarmEngine.Core.Replay;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of replay.test.ts. Golden replay tests — determinism exit criteria: a
/// scripted multi-day session applied twice must produce identical state
/// hashes, and 1,000 generated commands must be deterministic end to end.
/// </summary>
public class ReplayTests
{
    private const string NeedsModules = "needs the full engine (Tiles, WorldMovement, FarmingActions, Economy, GameTime, …) — enable at integration";

    private static GameProject MakeFarmProject()
    {
        var scene = Tiles.CreateEmptyScene("farm", "Farm", 10, 10);
        for (var x = 2; x <= 7; x++)
        {
            scene.Tiles[4][x] = Tiles.SetTileLayer(scene.Tiles[4][x], "soil");
        }
        scene.Tiles[2][2] = scene.Tiles[2][2] with { Node = new TileNode { TypeId = "node-tree", RemainingHealth = 4 } };
        var items = ContentBuiltin.CreateDefaultItems();
        return new GameProject
        {
            SchemaVersion = 4,
            Id = "replay-farm",
            Name = "Replay Farm",
            Version = "2.0",
            Scenes = [scene],
            Npcs = [],
            Items = items,
            Events = [],
            Dialogues = [],
            Quests = [],
            Player = new Player
            {
                X = 4, Y = 6, Direction = "up", SceneId = "farm",
                Inventory =
                [
                    CoreTestHelpers.Slot(items, "seed-wheat", 20),
                    CoreTestHelpers.Slot(items, "tool-hoe", 1),
                    CoreTestHelpers.Slot(items, "tool-watering-can", 1),
                    CoreTestHelpers.Slot(items, "tool-axe", 1),
                    CoreTestHelpers.Slot(items, "fertilizer-basic", 5),
                ],
                MaxInventorySize = 20,
                Money = 100,
                ActiveQuests = [], CompletedQuests = [],
                PixelX = 0, PixelY = 0, TargetX = 0, TargetY = 0,
            },
            EventFlags = [],
            StartSceneId = "farm",
            Mode = "play",
            SelectedTileType = "grass",
            SelectedNpcId = null,
            SelectedItemId = null,
            CurrentTime = 500_000,
            CustomAssets = [],
            CurrentSeason = "spring",
            CurrentDay = 1,
            CurrentTimeMinutes = 6 * 60,
            CurrentYear = 1,
            Shops = [ContentBuiltin.CreateDefaultShop()],
            NodeTypes = [],
            Settings = SettingsSchema.DefaultProjectSettings,
            Recipes = [],
            Actions = [],
            Minigames = [],
            MachineTypes = [],
            Weather = MigrationsSchema.DefaultWeatherConfig(),
            AnimalSpecies = [],
            Animals = [],
            FishTables = [],
            Mine = new MineConfig { Enabled = false },
            ContentPacks = [],
            GameStartTime = 500_000,
        };
    }

    private static (EngineContext Ctx, GameState State) MakeEngine(string seed)
    {
        var project = MakeFarmProject();
        return (new EngineContext(EngineState.CreateContentFromProject(project)), EngineState.CreateGameState(project, seed));
    }

    /// <summary>
    /// A scripted three-day playthrough: plant + water, sleep, gather wood,
    /// water, sleep, water, sleep, harvest, sell at the store, buy seeds.
    /// (M2 exit criterion: this hashes identically across runs.)
    /// </summary>
    private static readonly List<ReplayInput> ThreeDays =
    [
        // Day 1: plant and water
        Cmd(new MoveCommand("up")),                 // (4,5), facing the soil row
        Cmd(new InteractCommand()),                 // plant wheat on (4,4) (fertilized)
        Cmd(new UseToolCommand("watering-can")),
        Ticks(200),                                 // 10 in-game minutes pass
        Cmd(new SleepCommand()),
        // Day 2: chop the tree, keep watering
        Cmd(new UseToolCommand("watering-can")),
        Cmd(new SleepCommand()),
        // Day 3: water, sleep → mature on day 4 (wheat = 3 watered days)
        Cmd(new UseToolCommand("watering-can")),
        Cmd(new SleepCommand()),
        // Day 4: harvest, then trade
        Cmd(new InteractCommand()),                 // harvest (4,4)
        Cmd(new OpenShopCommand("shop-general")),
        Cmd(new SellItemCommand("crop-wheat", 1)),
        Cmd(new BuyItemCommand("seed-carrot", 2)),
        Cmd(new CloseShopCommand()),
    ];

    /// <summary>
    /// Committed golden hash for THREE_DAYS with seed 'golden' (from the TS
    /// reference engine). Same-process double-runs can only catch same-process
    /// nondeterminism; this constant is what actually detects divergence.
    /// </summary>
    private const string ThreeDaysGoldenHash = "6f884fd1bf6e2b5e";

    private static ReplayResult Run(string seed, IEnumerable<ReplayInput> inputs)
    {
        var (ctx, state) = MakeEngine(seed);
        return RunReplay(ctx, state, inputs);
    }

    [Fact]
    public void TheScriptedThreeDayPlaythroughMatchesTheCommittedGoldenHash()
    {
        var run = Run("golden", ThreeDays);
        Assert.Equal(ThreeDaysGoldenHash, run.Hash);
    }

    [Fact]
    public void TickBatchingDoesNotChangeTheOutcome()
    {
        var batched = Run("golden", ThreeDays);
        var singleTicks = ThreeDays.SelectMany(input =>
            input is TickInput tick
                ? Enumerable.Range(0, (int)tick.Ticks).Select(_ => Ticks(1))
                : [input]);
        var unbatched = Run("golden", singleTicks);
        Assert.Equal(batched.Hash, unbatched.Hash);
        CoreTestHelpers.AssertDeepEqual(batched.State, unbatched.State);
    }

    [Fact]
    public void TheScriptedThreeDayPlaythroughHashesIdenticallyAcrossTwoRuns()
    {
        var runA = Run("golden", ThreeDays);
        var runB = Run("golden", ThreeDays);
        Assert.Equal(runA.Hash, runB.Hash);
        CoreTestHelpers.AssertDeepEqual(runA.State, runB.State);
        // sanity: the loop closed — sold a harvest, bought seeds
        Assert.Equal(4, runA.State.Clock.Day);
        Assert.Contains(runA.State.Player.Inventory, s => s.Item.Id == "seed-carrot");
        Assert.NotEqual(100, runA.State.Player.Money);
    }

    [Fact]
    public void ADifferentSeedProducesADifferentStreamButStillSelfConsistent()
    {
        var runA = Run("seed-1", ThreeDays);
        var runB = Run("seed-2", ThreeDays);
        var runB2 = Run("seed-2", ThreeDays);
        Assert.Equal(runB.Hash, runB2.Hash);
        Assert.NotEqual(runA.Hash, runB.Hash);
    }

    [Fact]
    public void OneThousandGeneratedCommandsAreDeterministicEndToEnd()
    {
        // Simple LCG so the command log itself is reproducible test-side.
        var lcg = 123456789u;
        uint NextLcg()
        {
            lcg = unchecked(lcg * 1103515245u + 12345u);
            return lcg;
        }
        string[] dirs = ["up", "down", "left", "right"];
        var inputs = new List<ReplayInput>();
        for (var i = 0; i < 1000; i++)
        {
            var roll = NextLcg() % 12;
            Command cmd;
            if (roll < 5) cmd = new MoveCommand(dirs[NextLcg() % 4]);
            else if (roll < 7) cmd = new InteractCommand();
            else if (roll == 7) cmd = new UseToolCommand("watering-can");
            else if (roll == 8) cmd = new UseToolCommand("hoe");
            else if (roll == 9) cmd = new UseToolCommand("axe");
            else if (roll == 10) cmd = new SleepCommand();
            else cmd = new CloseDialogueCommand();
            inputs.Add(Cmd(cmd));
            if (i % 25 == 0) inputs.Add(Ticks(NextLcg() % 40));
        }

        var runA = Run("thousand", inputs);
        var runB = Run("thousand", inputs);
        Assert.Equal(runA.Hash, runB.Hash);
        CoreTestHelpers.AssertDeepEqual(runA.State, runB.State);
    }

    // --- C#-side additions: the replay log's JSON shape matches the TS union ---

    [Fact]
    public void ReplayInputsSerializeWithTheTsKindDiscriminator()
    {
        List<ReplayInput> inputs = [Cmd(new MoveCommand("up")), Ticks(200)];
        var json = JsonDefaults.Serialize(inputs);
        Assert.Equal("""[{"kind":"command","command":{"type":"move","dir":"up"}},{"kind":"tick","ticks":200}]""", json);
        var back = JsonDefaults.Deserialize<List<ReplayInput>>(json)!;
        Assert.Equal(inputs, back);
    }
}
