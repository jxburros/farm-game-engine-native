using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>TS <c>FishingResult</c>.</summary>
public sealed record FishingResult(GameState State, List<Effect> Effects, bool Caught);

/// <summary>TS <c>resolveFishing</c>'s <c>options: { score?: number }</c>.</summary>
public sealed record ResolveFishingOptions(double? Score = null);

/// <summary>
/// Fishing (M4e): cast at water, resolve deterministically through the
/// seeded RNG. Fish tables filter by scene and season; junk rolls first;
/// per-fish difficulty is offset by rod tier. (The timing minigame is a
/// shell-side flourish planned for M7 — it will resolve through this same
/// deterministic path.) (Port of engine-core/src/fishing.ts.)
/// </summary>
public static class Fishing
{
    public static FishTable? ActiveFishTable(EngineContext ctx, GameState state) =>
        ctx.Content.FishTables.Find(table =>
        {
            if (table.SceneIds is { Count: > 0 } && !table.SceneIds.Contains(state.Player.SceneId)) return false;
            if (table.Seasons is { Count: > 0 } && !table.Seasons.Contains(state.Clock.Season)) return false;
            return table.Entries.Count > 0;
        });

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Max(0, Math.Min(1, value)) : 0;

    /// <summary>Convenience overload for TS <c>resolveFishing(ctx, state, rodTier, { score })</c>.</summary>
    public static FishingResult ResolveFishing(EngineContext ctx, GameState state, double rodTier, double score) =>
        ResolveFishing(ctx, state, rodTier, new ResolveFishingOptions(score));

    /// <summary>
    /// Resolve a cast into water (caller already validated rod + water tile).
    /// <c>options.Score</c> (0–1, from the fishing minigame when one is declared)
    /// reduces the escape chance: a perfect score always lands the fish.
    /// </summary>
    public static FishingResult ResolveFishing(EngineContext ctx, GameState state, double rodTier, ResolveFishingOptions? options = null)
    {
        options ??= new ResolveFishingOptions();
        var table = ActiveFishTable(ctx, state);
        if (table is null)
        {
            return new FishingResult(state, [Effect.Message(MessageLevels.Info, "The water is quiet — nothing seems to live here.")], false);
        }

        var rng = new Rng(state.Rng);
        var effects = new List<Effect>();
        GameState nextState;
        var caught = false;

        // Junk first, then the weighted fish roll, then the escape check.
        // (The float is drawn only when junkChance > 0; `&&` short-circuits.)
        if (table.JunkChance > 0 && rng.Float() < table.JunkChance && !string.IsNullOrEmpty(table.JunkItemId))
        {
            var junk = ctx.Content.Items.Find(i => i.Id == table.JunkItemId);
            nextState = state with { Rng = rng.State };
            if (junk is not null)
            {
                var added = Inventory.AddItem(nextState.Player.Inventory, junk, 1, nextState.Player.MaxInventorySize);
                if (added.Added)
                {
                    nextState = nextState with { Player = nextState.Player with { Inventory = added.Inventory } };
                    effects.Add(Effect.Message(MessageLevels.Info, $"You fished up {junk.Name}…"));
                }
                else
                {
                    effects.Add(Effect.Message(MessageLevels.Error, "Inventory is full!"));
                }
            }
            return new FishingResult(nextState, effects, false);
        }

        var index = rng.Weighted(table.Entries.Select(entry => entry.Weight).ToList());
        if (index < 0)
        {
            return new FishingResult(state with { Rng = rng.State }, [Effect.Message(MessageLevels.Info, "Not even a nibble.")], false);
        }
        var entry = table.Entries[index];

        // Escape roll: difficulty reduced 15% per rod tier above 1, then scaled
        // down by minigame skill (score 1 → no escape chance at all).
        var skillScale = options.Score is double score ? 1 - Clamp01(score) : 1;
        var effectiveDifficulty = Math.Max(0, entry.Difficulty - 0.15 * (rodTier - 1)) * skillScale;
        if (rng.Float() < effectiveDifficulty)
        {
            return new FishingResult(state with { Rng = rng.State }, [Effect.Message(MessageLevels.Info, "It got away!")], false);
        }

        nextState = state with { Rng = rng.State };
        var fish = ctx.Content.Items.Find(i => i.Id == entry.ItemId);
        if (fish is not null)
        {
            var added = Inventory.AddItem(nextState.Player.Inventory, fish, 1, nextState.Player.MaxInventorySize);
            if (added.Added)
            {
                nextState = nextState with { Player = nextState.Player with { Inventory = added.Inventory } };
                effects.Add(Effect.Message(MessageLevels.Success, $"Caught a {fish.Name}!"));
                caught = true;
            }
            else
            {
                effects.Add(Effect.Message(MessageLevels.Error, "Inventory is full!"));
            }
        }

        if (caught)
        {
            var xp = Skills.GrantXp(ctx, nextState, "fishing", 8);
            nextState = xp.State;
            effects.AddRange(xp.Effects);
            var quests = Quests.ProgressQuests(ctx, nextState, "collect", entry.ItemId, 1);
            nextState = quests.State;
            effects.AddRange(quests.Effects);
        }

        return new FishingResult(nextState, effects, caught);
    }
}
