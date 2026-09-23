using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Player movement is FREE (Stardew-style), not grid-based: the position is a
/// fractional tile-unit coordinate (the center of the player's collision box)
/// integrated every tick from a held movement intent. Tiles remain the unit of
/// terrain, collision, farming and interaction targeting — the occupied tile
/// is <c>Math.floor(x/y)</c>. Port of world/movement.ts.
/// </summary>
public static class WorldMovement
{
    /// <summary>Half-extents of the player's collision box, in tile units. Kept below 0.5
    /// so one-tile gaps stay walkable.</summary>
    public const double PlayerHalfWidth = 0.3;
    public const double PlayerHalfHeight = 0.3;

    /// <summary>Collision skin so a clamped position never re-overlaps the blocking tile.</summary>
    private const double CollisionEpsilon = 1e-4;

    /// <summary>Diagonal input is normalized so it isn't √2 faster than cardinal input
    /// (JS <c>Math.SQRT1_2</c> = 0.7071067811865476).</summary>
    private const double InvSqrt2 = 0.7071067811865476;

    public static GetDirectionVectorResult GetDirectionVector(string direction)
    {
        switch (direction)
        {
            case "up": return new GetDirectionVectorResult(0, -1);
            case "down": return new GetDirectionVectorResult(0, 1);
            case "left": return new GetDirectionVectorResult(-1, 0);
            case "right": return new GetDirectionVectorResult(1, 0);
            default: return new GetDirectionVectorResult(0, 0);
        }
    }

    /// <summary>The tile the player currently occupies (position is the box center).</summary>
    public static PlayerTileResult PlayerTile(GameState state) =>
        new(Math.Floor(state.Player.X), Math.Floor(state.Player.Y));

    /// <summary>The tile the player is facing (interaction/tool target).</summary>
    public static FacingTargetResult FacingTarget(GameState state)
    {
        var (dx, dy) = GetDirectionVector(state.Player.Direction);
        var origin = PlayerTile(state);
        return new FacingTargetResult(origin.X + dx, origin.Y + dy);
    }

    /// <summary>
    /// Facing derived from a movement intent. On diagonals the current facing is
    /// kept when it already matches one axis (so walking down-left while facing
    /// down keeps facing down); otherwise the horizontal axis wins.
    /// </summary>
    public static string DirectionFromIntent(MoveIntent intent, string current)
    {
        if (intent.Dx == 0 && intent.Dy == 0) return current;
        string? horizontal = intent.Dx < 0 ? "left" : intent.Dx > 0 ? "right" : null;
        string? vertical = intent.Dy < 0 ? "up" : intent.Dy > 0 ? "down" : null;
        if (horizontal != null && vertical != null)
        {
            if (current == horizontal || current == vertical) return current;
            return horizontal;
        }
        // TS `(horizontal ?? vertical) as Direction` — undefined only for NaN intents.
        return (horizontal ?? vertical)!;
    }

    /// <summary>Collision check against tiles, gathering nodes, machines and NPC occupancy.</summary>
    public static bool CanMoveTo(
        Scene scene,
        double x,
        double y,
        IReadOnlyDictionary<string, NpcState> npcs,
        string? excludeNpcId = null,
        IReadOnlyDictionary<string, NodeTypeDefinition>? nodeTypes = null,
        IReadOnlyDictionary<string, MachineTypeDefinition>? machineTypes = null)
    {
        if (x < 0 || x >= scene.Width || y < 0 || y >= scene.Height) return false;
        var tile = scene.Tiles[(int)y][(int)x];
        if (tile.Collision) return false;
        if (tile.Node != null && tile.Node.RemainingHealth > 0)
        {
            NodeTypeDefinition? definition = null;
            if (nodeTypes != null && nodeTypes.TryGetValue(tile.Node.TypeId, out var def)) definition = def;
            // Unknown node types block by default (safe fallback).
            if (definition == null || definition.BlocksMovement != false) return false;
        }
        if (tile.Machine != null)
        {
            MachineTypeDefinition? definition = null;
            if (machineTypes != null && machineTypes.TryGetValue(tile.Machine.TypeId, out var def)) definition = def;
            if (definition == null || definition.BlocksMovement != false) return false;
        }
        // Iterates in insertion order (Object.entries); order doesn't affect the result.
        foreach (var npcId in npcs.Keys)
        {
            if (npcId == excludeNpcId) continue;
            var npc = npcs[npcId];
            if (npc.SceneId == scene.Id && npc.X == x && npc.Y == y) return false;
        }
        return true;
    }

