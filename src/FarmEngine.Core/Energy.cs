using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Player energy (M2). Per-action costs come from tool definitions; hitting
/// zero collapses the player, ending the day with a penalty. Toggleable per
/// project (settings.energyEnabled). Port of energy.ts.
/// </summary>
public static class Energy
{
    public const double LowEnergyFraction = 0.2;

    /// <summary>Effective energy cost for a tool at a given tier (higher tiers are more efficient).</summary>
    public static double EffectiveEnergyCost(ToolDefinition definition, double tier = 1) =>
        Math.Max(1, Js.Round(definition.EnergyCost * (1 - 0.15 * (tier - 1))));

    public static EnergySpendResult SpendEnergy(EngineContext ctx, GameState state, double amount)
    {
        if (!ctx.Content.Settings.EnergyEnabled || amount <= 0)
        {
            return new EnergySpendResult(state, [], false);
        }

        var before = state.Player.Energy;
        var after = before - amount;
        var effects = new List<Effect>();

        if (after <= 0)
        {
            var collapsedState = state with { Player = state.Player with { Energy = 0 } };
            var sleep = GameTime.PerformSleep(ctx, collapsedState, new SleepOptions(true));
            return new EnergySpendResult(sleep.State, [.. effects, .. sleep.Effects], true);
        }

        var lowThreshold = state.Player.MaxEnergy * LowEnergyFraction;
        if (after <= lowThreshold && before > lowThreshold)
        {
            effects.Add(Effect.Message("info", "You are getting exhausted — consider sleeping."));
        }

        return new EnergySpendResult(state with { Player = state.Player with { Energy = after } }, effects, false);
    }

    /// <summary>TS <c>EnergySpendResult</c>.</summary>
    /// <param name="Collapsed">True when the spend collapsed the player (day ended — stop processing).</param>
    public sealed record EnergySpendResult(GameState State, List<Effect> Effects, bool Collapsed);
}
