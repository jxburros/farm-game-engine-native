using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// The engine is a pure reducer over (state, content, command | tick)
/// (port of engine.ts). Same seed + same command/tick log ⇒ same state.
/// </summary>
public static class Engine
{
    /// <summary>Simulation runs at a fixed rate; rendering interpolates between ticks.</summary>
    public const double TicksPerSecond = 20;
    public const double MsPerTick = 1000 / TicksPerSecond;

    public static EngineStep ApplyCommand(EngineContext ctx, GameState state, Command command)
    {
        ctx.Hooks?.Emit(HookNames.OnCommand, new CommandHookPayload(command.Type));
        switch (command)
        {
            case SetMoveIntentCommand setMoveIntent:
            {
                var dx = Math.Max(-1, Math.Min(1, Js.Trunc(setMoveIntent.Dx)));
                var dy = Math.Max(-1, Math.Min(1, Js.Trunc(setMoveIntent.Dy)));
                var intent = new MoveIntent { Dx = dx, Dy = dy };
                var player = state.Player with
                {
                    MoveIntent = intent,
                    Direction = WorldMovement.DirectionFromIntent(intent, state.Player.Direction),
                };
                return EngineStep.Of(state with { Player = player });
            }
            case MoveCommand move:
                return WorldMovement.HandleMove(ctx, state, move.Dir);
            case UseToolCommand useTool:
                return FarmingActions.HandleUseTool(ctx, state, useTool.Tool);
            case InteractCommand:
                return FarmingActions.HandleInteract(ctx, state);
            case ChooseDialogueOptionCommand choose:
                return DialogueSystem.HandleChooseDialogueOption(ctx, state, choose.Index);
            case CloseDialogueCommand:
                return DialogueSystem.HandleCloseDialogue(state);
            case SleepCommand:
                return GameTime.PerformSleep(ctx, state, new SleepOptions { Collapsed = false });
            case OpenShopCommand openShop:
                return Economy.HandleOpenShop(ctx, state, openShop.ShopId);
            case CloseShopCommand:
                return Economy.HandleCloseShop(state);
            case BuyItemCommand buy:
                return Economy.HandleBuyItem(ctx, state, buy.ItemId, buy.Quantity);
            case SellItemCommand sell:
                return Economy.HandleSellItem(ctx, state, sell.ItemId, sell.Quantity);
            case RepairToolCommand repair:
                return Economy.HandleRepairTool(ctx, state, repair.ItemId);
            case CraftCommand craft:
                return Crafting.HandleCraft(ctx, state, craft.RecipeId);
            case PlaceMachineCommand place:
                return Crafting.HandlePlaceMachine(ctx, state, place.MachineTypeId);
            case MachineLoadCommand load:
                return Crafting.HandleMachineLoad(ctx, state, load.RecipeId);
            case GiveGiftCommand gift:
                return Social.HandleGiveGift(ctx, state, gift.ItemId);
            case DescendMineCommand descend:
                return Mines.DescendMine(ctx, state, descend.Floor);
            case ExitMineCommand:
                return Mines.ExitMine(ctx, state);
            case PerformActionCommand performAction:
            {
                var result = GameEvents.PerformAction(ctx, state, performAction.ActionId);
                return new EngineStep(result.State, result.Effects);
            }
            case UseItemCommand useItem:
                return Extensibility.HandleUseItem(ctx, state, useItem.ItemId);
            case StartMinigameCommand startMinigame:
                return Extensibility.HandleStartMinigame(ctx, state, startMinigame.MinigameId);
            case ResolveMinigameCommand resolveMinigame:
                return Extensibility.HandleResolveMinigame(ctx, state, resolveMinigame.Score);
            case CancelMinigameCommand:
                return Extensibility.HandleCancelMinigame(state);
            case PluginMutationCommand pluginMutation:
                return ApplyPluginMutation(ctx, state, pluginMutation.PluginId, pluginMutation.Mutation);
            default:
                return EngineStep.Of(state);
        }
    }

