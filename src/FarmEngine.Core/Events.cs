using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Event &amp; trigger runtime (M3) — port of events.ts. Deterministic: events
/// evaluate in content order; fire-once is tracked via an auto-managed flag;
/// repeatable events opt in explicitly.
/// </summary>
public static class GameEvents
{
    /// <summary>Actions may perform actions; cap the chain so cycles terminate.</summary>
    private const double MaxActionDepth = 4;

    private static bool PositionMatches(double x, double y, double? x2, double? y2, EventPosition pos)
    {
        var minX = Math.Min(x, x2 ?? x);
        var maxX = Math.Max(x, x2 ?? x);
        var minY = Math.Min(y, y2 ?? y);
        var maxY = Math.Max(y, y2 ?? y);
        return pos.X >= minX && pos.X <= maxX && pos.Y >= minY && pos.Y <= maxY;
    }

    public static bool ConditionMet(EngineContext ctx, GameState state, EventCondition condition, EventPosition? pos = null)
    {
        switch (condition)
        {
            case EnterTileCondition c:
                return pos is not null && PositionMatches(c.X, c.Y, c.X2, c.Y2, pos);
            case InteractTileCondition c:
                return pos is not null && PositionMatches(c.X, c.Y, c.X2, c.Y2, pos);
            case HasItemCondition c:
            {
                var held = state.Player.Inventory
                    .Where(slot => slot.Item.Id == c.ItemId)
                    .Aggregate(0.0, (sum, slot) => sum + slot.Quantity);
                return held >= c.Quantity;
            }
            case InventorySpaceCondition c:
            {
                var item = ctx.Content.Items.FirstOrDefault(i => i.Id == c.ItemId);
                return item is not null && Inventory.AddItem(state.Player.Inventory, item, c.Quantity, state.Player.MaxInventorySize).Added;
            }
            case FlagCondition c:
                return Js.Truthy(FlagValue(state, c.Flag)) == c.Value;
            case DayRangeCondition c:
                if (c.MinDay is { } minDay && state.Clock.Day < minDay) return false;
                if (c.MaxDay is { } maxDay && state.Clock.Day > maxDay) return false;
                return true;
            case SeasonCondition c:
                return c.Seasons.Contains(state.Clock.Season);
            case YearRangeCondition c:
                if (c.MinYear is { } minYear && state.Clock.Year < minYear) return false;
                if (c.MaxYear is { } maxYear && state.Clock.Year > maxYear) return false;
                return true;
            case TimeOfDayCondition c:
                return state.Clock.TimeMinutes >= c.MinMinute && state.Clock.TimeMinutes <= c.MaxMinute;
            case QuestStatusCondition c:
                return (state.Quests.TryGetValue(c.QuestId, out var progress) ? progress.Status : QuestStatusNotStarted) == c.Status;
            case FriendshipCondition c:
                return (state.Social.TryGetValue(c.NpcId, out var social) ? social.Friendship : 0) >= c.Min;
            case WeatherCondition c:
                return c.WeatherIds.Contains(state.Clock.WeatherId);
            case FestivalIdCondition c:
                return GameTime.FestivalOnDay(ctx.Content.Settings.Calendar, state.Clock.Day)?.Id == c.FestivalId;
            default:
                return false;
        }
    }

    private const string QuestStatusNotStarted = "not-started";

    /// <summary>TS <c>state.flags[name]</c> (undefined → null).</summary>
    internal static JsonElement? FlagValue(GameState state, string name) =>
        state.Flags.TryGetValue(name, out var value) ? value : null;

