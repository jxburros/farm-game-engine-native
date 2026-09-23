using FarmEngine.Core;
using FarmEngine.Schemas;

namespace FarmEngine.Content;

/// <summary>
/// Editor/app helpers (port of the non-factory parts of
/// <c>src/lib/game-helpers.ts</c>). World rules (tile layers, collision,
/// movement) live in <c>FarmEngine.Core</c> (<see cref="Tiles"/>,
/// <see cref="WorldMovement"/>); this keeps only editor concerns — legacy
/// tile migration and render interpolation. The project factories
/// (<c>createInitialProject</c>, <c>createDefaultPlayer</c>,
/// <c>createDefaultItems</c>) are on <see cref="DefaultContent"/>.
/// </summary>
public static class GameHelpers
{
    /// <summary>Movement speed in pixels per second for smooth interpolation.</summary>
    public const double MovementSpeed = 120;

    /// <summary>
    /// Migrates a legacy tile (without layer fields) to the layered format.
    /// (Kept for reference/tests; the load path uses the versioned pipeline in
    /// <c>Migrations</c>.) Returns the same instance when already migrated.
    /// </summary>
    public static Tile MigrateTile(Tile tile)
    {
        if (!string.IsNullOrEmpty(tile.Background)) return tile; // already migrated
        var layer = Tiles.ClassifyTileType(tile.Type);
        return tile with
        {
            Background = layer == "background" ? tile.Type : "grass",
            Overlay = layer == "overlay" ? tile.Type : null,
            Object = layer == "object" ? tile.Type : null,
        };
    }

    /// <summary>Migrates all tiles in a scene to the layered format (same instance when nothing needs it).</summary>
    public static Scene MigrateSceneTiles(Scene scene)
    {
        var needsMigration = scene.Tiles.Any(row => row.Any(tile => string.IsNullOrEmpty(tile.Background)));
        if (!needsMigration) return scene;
        return scene with { Tiles = scene.Tiles.Select(row => row.Select(MigrateTile).ToList()).ToList() };
    }

    /// <summary>
    /// Interpolates a value toward a target by speed * deltaTime, clamped to
    /// not overshoot.
    /// </summary>
    public static double InterpolateToward(double current, double target, double speed, double deltaTime)
    {
        if (current == target) return current;
        var diff = target - current;
        var step = speed * deltaTime;
        if (Math.Abs(diff) <= step) return target;
        // JS Math.sign: NaN stays NaN (C# Math.Sign would throw).
        return current + (diff > 0 ? 1 : diff < 0 ? -1 : double.NaN) * step;
    }

    /// <summary>Pixel position for a grid coordinate; tileSize + 1 accounts for the 1px gap between tiles.</summary>
    public static double GridToPixel(double gridPos, double tileSize, double padding) => padding + gridPos * (tileSize + 1);

    /// <summary>Editor-side collision check against project NPC placements (wraps <see cref="WorldMovement.CanMoveTo"/>).</summary>
    public static bool CanMoveTo(Scene scene, double x, double y, IEnumerable<Npc> npcs, string? currentNpcId = null)
    {
        var npcStates = new OrderedDictionary<string, NpcState>();
        foreach (var npc in npcs) npcStates[npc.Id] = new NpcState { X = npc.X, Y = npc.Y, SceneId = npc.SceneId };
        return WorldMovement.CanMoveTo(scene, x, y, npcStates, currentNpcId);
    }
}