    /// <summary>
    /// Apply a plugin-declared mutation (M5). Plugins run sandboxed and return
    /// these instead of mutating state; each is validated by the schema layer
    /// before it becomes a command, and unknown references fail soft here.
    /// </summary>
    private static EngineStep ApplyPluginMutation(EngineContext ctx, GameState state, string pluginId, PluginMutation mutation)
    {
        EngineStep PluginError(string text) => EngineStep.Of(state, Effect.Message("error", $"Plugin {pluginId}: {text}"));

        switch (mutation)
        {
            case GiveItemMutation giveItem:
            {
                var item = ctx.Content.Items.FirstOrDefault(entry => entry.Id == giveItem.ItemId);
                if (item is null) return PluginError($"unknown item '{giveItem.ItemId}'");
                var result = Inventory.AddItem(state.Player.Inventory, item, giveItem.Quantity, state.Player.MaxInventorySize);
                if (!result.Added) return EngineStep.Of(state, Effect.Message("info", "Inventory full!"));
                return EngineStep.Of(
                    state with { Player = state.Player with { Inventory = result.Inventory } },
                    Effect.Message("info", $"Received {Js.Num(giveItem.Quantity)}× {item.Name}"));
            }
            case SetFlagMutation setFlag:
                return EngineStep.Of(state with
                {
                    Flags = new OrderedDictionary<string, System.Text.Json.JsonElement>(state.Flags) { [setFlag.Flag] = setFlag.Value },
                });
            case MessageMutation message:
                return EngineStep.Of(state, Effect.Message("info", message.Text));
            case SetWeatherMutation setWeather:
            {
                if (Weather.WeatherTypeById(ctx, setWeather.WeatherId) is null)
                {
                    return PluginError($"unknown weather '{setWeather.WeatherId}'");
                }
                return EngineStep.Of(state with { Clock = state.Clock with { WeatherId = setWeather.WeatherId } });
            }

            // Mutations sharing the event-outcome executor (identical semantics to
            // the equivalent event/action outcome, including soft failure).
            case TakeItemMutation takeItem:
                return GameEvents.ApplyOutcomes(ctx, state, [new EventOutcome { Type = "takeItem", ItemId = takeItem.ItemId, ItemQuantity = takeItem.Quantity }]);
            case GiveMoneyMutation giveMoney:
                return GameEvents.ApplyOutcomes(ctx, state, [new EventOutcome { Type = "giveMoney", Amount = giveMoney.Amount }]);
            case TakeMoneyMutation takeMoney:
                return GameEvents.ApplyOutcomes(ctx, state, [new EventOutcome { Type = "takeMoney", Amount = takeMoney.Amount }]);
            case StartQuestMutation startQuest:
                return GameEvents.ApplyOutcomes(ctx, state, [new EventOutcome { Type = "startQuest", QuestId = startQuest.QuestId }]);
            case WarpPlayerMutation warp:
                return GameEvents.ApplyOutcomes(ctx, state, [new EventOutcome { Type = "warpPlayer", SceneId = warp.SceneId, X = warp.X, Y = warp.Y }]);
            case StartDialogueMutation startDialogue:
                return GameEvents.ApplyOutcomes(ctx, state, [new EventOutcome { Type = "startDialogue", NpcId = startDialogue.NpcId, DialogueId = startDialogue.DialogueId }]);
            case PlaySoundMutation playSound:
                return GameEvents.ApplyOutcomes(ctx, state, [new EventOutcome { Type = "playSound", SoundId = playSound.SoundId }]);

            case ModifyFriendshipMutation modifyFriendship:
            {
                if (!ctx.Content.Npcs.Any(npc => npc.Id == modifyFriendship.NpcId))
                {
                    return PluginError($"unknown NPC '{modifyFriendship.NpcId}'");
                }
                var current = state.Social.TryGetValue(modifyFriendship.NpcId, out var existing) && existing is not null
                    ? existing
                    : new NpcSocialState { Friendship = 0, GiftsToday = 0 };
                var friendship = Math.Max(0, Math.Min(SocialSchema.MaxFriendship, current.Friendship + modifyFriendship.Delta));
                var social = new OrderedDictionary<string, NpcSocialState>(state.Social)
                {
                    [modifyFriendship.NpcId] = current with { Friendship = friendship },
                };
                ctx.Hooks?.Emit(HookNames.OnRelationshipChange, new RelationshipChangeHookPayload(modifyFriendship.NpcId, friendship));
                return EngineStep.Of(state with { Social = social });
            }

            case GrantXpMutation grantXp:
                return Skills.GrantXp(ctx, state, grantXp.Skill, grantXp.Amount);

            case ModifyEnergyMutation modifyEnergy:
            {
                if (!ctx.Content.Settings.EnergyEnabled || modifyEnergy.Delta == 0) return EngineStep.Of(state);
                if (modifyEnergy.Delta < 0)
                {
                    var spent = Energy.SpendEnergy(ctx, state, -modifyEnergy.Delta);
                    return new EngineStep(spent.State, spent.Effects);
                }
                var energy = Math.Min(state.Player.MaxEnergy, state.Player.Energy + modifyEnergy.Delta);
                return EngineStep.Of(state with { Player = state.Player with { Energy = energy } });
            }

            case PerformActionMutation performAction:
            {
                var result = GameEvents.PerformAction(ctx, state, performAction.ActionId);
                return new EngineStep(result.State, result.Effects);
            }
            case StartMinigameMutation startMinigame:
                return Extensibility.HandleStartMinigame(ctx, state, startMinigame.MinigameId);

            default:
                return EngineStep.Of(state);
        }
    }