    private static EngineStep ApplyOutcome(EngineContext ctx, GameState state, EventOutcome outcome, double depth = 0)
    {
        switch (outcome.Type)
        {
            case "message":
                return !string.IsNullOrEmpty(outcome.Message)
                    ? EngineStep.Of(state, Effect.Message(MessageLevels.Info, outcome.Message))
                    : EngineStep.Of(state);

            case "modifyFriendship":
            {
                if (string.IsNullOrEmpty(outcome.NpcId) || !ctx.Content.Npcs.Any(n => n.Id == outcome.NpcId)) return EngineStep.Of(state);
                var npcId = outcome.NpcId;
                var current = state.Social.TryGetValue(npcId, out var existing) ? existing : new NpcSocialState { Friendship = 0, GiftsToday = 0 };
                var friendship = Math.Max(0, Math.Min(SocialSchema.MaxFriendship, current.Friendship + (outcome.Amount ?? 0)));
                ctx.Hooks?.Emit(HookNames.OnRelationshipChange, new RelationshipChangeHookPayload(npcId, friendship));
                return EngineStep.Of(state with
                {
                    Social = new OrderedDictionary<string, NpcSocialState>(state.Social) { [npcId] = current with { Friendship = friendship } },
                });
            }
            case "modifyEnergy":
            {
                var amount = outcome.Amount ?? 0;
                if (!ctx.Content.Settings.EnergyEnabled) return EngineStep.Of(state);
                if (amount < 0)
                {
                    var spend = Energy.SpendEnergy(ctx, state, -amount);
                    return new EngineStep(spend.State, spend.Effects);
                }
                return EngineStep.Of(state with
                {
                    Player = state.Player with { Energy = Math.Min(state.Player.MaxEnergy, state.Player.Energy + amount) },
                });
            }
            case "waterArea":
            {
                var radius = Math.Min(10, Math.Max(0, Math.Floor(outcome.Radius ?? 1)));
                var cx = Math.Floor(state.Player.X);
                var cy = Math.Floor(state.Player.Y);
                var day = state.Clock.Day;
                var scenes = state.World.Scenes.Select(scene => scene.Id != state.Player.SceneId ? scene : scene with
                {
                    Tiles = scene.Tiles.Select(row => row.Select(tile =>
                    {
                        if (Math.Abs(tile.X - cx) > radius || Math.Abs(tile.Y - cy) > radius || tile.Background != "soil") return tile;
                        return tile with
                        {
                            SoilState = "watered",
                            SoilMoisture = 100,
                            Crop = tile.Crop is not null ? tile.Crop with { Watered = true, LastWateredDay = day } : null,
                        };
                    }).ToList()).ToList(),
                }).ToList();
                return EngineStep.Of(
                    state with { World = state.World with { Scenes = scenes } },
                    Effect.Message(MessageLevels.Success, "The surrounding soil is watered."));
            }
            case "giveItem":
            {
                var item = ctx.Content.Items.FirstOrDefault(i => i.Id == outcome.ItemId);
                if (item is null) return EngineStep.Of(state);
                var quantity = outcome.ItemQuantity ?? 1;
                var result = Inventory.AddItem(state.Player.Inventory, item, quantity, state.Player.MaxInventorySize);
                if (!result.Added) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "Inventory is full!"));
                return EngineStep.Of(
                    state with { Player = state.Player with { Inventory = result.Inventory } },
                    Effect.Message(MessageLevels.Success, $"Received {item.Name}{(quantity > 1 ? $" x{Js.Num(quantity)}" : "")}"));
            }

            case "takeItem":
            {
                if (string.IsNullOrEmpty(outcome.ItemId)) return EngineStep.Of(state);
                var inventory = Inventory.RemoveItem(state.Player.Inventory, outcome.ItemId, outcome.ItemQuantity ?? 1);
                return EngineStep.Of(state with { Player = state.Player with { Inventory = inventory } });
            }

            case "giveMoney":
            {
                var amount = outcome.Amount ?? 0;
                if (amount <= 0) return EngineStep.Of(state);
                return EngineStep.Of(
                    state with { Player = state.Player with { Money = state.Player.Money + amount } },
                    Effect.Message(MessageLevels.Success, $"Received ${Js.Num(amount)}"));
            }

            case "takeMoney":
            {
                var amount = Math.Min(outcome.Amount ?? 0, state.Player.Money);
                if (amount <= 0) return EngineStep.Of(state);
                return EngineStep.Of(
                    state with { Player = state.Player with { Money = state.Player.Money - amount } },
                    Effect.Message(MessageLevels.Info, $"Paid ${Js.Num(amount)}"));
            }