    public static Scene? FindScene(GameState state, string sceneId) =>
        state.World.Scenes.Find(scene => scene.Id == sceneId);

    /// <summary><c>Object.fromEntries(list.map(def => [def.id, def]))</c> — later duplicates win.</summary>
    internal static OrderedDictionary<string, T> ById<T>(IEnumerable<T> defs, Func<T, string> id)
    {
        var map = new OrderedDictionary<string, T>();
        foreach (var def in defs) map[id(def)] = def;
        return map;
    }

    private sealed record CollisionContext(
        Scene Scene,
        OrderedDictionary<string, NpcState> Npcs,
        OrderedDictionary<string, NodeTypeDefinition> NodeTypes,
        OrderedDictionary<string, MachineTypeDefinition> MachineTypes);

    private static CollisionContext MakeCollisionContext(EngineContext ctx, GameState state, Scene scene) =>
        new(
            scene,
            state.Npcs,
            ById(ctx.Content.NodeTypes, def => def.Id),
            ById(ctx.Content.MachineTypes, def => def.Id));

    private static bool BlockedTile(CollisionContext c, double x, double y) =>
        !CanMoveTo(c.Scene, x, y, c.Npcs, null, c.NodeTypes, c.MachineTypes);

    /// <summary>
    /// Move the box center along one axis, clamping against the first blocked
    /// tile column/row the leading edge would enter. Axis-separated resolution
    /// gives natural wall sliding. Step sizes stay well under one tile
    /// (speed/tick ≈ 0.2), so single-cell checks cannot tunnel.
    /// </summary>
    private static double MoveAxis(CollisionContext c, double x, double y, double delta, bool axisX)
    {
        if (delta == 0) return axisX ? x : y;
        var alongHalf = axisX ? PlayerHalfWidth : PlayerHalfHeight;
        var crossHalf = axisX ? PlayerHalfHeight : PlayerHalfWidth;
        var cross = axisX ? y : x;
        var from = axisX ? x : y;
        var next = from + delta;

        var crossStart = Math.Floor(cross - crossHalf + CollisionEpsilon);
        var crossEnd = Math.Floor(cross + crossHalf - CollisionEpsilon);
        var leadingEdge = delta > 0 ? next + alongHalf : next - alongHalf;
        var leadingCell = Math.Floor(leadingEdge);
        var currentLeadingCell = Math.Floor(delta > 0 ? from + alongHalf - CollisionEpsilon : from - alongHalf + CollisionEpsilon);

        if (leadingCell != currentLeadingCell)
        {
            for (var cc = crossStart; cc <= crossEnd; cc++)
            {
                var tx = axisX ? leadingCell : cc;
                var ty = axisX ? cc : leadingCell;
                if (BlockedTile(c, tx, ty))
                {
                    next = delta > 0
                        ? leadingCell - alongHalf - CollisionEpsilon
                        : leadingCell + 1 + alongHalf + CollisionEpsilon;
                    break;
                }
            }
        }
        return next;
    }

    private sealed record SettleResult(GameState State, List<Effect> Effects, bool Aborted);

