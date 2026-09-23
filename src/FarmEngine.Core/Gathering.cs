using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>TS <c>NodeStrikeOutcome</c>.</summary>
/// <param name="Struck">True when the strike consumed the action (energy/durability should apply).</param>
public sealed record NodeStrikeOutcome(GameState State, List<Effect> Effects, bool Struck);

/// <summary>
/// Gathering nodes (M2): trees, rocks, weeds… content-defined node types
/// with health, required tool + tier, weighted drop tables and respawn rules.
/// (Port of engine-core/src/gathering.ts.)
/// </summary>
public static class Gathering
{
    public static NodeTypeDefinition? NodeTypeById(EngineContext ctx, string typeId) =>
        ctx.Content.NodeTypes.Find(def => def.Id == typeId);

    public static bool IsNodeActive(Tile tile) => tile.Node is not null && tile.Node.RemainingHealth > 0;

    /// <summary>JS <c>str.replace('-', ' ')</c>: replaces the FIRST occurrence only.</summary>
    internal static string ReplaceFirstDash(string value)
    {
        var index = value.IndexOf('-');
        return index < 0 ? value : string.Concat(value.AsSpan(0, index), " ", value.AsSpan(index + 1));
    }

    /// <summary>
    /// Strike the node on (sceneId, x, y) with the given tool. Assumes the caller
    /// already verified a node is present.
    /// </summary>
    public static NodeStrikeOutcome StrikeNode(
        EngineContext ctx,
        GameState state,
        string sceneId,
        double x,
        double y,
        string toolType,
        double toolTier,
        double toolPower)
    {
        var sceneIndex = state.World.Scenes.FindIndex(scene => scene.Id == sceneId);
        if (sceneIndex == -1) return new NodeStrikeOutcome(state, [], false);
        var tile = state.World.Scenes[sceneIndex].Tiles[(int)y][(int)x];
        if (tile.Node is null || tile.Node.RemainingHealth <= 0) return new NodeStrikeOutcome(state, [], false);
        var node = tile.Node;

        var definition = NodeTypeById(ctx, node.TypeId);
        if (definition is null) return new NodeStrikeOutcome(state, [], false);

        if (definition.RequiredTool != toolType)
        {
            return new NodeStrikeOutcome(
                state,
                [Effect.Message(MessageLevels.Info, $"{definition.Name} needs a {ReplaceFirstDash(definition.RequiredTool)}.")],
                false);
        }
        if (toolTier < definition.RequiredToolTier)
        {
            return new NodeStrikeOutcome(
                state,
                [Effect.Message(MessageLevels.Info, $"Your {ReplaceFirstDash(toolType)} isn't strong enough for {definition.Name.ToLowerInvariant()}.")],
                false);
        }

        var damage = Math.Max(1, toolPower);
        var remaining = node.RemainingHealth - damage;

        var scenes = new List<Scene>(state.World.Scenes);
        var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);
        var effects = new List<Effect>();
        var player = state.Player;
        var rngState = state.Rng;

        var addedDrops = new List<GatherDrop>();

        if (remaining > 0)
        {
            tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { Node = node with { RemainingHealth = remaining } };
            effects.Add(Effect.Message(MessageLevels.Info, $"{definition.Name}: {Js.Num(remaining)}/{Js.Num(definition.Health)}"));
        }
        else
        {
            // Depleted: roll the weighted drop table once.
            var rng = new Rng(rngState);
            var drops = new List<GatherDrop>();
            if (definition.Drops.Count > 0)
            {
                var index = rng.Weighted(definition.Drops.Select(drop => drop.Weight).ToList());
                if (index >= 0)
                {
                    var drop = definition.Drops[index];
                    var quantity = drop.Max > drop.Min ? rng.Int(drop.Min, drop.Max) : drop.Min;
                    if (quantity > 0) drops.Add(new GatherDrop(drop.ItemId, quantity));
                }
            }
            rngState = rng.State;

            var inventory = player.Inventory;
            var received = new List<string>();
            foreach (var drop in drops)
            {
                var item = ctx.Content.Items.Find(i => i.Id == drop.ItemId);
                if (item is null) continue;
                var result = Inventory.AddItem(inventory, item, drop.Quantity, player.MaxInventorySize);
                if (result.Added)
                {
                    inventory = result.Inventory;
                    received.Add($"{Js.Num(drop.Quantity)}x {item.Name}");
                    addedDrops.Add(drop);
                }
                else
                {
                    effects.Add(Effect.Message(MessageLevels.Error, "Inventory is full!"));
                }
            }
            player = player with { Inventory = inventory };

            if (definition.RespawnDays is not null)
            {
                tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with
                {
                    Node = new TileNode { TypeId = node.TypeId, RemainingHealth = 0, DepletedOnDay = state.Clock.Day },
                };
            }
            else
            {
                tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { Node = null };
            }

            effects.Add(Effect.Message(MessageLevels.Success, received.Count > 0
                ? $"{definition.Name} cleared! Got {string.Join(", ", received)}"
                : $"{definition.Name} cleared!"));
            ctx.Hooks?.Emit(HookNames.OnResourceGather, new ResourceGatherHookPayload(definition.Id, drops));
        }

        scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };

        var nextState = state with { World = state.World with { Scenes = scenes }, Player = player, Rng = rngState };

        // Gathered drops count toward collect objectives (M3).
        foreach (var drop in addedDrops)
        {
            var quests = Quests.ProgressQuests(ctx, nextState, "collect", drop.ItemId, drop.Quantity);
            nextState = quests.State;
            effects.AddRange(quests.Effects);
        }

        if (remaining <= 0)
        {
            // Skill XP (M4g): axes/scythes train foraging, pickaxes train mining.
            var skill = toolType == ToolTypes.Pickaxe ? "mining" : "foraging";
            var xp = Skills.GrantXp(ctx, nextState, skill, 5);
            nextState = xp.State;
            effects.AddRange(xp.Effects);

            // Mine floors: breaking a rock can reveal the ladder down (M4f).
            var ladder = Mines.MaybeRevealLadder(ctx, nextState, sceneId, x, y);
            nextState = ladder.State;
            effects.AddRange(ladder.Effects);
        }

        return new NodeStrikeOutcome(nextState, effects, true);
    }
}
