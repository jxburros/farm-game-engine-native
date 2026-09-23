using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>TS <c>PathPoint</c> (world/pathfinding.ts).</summary>
public sealed record PathPoint(double X, double Y);

/// <summary>TS <c>Walkability</c> (world/pathfinding.ts).</summary>
/// <param name="Scene">The scene grid.</param>
/// <param name="NodeTypes">Node definitions by id (TS <c>Record&lt;string, NodeTypeDefinition&gt;</c>).</param>
/// <param name="Blocked">Extra blocked tiles (e.g. other NPC positions), keyed <c>"x,y"</c>.</param>
public sealed record Walkability(
    Scene Scene,
    IReadOnlyDictionary<string, NodeTypeDefinition>? NodeTypes = null,
    HashSet<string>? Blocked = null);

/// <summary>
/// Simple A* pathfinding on a scene grid (M3 NPC schedules). 4-directional,
/// uniform cost, Manhattan heuristic. Deterministic: ties broken by
/// insertion order. Port of world/pathfinding.ts.
/// </summary>
public static class Pathfinding
{
    private static readonly (double Dx, double Dy)[] Neighbors = [(0, -1), (0, 1), (-1, 0), (1, 0)];

    public static bool IsWalkable(Walkability w, double x, double y)
    {
        var scene = w.Scene;
        if (x < 0 || x >= scene.Width || y < 0 || y >= scene.Height) return false;
        var tile = scene.Tiles[(int)y][(int)x];
        if (tile.Collision) return false;
        if (tile.Node != null && tile.Node.RemainingHealth > 0)
        {
            NodeTypeDefinition? definition = null;
            if (w.NodeTypes != null && w.NodeTypes.TryGetValue(tile.Node.TypeId, out var def)) definition = def;
            if (definition == null || definition.BlocksMovement != false) return false;
        }
        if (w.Blocked != null && w.Blocked.Contains($"{Js.Num(x)},{Js.Num(y)}")) return false;
        return true;
    }

    private sealed record OpenNode(double X, double Y, double F, double G);

    private static string Key(double x, double y) => $"{Js.Num(x)},{Js.Num(y)}";

    /// <summary>
    /// Find a path from start to goal (exclusive of start, inclusive of goal).
    /// Returns null when unreachable. Bounded by the scene size.
    /// </summary>
    public static List<PathPoint>? FindPath(Walkability w, PathPoint start, PathPoint goal)
    {
        if (start.X == goal.X && start.Y == goal.Y) return [];
        if (!IsWalkable(w, goal.X, goal.Y)) return null;

        var open = new List<OpenNode>
        {
            new(start.X, start.Y, Math.Abs(goal.X - start.X) + Math.Abs(goal.Y - start.Y), 0),
        };
        var cameFrom = new Dictionary<string, (string Key, double X, double Y)>();
        var gScore = new Dictionary<string, double> { [Key(start.X, start.Y)] = 0 };
        var closed = new HashSet<string>();

        var maxIterations = w.Scene.Width * w.Scene.Height * 4;

        for (double iterations = 0; open.Count > 0 && iterations < maxIterations; iterations++)
        {
            // Lowest f wins; stable for determinism.
            var bestIndex = 0;
            for (var i = 1; i < open.Count; i++)
            {
                if (open[i].F < open[bestIndex].F) bestIndex = i;
            }
            var current = open[bestIndex];
            open.RemoveAt(bestIndex);
            var currentKey = Key(current.X, current.Y);
            if (closed.Contains(currentKey)) continue;
            closed.Add(currentKey);

            if (current.X == goal.X && current.Y == goal.Y)
            {
                // Reconstruct (goal → start), then reverse and drop the start tile.
                var path = new List<PathPoint>();
                var startKey = Key(start.X, start.Y);
                string? cursor = currentKey;
                double cx = current.X, cy = current.Y;
                while (!string.IsNullOrEmpty(cursor) && cursor != startKey)
                {
                    path.Add(new PathPoint(cx, cy));
                    if (cameFrom.TryGetValue(cursor, out var prev))
                    {
                        cursor = prev.Key;
                        cx = prev.X;
                        cy = prev.Y;
                    }
                    else
                    {
                        cursor = null;
                    }
                }
                path.Reverse();
                return path;
            }

            foreach (var (dx, dy) in Neighbors)
            {
                var nx = current.X + dx;
                var ny = current.Y + dy;
                var neighborKey = Key(nx, ny);
                if (closed.Contains(neighborKey)) continue;
                if (!IsWalkable(w, nx, ny)) continue;
                var tentativeG = current.G + 1;
                if (gScore.TryGetValue(neighborKey, out var known) && known <= tentativeG) continue;
                gScore[neighborKey] = tentativeG;
                cameFrom[neighborKey] = (currentKey, current.X, current.Y);
                open.Add(new OpenNode(nx, ny, tentativeG + Math.Abs(goal.X - nx) + Math.Abs(goal.Y - ny), tentativeG));
            }
        }

        return null;
    }
}
