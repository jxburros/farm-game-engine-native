using FarmEngine.Json;
using FarmEngine.Schemas;
using static FarmEngine.Core.Tests.Core.CoreTestHelpers;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of m3-systems.test.ts. M3 system tests: event runtime, pathfinding,
/// NPC schedules, validation, quest-giver binding.
/// </summary>
public class M3SystemsTests
{
    private const string NeedsModules = "needs the full engine (Tiles, WorldMovement, GameEvents, NpcMovement, Quests, …) — enable at integration";
    private const string NeedsPathfinding = "needs Tiles + Pathfinding — enable at integration";
    private const string NeedsValidation = "needs Tiles + Validation — enable at integration";

    private static (EngineContext Ctx, GameState State, GameProject Project) MakeEngine(Func<GameProject, GameProject>? mutate = null)
    {
        var project = EngineTests.MakeProject();
        if (mutate is not null) project = mutate(project);
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "m3");
        return (ctx, state, project);
    }

    private static readonly GameEvent WelcomeEvent = new()
    {
        Id = "evt-welcome",
        Name = "Welcome",
        SceneId = "scene-test",
        Trigger = "enter",
        Conditions = [new EnterTileCondition { X = 3, Y = 5 }],
        Outcomes =
        [
            new EventOutcome { Type = "message", Message = "Welcome to the farm!" },
            new EventOutcome { Type = "giveMoney", Amount = 10 },
            new EventOutcome { Type = "setFlag", FlagName = "welcomed" },
        ],
        Active = true,
        Repeatable = false,
    };

    private static List<SceneTransition> LockedCaveTransition() =>
        [new SceneTransition { FromX = 3, FromY = 5, ToSceneId = "scene-cave", ToX = 2, ToY = 2, Locked = true }];

    // --- event runtime ---

    [Fact]
    public void EnterEventsFireOnceAtTheTriggerTileAndSetTheirFiredFlag()
    {
        var (ctx, state, _) = MakeEngine(project => project with { Events = [WelcomeEvent] });
        // player at (3,4): move down onto (3,5)
        var step = Engine.ApplyCommand(ctx, state, new MoveCommand("down"));
        Assert.True(HasMessage(step.Effects, t => t == "Welcome to the farm!"));
        Assert.Equal(110, step.State.Player.Money);
        Assert.True(FlagIsTrue(step.State, "welcomed"));
        Assert.True(FlagIsTrue(step.State, "event:evt-welcome:fired"));

        // stepping off and back on does NOT re-fire
        var off = Engine.ApplyCommand(ctx, step.State, new MoveCommand("up"));
        var backOn = Engine.ApplyCommand(ctx, off.State, new MoveCommand("down"));
        Assert.Equal(110, backOn.State.Player.Money);
    }

    [Fact]
    public void RepeatableEventsFireEveryTime()
    {
        var repeatable = WelcomeEvent with
        {
            Id = "evt-rep",
            Repeatable = true,
            Outcomes = [new EventOutcome { Type = "giveMoney", Amount = 5 }],
        };
        var (ctx, state, _) = MakeEngine(project => project with { Events = [repeatable] });
        var first = Engine.ApplyCommand(ctx, state, new MoveCommand("down"));
        var off = Engine.ApplyCommand(ctx, first.State, new MoveCommand("up"));
        var second = Engine.ApplyCommand(ctx, off.State, new MoveCommand("down"));
        Assert.Equal(110, second.State.Player.Money);
    }

    [Fact]
    public void ConditionsCombineAllMustHold()
    {
        var gated = WelcomeEvent with
        {
            Id = "evt-gated",
            Conditions =
            [
                new EnterTileCondition { X = 3, Y = 5 },
                new FlagCondition { Flag = "door-open", Value = true },
            ],
        };
        var (ctx, state, _) = MakeEngine(project => project with { Events = [gated] });
        var blocked = Engine.ApplyCommand(ctx, state, new MoveCommand("down"));
        Assert.Equal(100, blocked.State.Player.Money);

        var withFlag = state with { Flags = new OrderedDictionary<string, System.Text.Json.JsonElement> { ["door-open"] = Js.Value(true) } };
        var fired = Engine.ApplyCommand(ctx, withFlag, new MoveCommand("down"));
        Assert.Equal(110, fired.State.Player.Money);
    }

    [Fact]
    public void InteractEventsTakePriorityOverBuiltInInteractions()
    {
        var sign = new GameEvent
        {
            Id = "evt-sign",
            Name = "Sign",
            SceneId = "scene-test",
            Trigger = "interact",
            Conditions = [new InteractTileCondition { X = 3, Y = 3 }],
            Outcomes = [new EventOutcome { Type = "message", Message = "A weathered sign." }],
            Active = true,
            Repeatable = true,
        };
        var (ctx, state, _) = MakeEngine(project => project with { Events = [sign] });
        var step = Engine.ApplyCommand(ctx, state, new InteractCommand()); // facing (3,3)
        Assert.True(HasMessage(step.Effects, t => t == "A weathered sign."));
    }

    [Fact]
    public void WarpOutcomeMovesThePlayerAcrossScenes()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Scenes.Add(Tiles.CreateEmptyScene("scene-cave", "Cave", 5, 5));
            return project with
            {
                Events =
                [
                    new GameEvent
                    {
                        Id = "evt-warp",
                        Name = "Warp",
                        SceneId = "scene-test",
                        Trigger = "enter",
                        Conditions = [new EnterTileCondition { X = 3, Y = 5 }],
                        Outcomes = [new EventOutcome { Type = "warpPlayer", SceneId = "scene-cave", X = 2, Y = 2 }],
                        Active = true,
                        Repeatable = false,
                    },
                ],
            };
        });
        var step = Engine.ApplyCommand(ctx, state, new MoveCommand("down"));
        Assert.Equal("scene-cave", step.State.Player.SceneId);
        Assert.Equal(2.5, step.State.Player.X);
        Assert.Contains(step.Effects, e => e is SceneChangedEffect { SceneId: "scene-cave" });
    }

    [Fact]
    public void TickEventsEvaluateOnMinuteBoundaries()
    {
        var timed = new GameEvent
        {
            Id = "evt-timed",
            Name = "Morning bell",
            SceneId = "",
            Trigger = "tick",
            Conditions = [new TimeOfDayCondition { MinMinute = 6 * 60, MaxMinute = 6 * 60 + 5 }],
            Outcomes = [new EventOutcome { Type = "setFlag", FlagName = "bell-rang" }],
            Active = true,
            Repeatable = false,
        };
        var (ctx, state, _) = MakeEngine(project => project with { Events = [timed] });
        // 20 ticks = 1 game minute → minute boundary crossing evaluates ticks
        var step = Engine.AdvanceTick(ctx, state, 20);
        Assert.True(FlagIsTrue(step.State, "bell-rang"));
    }

    [Fact]
    public void LockedTransitionsDoNotFireUntilUnlockedByAnEvent()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Scenes.Add(Tiles.CreateEmptyScene("scene-cave", "Cave", 5, 5));
            project.Scenes[0] = project.Scenes[0] with { Transitions = LockedCaveTransition() };
            return project;
        });
        var blocked = Engine.ApplyCommand(ctx, state, new MoveCommand("down"));
        Assert.Equal("scene-test", blocked.State.Player.SceneId); // stayed

        // Unlock via event outcome, then walk again
        var (ctx2, state2, _) = MakeEngine(project =>
        {
            project.Scenes.Add(Tiles.CreateEmptyScene("scene-cave", "Cave", 5, 5));
            project.Scenes[0] = project.Scenes[0] with { Transitions = LockedCaveTransition() };
            return project with
            {
                Events =
                [
                    new GameEvent
                    {
                        Id = "evt-unlock",
                        Name = "Unlock",
                        SceneId = "scene-test",
                        Trigger = "interact",
                        Conditions = [new InteractTileCondition { X = 3, Y = 3 }],
                        Outcomes = [new EventOutcome { Type = "unlockTransition", SceneId = "scene-test", X = 3, Y = 5 }],
                        Active = true,
                        Repeatable = false,
                    },
                ],
            };
        });
        var unlocked = Engine.ApplyCommand(ctx2, state2, new InteractCommand());
        var walked = Engine.ApplyCommand(ctx2, unlocked.State, new MoveCommand("down"));
        Assert.Equal("scene-cave", walked.State.Player.SceneId);
    }

    // --- pathfinding ---

    private static PathPoint P(double x, double y) => new(x, y);

    [Fact]
    public void RoutesAroundObstacles()
    {
        var scene = Tiles.CreateEmptyScene("s", "S", 5, 5);
        // wall across row 2 except (4,2)
        for (var x = 0; x < 4; x++) scene.Tiles[2][x] = Tiles.SetTileLayer(scene.Tiles[2][x], "wall");
        var path = Pathfinding.FindPath(new Walkability(scene), P(0, 0), P(0, 4));
        Assert.NotNull(path);
        Assert.Equal(P(0, 4), path![^1]);
        // must pass through the gap at (4,2)
        Assert.Contains(path, p => p.X == 4 && p.Y == 2);
    }

    [Fact]
    public void ReturnsNullWhenUnreachable()
    {
        var scene = Tiles.CreateEmptyScene("s", "S", 5, 5);
        for (var x = 0; x < 5; x++) scene.Tiles[2][x] = Tiles.SetTileLayer(scene.Tiles[2][x], "wall");
        Assert.Null(Pathfinding.FindPath(new Walkability(scene), P(0, 0), P(0, 4)));
    }

    [Fact]
    public void IsDeterministic()
    {
        var scene = Tiles.CreateEmptyScene("s", "S", 8, 8);
        var a = Pathfinding.FindPath(new Walkability(scene), P(0, 0), P(7, 7));
        var b = Pathfinding.FindPath(new Walkability(scene), P(0, 0), P(7, 7));
        AssertDeepEqual(a, b);
    }

    // --- NPC schedules & movement ---

    [Fact]
    public void WalksAnNpcTowardItsScheduledDestinationOneStepPerMinute()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Npcs[0] = project.Npcs[0] with
            {
                CanMove = true,
                Schedule = [new NpcScheduleEntry { Minute = 6 * 60, SceneId = "scene-test", X = 4, Y = 1 }],
            };
            return project;
        });
        // npc starts at (1,1); destination (4,1) = 3 steps
        var current = state;
        for (var i = 0; i < 3; i++)
        {
            current = Engine.AdvanceTick(ctx, current, 20).State; // 1 minute each
        }
        Assert.Equal(4, current.Npcs["npc-test"].X);
        Assert.Equal(1, current.Npcs["npc-test"].Y);
    }

    [Fact]
    public void TeleportsForCrossSceneScheduleEntries()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Scenes.Add(Tiles.CreateEmptyScene("scene-house", "House", 5, 5));
            project.Npcs[0] = project.Npcs[0] with
            {
                CanMove = true,
                Schedule = [new NpcScheduleEntry { Minute = 6 * 60, SceneId = "scene-house", X = 2, Y = 2 }],
            };
            return project;
        });
        var step = Engine.AdvanceTick(ctx, state, 20);
        Assert.Equal("scene-house", step.State.Npcs["npc-test"].SceneId);
        Assert.Equal(2, step.State.Npcs["npc-test"].X);
    }

    [Fact]
    public void PausesWhenThePlayerStandsAdjacent()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Npcs[0] = project.Npcs[0] with
            {
                CanMove = true,
                Schedule = [new NpcScheduleEntry { Minute = 6 * 60, SceneId = "scene-test", X = 4, Y = 1 }],
            };
            return project;
        });
        var nextToNpc = state with { Player = state.Player with { X = 1, Y = 2 } };
        var step = Engine.AdvanceTick(ctx, nextToNpc, 20);
        Assert.Equal(1, step.State.Npcs["npc-test"].X);
        Assert.Equal(1, step.State.Npcs["npc-test"].Y);
    }

    [Fact]
    public void WanderingNpcsStayWithinTheirRadiusAndUseTheSeededRng()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Npcs[0] = project.Npcs[0] with { CanMove = true, MovePattern = "wander", WanderRadius = 2 };
            return project;
        });
        var a = state;
        var b = state;
        for (var i = 0; i < 30; i++)
        {
            a = Engine.AdvanceTick(ctx, a, 20).State;
            b = Engine.AdvanceTick(ctx, b, 20).State;
        }
        // deterministic across identical runs
        AssertDeepEqual(a.Npcs["npc-test"], b.Npcs["npc-test"]);
        Assert.True(Math.Abs(a.Npcs["npc-test"].X - 1) <= 2);
        Assert.True(Math.Abs(a.Npcs["npc-test"].Y - 1) <= 2);
    }

    // --- quest-giver binding & collect objectives ---

    [Fact]
    public void ADialogueOptionCanOfferAQuest()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Quests.Add(new Quest
            {
                Id = "quest-offered",
                Name = "Offered",
                Description = "From dialogue",
                Status = "not-started",
                Objectives =
                [
                    new QuestObjective { Id = "o", Type = "collect", Description = "x", TargetItemId = "material-wood", TargetItemQuantity = 2, Completed = false, Progress = 0 },
                ],
                Rewards = new QuestRewards(),
            });
            project.Npcs[0].Dialogue[0].Options.Add(new DialogueOption { Text = "Any work?", OfferQuestId = "quest-offered" });
            return project;
        });
        var inDialogue = state with { Dialogue = new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" } };
        var step = Engine.ApplyCommand(ctx, inDialogue, new ChooseDialogueOptionCommand(2));
        Assert.Contains("quest-offered", step.State.Player.ActiveQuests);
        Assert.True(HasMessage(step.Effects, t => t.Contains("New quest")));
    }

    [Fact]
    public void BuyingItemsProgressesCollectObjectives()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Quests.Add(new Quest
            {
                Id = "quest-buy",
                Name = "Buy",
                Description = "Buy carrots seeds",
                Status = "active",
                Objectives =
                [
                    new QuestObjective { Id = "o", Type = "collect", Description = "x", TargetItemId = "seed-carrot", TargetItemQuantity = 2, Completed = false, Progress = 0 },
                ],
                Rewards = new QuestRewards { Money = 5 },
            });
            project.Player.ActiveQuests.Add("quest-buy");
            return project with
            {
                Shops =
                [
                    new ShopDefinition
                    {
                        Id = "shop-s", Name = "S", Stock = [new ShopStockEntry { ItemId = "seed-carrot" }],
                        SellPriceMultiplier = 1, BuysItems = true, RepairsTools = false, RepairCostPerPoint = 0.5,
                    },
                ],
            };
        });
        var current = Engine.ApplyCommand(ctx, state, new OpenShopCommand("shop-s")).State;
        var step = Engine.ApplyCommand(ctx, current, new BuyItemCommand("seed-carrot", 2));
        Assert.Contains("quest-buy", step.State.Player.CompletedQuests);
    }

    [Fact]
    public void QuestAvailabilityWindowsGateAutostart()
    {
        var (ctx, state, _) = MakeEngine(project =>
        {
            project.Quests.Add(new Quest
            {
                Id = "quest-summer",
                Name = "Summer only",
                Description = "s",
                Status = "not-started",
                Objectives =
                [
                    new QuestObjective { Id = "o", Type = "talk", Description = "x", TargetNpcId = "npc-test", Completed = false, Progress = 0 },
                ],
                Rewards = new QuestRewards(),
                AutoStart = true,
                AvailableSeasons = ["summer"],
            });
            return project;
        });
        var spring = Quests.AutoStartQuests(ctx, state);
        Assert.DoesNotContain("quest-summer", spring.Player.ActiveQuests);
        var summer = Quests.AutoStartQuests(ctx, state with { Clock = state.Clock with { Season = "summer" } });
        Assert.Contains("quest-summer", summer.Player.ActiveQuests);
    }

    // --- project validation (Problems panel) ---

    [Fact]
    public void AHealthyStarterProjectHasNoErrors()
    {
        var project = EngineTests.MakeProject();
        var problems = Validation.ValidateProjectContent(project);
        Assert.DoesNotContain(problems, p => p.Severity == "error");
    }

    [Fact]
    public void CatchesDanglingReferencesAcrossContentFamilies()
    {
        var project = EngineTests.MakeProject();
        project.Scenes[0] = project.Scenes[0] with
        {
            Transitions = [new SceneTransition { FromX = 0, FromY = 0, ToSceneId = "nope", ToX = 0, ToY = 0 }],
        };
        // Options list is shared with project.Dialogues (TS mutates the same object).
        var options = project.Npcs[0].Dialogue[0].Options;
        options[1] = options[1] with { NextDialogueId = "dlg-missing" };
        project.Quests[0] = project.Quests[0] with
        {
            Rewards = project.Quests[0].Rewards with { Items = [new QuestRewardItem { ItemId = "item-missing", Quantity = 1 }] },
        };
        project = project with
        {
            StartSceneId = "scene-missing",
            Shops =
            [
                new ShopDefinition
                {
                    Id = "s", Name = "S", Stock = [new ShopStockEntry { ItemId = "ghost" }],
                    SellPriceMultiplier = 1, BuysItems = true, RepairsTools = false, RepairCostPerPoint = 0.5,
                },
            ],
            Events =
            [
                new GameEvent
                {
                    Id = "e", Name = "E", SceneId = "scene-missing-2", Trigger = "enter",
                    Conditions = [new HasItemCondition { ItemId = "no-item", Quantity = 1 }],
                    Outcomes = [new EventOutcome { Type = "startQuest", QuestId = "no-quest" }],
                    Active = true, Repeatable = false,
                },
            ],
        };

        var problems = Validation.ValidateProjectContent(project);
        var categories = problems.Select(p => p.Category).ToHashSet();
        Assert.Contains("scenes", categories);
        Assert.Contains("transitions", categories);
        Assert.Contains("dialogue", categories);
        Assert.Contains("quests", categories);
        Assert.Contains("shops", categories);
        Assert.Contains("events", categories);
        Assert.True(problems.Count >= 7);
    }

    [Fact]
    public void FlagsUnreachableScenesAsWarnings()
    {
        var project = EngineTests.MakeProject();
        project.Scenes.Add(Tiles.CreateEmptyScene("scene-island", "Island", 4, 4));
        var problems = Validation.ValidateProjectContent(project);
        Assert.Contains(problems, p => p.Severity == "warning" && p.Message.Contains("Island"));
    }
}