    /// <summary>
    /// Advance simulation time by whole ticks. The game clock accrues in-game
    /// minutes; passing the configured day end forces a collapse (the world
    /// moves on without you).
    /// </summary>
    public static EngineStep AdvanceTick(EngineContext ctx, GameState state, double ticks = 1)
    {
        if (ticks <= 0) return EngineStep.Of(state);

        // Ticks are processed one at a time so that advanceTick(N) is exactly
        // equivalent to N × advanceTick(1). The batched fast-path used to evaluate
        // minute-boundary systems once per *call*, which made live simulation
        // frame-rate dependent (a slow frame delivering 3 ticks skipped minute
        // boundaries a fast machine would have hit).
        var nextState = state;
        var effects = new List<Effect>();
        for (double i = 0; i < ticks; i++)
        {
            var step = AdvanceSingleTick(ctx, nextState);
            nextState = step.State;
            effects.AddRange(step.Effects);
        }
        return new EngineStep(nextState, effects);
    }

    /// <summary>
    /// Clock quantum: game-minute values are kept on a 1e-6 grid. Per-tick float
    /// accumulation would otherwise drift (24 000 × 0.05 ≠ 1200 in IEEE-754) and
    /// make minute/day boundaries land one tick late. Rounding is deterministic,
    /// so this preserves the replay guarantee.
    /// </summary>
    private const double MinuteQuantum = 1e6;

    private static EngineStep AdvanceSingleTick(EngineContext ctx, GameState state)
    {
        var settings = ctx.Content.Settings;
        var minutesPerTick = settings.Time.MinutesPerRealSecond / TicksPerSecond;

        var beforeMinute = Math.Floor(state.Clock.TimeMinutes);
        var nextState = state with
        {
            Clock = state.Clock with
            {
                Tick = state.Clock.Tick + 1,
                TimeMinutes = Js.Round((state.Clock.TimeMinutes + minutesPerTick) * MinuteQuantum) / MinuteQuantum,
            },
        };
        var effects = new List<Effect>();

        // Free movement integrates every tick from the held intent. Frozen while
        // a dialogue, shop or minigame is open (modal interactions pause the body).
        if (nextState.Dialogue is null && nextState.Shop is null && nextState.Minigame is null)
        {
            var moved = WorldMovement.IntegrateMovement(ctx, nextState, 1 / TicksPerSecond);
            nextState = moved.State;
            effects.AddRange(moved.Effects);
        }

        // Minute boundary: NPC movement/schedules step and tick-events evaluate
        // once per whole in-game minute (bounds evaluation cost).
        var afterMinute = Math.Floor(nextState.Clock.TimeMinutes);
        if (afterMinute > beforeMinute)
        {
            nextState = NpcMovement.AdvanceNpcs(ctx, nextState, afterMinute - beforeMinute);
            nextState = Crafting.SettleMachines(ctx, nextState);
            var eventResult = GameEvents.EvaluateEvents(ctx, nextState, "tick");
            nextState = eventResult.State;
            effects.AddRange(eventResult.Effects);
        }

        if (nextState.Clock.TimeMinutes >= settings.Time.DayEndMinute)
        {
            var sleep = GameTime.PerformSleep(ctx, nextState, new SleepOptions { Collapsed = true });
            nextState = sleep.State;
            effects.AddRange(sleep.Effects);
        }

        return new EngineStep(nextState, effects);
    }
}
