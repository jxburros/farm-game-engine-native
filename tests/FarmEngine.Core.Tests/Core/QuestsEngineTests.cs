using FarmEngine.Content;
using FarmEngine.Schemas;
using static FarmEngine.Core.Tests.Core.CoreTestHelpers;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of tests/unit/quests.engine.test.ts — quest behavior against the ONE
/// canonical quest engine (Core <see cref="Quests"/>): immutability, no
/// double-start/double-complete, prerequisite gating, target-0 objectives,
/// full-inventory reward messages. The src/lib/quests selectors are one-line
/// filters, reproduced inline.
/// </summary>
public class QuestsEngineTests
{
    private static Quest MakeQuest(string id, QuestRewards? rewards = null, List<string>? prerequisites = null, bool? autoStart = null,
        List<QuestObjective>? objectives = null, string status = "not-started") => new()
    {
        Id = id,
        Name = id,
        Description = "test quest",
        Status = status,
        Objectives = objectives ??
        [
            new QuestObjective { Id = "obj-1", Type = "collect", Description = "collect wood", TargetItemId = "material-wood", TargetItemQuantity = 2, Completed = false, Progress = 0 },
        ],
        Rewards = rewards ?? new QuestRewards(),
        Prerequisites = prerequisites,
        AutoStart = autoStart,
    };

    private static GameProject MakeProject(Func<GameProject, GameProject>? mutate = null)
    {
        var project = new GameProject
        {
            SchemaVersion = 7,
            Id = "quest-test",
            Name = "Quest Test",
            Version = "2.0",
            Scenes = [Tiles.CreateEmptyScene("farm", "Farm", 8, 8)],
            Npcs = [],
            Items = ContentBuiltin.CreateDefaultItems(),
            Events = [],
            Dialogues = [],
            Quests = [],
            Player = new Player
            {
                X = 4, Y = 4, Direction = "up", SceneId = "farm", Inventory = [], MaxInventorySize = 3, Money = 100,
                ActiveQuests = [], CompletedQuests = [], PixelX = 0, PixelY = 0, TargetX = 0, TargetY = 0,
            },
            EventFlags = [],
            StartSceneId = "farm",
            Mode = "play",
            SelectedTileType = "grass",
            SelectedNpcId = null,
            SelectedItemId = null,
            CurrentTime = 1,
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
            GameStartTime = 1,
        };
        return mutate is null ? project : mutate(project);
    }

    private static (EngineContext Ctx, GameState State) MakeEngine(Func<GameProject, GameProject>? mutate = null)
    {
        var project = MakeProject(mutate);
        return (new EngineContext(EngineState.CreateContentFromProject(project)), EngineState.CreateGameState(project, seed: "quests"));
    }

    private static Func<GameProject, GameProject> WithQuests(params Quest[] quests) => p => p with { Quests = [.. p.Quests, .. quests] };

    private static Func<GameProject, GameProject> Activate(Quest quest, List<InventorySlot>? inventory = null) => p => p with
    {
        Quests = [.. p.Quests, quest with { Status = "active" }],
        Player = p.Player with { ActiveQuests = [.. p.Player.ActiveQuests, quest.Id], Inventory = inventory ?? p.Player.Inventory },
    };

    private static GameState Completed(GameState state, params string[] ids) =>
        state with { Player = state.Player with { CompletedQuests = [.. ids] } };

    // --- startQuestById ---

    [Fact]
    public void ActivatesAQuestImmutablyAndAnnouncesIt()
    {
        var (ctx, state) = MakeEngine(WithQuests(MakeQuest("q1")));
        var step = Quests.StartQuestById(ctx, state, "q1");
        Assert.Contains("q1", step.State.Player.ActiveQuests);
        Assert.DoesNotContain("q1", state.Player.ActiveQuests); // input untouched
        Assert.True(HasMessage(step.Effects, t => t.Contains("New quest")));
    }

    [Fact]
    public void DoesNotStartAnAlreadyActiveOrAlreadyCompletedQuest()
    {
        var (ctx, state) = MakeEngine(WithQuests(MakeQuest("q1")));
        var active = Quests.StartQuestById(ctx, state, "q1").State;
        Assert.Same(active, Quests.StartQuestById(ctx, active, "q1").State);

        var after = Quests.StartQuestById(ctx, Completed(state, "q1"), "q1").State;
        Assert.DoesNotContain("q1", after.Player.ActiveQuests);
    }

    [Fact]
    public void GatesOnPrerequisites()
    {
        var (ctx, state) = MakeEngine(WithQuests(MakeQuest("q1"), MakeQuest("q2", prerequisites: ["q1"])));
        Assert.DoesNotContain("q2", Quests.StartQuestById(ctx, state, "q2").State.Player.ActiveQuests);
        Assert.Contains("q2", Quests.StartQuestById(ctx, Completed(state, "q1"), "q2").State.Player.ActiveQuests);
    }

    // --- progressQuests ---

    [Fact]
    public void AccumulatesAndClampsProgressToTheObjectiveTargetThenAutoCompletes()
    {
        var (ctx, state) = MakeEngine(Activate(MakeQuest("q1", rewards: new QuestRewards { Money = 10 })));
        var half = Quests.ProgressQuests(ctx, state, "collect", "material-wood", 1);
        Assert.Equal(1, half.State.Quests["q1"].Objectives!["obj-1"].Progress);
        Assert.DoesNotContain("q1", half.State.Player.CompletedQuests);

        var done = Quests.ProgressQuests(ctx, half.State, "collect", "material-wood", 5);
        Assert.Contains("q1", done.State.Player.CompletedQuests);
        Assert.DoesNotContain("q1", done.State.Player.ActiveQuests);
        Assert.Equal(110, done.State.Player.Money);
        Assert.Contains(done.Effects, e => e is QuestCompletedEffect);
    }