            case "setFlag":
                if (string.IsNullOrEmpty(outcome.FlagName)) return EngineStep.Of(state);
                return EngineStep.Of(state with { Flags = new OrderedDictionary<string, JsonElement>(state.Flags) { [outcome.FlagName] = Js.Value(true) } });

            case "clearFlag":
                if (string.IsNullOrEmpty(outcome.FlagName)) return EngineStep.Of(state);
                return EngineStep.Of(state with { Flags = new OrderedDictionary<string, JsonElement>(state.Flags) { [outcome.FlagName] = Js.Value(false) } });

            case "startQuest":
                if (string.IsNullOrEmpty(outcome.QuestId)) return EngineStep.Of(state);
                return Quests.StartQuestById(ctx, state, outcome.QuestId);

            case "completeQuest":
                if (string.IsNullOrEmpty(outcome.QuestId)) return EngineStep.Of(state);
                return Quests.CompleteQuestById(ctx, state, outcome.QuestId);

            case "spawnNPC":
            {
                if (string.IsNullOrEmpty(outcome.NpcId)) return EngineStep.Of(state);
                var npcDef = ctx.Content.Npcs.FirstOrDefault(n => n.Id == outcome.NpcId);
                if (npcDef is null) return EngineStep.Of(state);
                var npcState = new NpcState
                {
                    X = outcome.X ?? npcDef.X,
                    Y = outcome.Y ?? npcDef.Y,
                    SceneId = outcome.SceneId ?? npcDef.SceneId,
                };
                return EngineStep.Of(state with { Npcs = new OrderedDictionary<string, NpcState>(state.Npcs) { [outcome.NpcId] = npcState } });
            }

            case "removeNPC":
            {
                if (string.IsNullOrEmpty(outcome.NpcId) || !state.Npcs.ContainsKey(outcome.NpcId)) return EngineStep.Of(state);
                var npcs = new OrderedDictionary<string, NpcState>(state.Npcs);
                npcs.Remove(outcome.NpcId);
                return EngineStep.Of(state with { Npcs = npcs });
            }

            case "changeTile":
            {
                if (outcome.TileX is not { } tileX || outcome.TileY is not { } tileY || string.IsNullOrEmpty(outcome.NewTileType)) return EngineStep.Of(state);
                var sceneId = !string.IsNullOrEmpty(outcome.SceneId) ? outcome.SceneId : state.Player.SceneId;
                var sceneIndex = state.World.Scenes.FindIndex(s => s.Id == sceneId);
                if (sceneIndex == -1) return EngineStep.Of(state);
                var scene = state.World.Scenes[sceneIndex];
                if (tileY < 0 || tileY >= scene.Height || tileX < 0 || tileX >= scene.Width) return EngineStep.Of(state);
                var scenes = new List<Scene>(state.World.Scenes);
                var tiles = Tiles.CloneTiles(scene.Tiles);
                tiles[(int)tileY][(int)tileX] = Tiles.SetTileLayer(tiles[(int)tileY][(int)tileX], outcome.NewTileType);
                scenes[sceneIndex] = scene with { Tiles = tiles };
                return EngineStep.Of(state with { World = state.World with { Scenes = scenes } });
            }

            case "warpPlayer":
            {
                if (string.IsNullOrEmpty(outcome.SceneId) || outcome.X is not { } x || outcome.Y is not { } y) return EngineStep.Of(state);
                var target = state.World.Scenes.FirstOrDefault(s => s.Id == outcome.SceneId);
                if (target is null) return EngineStep.Of(state);
                return EngineStep.Of(
                    // Warp targets are authored as tile coordinates; land on the center.
                    state with { Player = state.Player with { SceneId = outcome.SceneId, X = x + 0.5, Y = y + 0.5 } },
                    new SceneChangedEffect(outcome.SceneId, x, y));
            }