    /// <summary>
    /// Everything that happens when the player's occupied TILE changes: unlocked
    /// transitions fire, ground items are picked up, quests progress, revealed
    /// mine ladders descend, and enter-triggered events evaluate at the final
    /// position. Shared by grid steps (<c>move</c> command) and free movement.
    /// </summary>
    private static SettleResult SettleTileEntry(
        EngineContext ctx,
        GameState state,
        Scene enteredScene,
        double tileX,
        double tileY)
    {
        var effects = new List<Effect>();
        var player = state.Player;
        var scenes = state.World.Scenes;
        string? pickedUpItemId = null;
        var changedScene = false;

        var transition = enteredScene.Transitions?.Find(t => t.FromX == tileX && t.FromY == tileY && t.Locked != true);
        if (transition != null)
        {
            var targetScene = FindScene(state, transition.ToSceneId);
            if (targetScene != null)
            {
                player = player with { SceneId = transition.ToSceneId, X = transition.ToX + 0.5, Y = transition.ToY + 0.5 };
                changedScene = true;
                effects.Add(new SceneChangedEffect(transition.ToSceneId, transition.ToX, transition.ToY));
                effects.Add(Effect.Message("success", $"Entered {targetScene.Name}"));
            }
        }

        // Item pickup checks the tile stepped onto in the ORIGINAL scene
        // (historical behavior, even if a transition just fired).
        var tile = TileAt(enteredScene, tileX, tileY);
        if (tile?.Item != null)
        {
            var result = Inventory.AddItem(player.Inventory, tile.Item, 1, player.MaxInventorySize,
                new Inventory.AddItemOptions { RequireStackableForMerge = true });
            if (!result.Added)
            {
                return new SettleResult(state, [Effect.Message("error", "Inventory is full!")], true);
            }
            player = player with { Inventory = result.Inventory };
            pickedUpItemId = tile.Item.Id;
            effects.Add(Effect.Message("success", $"Picked up {tile.Item.Name}"));

            var sceneIndex = scenes.FindIndex(s => s.Id == enteredScene.Id);
            if (sceneIndex != -1)
            {
                var updatedScenes = new List<Scene>(scenes);
                var tiles = Tiles.CloneTiles(updatedScenes[sceneIndex].Tiles);
                tiles[(int)tileY][(int)tileX] = tiles[(int)tileY][(int)tileX] with { Item = null };
                updatedScenes[sceneIndex] = updatedScenes[sceneIndex] with { Tiles = tiles };
                scenes = updatedScenes;
            }
        }

        var nextState = state with { Player = player, World = state.World with { Scenes = scenes } };

        if (pickedUpItemId != null)
        {
            var quests = Quests.ProgressQuests(ctx, nextState, "collect", pickedUpItemId, 1);
            nextState = quests.State;
            effects.AddRange(quests.Effects);
        }
        if (changedScene)
        {
            var quests = Quests.ProgressQuests(ctx, nextState, "visit", player.SceneId, 1);
            nextState = quests.State;
            effects.AddRange(quests.Effects);
        }

        // Mine ladders (M4): stepping onto a revealed ladder descends a floor.
        var finalTile = PlayerTile(nextState);
        var landedScene = FindScene(nextState, nextState.Player.SceneId);
        var landedTile = landedScene == null ? null : TileAt(landedScene, finalTile.X, finalTile.Y);
        if (landedTile?.LadderDown == true && Mines.IsMineScene(nextState.Player.SceneId))
        {
            var descent = Mines.DescendMine(ctx, nextState, nextState.Mine.CurrentFloor + 1);
            return new SettleResult(descent.State, [.. effects, .. descent.Effects], false);
        }

        // Enter-triggered events fire at the final position (M3)
        var eventResult = GameEvents.EvaluateEvents(ctx, nextState, "enter", new GameEvents.EventPosition(finalTile.X, finalTile.Y));
        nextState = eventResult.State;
        effects.AddRange(eventResult.Effects);

        return new SettleResult(nextState, effects, false);
    }

    /// <summary>TS <c>scene.tiles[y]?.[x]</c>.</summary>
    private static Tile? TileAt(Scene scene, double x, double y)
    {
        if (!(y >= 0 && y < scene.Tiles.Count) || y != Math.Floor(y)) return null;
        var row = scene.Tiles[(int)y];
        if (!(x >= 0 && x < row.Count) || x != Math.Floor(x)) return null;
        return row[(int)x];
    }

