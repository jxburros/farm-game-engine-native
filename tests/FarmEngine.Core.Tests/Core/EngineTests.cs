using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Core;

/// <summary>Shared assertions for the ported engine-core tests.</summary>
public static class CoreTestHelpers
{
    /// <summary>TS <c>effects.some(e => e.type === 'message' &amp;&amp; predicate(e.text))</c>.</summary>
    public static bool HasMessage(IEnumerable<Effect> effects, Func<string, bool> predicate) =>
        effects.Any(e => e is MessageEffect m && predicate(m.Text));

    /// <summary>TS <c>expect(a).toEqual(b)</c> for state-shaped values: deep structural equality.</summary>
    public static void AssertDeepEqual<T>(T expected, T actual) =>
        Assert.Equal(StableJson.Stringify(expected), StableJson.Stringify(actual));

    /// <summary>TS <c>expect(x).toBeCloseTo(expected)</c> (2 decimal digits).</summary>
    public static void AssertCloseTo(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) < 0.005, $"expected {actual} to be close to {expected}");

    /// <summary>TS <c>expect(state.flags[key]).toBe(true)</c>.</summary>
    public static bool FlagIsTrue(GameState state, string key) =>
        state.Flags.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;

    public static InventorySlot Slot(List<Item> items, string id, double quantity) =>
        new() { Item = items.First(i => i.Id == id), Quantity = quantity };
}

/// <summary>Port of engine.test.ts.</summary>
public class EngineTests
{
    private const string NeedsModules = "needs Tiles/WorldMovement/FarmingActions/Economy/GameTime/… — enable at integration";

    /// <summary>Minimal test project: 6x6 open field with soil at (3,2), npc at (1,1).</summary>
    public static GameProject MakeProject()
    {
        var scene = Tiles.CreateEmptyScene("scene-test", "Test Farm", 6, 6);
        scene.Tiles[2][3] = Tiles.SetTileLayer(scene.Tiles[2][3], "soil");
        scene.Tiles[4][4] = Tiles.SetTileLayer(scene.Tiles[4][4], "wall");

        var items = ContentBuiltin.CreateDefaultItems();
        var npc = new Npc
        {
            Id = "npc-test",
            Name = "Testy",
            X = 1,
            Y = 1,
            SceneId = "scene-test",
            Dialogue =
            [
                new Dialogue
                {
                    Id = "dlg-1",
                    NpcId = "npc-test",
                    Text = "Hello!",
                    Options =
                    [
                        new DialogueOption { Text = "Bye" },
                        new DialogueOption { Text = "Gift me", GiveMoney = 25, NextDialogueId = "dlg-2" },
                    ],
                },
                new Dialogue { Id = "dlg-2", NpcId = "npc-test", Text = "More?", Options = [new DialogueOption { Text = "No" }] },
            ],
            CanMove = false,
            Appearance = "farmer",
        };

        var quest = new Quest
        {
            Id = "quest-wheat",
            Name = "Wheat!",
            Description = "Harvest 1 wheat",
            Status = "active",
            Objectives =
            [
                new QuestObjective
                {
                    Id = "obj-1", Type = "harvest", Description = "Harvest wheat", TargetCropType = "wheat",
                    TargetCropQuantity = 1, Completed = false, Progress = 0,
                },
            ],
            Rewards = new QuestRewards { Money = 100 },
        };

        static List<WeatherTableEntry> SunOnly() => [new WeatherTableEntry { WeatherId = "sun", Weight = 1 }];

        return new GameProject
        {
            SchemaVersion = 4,
            Id = "proj-test",
            Name = "Test",
            Version = "2.0",
            Scenes = [scene],
            Npcs = [npc],
            Items = items,
            Events = [],
            // Same list instance as the NPC's dialogue (TS `dialogues: npc.dialogue`).
            Dialogues = npc.Dialogue,
            Quests = [quest],
            Player = new Player
            {
                X = 3,
                Y = 4,
                Direction = "up",
                SceneId = "scene-test",
                Inventory =
                [
                    CoreTestHelpers.Slot(items, "seed-wheat", 5),
                    CoreTestHelpers.Slot(items, "tool-hoe", 1),
                    CoreTestHelpers.Slot(items, "tool-watering-can", 1),
                ],
                MaxInventorySize = 10,
                Money = 100,
                ActiveQuests = ["quest-wheat"],
                CompletedQuests = [],
                PixelX = 0, PixelY = 0, TargetX = 0, TargetY = 0,
            },
            EventFlags = [],
            StartSceneId = "scene-test",
            Mode = "play",
            SelectedTileType = "grass",
            SelectedNpcId = null,
            SelectedItemId = null,
            CurrentTime = 1_000_000,
            CustomAssets = [],
            CurrentSeason = "spring",
            CurrentDay = 1,
            CurrentTimeMinutes = 6 * 60,
            CurrentYear = 1,
            Shops = [],
            NodeTypes = [],
            Settings = SettingsSchema.DefaultProjectSettings,
            Recipes = [],
            Actions = [],
            Minigames = [],
            MachineTypes = [],
            // Sun-only table keeps sim tests weather-independent (weather has its
            // own dedicated tests).
            Weather = new WeatherConfig
            {
                Types = [new WeatherTypeDefinition { Id = "sun", Name = "Sunny" }],
                Table = new OrderedDictionary<string, List<WeatherTableEntry>>
                {
                    ["spring"] = SunOnly(),
                    ["summer"] = SunOnly(),
                    ["fall"] = SunOnly(),
                    ["winter"] = SunOnly(),
                },
            },
            AnimalSpecies = [],
            Animals = [],
            FishTables = [],
            Mine = new MineConfig { Enabled = false },
            ContentPacks = [],
            GameStartTime = 1_000_000,
        };
    }

