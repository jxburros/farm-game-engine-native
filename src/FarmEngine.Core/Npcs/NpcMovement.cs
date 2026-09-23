using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// NPC movement &amp; schedules (M3). Runs once per whole in-game minute from
/// advanceTick. Patterns: stationary, wander (radius around home), patrol
/// (waypoints), plus time-based schedules (walk to a destination via A*;
/// cross-scene destinations teleport — documented v1 simplification).
///
/// Determinism: wander draws flow through the seeded RNG in state.
/// Port of npcs/movement.ts.
/// </summary>
public static class NpcMovement
{
    private const double WanderPeriodMinutes = 3;

    private static readonly (double Dx, double Dy)[] Directions = [(0, -1), (0, 1), (-1, 0), (1, 0)];

    private static Walkability? MakeWalkability(EngineContext ctx, GameState state, string npcId, string sceneId)
    {
        var scene = state.World.Scenes.Find(s => s.Id == sceneId);
        if (scene == null) return null;
        var blocked = new HashSet<string>();
        // Other NPCs in the scene block.
        foreach (var otherId in state.Npcs.Keys)
        {
            var other = state.Npcs[otherId];
            if (otherId != npcId && other.SceneId == sceneId) blocked.Add($"{Js.Num(other.X)},{Js.Num(other.Y)}");
        }
        // The player blocks too (fractional position → occupied tile).
        if (state.Player.SceneId == sceneId) blocked.Add($"{Js.Num(Math.Floor(state.Player.X))},{Js.Num(Math.Floor(state.Player.Y))}");
        return new Walkability(scene, WorldMovement.ById(ctx.Content.NodeTypes, def => def.Id), blocked);
    }

    private static bool PlayerIsAdjacent(GameState state, NpcState npc)
    {
        if (state.Player.SceneId != npc.SceneId) return false;
        return Math.Abs(Math.Floor(state.Player.X) - npc.X) + Math.Abs(Math.Floor(state.Player.Y) - npc.Y) <= 1;
    }

    /// <summary>Latest schedule entry whose minute has passed (entries sorted by minute).</summary>
    private static NpcScheduleEntry? ActiveScheduleEntry(Npc def, double timeMinutes)
    {
        if (def.Schedule == null || def.Schedule.Count == 0) return null;
        var sorted = Js.StableSort(def.Schedule, (a, b) => CompareNumbers(a.Minute - b.Minute));
        NpcScheduleEntry? active = null;
        foreach (var entry in sorted)
        {
            if (entry.Minute <= timeMinutes) active = entry;
        }
        return active;
    }

    /// <summary>JS comparator result → sign (NaN compares equal, as in Array.prototype.sort).</summary>
    private static int CompareNumbers(double diff) => diff < 0 ? -1 : diff > 0 ? 1 : 0;

    private static NpcState StepAlongPath(EngineContext ctx, GameState state, string npcId, NpcState npc)
    {
        if (npc.Path == null || npc.Path.Count == 0) return npc;
        var next = npc.Path[0];
        var w = MakeWalkability(ctx, state, npcId, npc.SceneId);
        if (w == null || !Pathfinding.IsWalkable(w, next.X, next.Y))
        {
            // Blocked: drop the path; it will be recomputed next minute.
            return npc with { Path = null };
        }
        return npc with { X = next.X, Y = next.Y, Path = npc.Path.Skip(1).ToList() };
    }

    private static List<GridPoint>? ToGridPath(List<PathPoint>? path) =>
        path?.Select(p => new GridPoint { X = p.X, Y = p.Y }).ToList();

    private static OrderedDictionary<string, NpcState> With(OrderedDictionary<string, NpcState> npcs, string id, NpcState npc) =>
        new(npcs) { [id] = npc };