    /// <summary>
    /// Discrete one-tile step (the legacy <c>move</c> command; still the primitive for
    /// scripted movement and tests). Historical semantics preserved exactly: the
    /// facing direction updates even on a blocked move, and a pickup into a full
    /// inventory aborts the whole move (including the direction change).
    /// </summary>
    public static EngineStep HandleMove(EngineContext ctx, GameState state, string dir)
    {
        var scene = FindScene(state, state.Player.SceneId);
        if (scene == null) return EngineStep.Of(state);

        var (dx, dy) = GetDirectionVector(dir);
        var from = PlayerTile(state);
        var newX = from.X + dx;
        var newY = from.Y + dy;

        var player = state.Player with { Direction = dir };

        var nodeTypes = ById(ctx.Content.NodeTypes, def => def.Id);
        var machineTypes = ById(ctx.Content.MachineTypes, def => def.Id);

        if (!CanMoveTo(scene, newX, newY, state.Npcs, null, nodeTypes, machineTypes))
        {
            return EngineStep.Of(state with { Player = player });
        }

        player = player with { X = newX + 0.5, Y = newY + 0.5 };
        var effects = new List<Effect> { new PlayerMovedEffect(newX, newY) };

        var settled = SettleTileEntry(ctx, state with { Player = player }, scene, newX, newY);
        if (settled.Aborted)
        {
            // Full-inventory pickup aborts the whole move, direction change included.
            return new EngineStep(state, settled.Effects);
        }
        return new EngineStep(settled.State, [.. effects, .. settled.Effects]);
    }

    /// <summary>
    /// Integrate free movement for one tick from the held intent. Runs inside
    /// <c>advanceTick</c>, so replay determinism needs only the intent-change commands
    /// in the log — never per-tick positions.
    /// </summary>
    public static EngineStep IntegrateMovement(EngineContext ctx, GameState state, double dtSeconds)
    {
        var intent = state.Player.MoveIntent;
        if (intent == null || (intent.Dx == 0 && intent.Dy == 0)) return EngineStep.Of(state);

        var scene = FindScene(state, state.Player.SceneId);
        if (scene == null) return EngineStep.Of(state);

        var speed = ctx.Content.Settings.Movement?.PlayerSpeed ?? 4.5;
        var scale = intent.Dx != 0 && intent.Dy != 0 ? InvSqrt2 : 1;
        // Left-to-right evaluation, exactly as TS: ((dx * speed) * scale) * dt.
        var stepX = intent.Dx * speed * scale * dtSeconds;
        var stepY = intent.Dy * speed * scale * dtSeconds;

        var c = MakeCollisionContext(ctx, state, scene);
        var fromX = state.Player.X;
        var fromY = state.Player.Y;
        var nx = MoveAxis(c, fromX, fromY, stepX, axisX: true);
        var ny = MoveAxis(c, nx, fromY, stepY, axisX: false);
        if (nx == fromX && ny == fromY) return EngineStep.Of(state);

        var prevTile = PlayerTile(state);
        var nextState = state with { Player = state.Player with { X = nx, Y = ny } };
        var effects = new List<Effect>();

        var tileX = Math.Floor(nx);
        var tileY = Math.Floor(ny);
        if (tileX != prevTile.X || tileY != prevTile.Y)
        {
            effects.Add(new PlayerMovedEffect(tileX, tileY));
            var settled = SettleTileEntry(ctx, nextState, scene, tileX, tileY);
            if (settled.Aborted)
            {
                // Full inventory: the item stays on the ground; movement itself stands.
                effects.AddRange(settled.Effects);
            }
            else
            {
                nextState = settled.State;
                effects.AddRange(settled.Effects);
            }
        }
        return new EngineStep(nextState, effects);
    }

    /// <summary>TS <c>getDirectionVector</c> return <c>{ dx, dy }</c>.</summary>
    public sealed record GetDirectionVectorResult(double Dx, double Dy);

    /// <summary>TS <c>playerTile</c> return <c>{ x, y }</c>.</summary>
    public sealed record PlayerTileResult(double X, double Y);

    /// <summary>TS <c>facingTarget</c> return <c>{ x, y }</c>.</summary>
    public sealed record FacingTargetResult(double X, double Y);
}