    [Fact]
    public void TreatsAnAuthoredTargetOf0AsAlreadySatisfied()
    {
        var quest = MakeQuest("q0", objectives:
        [
            new QuestObjective { Id = "o", Type = "collect", Description = "none", TargetItemId = "material-wood", TargetItemQuantity = 0, Completed = false, Progress = 0 },
        ]);
        var (ctx, state) = MakeEngine(Activate(quest));
        var step = Quests.ProgressQuests(ctx, state, "collect", "material-wood", 0);
        Assert.Contains("q0", step.State.Player.CompletedQuests);
    }

    [Fact]
    public void IgnoresNonMatchingKindsAndTargetIds()
    {
        var (ctx, state) = MakeEngine(Activate(MakeQuest("q1")));
        Assert.Equal(0, Quests.ProgressQuests(ctx, state, "harvest", "material-wood", 1).State.Quests["q1"].Objectives!["obj-1"].Progress);
        Assert.Equal(0, Quests.ProgressQuests(ctx, state, "collect", "material-stone", 1).State.Quests["q1"].Objectives!["obj-1"].Progress);
    }

    [Fact]
    public void AnnouncesItemRewardsThatDoNotFitInsteadOfDroppingThemSilently()
    {
        var items = ContentBuiltin.CreateDefaultItems();
        var quest = MakeQuest("q1", rewards: new QuestRewards { Items = [new QuestRewardItem { ItemId = "seed-wheat", Quantity = 3 }] });
        var (ctx, state) = MakeEngine(Activate(quest, [Slot(items, "tool-hoe", 1), Slot(items, "tool-axe", 1), Slot(items, "tool-pickaxe", 1)]));
        var step = Quests.ProgressQuests(ctx, state, "collect", "material-wood", 2);
        Assert.Contains("q1", step.State.Player.CompletedQuests);
        Assert.Contains(step.Effects, e => e is MessageEffect { Level: "error" } m && m.Text.Contains("reward lost"));
    }

    // --- completeQuestById ---

    [Fact]
    public void OnlyCompletesActiveQuestsAndOnlyOnce()
    {
        var (ctx, state) = MakeEngine(p => p with
        {
            Quests = [MakeQuest("q1", rewards: new QuestRewards { Money = 25 })],
            Player = p.Player with { ActiveQuests = ["q1"] },
        });
        var done = Quests.CompleteQuestById(ctx, state, "q1").State;
        Assert.Equal(125, done.Player.Money);
        Assert.Equal(["q1"], done.Player.CompletedQuests);
        // Second completion is a no-op: not active any more.
        var again = Quests.CompleteQuestById(ctx, done, "q1").State;
        Assert.Equal(125, again.Player.Money);
        Assert.Equal(["q1"], again.Player.CompletedQuests);
    }

    // --- autoStartQuests ---

    [Fact]
    public void StartsOnlyAutoStartQuestsWhosePrerequisitesAreMet()
    {
        var (ctx, state) = MakeEngine(WithQuests(
            MakeQuest("auto-ok", autoStart: true),
            MakeQuest("auto-gated", autoStart: true, prerequisites: ["auto-ok"]),
            MakeQuest("manual")));
        Assert.Equal(["auto-ok"], Quests.AutoStartQuests(ctx, state).Player.ActiveQuests);
    }

    [Fact]
    public void ReturnsTheSameStateReferenceWhenNothingStarts()
    {
        var (ctx, state) = MakeEngine();
        Assert.Same(state, Quests.AutoStartQuests(ctx, state));
    }

    // --- project-level quest selectors (src/lib/quests) ---

    [Fact]
    public void SelectsByMembershipInThePlayerLists()
    {
        var project = MakeProject(p => p with
        {
            Quests = [MakeQuest("a"), MakeQuest("b"), MakeQuest("c")],
            Player = p.Player with { ActiveQuests = ["a"], CompletedQuests = ["c"] },
        });
        Assert.Equal(["a"], project.Quests.Where(q => project.Player.ActiveQuests.Contains(q.Id)).Select(q => q.Id));
        Assert.Equal(["c"], project.Quests.Where(q => project.Player.CompletedQuests.Contains(q.Id)).Select(q => q.Id));
    }

    /// <summary>
    /// TS <c>{ ...state.quests[questId], status: 'completed' }</c> on a missing entry writes no
    /// <c>objectives</c> key; the state hash must match that, not an empty map.
    /// </summary>
    [Fact]
    public void CompletingAQuestWithoutAProgressEntryWritesNoObjectivesKey()
    {
        var project = DefaultContent.CreateInitialProject(0);
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var created = EngineState.CreateGameState(project, "quests");
        // Active (a pack or host activated it) but with no progress entry yet.
        var state = created with
        {
            Quests = new OrderedDictionary<string, QuestProgress>(),
            Player = created.Player with { ActiveQuests = ["quest-first-harvest"] },
        };
        var step = Quests.CompleteQuestById(ctx, state, "quest-first-harvest");
        var json = Hash.StableStringify(step.State.Quests["quest-first-harvest"]);
        Assert.Equal("{\"status\":\"completed\"}", json);
        Assert.Null(step.State.Quests["quest-first-harvest"].Objectives);
    }
}
