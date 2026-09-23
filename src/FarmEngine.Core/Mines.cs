using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Mining (M4f): procedurally generated floors, deterministic per
/// (engine seed, floor number). Floors are transient world scenes that are
/// NOT synced back to the project — leaving and re-entering regenerates the
/// identical layout (state on a floor resets when you leave, genre-standard
/// and deliberate). Ladders down are revealed by breaking rocks; elevator
/// checkpoints every N floors let you skip ahead. Hazards v1 = the energy
/// cost of swinging your pickaxe (combat is an explicit non-goal until
/// demanded). Port of mines.ts.
/// </summary>
public static class Mines
{
    public const string MineScenePrefix = "mine-floor-";

    public static string MineFloorSceneId(double floor) => $"{MineScenePrefix}{Js.Num(floor)}";

    public static bool IsMineScene(string sceneId) => sceneId.StartsWith(MineScenePrefix, StringComparison.Ordinal);

    private static MineBand? BandForFloor(MineConfig config, double floor) =>
        config.Bands.Find(band => floor >= band.FromFloor && floor <= band.ToFloor)
        ?? (config.Bands.Count > 0 ? config.Bands[^1] : null);

    /// <summary>
    /// Deterministically generate a mine floor: walled border, entry ladder at
    /// the top-left corner area, rocks from the depth band's weighted table.
    /// </summary>
    public static Scene GenerateMineFloor(EngineContext ctx, string engineSeed, double floor)
    {
        var config = ctx.Content.Mine;
        var scene = Tiles.CreateEmptyScene(MineFloorSceneId(floor), $"Mine — Floor {Js.Num(floor)}", config.FloorWidth, config.FloorHeight);
        // TS: createRngState(hashStringToU32(...)) — the numeric-seed overload.
        var rng = new Rng(RngMath.CreateRngState((double)RngMath.HashStringToU32($"{engineSeed}:mine:{Js.Num(floor)}")));
        var tiles = scene.Tiles; // freshly created by CreateEmptyScene — safe to fill in place

        // Cave look: floor tiles + wall border
        for (double y = 0; y < scene.Height; y++)
        {
            for (double x = 0; x < scene.Width; x++)
            {
                var border = x == 0 || y == 0 || x == scene.Width - 1 || y == scene.Height - 1;
                tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with
                {
                    Type = border ? "wall" : "floor",
                    Background = "floor",
                    Overlay = null,
                    Object = border ? "wall" : null,
                    Collision = border,
                };
            }
        }

        const double entryX = 1, entryY = 1;
        var band = BandForFloor(config, floor);

        if (band != null)
        {
            for (double y = 1; y < scene.Height - 1; y++)
            {
                for (double x = 1; x < scene.Width - 1; x++)
                {
                    if (x == entryX && y == entryY) continue;
                    if (rng.Float() >= band.Density) continue;
                    var pick = rng.Weighted(band.Rocks.Select(rock => rock.Weight).ToList());
                    if (pick < 0) continue;
                    var nodeTypeId = band.Rocks[pick].NodeTypeId;
                    var nodeDef = ctx.Content.NodeTypes.Find(def => def.Id == nodeTypeId);
                    if (nodeDef == null) continue;
                    tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { Node = new TileNode { TypeId = nodeTypeId, RemainingHealth = nodeDef.Health } };
                }
            }
        }

        // Mark the scene as generated so the project bridge skips it.
        var extra = scene.Extra != null ? new Dictionary<string, JsonElement>(scene.Extra) : new Dictionary<string, JsonElement>();
        extra["generated"] = Js.Value(true);
        return scene with { Extra = extra };
    }

    /// <summary>Enter the mine (from the configured entrance) or descend one floor.</summary>
    public static EngineStep DescendMine(EngineContext ctx, GameState state, double toFloor)
    {
        var config = ctx.Content.Mine;
        if (!config.Enabled) return EngineStep.Of(state);
        var floor = Math.Max(1, Math.Min(config.Floors, toFloor));

        var sceneId = MineFloorSceneId(floor);
        var scenes = state.World.Scenes;
        if (!scenes.Any(scene => scene.Id == sceneId))
        {
            scenes = [.. scenes, GenerateMineFloor(ctx, state.Meta.EngineSeed, floor)];
        }

        var nextState = state with
        {
            World = state.World with { Scenes = scenes },
            Player = state.Player with { SceneId = sceneId, X = 1.5, Y = 1.5 },
            Mine = new MineProgress
            {
                CurrentFloor = floor,
                DeepestFloor = Math.Max(state.Mine.DeepestFloor, floor),
            },
        };

        return EngineStep.Of(
            nextState,
            new SceneChangedEffect(sceneId, 1, 1),
            Effect.Message("info", $"Mine — floor {Js.Num(floor)}{(floor % config.ElevatorEvery == 0 ? " (elevator checkpoint)" : "")}"));
    }

    /// <summary>Leave the mine back to the entrance scene.</summary>
    public static EngineStep ExitMine(EngineContext ctx, GameState state)
    {
        var config = ctx.Content.Mine;
        var targetSceneId = config.EntranceSceneId ?? ctx.Content.StartSceneId;
        var target = state.World.Scenes.Find(scene => scene.Id == targetSceneId);
        if (target == null) return EngineStep.Of(state);
        var x = config.EntranceX ?? Math.Floor(target.Width / 2);
        var y = config.EntranceY ?? Math.Floor(target.Height / 2);

        return EngineStep.Of(
            state with
            {
                // Drop generated floors so they regenerate fresh next visit.
                World = state.World with { Scenes = state.World.Scenes.Where(scene => !IsMineScene(scene.Id)).ToList() },
                Player = state.Player with { SceneId = targetSceneId, X = x + 0.5, Y = y + 0.5 },
                Mine = state.Mine with { CurrentFloor = 0 },
            },
            new SceneChangedEffect(targetSceneId, x, y),
            Effect.Message("info", "You climb back to the surface."));
    }

    /// <summary>
    /// Ladder discovery: called when a node is destroyed inside a mine scene.
    /// Rolls the ladder chance and, when successful, drops a ladder on the tile.
    /// </summary>
    public static EngineStep MaybeRevealLadder(EngineContext ctx, GameState state, string sceneId, double x, double y)
    {
        if (!IsMineScene(sceneId)) return EngineStep.Of(state);
        var config = ctx.Content.Mine;
        var rng = new Rng(state.Rng);
        var revealed = rng.Float() < config.LadderChance;
        var nextState = state with { Rng = rng.State };
        if (!revealed) return EngineStep.Of(nextState);

        var sceneIndex = nextState.World.Scenes.FindIndex(scene => scene.Id == sceneId);
        if (sceneIndex == -1) return EngineStep.Of(nextState);
        var scenes = new List<Scene>(nextState.World.Scenes);
        var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);
        tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { LadderDown = true };
        scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };

        return EngineStep.Of(
            nextState with { World = nextState.World with { Scenes = scenes } },
            Effect.Message("success", "A ladder to the next floor appears!"));
    }
}
