using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Minigame session lifecycle + item-triggered actions (extensibility layer) —
/// port of extensibility.ts.
///
/// A minigame is opened by the simulation (<c>startMinigame</c>), played in the
/// host (an implementation registered for the def's <c>kind</c>), and resolved by
/// a single deterministic command carrying the score — so replays only need
/// the command log, never the realtime input of the minigame itself.
/// (TS <c>EngineStepLike</c> is <see cref="EngineStep"/>.)
/// </summary>
public static class Extensibility
{
    /// <summary>Clamp an untrusted score into [0, 1]; NaN counts as 0.</summary>
    public static double ClampScore(double score)
    {
        if (!double.IsFinite(score)) return 0;
        return Math.Max(0, Math.Min(1, score));
    }

    public static EngineStep HandleStartMinigame(EngineContext ctx, GameState state, string minigameId) =>
        GameEvents.StartMinigameSession(ctx, state, minigameId);

    public static EngineStep HandleCancelMinigame(GameState state)
    {
        if (state.Minigame is null) return EngineStep.Of(state);
        return EngineStep.Of(state with { Minigame = null });
    }

    /// <summary>
    /// Resolve the open minigame with a score in [0, 1]: built-in bindings run
    /// first (fishing), then the def's score tiers (highest matching <c>minScore</c>
    /// wins), then the <c>onMinigameResolve</c> hook for plugins.
    /// </summary>
    public static EngineStep HandleResolveMinigame(EngineContext ctx, GameState state, double rawScore)
    {
        var session = state.Minigame;
        if (session is null) return EngineStep.Of(state);
        var score = ClampScore(rawScore);
        var definition = ctx.Content.Minigames.FirstOrDefault(def => def.Id == session.MinigameId);

        var currentState = state with { Minigame = null };
        var effects = new List<Effect>();

        // Built-in binding: the fishing minigame resolves the pending cast.
        if (session.Context.TryGetValue("builtin", out var builtin)
            && builtin.ValueKind == JsonValueKind.String && builtin.GetString() == "fishing")
        {
            var rodTier = session.Context.TryGetValue("rodTier", out var tier) && tier.ValueKind == JsonValueKind.Number
                ? tier.GetDouble()
                : 1;
            var caught = Fishing.ResolveFishing(ctx, currentState, rodTier, score);
            currentState = caught.State;
            effects.AddRange(caught.Effects);
        }

        if (definition is not null && definition.ResultTiers.Count > 0)
        {
            var tier = Js.StableSort(definition.ResultTiers, (a, b) =>
                {
                    var d = b.MinScore - a.MinScore;
                    return d > 0 ? 1 : d < 0 ? -1 : 0;
                })
                .FirstOrDefault(candidate => score >= candidate.MinScore);
            if (tier is not null)
            {
                var applied = GameEvents.ApplyOutcomes(ctx, currentState, tier.Outcomes);
                currentState = applied.State;
                effects.AddRange(applied.Effects);
            }
        }

        ctx.Hooks?.Emit(HookNames.OnMinigameResolve, new MinigameResolveHookPayload(session.MinigameId, score));
        return new EngineStep(currentState, effects);
    }

    /// <summary>
    /// Use an inventory item: runs its bound action, consuming one on a
    /// successful run when the item declares <c>consumeOnUse</c>.
    /// </summary>
    public static EngineStep HandleUseItem(EngineContext ctx, GameState state, string itemId)
    {
        var slot = state.Player.Inventory.FirstOrDefault(entry => entry.Item.Id == itemId);
        if (slot is null) return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "You don't have that item."));
        if (string.IsNullOrEmpty(slot.Item.UseActionId))
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Info, $"{slot.Item.Name} can't be used like that."));
        }

        var result = GameEvents.PerformAction(ctx, state, slot.Item.UseActionId);
        if (result.Ran && slot.Item.ConsumeOnUse == true)
        {
            var inventory = Inventory.RemoveItem(result.State.Player.Inventory, itemId, 1);
            return new EngineStep(
                result.State with { Player = result.State.Player with { Inventory = inventory } },
                result.Effects);
        }
        return new EngineStep(result.State, result.Effects);
    }
}
