using FarmEngine.Schemas;
using static FarmEngine.Core.Tests.Core.CoreTestHelpers;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of m2-systems.test.ts. M2 system tests: calendar math, nightly pass,
/// economy, gathering, energy.
/// </summary>
public class M2SystemsTests
{
    private const string NeedsModules = "needs the full engine (Tiles, GameTime, Economy, Gathering, Energy, …) — enable at integration";
    private const string NeedsTime = "needs GameTime — enable at integration";

    private static readonly CalendarConfig ClassicCalendar = new() { Seasons = SettingsSchema.ClassicCalendarSeasons(), Festivals = [] };

    private static (EngineContext Ctx, GameState State, GameProject Project) MakeEngine(Func<GameProject, GameProject>? mutate = null)
    {
        var project = EngineTests.MakeProject() with
        {
            Shops =
            [
                new ShopDefinition
                {
                    Id = "shop-test",
                    Name = "Test Shop",
                    Stock =
                    [
                        new ShopStockEntry { ItemId = "seed-wheat" },
                        new ShopStockEntry { ItemId = "seed-tomato", Seasons = ["summer"] },
                        new ShopStockEntry { ItemId = "fertilizer-quality", DailyLimit = 2 },
                        new ShopStockEntry { ItemId = "seed-carrot", Price = 3 },
                    ],
                    SellPriceMultiplier = 1,
                    BuysItems = true,
                    RepairsTools = true,
                    RepairCostPerPoint = 0.5,
                },
            ],
        };
        if (mutate is not null) project = mutate(project);
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "m2");
        return (ctx, state, project);
    }

    private static GameState FacingSoil(GameState state) =>
        state with { Player = state.Player with { X = 3, Y = 3, Direction = "up" } };

    // --- calendar math ---

    [Fact]
    public void MapsAbsoluteDaysToDayOfSeasonSeasonYear()
    {
        Assert.Equal(1, GameTime.DayOfSeason(ClassicCalendar, 1));
        Assert.Equal(28, GameTime.DayOfSeason(ClassicCalendar, 28));
        Assert.Equal(1, GameTime.DayOfSeason(ClassicCalendar, 29));
        Assert.Equal("spring", GameTime.SeasonForDay(ClassicCalendar, 1));
        Assert.Equal("spring", GameTime.SeasonForDay(ClassicCalendar, 28));
        Assert.Equal("summer", GameTime.SeasonForDay(ClassicCalendar, 29));
        Assert.Equal("winter", GameTime.SeasonForDay(ClassicCalendar, 112));
        Assert.Equal("spring", GameTime.SeasonForDay(ClassicCalendar, 113));
        Assert.Equal(1, GameTime.YearForDay(ClassicCalendar, 112));
        Assert.Equal(2, GameTime.YearForDay(ClassicCalendar, 113));
    }

    [Fact]
    public void FormatsTimeOfDay()
    {
        Assert.Equal("6:00 AM", GameTime.FormatTimeOfDay(6 * 60));
        Assert.Equal("12:00 PM", GameTime.FormatTimeOfDay(12 * 60));
        Assert.Equal("1:30 PM", GameTime.FormatTimeOfDay(13 * 60 + 30));
        Assert.Equal("12:00 AM", GameTime.FormatTimeOfDay(0));
        Assert.Equal("1:00 AM", GameTime.FormatTimeOfDay(25 * 60)); // past-midnight wrap
    }

    [Fact]
    public void ClassifiesDayPhases()
    {
        Assert.Equal("morning", GameTime.DayPhase(6 * 60));
        Assert.Equal("day", GameTime.DayPhase(12 * 60));
        Assert.Equal("evening", GameTime.DayPhase(18 * 60));
        Assert.Equal("night", GameTime.DayPhase(23 * 60));
    }

    // --- nightly pass ---

    [Fact]
    public void SleepRollsTheDayResetsTheClockAndRestoresEnergy()
    {
        var (ctx, state, _) = MakeEngine();
        var tired = state with { Player = state.Player with { Energy = 12 }, Clock = state.Clock with { TimeMinutes = 20 * 60 } };
        var step = Engine.ApplyCommand(ctx, tired, new SleepCommand());
        Assert.Equal(2, step.State.Clock.Day);
        Assert.Equal(6 * 60, step.State.Clock.TimeMinutes);
        Assert.Equal(100, step.State.Player.Energy);
        Assert.Contains(step.Effects, e => e is DayStartedEffect);
    }

    [Fact]
    public void SeasonRollsOnDay29AndWithersOutOfSeasonCrops()
    {
        var (ctx, state, _) = MakeEngine();
        // Plant wheat (spring/fall crop), then jump to the last day of spring.
        var current = FacingSoil(state);
        current = Engine.ApplyCommand(ctx, current, new InteractCommand()).State;
        current = current with { Clock = current.Clock with { Day = 28 } };
        var step = Engine.ApplyCommand(ctx, current, new SleepCommand());
        Assert.Equal(29, step.State.Clock.Day);
        Assert.Equal("summer", step.State.Clock.Season);
        Assert.True(step.State.World.Scenes[0].Tiles[2][3].Crop?.Withered);
        Assert.True(HasMessage(step.Effects, t => t.Contains("Summer has arrived")));
    }

    [Fact]
    public void WateredSoilDriesOvernight()
    {
        var (ctx, state, _) = MakeEngine();
        var current = Engine.ApplyCommand(ctx, FacingSoil(state), new UseToolCommand("watering-can")).State;
        Assert.Equal("watered", current.World.Scenes[0].Tiles[2][3].SoilState);
        current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        Assert.Equal("dry", current.World.Scenes[0].Tiles[2][3].SoilState);
        Assert.Equal(0, current.World.Scenes[0].Tiles[2][3].SoilMoisture);
    }

    [Fact]
    public void RegrowingCropsResetToAPartialGrowthStateOnHarvest()
    {
        var (ctx, state, _) = MakeEngine(project => project with
        {
            Player = project.Player with
            {
                Inventory =
                [
                    Slot(project.Items, "seed-tomato", 1),
                    Slot(project.Items, "tool-watering-can", 1),
                ],
            },
        });
        // Summer for tomatoes (4 growth days, 2 regrowth days). Day 29 keeps
        // the season stable across the sleeps below.
        var current = state with
        {
            Clock = state.Clock with { Season = "summer", Day = 29 },
            Player = state.Player with { X = 3, Y = 3, Direction = "up" },
        };
        current = Engine.ApplyCommand(ctx, current, new InteractCommand()).State;
        for (var i = 0; i < 4; i++)
        {
            current = Engine.ApplyCommand(ctx, current, new UseToolCommand("watering-can")).State;
            current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        }
        var step = Engine.ApplyCommand(ctx, current, new InteractCommand());
        var crop = step.State.World.Scenes[0].Tiles[2][3].Crop;
        Assert.NotNull(crop);
        Assert.Equal(1, crop!.HarvestCount);
        Assert.Equal(2, crop.DaysGrown); // growthDays 4 - regrowthDays 2
    }

    // --- economy ---

    private static GameState OpenShop(EngineContext ctx, GameState state) =>
        Engine.ApplyCommand(ctx, state, new OpenShopCommand("shop-test")).State;

    [Fact]
    public void BuysStockChargingMoney()
    {
        var (ctx, state, _) = MakeEngine();
        var inShop = OpenShop(ctx, state);
        var step = Engine.ApplyCommand(ctx, inShop, new BuyItemCommand("seed-wheat", 2));
        Assert.Equal(100 - 20, step.State.Player.Money);
        var seeds = step.State.Player.Inventory.FirstOrDefault(s => s.Item.Id == "seed-wheat");
        Assert.Equal(7, seeds?.Quantity); // 5 starting + 2
    }

    [Fact]
    public void HonorsPriceOverrides()
    {
        var (ctx, state, _) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, OpenShop(ctx, state), new BuyItemCommand("seed-carrot", 1));
        Assert.Equal(97, step.State.Player.Money);
    }

    [Fact]
    public void RejectsPurchasesWithoutEnoughMoney()
    {
        var (ctx, state, _) = MakeEngine();
        var broke = OpenShop(ctx, state) with { Player = state.Player with { Money = 5 } };
        var step = Engine.ApplyCommand(ctx, broke, new BuyItemCommand("seed-wheat", 1));
        Assert.True(HasMessage(step.Effects, t => t == "Not enough money!"));
        Assert.Equal(5, step.State.Player.Money);
    }

    [Fact]
    public void EnforcesSeasonalStock()
    {
        var (ctx, state, _) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, OpenShop(ctx, state), new BuyItemCommand("seed-tomato", 1));
        Assert.True(HasMessage(step.Effects, t => t.Contains("Not available in spring")));
    }

    [Fact]
    public void EnforcesAndResetsDailyLimits()
    {
        var (ctx, state, _) = MakeEngine();
        var current = OpenShop(ctx, state);
        current = Engine.ApplyCommand(ctx, current, new BuyItemCommand("fertilizer-quality", 2)).State;
        var blocked = Engine.ApplyCommand(ctx, current, new BuyItemCommand("fertilizer-quality", 1));
        Assert.True(HasMessage(blocked.Effects, t => t.Contains("Sold out for today")));

        // Next day the limit resets.
        current = Engine.ApplyCommand(ctx, blocked.State, new SleepCommand()).State;
        current = OpenShop(ctx, current);
        var again = Engine.ApplyCommand(ctx, current, new BuyItemCommand("fertilizer-quality", 1));
        Assert.True(HasMessage(again.Effects, t => t.StartsWith("Bought", StringComparison.Ordinal)));
    }

    [Fact]
    public void SellsItemsForTheirValue()
    {
        var (ctx, state, _) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, OpenShop(ctx, state), new SellItemCommand("seed-wheat", 5));
        Assert.Equal(100 + 50, step.State.Player.Money);
        Assert.DoesNotContain(step.State.Player.Inventory, s => s.Item.Id == "seed-wheat");
    }

    [Fact]
    public void RepairsDamagedToolsForAFee()
    {
        var (ctx, state, _) = MakeEngine();
        var damaged = OpenShop(ctx, state) with
        {
            Player = state.Player with
            {
                Inventory = state.Player.Inventory
                    .Select(slot => slot.Item.Id == "tool-hoe" ? slot with { Item = slot.Item with { Durability = 0 } } : slot)
                    .ToList(),
            },
        };
        var step = Engine.ApplyCommand(ctx, damaged, new RepairToolCommand("tool-hoe"));
        var hoe = step.State.Player.Inventory.First(s => s.Item.Id == "tool-hoe");
        Assert.Equal(100, hoe.Item.Durability);
        Assert.Equal(100 - 50, step.State.Player.Money); // 100 points * 0.5
    }

    [Fact]
    public void OpensAShopFromADialogueOption()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Npcs[0].Dialogue[0].Options.Add(new DialogueOption { Text = "Trade", OpenShopId = "shop-test" });
            return project;
        });
        var inDialogue = state with { Dialogue = new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" } };
        var step = Engine.ApplyCommand(ctx, inDialogue, new ChooseDialogueOptionCommand(2));
        Assert.Equal(new ShopSession { ShopId = "shop-test" }, step.State.Shop);
        Assert.Null(step.State.Dialogue);
    }

    // --- gathering ---

    private static (EngineContext Ctx, GameState State, GameProject Project) WithTree(double mutateHealth = 4) =>
        MakeEngine(project =>
        {
            project.Scenes[0].Tiles[3][3] = project.Scenes[0].Tiles[3][3] with
            {
                Node = new TileNode { TypeId = "node-tree", RemainingHealth = mutateHealth },
            };
            project.Player.Inventory.Add(Slot(project.Items, "tool-axe", 1));
            project.Player.Inventory.Add(Slot(project.Items, "tool-pickaxe", 1));
            return project;
        });

    [Fact]
    public void RequiresTheRightTool()
    {
        var (ctx, state, _) = WithTree();
        var step = Engine.ApplyCommand(ctx, state, new UseToolCommand("pickaxe"));
        Assert.True(HasMessage(step.Effects, t => t.Contains("needs a axe")));
        Assert.Equal(4, step.State.World.Scenes[0].Tiles[3][3].Node?.RemainingHealth);
    }

    [Fact]
    public void StrikingDepletesHealthAndFinallyDropsMaterials()
    {
        var (ctx, state, _) = WithTree();
        var current = state;
        for (var i = 0; i < 4; i++)
        {
            current = Engine.ApplyCommand(ctx, current, new UseToolCommand("axe")).State;
        }
        Assert.Null(current.World.Scenes[0].Tiles[3][3].Node); // trees don't respawn
        var wood = current.Player.Inventory.FirstOrDefault(s => s.Item.Id == "material-wood");
        Assert.NotNull(wood);
        Assert.InRange(wood!.Quantity, 2, 4);
    }

    [Fact]
    public void NodesBlockMovementUntilCleared()
    {
        var (ctx, state, _) = WithTree();
        // player (3,4); tree node at (3,3) above.
        var blocked = Engine.ApplyCommand(ctx, state, new MoveCommand("up"));
        Assert.Equal(4.5, blocked.State.Player.Y);

        var current = state;
        for (var i = 0; i < 4; i++)
        {
            current = Engine.ApplyCommand(ctx, current, new UseToolCommand("axe")).State;
        }
        var step = Engine.ApplyCommand(ctx, current, new MoveCommand("up"));
        Assert.Equal(3.5, step.State.Player.Y);
    }

    [Fact]
    public void RespawningNodesComeBackAfterTheirRespawnWindow()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Scenes[0].Tiles[3][3] = project.Scenes[0].Tiles[3][3] with
            {
                Node = new TileNode { TypeId = "node-rock", RemainingHealth = 3 },
            };
            project.Player.Inventory.Add(Slot(project.Items, "tool-pickaxe", 1));
            return project;
        });
        var current = state;
        for (var i = 0; i < 3; i++)
        {
            current = Engine.ApplyCommand(ctx, current, new UseToolCommand("pickaxe")).State;
        }
        var node = current.World.Scenes[0].Tiles[3][3].Node;
        Assert.Equal(0, node?.RemainingHealth);
        Assert.Equal(1, node?.DepletedOnDay);

        // Rocks respawn after 3 days.
        for (var i = 0; i < 3; i++)
        {
            current = Engine.ApplyCommand(ctx, current, new SleepCommand()).State;
        }
        node = current.World.Scenes[0].Tiles[3][3].Node;
        Assert.Equal(3, node?.RemainingHealth);
        Assert.Null(node?.DepletedOnDay);
    }

    [Fact]
    public void TierGatesHighEndNodes()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Scenes[0].Tiles[3][3] = project.Scenes[0].Tiles[3][3] with
            {
                Node = new TileNode { TypeId = "node-boulder", RemainingHealth = 6 },
            };
            project.Player.Inventory.Add(Slot(project.Items, "tool-pickaxe", 1));
            project.Player.Inventory.Add(Slot(project.Items, "tool-pickaxe-2", 1));
            return project;
        });
        var weak = Engine.ApplyCommand(ctx, state, new UseToolCommand("pickaxe"));
        Assert.True(HasMessage(weak.Effects, t => t.Contains("isn't strong enough")));

        // Swap: remove the tier-1 pickaxe so the tier-2 one is found.
        var upgraded = state with
        {
            Player = state.Player with
            {
                Inventory = state.Player.Inventory.Where(s => s.Item.Id != "tool-pickaxe").ToList(),
            },
        };
        var strong = Engine.ApplyCommand(ctx, upgraded, new UseToolCommand("pickaxe"));
        // tier-2 pickaxe has power 2 → 6 → 4
        Assert.Equal(4, strong.State.World.Scenes[0].Tiles[3][3].Node?.RemainingHealth);
    }

    // --- energy ---

    [Fact]
    public void CollapsingAtZeroEnergyEndsTheDayWithAPenalty()
    {
        var (ctx, state, _) = MakeEngine();
        var exhausted = state with { Player = state.Player with { Energy = 3, X = 3, Y = 3, Direction = "up" } };
        var step = Engine.ApplyCommand(ctx, exhausted, new UseToolCommand("watering-can")); // 3-2=1, no collapse
        Assert.Equal(1, step.State.Player.Energy);

        var step2 = Engine.ApplyCommand(ctx, step.State, new UseToolCommand("watering-can")); // 1-2 → collapse
        Assert.Equal(2, step2.State.Clock.Day);
        Assert.Equal(50, step2.State.Player.Energy);
        Assert.Equal(50, step2.State.Player.Money);
    }

    [Fact]
    public void WarnsWhenCrossingTheLowEnergyThreshold()
    {
        var (ctx, state, _) = MakeEngine();
        var tired = state with { Player = state.Player with { Energy = 21, X = 3, Y = 3, Direction = "up" } };
        var step = Engine.ApplyCommand(ctx, tired, new UseToolCommand("watering-can"));
        Assert.True(HasMessage(step.Effects, t => t.Contains("exhausted")));
    }

    [Fact]
    public void EnergySystemCanBeDisabledPerProject()
    {
        var (ctx, state, _) = MakeEngine(project => project with
        {
            Settings = project.Settings with { EnergyEnabled = false },
        });
        var step = Engine.ApplyCommand(ctx, FacingSoil(state), new UseToolCommand("watering-can"));
        Assert.Equal(100, step.State.Player.Energy);
    }
}