            case "startDialogue":
            {
                if (string.IsNullOrEmpty(outcome.NpcId)) return EngineStep.Of(state);
                var npcDef = ctx.Content.Npcs.FirstOrDefault(n => n.Id == outcome.NpcId);
                var dialogueId = outcome.DialogueId ?? npcDef?.Dialogue?.FirstOrDefault()?.Id;
                if (string.IsNullOrEmpty(dialogueId)) return EngineStep.Of(state);
                return EngineStep.Of(state with { Dialogue = new DialogueState { NpcId = outcome.NpcId, DialogueId = dialogueId } });
            }

            case "lockTransition":
            case "unlockTransition":
            {
                var sceneId = !string.IsNullOrEmpty(outcome.SceneId) ? outcome.SceneId : state.Player.SceneId;
                var sceneIndex = state.World.Scenes.FindIndex(s => s.Id == sceneId);
                if (sceneIndex == -1 || outcome.X is not { } x || outcome.Y is not { } y) return EngineStep.Of(state);
                var scene = state.World.Scenes[sceneIndex];
                var locked = outcome.Type == "lockTransition";
                var scenes = new List<Scene>(state.World.Scenes);
                scenes[sceneIndex] = scene with
                {
                    Transitions = (scene.Transitions ?? []).Select(t =>
                        t.FromX == x && t.FromY == y ? t with { Locked = locked } : t).ToList(),
                };
                return EngineStep.Of(state with { World = state.World with { Scenes = scenes } });
            }

            case "playSound":
                return !string.IsNullOrEmpty(outcome.SoundId)
                    ? EngineStep.Of(state, new SoundEffect(outcome.SoundId))
                    : EngineStep.Of(state);

            case "performAction":
                if (string.IsNullOrEmpty(outcome.ActionId)) return EngineStep.Of(state);
                return PerformActionInternal(ctx, state, outcome.ActionId, depth + 1);

            case "startMinigame":
                if (string.IsNullOrEmpty(outcome.MinigameId)) return EngineStep.Of(state);
                return StartMinigameSession(ctx, state, outcome.MinigameId);

            case "unlockScene":
            {
                // Legacy outcome (pre-v5): unlock every transition that leads to the
                // named scene. It was authorable in the editor but a runtime no-op.
                if (string.IsNullOrEmpty(outcome.SceneId)) return EngineStep.Of(state);
                var scenes = state.World.Scenes.Select(scene => scene with
                {
                    Transitions = (scene.Transitions ?? []).Select(t =>
                        t.ToSceneId == outcome.SceneId && t.Locked == true ? t with { Locked = false } : t).ToList(),
                }).ToList();
                return EngineStep.Of(state with { World = state.World with { Scenes = scenes } });
            }