    private static (EngineContext Ctx, GameState State) MakeEngine()
    {
        var project = MakeProject();
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "engine-test");
        return (ctx, state);
    }

    /// <summary>Water the facing tile then sleep — one full watered day for a crop.</summary>
    private static GameState WaterAndSleep(EngineContext ctx, GameState state, int days)
    {
        var current = state;
        for (var i = 0; i < days; i++)
        {
            current = Engine.ApplyCommand(ctx, current, new UseToolCommand("watering-can")).State;
            current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        }
        return current;
    }

    private static GameState FacingSoil(GameState state) =>
        state with { Player = state.Player with { X = 3, Y = 3, Direction = "up" } };

    // --- movement ---

    [Fact]
    public void MovesThePlayerAndUpdatesDirection()
    {
        var (ctx, state) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, state, new MoveCommand("left"));
        Assert.Equal(2.5, step.State.Player.X);
        Assert.Equal(4.5, step.State.Player.Y);
        Assert.Equal("left", step.State.Player.Direction);
        Assert.Contains(new PlayerMovedEffect(2, 4), step.Effects);
    }

    [Fact]
    public void BlocksMovementIntoWallsButStillTurns()
    {
        var (ctx, state) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, state, new MoveCommand("right"));
        Assert.Equal(3.5, step.State.Player.X);
        Assert.Equal("right", step.State.Player.Direction);
    }

    [Fact]
    public void BlocksMovementOutOfBounds()
    {
        var (ctx, state) = MakeEngine();
        var current = state;
        for (var i = 0; i < 10; i++)
        {
            current = Engine.ApplyCommand(ctx, current, new MoveCommand("down")).State;
        }
        Assert.Equal(5.5, current.Player.Y);
    }

    [Fact]
    public void BlocksMovementOntoNpcs()
    {
        var (ctx, state) = MakeEngine();
        var start = state with { Player = state.Player with { X = 1, Y = 2 } };
        var step = Engine.ApplyCommand(ctx, start, new MoveCommand("up"));
        Assert.Equal(2, step.State.Player.Y); // npc at (1,1)
    }

    // --- tools ---

    [Fact]
    public void TillsGrassIntoSoilConsumingDurabilityAndEnergy()
    {
        var (ctx, state) = MakeEngine();
        // player at (3,4) facing up → target (3,3) is grass
        var step = Engine.ApplyCommand(ctx, state, new UseToolCommand("hoe"));
        var tile = step.State.World.Scenes[0].Tiles[3][3];
        Assert.Equal("soil", tile.Background);
        Assert.Equal("dry", tile.SoilState);
        Assert.Contains(new MessageEffect("success", "Tilled soil!"), step.Effects);
        var hoe = step.State.Player.Inventory.First(s => s.Item.ToolType == "hoe");
        Assert.Equal(99, hoe.Item.Durability);
        Assert.Equal(96, step.State.Player.Energy); // hoe costs 4
    }

    [Fact]
    public void WatersSoilAndMarksCropsWatered()
    {
        var (ctx, state) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, FacingSoil(state), new UseToolCommand("watering-can"));
        var tile = step.State.World.Scenes[0].Tiles[2][3];
        Assert.Equal(100, tile.SoilMoisture);
        Assert.Equal("watered", tile.SoilState);
        Assert.Equal(98, step.State.Player.Energy); // watering can costs 2
    }

    [Fact]
    public void ReportsMissingTools()
    {
        var (ctx, state) = MakeEngine();
        var noTools = state with { Player = state.Player with { Inventory = [] } };
        var step = Engine.ApplyCommand(ctx, noTools, new UseToolCommand("watering-can"));
        Assert.Contains(new MessageEffect("error", "You need a watering can!"), step.Effects);
        Assert.Same(noTools, step.State);
    }

    // --- planting and harvesting (day-based) ---

    [Fact]
    public void PlantsASeedOnSoilConsumingItCropStartsUnwatered()
    {
        var (ctx, state) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, FacingSoil(state), new InteractCommand());
        var tile = step.State.World.Scenes[0].Tiles[2][3];
        Assert.Equal("wheat", tile.Crop?.Type);
        Assert.False(tile.Crop?.Watered);
        Assert.Equal(0, tile.Crop?.DaysGrown);
        Assert.Equal(1, tile.Crop?.PlantedOnDay);
        var seeds = step.State.Player.Inventory.First(s => s.Item.Id == "seed-wheat");
        Assert.Equal(4, seeds.Quantity);
    }

    [Fact]
    public void RefusesOutOfSeasonCrops()
    {
        var (ctx, state) = MakeEngine();
        var summer = state with
        {
            Clock = state.Clock with { Season = "summer" },
            Player = state.Player with { X = 3, Y = 3, Direction = "up" },
        };
        var step = Engine.ApplyCommand(ctx, summer, new InteractCommand());
        Assert.Null(step.State.World.Scenes[0].Tiles[2][3].Crop);
        Assert.True(CoreTestHelpers.HasMessage(step.Effects, t => t.Contains("cannot grow in summer")));
    }

    [Fact]
    public void CropsMatureAfterGrowthDaysWateredDaysHarvestYieldsItemsButNoAutoMoney()
    {
        var (ctx, state) = MakeEngine();
        var current = FacingSoil(state);
        current = Engine.ApplyCommand(ctx, current, new InteractCommand()).State; // plant wheat (3 growth days)
        current = WaterAndSleep(ctx, current, 3);

        Assert.Equal(3, current.World.Scenes[0].Tiles[2][3].Crop?.DaysGrown);
        Assert.Equal(4, current.Clock.Day);

        var moneyBeforeHarvest = current.Player.Money;
        var step = Engine.ApplyCommand(ctx, current, new InteractCommand());
        Assert.Null(step.State.World.Scenes[0].Tiles[2][3].Crop);
        var wheat = step.State.Player.Inventory.FirstOrDefault(s => s.Item.Id == "crop-wheat");
        Assert.NotNull(wheat);
        Assert.True(wheat!.Quantity >= 1);
        // No auto-sell: money only moves via the quest reward (100).
        Assert.Equal(moneyBeforeHarvest + 100, step.State.Player.Money);
        Assert.Contains("quest-wheat", step.State.Player.CompletedQuests);
        Assert.Equal("completed", step.State.Quests["quest-wheat"].Status);
        Assert.Contains(step.Effects, e => e is QuestCompletedEffect);
    }

    [Fact]
    public void UnwateredCropsDoNotGrowOvernight()
    {
        var (ctx, state) = MakeEngine();
        var current = FacingSoil(state);
        current = Engine.ApplyCommand(ctx, current, new InteractCommand()).State;
        current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State; // no watering
        var crop = current.World.Scenes[0].Tiles[2][3].Crop!;
        Assert.Equal(0, crop.DaysGrown);
        Assert.Equal(1, crop.DaysWithoutWater);
    }

    [Fact]
    public void IsNotHarvestableBeforeMaturity()
    {
        var (ctx, state) = MakeEngine();
        var current = FacingSoil(state);
        current = Engine.ApplyCommand(ctx, current, new InteractCommand()).State;
        var step = Engine.ApplyCommand(ctx, current, new InteractCommand());
        Assert.Contains(new MessageEffect("info", "Crop is not ready to harvest yet"), step.Effects);
        Assert.NotNull(step.State.World.Scenes[0].Tiles[2][3].Crop);
    }

    // --- dialogue ---

    [Fact]
    public void OpensDialogueWhenInteractingWithAnNpc()
    {
        var (ctx, state) = MakeEngine();
        var nextToNpc = state with { Player = state.Player with { X = 1, Y = 2, Direction = "up" } };
        var step = Engine.ApplyCommand(ctx, nextToNpc, new InteractCommand());
        Assert.Equal(new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" }, step.State.Dialogue);
    }

    [Fact]
    public void AppliesOptionRewardsAndFollowsNextDialogueId()
    {
        var (ctx, state) = MakeEngine();
        var inDialogue = state with { Dialogue = new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" } };
        var step = Engine.ApplyCommand(ctx, inDialogue, new ChooseDialogueOptionCommand(1));
        Assert.Equal(125, step.State.Player.Money);
        Assert.Equal(new DialogueState { NpcId = "npc-test", DialogueId = "dlg-2" }, step.State.Dialogue);

        var closed = Engine.ApplyCommand(ctx, step.State, new ChooseDialogueOptionCommand(0));
        Assert.Null(closed.State.Dialogue);
    }

    [Fact]
    public void CloseDialogueClearsDialogueState()
    {
        var (ctx, state) = MakeEngine();
        var inDialogue = state with { Dialogue = new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" } };
        var step = Engine.ApplyCommand(ctx, inDialogue, new CloseDialogueCommand());
        Assert.Null(step.State.Dialogue);
    }

    [Fact]
    public void TalkingToAnNpcProgressesTalkQuests()
    {
        var project = MakeProject();
        project.Quests.Add(new Quest
        {
            Id = "quest-talk",
            Name = "Say hi",
            Description = "Talk to Testy",
            Status = "active",
            Objectives =
            [
                new QuestObjective { Id = "obj-t", Type = "talk", Description = "Talk", TargetNpcId = "npc-test", Completed = false, Progress = 0 },
            ],
            Rewards = new QuestRewards { Money = 10 },
        });
        project.Player.ActiveQuests.Add("quest-talk");
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "talk");
        var nextToNpc = state with { Player = state.Player with { X = 1, Y = 2, Direction = "up" } };
        var step = Engine.ApplyCommand(ctx, nextToNpc, new InteractCommand());
        Assert.Contains("quest-talk", step.State.Player.CompletedQuests);
        Assert.Equal(110, step.State.Player.Money);
    }

    // --- state ⇄ project bridge ---

    [Fact]
    public void ApplyStateToProjectRoundTripsPlayerWorldQuestsAndFlags()
    {
        var project = MakeProject();
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "bridge");

        state = Engine.ApplyCommand(ctx, state, new MoveCommand("left")).State;
        state = Engine.ApplyCommand(ctx, FacingSoil(state), new InteractCommand()).State;

        var synced = EngineState.ApplyStateToProject(project, state);
        Assert.Equal(3, synced.Player.X);
        Assert.Equal(3, synced.Player.Y);
        Assert.Equal("wheat", synced.Scenes[0].Tiles[2][3].Crop?.Type);
        CoreTestHelpers.AssertDeepEqual(state.Rng, synced.RngState);
        Assert.Equal(state.Clock.TimeMinutes, synced.CurrentTimeMinutes);
        // editor-only fields are preserved
        Assert.Equal(project.Mode, synced.Mode);
        Assert.Same(project.CustomAssets, synced.CustomAssets);
    }

    [Fact]
    public void AdvanceTickAdvancesTheGameClockAndNothingElseBeforeDayEnd()
    {
        var project = MakeProject();
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "clock");
        var step = Engine.AdvanceTick(ctx, state, 20); // 20 ticks = 1 real second = 1 game minute
        Assert.Equal(20, step.State.Clock.Tick);
        CoreTestHelpers.AssertCloseTo(6 * 60 + 1, step.State.Clock.TimeMinutes);
        Assert.Same(state.Player, step.State.Player);
        Assert.Same(state.World, step.State.World);
        Assert.Empty(step.Effects);
    }

    [Fact]
    public void TheClockPassingDayEndForcesACollapseIntoTheNextDay()
    {
        var project = MakeProject();
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "collapse");
        // 2am is 20h of game time from 6:00 = 1200 game minutes = 1200 real seconds = 24000 ticks
        var step = Engine.AdvanceTick(ctx, state, 24000);
        Assert.Equal(2, step.State.Clock.Day);
        Assert.Equal(6 * 60, step.State.Clock.TimeMinutes);
        Assert.True(CoreTestHelpers.HasMessage(step.Effects, t => t.Contains("collapsed")));
        // collapse penalty: half energy, money fee
        Assert.Equal(50, step.State.Player.Energy);
        Assert.Equal(50, step.State.Player.Money);
    }
}
