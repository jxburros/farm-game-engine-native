using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Extensibility layer (custom actions, minigames, expanded plugin
/// mutations) — the "customize anything with code" surface.
/// Port of extensibility.test.ts, plus self-contained checks of the
/// GameEvents / Extensibility / DialogueSystem executors that run without the
/// rest of the engine.
/// </summary>
public class ExtensibilityTests
{
    private const string NeedsEngine = "needs Engine/EngineState/Inventory/Energy — enable at integration";

    private sealed record TestEngine(EngineContext Ctx, GameState State, HookBus Hooks);

    private static TestEngine MakeEngine(Func<GameProject, GameProject>? mutate = null, string seed = "ext-test")
    {
        var project = EngineTests.MakeProject();
        if (mutate is not null) project = mutate(project);
        var hooks = new HookBus();
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project), hooks);
        var state = EngineState.CreateGameState(project, seed: seed);
        return new TestEngine(ctx, state, hooks);
    }

    private static readonly ActionDef SnackAction = new()
    {
        Id = "action-snack",
        Name = "Snack",
        Description = "",
        Conditions = [],
        FailMessage = "",
        Outcomes =
        [
            new EventOutcome { Type = "giveMoney", Amount = 5 },
            new EventOutcome { Type = "setFlag", FlagName = "snacked" },
        ],
        EnergyCost = 0,
    };

    private static JsonElement? Flag(GameState state, string name) =>
        state.Flags.TryGetValue(name, out var value) ? value : null;

    private static bool IsTrue(JsonElement? value) => value is { ValueKind: JsonValueKind.True };

    private static bool HasMessage(IEnumerable<Effect> effects, string level) =>
        effects.Any(e => e is MessageEffect m && m.Level == level);

    // ---- custom actions -------------------------------------------------

    [Fact]
    public void AppliesOutcomesInOrderAndEmitsTheOnActionHook()
    {
        var (ctx, state, hooks) = MakeEngine(p => p with { Actions = [SnackAction] });
        var seen = new List<string>();
        hooks.On<ActionHookPayload>(HookNames.OnAction, payload => seen.Add(payload.ActionId));

        var step = Engine.ApplyCommand(ctx, state, new PerformActionCommand("action-snack"));
        Assert.Equal(state.Player.Money + 5, step.State.Player.Money);
        Assert.True(IsTrue(Flag(step.State, "snacked")));
        Assert.Equal(["action-snack"], seen);
    }

    [Fact]
    public void GatesOnConditionsWithTheFailMessageAndSkipsOutcomes()
    {
        var (ctx, state, _) = MakeEngine(p => p with
        {
            Actions = [SnackAction with { Conditions = [new FlagCondition { Flag = "unlocked", Value = true }], FailMessage = "Not yet!" }],
        });
        var blocked = Engine.ApplyCommand(ctx, state, new PerformActionCommand("action-snack"));
        Assert.Equal(state.Player.Money, blocked.State.Player.Money);
        Assert.Contains(new MessageEffect("info", "Not yet!"), blocked.Effects);

        var unlocked = Engine.ApplyCommand(ctx, state with { Flags = new() { ["unlocked"] = Js.Value(true) } }, new PerformActionCommand("action-snack"));
        Assert.Equal(state.Player.Money + 5, unlocked.State.Player.Money);
    }

    [Fact]
    public void SpendsTheActionEnergyCost()
    {
        var (ctx, state, _) = MakeEngine(p => p with { Actions = [SnackAction with { EnergyCost = 10 }] });
        var step = Engine.ApplyCommand(ctx, state, new PerformActionCommand("action-snack"));
        Assert.Equal(state.Player.Energy - 10, step.State.Player.Energy);
    }

    [Fact]
    public void ReportsUnknownActionsWithoutCrashing()
    {
        var (ctx, state, _) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, state, new PerformActionCommand("nope"));
        Assert.True(HasMessage(step.Effects, "error"));
    }

    [Fact]
    public void TerminatesActionToActionRecursionAtTheDepthCap()
    {
        var (ctx, state, _) = MakeEngine(p => p with
        {
            Actions =
            [
                SnackAction with
                {
                    Id = "action-loop",
                    Outcomes =
                    [
                        new EventOutcome { Type = "giveMoney", Amount = 1 },
                        new EventOutcome { Type = "performAction", ActionId = "action-loop" },
                    ],
                },
            ],
        });
        var result = GameEvents.PerformAction(ctx, state, "action-loop");
        // Depth cap (4 nested levels) bounds the chain; money proves it ran a
        // finite number of times rather than hanging.
        Assert.True(result.State.Player.Money <= state.Player.Money + 6);
        Assert.True(result.State.Player.Money > state.Player.Money);
    }

    // ---- item-bound actions (useItem) -----------------------------------

    private static GameProject WithSnackItem(GameProject project, bool consumeOnUse, List<EventCondition>? conditions = null)
    {
        var snack = new Item
        {
            Id = "item-snack", Name = "Snack", Description = "", Type = "material",
            Stackable = true, MaxStack = 10, Value = 1,
            UseActionId = "action-snack", ConsumeOnUse = consumeOnUse,
        };
        return project with
        {
            Actions = [SnackAction with { Conditions = conditions ?? [], FailMessage = "Cannot." }],
            Items = [.. project.Items ?? [], snack],
            Player = project.Player with { Inventory = [.. project.Player.Inventory, new InventorySlot { Item = snack, Quantity = 2 }] },
        };
    }

    [Fact]
    public void RunsTheBoundActionAndConsumesConsumeOnUseItemsOnSuccess()
    {
        var (ctx, state, _) = MakeEngine(p => WithSnackItem(p, true));
        var step = Engine.ApplyCommand(ctx, state, new UseItemCommand("item-snack"));
        Assert.True(IsTrue(Flag(step.State, "snacked")));
        Assert.Equal(1, step.State.Player.Inventory.FirstOrDefault(s => s.Item.Id == "item-snack")?.Quantity);
    }

    [Fact]
    public void DoesNotConsumeTheItemWhenTheActionConditionsFail()
    {
        var (ctx, state, _) = MakeEngine(p => WithSnackItem(p, true, [new FlagCondition { Flag = "never-set", Value = true }]));
        var step = Engine.ApplyCommand(ctx, state, new UseItemCommand("item-snack"));
        Assert.Null(Flag(step.State, "snacked"));
        Assert.Equal(2, step.State.Player.Inventory.FirstOrDefault(s => s.Item.Id == "item-snack")?.Quantity);
    }

    [Fact]
    public void ExplainsItemsWithNoBoundAction()
    {
        var (ctx, state, _) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, state, new UseItemCommand("seed-wheat"));
        Assert.True(HasMessage(step.Effects, "info"));
    }

    // ---- minigames --------------------------------------------------------

    private static readonly MinigameDef TimingMinigame = new()
    {
        Id = "mg-test",
        Name = "Test Game",
        Kind = "timing-bar",
        Config = [],
        ResultTiers =
        [
            new MinigameResultTier { MinScore = 0.8, Outcomes = [new EventOutcome { Type = "giveMoney", Amount = 100 }] },
            new MinigameResultTier { MinScore = 0.3, Outcomes = [new EventOutcome { Type = "giveMoney", Amount = 10 }] },
        ],
    };

    [Fact]
    public void OpensASessionFreezesMovementAndResolvesTheMatchingScoreTier()
    {
        var (ctx, state, hooks) = MakeEngine(p => p with { Minigames = [TimingMinigame] });
        var resolved = new List<MinigameResolveHookPayload>();
        hooks.On<MinigameResolveHookPayload>(HookNames.OnMinigameResolve, resolved.Add);

        var opened = Engine.ApplyCommand(ctx, state, new StartMinigameCommand("mg-test"));
        Assert.Equal("mg-test", opened.State.Minigame?.MinigameId);
        Assert.Empty(opened.State.Minigame!.Context);

        // Movement intent is ignored while the minigame is open.
        var moving = Engine.ApplyCommand(ctx, opened.State, new SetMoveIntentCommand(1, 0)).State;
        var ticked = Engine.AdvanceTick(ctx, moving, 10).State;
        Assert.Equal(moving.Player.X, ticked.Player.X);

        var great = Engine.ApplyCommand(ctx, ticked, new ResolveMinigameCommand(0.9));
        Assert.Null(great.State.Minigame);
        Assert.Equal(state.Player.Money + 100, great.State.Player.Money);
        Assert.Equal([new MinigameResolveHookPayload("mg-test", 0.9)], resolved);

        var okRun = Engine.ApplyCommand(ctx, opened.State, new ResolveMinigameCommand(0.5));
        Assert.Equal(state.Player.Money + 10, okRun.State.Player.Money);

        var poor = Engine.ApplyCommand(ctx, opened.State, new ResolveMinigameCommand(0.1));
        Assert.Equal(state.Player.Money, poor.State.Player.Money);
    }

    [Fact]
    public void ClampsOutOfRangeScoresAndCancelsCleanly()
    {
        var (ctx, state, _) = MakeEngine(p => p with { Minigames = [TimingMinigame] });
        var opened = Engine.ApplyCommand(ctx, state, new StartMinigameCommand("mg-test")).State;
        var clamped = Engine.ApplyCommand(ctx, opened, new ResolveMinigameCommand(99));
        Assert.Equal(state.Player.Money + 100, clamped.State.Player.Money);

        var cancelled = Engine.ApplyCommand(ctx, opened, new CancelMinigameCommand());
        Assert.Null(cancelled.State.Minigame);
        Assert.Equal(state.Player.Money, cancelled.State.Player.Money);
    }

    [Fact]
    public void ErrorsSoftlyOnUnknownMinigames()
    {
        var (ctx, state, _) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, state, new StartMinigameCommand("missing"));
        Assert.Null(step.State.Minigame);
        Assert.True(HasMessage(step.Effects, "error"));
    }

    // ---- fishing minigame binding -------------------------------------------

    private static GameProject WithFishing(GameProject project)
    {
        // Water directly above the player start (3,4) → facing up hits (3,3).
        var scene = project.Scenes[0];
        var tiles = scene.Tiles.Select(row => row.ToList()).ToList();
        tiles[3][3] = tiles[3][3] with { Type = "water", Background = "water", Collision = false };
        var rod = new Item
        {
            Id = "tool-fishing-rod", Name = "Fishing Rod", Description = "", Type = "tool", Stackable = false, MaxStack = 1,
            Value = 50, ToolType = "fishing-rod", ToolTier = 1, Durability = 50, MaxDurability = 50,
        };
        return project with
        {
            Scenes = [scene with { Tiles = tiles }, .. project.Scenes.Skip(1)],
            Items = [.. project.Items ?? [], rod],
            Player = project.Player with { Inventory = [.. project.Player.Inventory, new InventorySlot { Item = rod, Quantity = 1 }] },
            FishTables =
            [
                new FishTable
                {
                    Id = "ft-test", Name = "Test Waters", JunkChance = 0,
                    Entries = [new FishTableEntry { ItemId = "crop-wheat", Weight = 1, Difficulty = 1 }],
                },
            ],
            Minigames = [new MinigameDef { Id = "fishing", Name = "Fishing", Kind = "timing-bar", Config = [], ResultTiers = [] }],
        };
    }

    [Fact]
    public void CastingOpensTheFishingMinigameAPerfectScoreAlwaysLandsTheFish()
    {
        var (ctx, state, _) = MakeEngine(WithFishing);
        var cast = Engine.ApplyCommand(ctx, state, new UseToolCommand("fishing-rod"));
        Assert.Equal("fishing", cast.State.Minigame?.MinigameId);
        Assert.Equal("fishing", cast.State.Minigame!.Context["builtin"].GetString());
        Assert.Equal(1, cast.State.Minigame!.Context["rodTier"].GetDouble());

        // difficulty 1 → always escapes unassisted, but score 1 zeroes it.
        var caught = Engine.ApplyCommand(ctx, cast.State, new ResolveMinigameCommand(1));
        Assert.Contains(caught.State.Player.Inventory, s => s.Item.Id == "crop-wheat");

        var escaped = Engine.ApplyCommand(ctx, cast.State, new ResolveMinigameCommand(0));
        Assert.DoesNotContain(escaped.State.Player.Inventory, s => s.Item.Id == "crop-wheat");
        Assert.Contains(escaped.Effects, e => e is MessageEffect m && m.Text.Contains("got away"));
    }

    [Fact]
    public void WithoutADeclaredFishingMinigameTheCastResolvesInstantly()
    {
        var (ctx, state, _) = MakeEngine(p => WithFishing(p) with { Minigames = [] });
        var cast = Engine.ApplyCommand(ctx, state, new UseToolCommand("fishing-rod"));
        Assert.Null(cast.State.Minigame);
        // difficulty 1, no score assist → it always gets away, deterministically.
        Assert.Contains(cast.Effects, e => e is MessageEffect m && m.Text.Contains("got away"));
    }

    // ---- dialogue option actions ---------------------------------------------

    [Fact]
    public void RunsTheBoundActionWhenTheOptionIsChosen()
    {
        var (ctx, state, _) = MakeEngine(p =>
        {
            var npc = p.Npcs[0];
            var first = npc.Dialogue[0];
            var dialogues = npc.Dialogue.ToList();
            dialogues[0] = first with { Options = [new DialogueOption { Text = "Snack time", ActionId = "action-snack" }, .. first.Options.Skip(1)] };
            return p with
            {
                Actions = [SnackAction],
                Npcs = [npc with { Dialogue = dialogues }, .. p.Npcs.Skip(1)],
                Dialogues = dialogues,
            };
        });
        var talking = state with { Dialogue = new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" } };
        var step = Engine.ApplyCommand(ctx, talking, new ChooseDialogueOptionCommand(0));
        Assert.True(IsTrue(Flag(step.State, "snacked")));
        Assert.Equal(state.Player.Money + 5, step.State.Player.Money);
    }

    // ---- expanded plugin mutations ---------------------------------------------

    private static EngineStep Mutate(EngineContext ctx, GameState state, PluginMutation mutation) =>
        Engine.ApplyCommand(ctx, state, new PluginMutationCommand("test-plugin", mutation));

    [Fact]
    public void TakeItemGiveMoneyTakeMoneyFlowThroughTheOutcomeExecutor()
    {
        var (ctx, state, _) = MakeEngine();
        var taken = Mutate(ctx, state, new TakeItemMutation { ItemId = "seed-wheat", Quantity = 2 });
        Assert.Equal(3, taken.State.Player.Inventory.FirstOrDefault(s => s.Item.Id == "seed-wheat")?.Quantity);

        var paid = Mutate(ctx, state, new GiveMoneyMutation { Amount = 25 });
        Assert.Equal(state.Player.Money + 25, paid.State.Player.Money);

        var charged = Mutate(ctx, state, new TakeMoneyMutation { Amount = 9999 });
        Assert.Equal(0, charged.State.Player.Money); // clamped, never negative
    }

    [Fact]
    public void ModifyFriendshipClampsIntoRangeAndValidatesTheNpc()
    {
        var (ctx, state, hooks) = MakeEngine();
        var changes = new List<double>();
        hooks.On<RelationshipChangeHookPayload>(HookNames.OnRelationshipChange, payload => changes.Add(payload.Friendship));

        var up = Mutate(ctx, state, new ModifyFriendshipMutation { NpcId = "npc-test", Delta = 200 });
        Assert.Equal(200, up.State.Social["npc-test"].Friendship);
        var down = Mutate(ctx, up.State, new ModifyFriendshipMutation { NpcId = "npc-test", Delta = -1000 });
        Assert.Equal(0, down.State.Social["npc-test"].Friendship);
        Assert.Equal([200.0, 0.0], changes);

        var unknown = Mutate(ctx, state, new ModifyFriendshipMutation { NpcId = "ghost", Delta = 10 });
        Assert.True(HasMessage(unknown.Effects, "error"));
    }

    [Fact]
    public void GrantXpAndModifyEnergyRespectSettingsAndCaps()
    {
        var (ctx, state, _) = MakeEngine();
        var xp = Mutate(ctx, state, new GrantXpMutation { Skill = "farming", Amount = 60 });
        Assert.Equal(60, xp.State.Player.Skills["farming"].Xp);
        Assert.Equal(1, xp.State.Player.Skills["farming"].Level);

        var drained = Mutate(ctx, state, new ModifyEnergyMutation { Delta = -30 });
        Assert.Equal(state.Player.Energy - 30, drained.State.Player.Energy);
        var healed = Mutate(ctx, drained.State, new ModifyEnergyMutation { Delta = 999 });
        Assert.Equal(state.Player.MaxEnergy, healed.State.Player.Energy);
    }

    [Fact]
    public void WarpPlayerStartDialogueAndPerformActionDispatchLikeTheirOutcomes()
    {
        var (ctx, state, _) = MakeEngine(p => p with { Actions = [SnackAction] });
        var warped = Mutate(ctx, state, new WarpPlayerMutation { SceneId = "scene-test", X = 1, Y = 3 });
        Assert.Equal(1.5, warped.State.Player.X);
        Assert.Equal(3.5, warped.State.Player.Y);

        var talking = Mutate(ctx, state, new StartDialogueMutation { NpcId = "npc-test" });
        Assert.Equal(new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" }, talking.State.Dialogue);

        var acted = Mutate(ctx, state, new PerformActionMutation { ActionId = "action-snack" });
        Assert.True(IsTrue(Flag(acted.State, "snacked")));

        var sound = Mutate(ctx, state, new PlaySoundMutation { SoundId = "ui" });
        Assert.Contains(new SoundEffect("ui"), sound.Effects);
    }

    // ---- actions & minigames in content packs ------------------------------------

    [Fact]
    public void MergesAndNamespacesPackActionsMinigamesWithRewrittenReferences()
    {
        var project = EngineTests.MakeProject();
        var @base = EngineState.CreateBaseContentFromProject(project);

        using var raw = JsonDocument.Parse("""
            {
              "manifest": { "id": "mod-magic", "name": "Magic", "version": "1.0.0" },
              "content": {
                "items": [{
                  "id": "wand", "name": "Wand", "description": "", "type": "material",
                  "stackable": false, "maxStack": 1, "value": 10, "useActionId": "cast-spark"
                }],
                "actions": [{
                  "id": "cast-spark", "name": "Cast Spark",
                  "outcomes": [{ "type": "startMinigame", "minigameId": "spark-weave" }]
                }],
                "minigames": [{ "id": "spark-weave", "name": "Spark Weave", "kind": "timing-bar" }]
              },
              "plugins": []
            }
            """);
        var pack = PacksSchema.ValidateContentPack(raw.RootElement).Pack!;

        List<PackInstallation> installs = [new PackInstallation { Pack = pack, Enabled = true }];
        var merged = Packs.MergePacksIntoContent(@base, installs);
        Assert.DoesNotContain(merged.Problems, p => p.Severity == "error");

        var action = merged.Content.Actions.FirstOrDefault(a => a.Id == "mod-magic:cast-spark");
        var minigame = merged.Content.Minigames.FirstOrDefault(m => m.Id == "mod-magic:spark-weave");
        var wand = merged.Content.Items.FirstOrDefault(i => i.Id == "mod-magic:wand");
        Assert.NotNull(action);
        Assert.NotNull(minigame);
        // References to same-pack definitions are rewritten to namespaced ids.
        Assert.Equal("mod-magic:cast-spark", wand?.UseActionId);
        Assert.Equal("mod-magic:spark-weave", action!.Outcomes[0].MinigameId);
    }

    // ---- self-contained executor checks (no foreign modules) -------------------

    private static (EngineContext Ctx, GameState State, HookBus Hooks) Bare(GameContent? content = null)
    {
        var hooks = new HookBus();
        var ctx = new EngineContext(content ?? new GameContent(), hooks);
        var scene = new Scene
        {
            Id = "scene-a",
            Name = "A",
            Width = 3,
            Height = 3,
            Transitions =
            [
                new SceneTransition { FromX = 0, FromY = 0, ToSceneId = "scene-b", ToX = 1, ToY = 1, Locked = true },
                new SceneTransition { FromX = 2, FromY = 2, ToSceneId = "scene-a", ToX = 1, ToY = 1 },
            ],
        };
        var state = new GameState
        {
            Clock = new ClockState { Day = 1, Season = "spring", Year = 1, TimeMinutes = 360 },
            World = new WorldState { Scenes = [scene, new Scene { Id = "scene-b", Name = "B", Width = 3, Height = 3 }] },
            Player = new PlayerState { X = 1.5, Y = 1.5, SceneId = "scene-a", Money = 100, Energy = 50, MaxEnergy = 100, MaxInventorySize = 10 },
        };
        return (ctx, state, hooks);
    }

    private static readonly Npc Testy = new()
    {
        Id = "npc-test",
        Name = "Testy",
        X = 1,
        Y = 1,
        SceneId = "scene-a",
        Dialogue =
        [
            new Dialogue
            {
                Id = "dlg-1", NpcId = "npc-test", Text = "Hello!",
                Options =
                [
                    new DialogueOption { Text = "Snack time", ActionId = "action-snack" },
                    new DialogueOption { Text = "Gift me", GiveMoney = 25, NextDialogueId = "dlg-2" },
                    new DialogueOption { Text = "Secret", RequiresFlag = "secret" },
                ],
            },
            new Dialogue { Id = "dlg-2", NpcId = "npc-test", Text = "More?", Options = [new DialogueOption { Text = "No" }] },
        ],
        Appearance = "farmer",
    };

    [Fact]
    public void PerformActionAppliesOutcomesInOrderAndEmitsOnAction()
    {
        var (ctx, state, hooks) = Bare(new GameContent { Actions = [SnackAction] });
        var seen = new List<string>();
        hooks.On<ActionHookPayload>(HookNames.OnAction, payload => seen.Add(payload.ActionId));

        var result = GameEvents.PerformAction(ctx, state, "action-snack");
        Assert.True(result.Ran);
        Assert.Equal(105, result.State.Player.Money);
        Assert.True(IsTrue(Flag(result.State, "snacked")));
        Assert.Equal([new MessageEffect("success", "Received $5")], result.Effects);
        Assert.Equal(["action-snack"], seen);
    }

    [Fact]
    public void PerformActionGatesOnConditionsWithTheFailMessage()
    {
        var gated = SnackAction with { Conditions = [new FlagCondition { Flag = "unlocked", Value = true }], FailMessage = "Not yet!" };
        var (ctx, state, _) = Bare(new GameContent { Actions = [gated] });
        var blocked = GameEvents.PerformAction(ctx, state, "action-snack");
        Assert.False(blocked.Ran);
        Assert.Same(state, blocked.State);
        Assert.Equal([new MessageEffect("info", "Not yet!")], blocked.Effects);

        // Any JS-truthy flag value satisfies `value: true`.
        var unlocked = GameEvents.PerformAction(ctx, state with { Flags = new() { ["unlocked"] = Js.Value(3) } }, "action-snack");
        Assert.True(unlocked.Ran);
        Assert.Equal(105, unlocked.State.Player.Money);
    }

    [Fact]
    public void PerformActionReportsUnknownActions()
    {
        var (ctx, state, _) = Bare();
        var result = GameEvents.PerformAction(ctx, state, "nope");
        Assert.False(result.Ran);
        Assert.Equal([new MessageEffect("error", "Unknown action 'nope'")], result.Effects);
    }

    [Fact]
    public void ActionRecursionStopsAtTheDepthCap()
    {
        var loop = SnackAction with
        {
            Id = "action-loop",
            Outcomes = [new EventOutcome { Type = "giveMoney", Amount = 1 }, new EventOutcome { Type = "performAction", ActionId = "action-loop" }],
        };
        var (ctx, state, _) = Bare(new GameContent { Actions = [loop] });
        var result = GameEvents.PerformAction(ctx, state, "action-loop");
        // Depths 0..4 run; depth 5 is refused.
        Assert.Equal(105, result.State.Player.Money);
    }

    [Fact]
    public void OutcomesSeeTheStateProducedByThePreviousOne()
    {
        var (ctx, state, _) = Bare();
        var step = GameEvents.ApplyOutcomes(ctx, state,
        [
            new EventOutcome { Type = "giveMoney", Amount = 50 },
            new EventOutcome { Type = "takeMoney", Amount = 1000 },
            new EventOutcome { Type = "giveMoney", Amount = 0.5 },
            new EventOutcome { Type = "setFlag", FlagName = "a" },
            new EventOutcome { Type = "clearFlag", FlagName = "a" },
            new EventOutcome { Type = "message", Message = "hi" },
            new EventOutcome { Type = "message", Message = "" },
            new EventOutcome { Type = "playSound", SoundId = "ui" },
        ]);
        Assert.Equal(0.5, step.State.Player.Money);
        Assert.Equal(JsonValueKind.False, step.State.Flags["a"].ValueKind);
        Assert.Equal(
            [
                new MessageEffect("success", "Received $50"),
                new MessageEffect("info", "Paid $150"),
                new MessageEffect("success", "Received $0.5"),
                new MessageEffect("info", "hi"),
                new SoundEffect("ui"),
            ],
            step.Effects);
    }

    [Fact]
    public void WorldOutcomesWarpDialogueNpcsAndTransitions()
    {
        var (ctx, state, hooks) = Bare(new GameContent { Npcs = [Testy] });
        var friendship = new List<double>();
        hooks.On<RelationshipChangeHookPayload>(HookNames.OnRelationshipChange, p => friendship.Add(p.Friendship));

        var step = GameEvents.ApplyOutcomes(ctx, state,
        [
            new EventOutcome { Type = "spawnNPC", NpcId = "npc-test", X = 2 },
            new EventOutcome { Type = "modifyFriendship", NpcId = "npc-test", Amount = 2000 },
            new EventOutcome { Type = "modifyFriendship", NpcId = "ghost", Amount = 10 },
            new EventOutcome { Type = "startDialogue", NpcId = "npc-test" },
            new EventOutcome { Type = "unlockScene", SceneId = "scene-b" },
            new EventOutcome { Type = "lockTransition", X = 2, Y = 2 },
            new EventOutcome { Type = "warpPlayer", SceneId = "scene-b", X = 1, Y = 2 },
        ]);
        Assert.Equal(new NpcState { X = 2, Y = 1, SceneId = "scene-a" }, step.State.Npcs["npc-test"]);
        Assert.Equal(SocialSchema.MaxFriendship, step.State.Social["npc-test"].Friendship);
        Assert.Equal([SocialSchema.MaxFriendship], friendship);
        Assert.Equal(new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" }, step.State.Dialogue);
        var transitions = step.State.World.Scenes[0].Transitions;
        Assert.False(transitions[0].Locked);
        Assert.True(transitions[1].Locked);
        Assert.Equal(("scene-b", 1.5, 2.5), (step.State.Player.SceneId, step.State.Player.X, step.State.Player.Y));
        Assert.Equal([new SceneChangedEffect("scene-b", 1, 2)], step.Effects);

        var removed = GameEvents.ApplyOutcomes(ctx, step.State, [new EventOutcome { Type = "removeNPC", NpcId = "npc-test" }]);
        Assert.Empty(removed.State.Npcs);
    }

    [Fact]
    public void EventsFireOnceInContentOrderAndLaterEventsSeeEarlierFlags()
    {
        var first = new GameEvent
        {
            Id = "ev-1", Name = "One", Trigger = "tick", Active = true,
            Outcomes = [new EventOutcome { Type = "setFlag", FlagName = "one" }, new EventOutcome { Type = "giveMoney", Amount = 1 }],
        };
        var second = new GameEvent
        {
            Id = "ev-2", Name = "Two", Trigger = "tick", Active = true, Repeatable = true,
            Conditions = [new FlagCondition { Flag = "one" }],
            Outcomes = [new EventOutcome { Type = "giveMoney", Amount = 10 }],
        };
        var otherScene = new GameEvent
        {
            Id = "ev-3", Name = "Elsewhere", SceneId = "scene-b", Trigger = "tick", Active = true,
            Outcomes = [new EventOutcome { Type = "giveMoney", Amount = 1000 }],
        };
        var (ctx, state, _) = Bare(new GameContent { Events = [first, second, otherScene] });

        var once = GameEvents.EvaluateEvents(ctx, state, "tick");
        Assert.Equal(111, once.State.Player.Money);
        Assert.True(IsTrue(Flag(once.State, EventsSchema.EventFiredFlag("ev-1"))));
        Assert.Null(Flag(once.State, EventsSchema.EventFiredFlag("ev-2")));

        var twice = GameEvents.EvaluateEvents(ctx, once.State, "tick");
        Assert.Equal(121, twice.State.Player.Money);

        var interact = GameEvents.EvaluateEvents(ctx, state, "interact");
        Assert.Same(state, interact.State);
    }

    [Fact]
    public void PositionConditionsMatchRegionsInEitherCornerOrder()
    {
        var (ctx, state, _) = Bare();
        var region = new EnterTileCondition { X = 3, Y = 3, X2 = 1, Y2 = 1 };
        Assert.True(GameEvents.ConditionMet(ctx, state, region, new GameEvents.EventPosition(2, 2)));
        Assert.False(GameEvents.ConditionMet(ctx, state, region, new GameEvents.EventPosition(0, 2)));
        Assert.False(GameEvents.ConditionMet(ctx, state, region));
        Assert.True(GameEvents.ConditionMet(ctx, state, new QuestStatusCondition { QuestId = "q", Status = "not-started" }));
        Assert.True(GameEvents.ConditionMet(ctx, state, new FlagCondition { Flag = "missing", Value = false }));
    }

    [Fact]
    public void MinigameSessionsOpenResolveByTierAndCancel()
    {
        var (ctx, state, hooks) = Bare(new GameContent { Minigames = [TimingMinigame] });
        var resolved = new List<MinigameResolveHookPayload>();
        hooks.On<MinigameResolveHookPayload>(HookNames.OnMinigameResolve, resolved.Add);

        var opened = Extensibility.HandleStartMinigame(ctx, state, "mg-test").State;
        Assert.Equal("mg-test", opened.Minigame?.MinigameId);
        // A second start while one is open is a no-op.
        Assert.Same(opened, Extensibility.HandleStartMinigame(ctx, opened, "mg-test").State);

        Assert.Equal(200, Extensibility.HandleResolveMinigame(ctx, opened, 0.9).State.Player.Money);
        Assert.Equal(110, Extensibility.HandleResolveMinigame(ctx, opened, 0.5).State.Player.Money);
        Assert.Equal(100, Extensibility.HandleResolveMinigame(ctx, opened, 0.1).State.Player.Money);
        var clamped = Extensibility.HandleResolveMinigame(ctx, opened, 99);
        Assert.Null(clamped.State.Minigame);
        Assert.Equal(200, clamped.State.Player.Money);
        Assert.Equal(1, resolved[^1].Score);
        Assert.Equal(0, Extensibility.ClampScore(double.NaN));

        var cancelled = Extensibility.HandleCancelMinigame(opened);
        Assert.Null(cancelled.State.Minigame);

        var unknown = Extensibility.HandleStartMinigame(ctx, state, "missing");
        Assert.Equal([new MessageEffect("error", "Unknown minigame 'missing'")], unknown.Effects);
    }

    [Fact]
    public void ChoosingADialogueOptionRunsItsActionAndAdvances()
    {
        var content = new GameContent { Npcs = [Testy], Actions = [SnackAction] };
        var (ctx, state, _) = Bare(content);
        var talking = state with { Dialogue = new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" } };

        var snack = DialogueSystem.HandleChooseDialogueOption(ctx, talking, 0);
        Assert.True(IsTrue(Flag(snack.State, "snacked")));
        Assert.Equal(105, snack.State.Player.Money);
        Assert.Null(snack.State.Dialogue);

        var gift = DialogueSystem.HandleChooseDialogueOption(ctx, talking, 1);
        Assert.Equal(125, gift.State.Player.Money);
        Assert.Equal(new DialogueState { NpcId = "npc-test", DialogueId = "dlg-2" }, gift.State.Dialogue);
        Assert.Equal([new MessageEffect("success", "Received $25")], gift.Effects);

        // The flag-gated option is hidden, so index 2 is out of range → closes.
        Assert.Equal(2, Social.VisibleDialogueOptions(ctx, talking, Testy.Dialogue[0]).Count);
        Assert.Null(DialogueSystem.HandleChooseDialogueOption(ctx, talking, 2).State.Dialogue);
        var withSecret = talking with { Flags = new() { ["secret"] = Js.Value("yes") } };
        Assert.Equal(3, Social.VisibleDialogueOptions(ctx, withSecret, Testy.Dialogue[0]).Count);
    }

    [Fact]
    public void QuestsStartProgressAndComplete()
    {
        var quest = new Quest
        {
            Id = "q-1", Name = "Talk!", Status = "not-started",
            Objectives = [new QuestObjective { Id = "o-1", Type = "talk", TargetNpcId = "npc-test", Description = "" }],
            Rewards = new QuestRewards { Money = 7 },
        };
        var (ctx, state, _) = Bare(new GameContent { Quests = [quest] });
        var started = Quests.StartQuestById(ctx, state, "q-1");
        Assert.Equal(["q-1"], started.State.Player.ActiveQuests);
        Assert.Equal([new MessageEffect("info", "New quest: Talk!")], started.Effects);

        var done = Quests.ProgressQuests(ctx, started.State, "talk", "npc-test", 1);
        Assert.Empty(done.State.Player.ActiveQuests);
        Assert.Equal(["q-1"], done.State.Player.CompletedQuests);
        Assert.Equal("completed", done.State.Quests["q-1"].Status);
        Assert.Equal(107, done.State.Player.Money);
        Assert.Equal([new QuestCompletedEffect("q-1")], done.Effects);
    }
}