            default:
                return EngineStep.Of(state);
        }
    }

    /// <summary>
    /// Apply a sequence of outcomes (the shared executor behind events, custom
    /// actions, minigame result tiers and plugin mutations).
    /// </summary>
    public static EngineStep ApplyOutcomes(EngineContext ctx, GameState state, List<EventOutcome> outcomes, double depth = 0)
    {
        var currentState = state;
        var effects = new List<Effect>();
        foreach (var outcome in outcomes)
        {
            var result = ApplyOutcome(ctx, currentState, outcome, depth);
            currentState = result.State;
            effects.AddRange(result.Effects);
        }
        return new EngineStep(currentState, effects);
    }

    /// <summary>
    /// Run a creator-defined action (extensibility layer): check its conditions,
    /// spend energy, apply its outcomes, and notify plugins via <c>onAction</c>.
    /// </summary>
    public static PerformActionResult PerformAction(EngineContext ctx, GameState state, string actionId) =>
        PerformActionDetailed(ctx, state, actionId, 0);

    private static EngineStep PerformActionInternal(EngineContext ctx, GameState state, string actionId, double depth)
    {
        var result = PerformActionDetailed(ctx, state, actionId, depth);
        return new EngineStep(result.State, result.Effects);
    }

    private static PerformActionResult PerformActionDetailed(EngineContext ctx, GameState state, string actionId, double depth)
    {
        if (depth > MaxActionDepth) return new PerformActionResult(state, [], false);
        var action = ctx.Content.Actions.FirstOrDefault(def => def.Id == actionId);
        if (action is null)
        {
            return new PerformActionResult(state, [Effect.Message(MessageLevels.Error, $"Unknown action '{actionId}'")], false);
        }

        foreach (var condition in action.Conditions)
        {
            if (!ConditionMet(ctx, state, condition))
            {
                return new PerformActionResult(
                    state,
                    !string.IsNullOrEmpty(action.FailMessage) ? [Effect.Message(MessageLevels.Info, action.FailMessage)] : [],
                    false);
            }
        }

        var currentState = state;
        var effects = new List<Effect>();

        if (action.EnergyCost > 0)
        {
            var spend = Energy.SpendEnergy(ctx, currentState, action.EnergyCost);
            currentState = spend.State;
            effects.AddRange(spend.Effects);
            if (spend.Collapsed) return new PerformActionResult(currentState, effects, false);
        }

        var applied = ApplyOutcomes(ctx, currentState, action.Outcomes, depth);
        currentState = applied.State;
        effects.AddRange(applied.Effects);

        ctx.Hooks?.Emit(HookNames.OnAction, new ActionHookPayload(action.Id));
        return new PerformActionResult(currentState, effects, true);
    }

    /// <summary>Open a declared minigame session (modal, resolves via the command log).</summary>
    public static EngineStep StartMinigameSession(
        EngineContext ctx,
        GameState state,
        string minigameId,
        OrderedDictionary<string, JsonElement>? context = null)
    {
        var minigame = ctx.Content.Minigames.FirstOrDefault(def => def.Id == minigameId);
        if (minigame is null)
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, $"Unknown minigame '{minigameId}'"));
        }
        if (state.Minigame is not null) return EngineStep.Of(state);
        return EngineStep.Of(state with { Minigame = new MinigameSession { MinigameId = minigameId, Context = context ?? [] } });
    }

    public static EngineStep FireEvent(EngineContext ctx, GameState state, GameEvent @event)
    {
        var currentState = state;
        var effects = new List<Effect>();
        foreach (var outcome in @event.Outcomes)
        {
            var result = ApplyOutcome(ctx, currentState, outcome, 0);
            currentState = result.State;
            effects.AddRange(result.Effects);
        }
        if (!@event.Repeatable)
        {
            currentState = currentState with
            {
                Flags = new OrderedDictionary<string, JsonElement>(currentState.Flags) { [EventsSchema.EventFiredFlag(@event.Id)] = Js.Value(true) },
            };
        }
        return new EngineStep(currentState, effects);
    }

    /// <summary>
    /// Evaluate all events for a trigger kind. <paramref name="pos"/> is the tile
    /// entered or interacted with (for position conditions).
    /// </summary>
    public static EngineStep EvaluateEvents(EngineContext ctx, GameState state, string trigger, EventPosition? pos = null)
    {
        var currentState = state;
        var effects = new List<Effect>();

        foreach (var @event in ctx.Content.Events)
        {
            if (!@event.Active || @event.Trigger != trigger) continue;
            if (!string.IsNullOrEmpty(@event.SceneId) && @event.SceneId != currentState.Player.SceneId) continue;
            if (!@event.Repeatable && Js.Truthy(FlagValue(currentState, EventsSchema.EventFiredFlag(@event.Id)))) continue;
            var evaluated = currentState;
            if (!@event.Conditions.All(condition => ConditionMet(ctx, evaluated, condition, pos))) continue;

            var result = FireEvent(ctx, currentState, @event);
            currentState = result.State;
            effects.AddRange(result.Effects);
        }

        return new EngineStep(currentState, effects);
    }

    /// <summary>TS <c>EventPosition</c>: the tile entered or interacted with.</summary>
    public sealed record EventPosition(double X, double Y);

    /// <summary>TS <c>performAction</c> return: <c>{ state, effects, ran }</c>.</summary>
    public sealed record PerformActionResult(GameState State, List<Effect> Effects, bool Ran);
}