    public static GameState AdvanceNpcs(EngineContext ctx, GameState state, double minutes)
    {
        if (minutes <= 0) return state;
        var movers = ctx.Content.Npcs.Where(def => def.CanMove || (def.Schedule?.Count ?? 0) > 0).ToList();
        if (movers.Count == 0) return state;

        var npcs = state.Npcs;
        var rngState = state.Rng;
        var changed = false;

        // Storms keep scheduled NPCs home (M4b).
        var stayInside = Weather.CurrentWeather(ctx, state)?.NpcsStayInside ?? false;

        foreach (var def in movers)
        {
            if (!npcs.TryGetValue(def.Id, out var npc) || npc == null) continue;

            // Player proximity pause: never move while the player stands beside.
            if (PlayerIsAdjacent(state, npc)) continue;

            var scheduleTarget = stayInside ? null : ActiveScheduleEntry(def, state.Clock.TimeMinutes);

            if (scheduleTarget != null && scheduleTarget.SceneId != npc.SceneId)
            {
                // Cross-scene schedule: teleport (v1 simplification).
                npc = npc with { SceneId = scheduleTarget.SceneId, X = scheduleTarget.X, Y = scheduleTarget.Y, Path = null };
                npcs = With(npcs, def.Id, npc);
                changed = true;
                continue;
            }

            if (scheduleTarget != null && (npc.X != scheduleTarget.X || npc.Y != scheduleTarget.Y))
            {
                if (npc.Path == null || npc.Path.Count == 0)
                {
                    var w = MakeWalkability(ctx, state with { Npcs = npcs }, def.Id, npc.SceneId);
                    var path = w != null
                        ? Pathfinding.FindPath(w, new PathPoint(npc.X, npc.Y), new PathPoint(scheduleTarget.X, scheduleTarget.Y))
                        : null;
                    npc = npc with { Path = ToGridPath(path) };
                }
                var stepped = StepAlongPath(ctx, state with { Npcs = npcs }, def.Id, npc);
                if (!ReferenceEquals(stepped, npc))
                {
                    npc = stepped;
                    npcs = With(npcs, def.Id, npc);
                    changed = true;
                }
                else if (!ReferenceEquals(npc.Path, npcs[def.Id].Path))
                {
                    npcs = With(npcs, def.Id, npc);
                    changed = true;
                }
                continue;
            }

            if (!def.CanMove) continue;

            if (def.MovePattern == "patrol" && def.PatrolPoints != null && def.PatrolPoints.Count > 0)
            {
                var index = (npc.PatrolIndex ?? 0) % def.PatrolPoints.Count;
                var waypoint = def.PatrolPoints[(int)index];
                if (npc.X == waypoint.X && npc.Y == waypoint.Y)
                {
                    npc = npc with { PatrolIndex = (index + 1) % def.PatrolPoints.Count, Path = null };
                    npcs = With(npcs, def.Id, npc);
                    changed = true;
                    continue;
                }
                if (npc.Path == null || npc.Path.Count == 0)
                {
                    var w = MakeWalkability(ctx, state with { Npcs = npcs }, def.Id, npc.SceneId);
                    var path = w != null
                        ? Pathfinding.FindPath(w, new PathPoint(npc.X, npc.Y), new PathPoint(waypoint.X, waypoint.Y))
                        : null;
                    npc = npc with { Path = ToGridPath(path) };
                }
                var stepped = StepAlongPath(ctx, state with { Npcs = npcs }, def.Id, npc);
                if (!ReferenceEquals(stepped, npc) || !ReferenceEquals(stepped.Path, npcs[def.Id].Path))
                {
                    npcs = With(npcs, def.Id, stepped);
                    changed = true;
                }
                continue;
            }

            if (def.MovePattern == "wander")
            {
                // Move at most once per WANDER_PERIOD_MINUTES; use the minute counter
                // so behavior is time-based rather than frame-based.
                if (Math.Floor(state.Clock.TimeMinutes) % WanderPeriodMinutes != 0) continue;
                var rng = new Rng(rngState);
                var pick = Directions[(int)rng.Int(0, 3)];
                rngState = rng.State;
                var nx = npc.X + pick.Dx;
                var ny = npc.Y + pick.Dy;
                var radius = def.WanderRadius ?? 3;
                if (Math.Abs(nx - def.X) > radius || Math.Abs(ny - def.Y) > radius) continue;
                var w = MakeWalkability(ctx, state with { Npcs = npcs }, def.Id, npc.SceneId);
                if (w != null && Pathfinding.IsWalkable(w, nx, ny))
                {
                    npc = npc with { X = nx, Y = ny };
                    npcs = With(npcs, def.Id, npc);
                    changed = true;
                }
            }
        }

        if (!changed && ReferenceEquals(rngState, state.Rng)) return state;
        return state with { Npcs = npcs, Rng = rngState };
    }
}
