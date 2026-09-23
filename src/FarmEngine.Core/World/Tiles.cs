using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Tile layer rules — extracted from src/lib/game-helpers.ts,
/// behavior-identical (characterization-tested). Port of world/tiles.ts.
/// </summary>
public static class Tiles
{
    /// <summary>Returns <c>"background"</c>, <c>"overlay"</c> or <c>"object"</c>.</summary>
    public static string ClassifyTileType(string type)
    {
        switch (type)
        {
            case "path":
                return "overlay";
            case "wall":
            case "door":
                return "object";
            default:
                return "background";
        }
    }

    public static Tile CreateEmptyTile(double x, double y, string type = "grass")
    {
        var layer = ClassifyTileType(type);
        return new Tile
        {
            X = x,
            Y = y,
            Type = type,
            Background = layer == "background" ? type : "grass",
            Overlay = layer == "overlay" ? type : null,
            Object = layer == "object" ? type : null,
            Collision = type == "wall",
            SoilMoisture = 0,
            SoilFertility = 0,
        };
    }

    public static Tile SetTileLayer(Tile tile, string newType, VisualRef? visual = null)
    {
        var layer = ClassifyTileType(newType);
        var updated = tile with { Type = newType };
        if (visual != null) updated = updated with { CustomImage = null };
        if (visual != null || tile.Visuals != null)
        {
            // { ...tile.visuals, [layer]: visual }
            var visuals = tile.Visuals ?? new TileVisuals();
            visuals = layer switch
            {
                "background" => visuals with { Background = visual },
                "overlay" => visuals with { Overlay = visual },
                _ => visuals with { Object = visual },
            };
            updated = updated with { Visuals = visuals };
        }

        switch (layer)
        {
            case "background":
                updated = updated with { Background = newType };
                break;
            case "overlay":
                updated = updated with { Overlay = newType };
                break;
            case "object":
                updated = updated with { Object = newType, Collision = newType == "wall" };
                break;
        }

        return updated;
    }

    public static Scene CreateEmptyScene(string id, string name, double width = 12, double height = 12)
    {
        var tiles = new List<List<Tile>>();
        for (double y = 0; y < height; y++)
        {
            var row = new List<Tile>();
            for (double x = 0; x < width; x++)
            {
                row.Add(CreateEmptyTile(x, y, "grass"));
            }
            tiles.Add(row);
        }

        return new Scene { Id = id, Name = name, Width = width, Height = height, Tiles = tiles, Transitions = [], Npcs = [], Events = [] };
    }

    /// <summary>Clone a scene's tile grid (rows and tiles) for immutable updates.</summary>
    public static List<List<Tile>> CloneTiles(List<List<Tile>> tiles) =>
        tiles.Select(row => row.Select(tile => tile with { }).ToList()).ToList();

    /// <summary>Paint a rectangle (inclusive corners) with a tile type. Returns new tiles.</summary>
    public static List<List<Tile>> PaintRect(List<List<Tile>> tiles, double x1, double y1, double x2, double y2, string type, VisualRef? visual = null)
    {
        var next = CloneTiles(tiles);
        var minX = Math.Max(0, Math.Min(x1, x2));
        var maxX = Math.Min((tiles.Count > 0 ? tiles[0].Count : 0) - 1, Math.Max(x1, x2));
        var minY = Math.Max(0, Math.Min(y1, y2));
        var maxY = Math.Min(tiles.Count - 1, Math.Max(y1, y2));
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var row = next[(int)y];
                row[(int)x] = SetTileLayer(row[(int)x], type, visual) with { Crop = null, Node = null };
            }
        }
        return next;
    }

    /// <summary>
    /// Flood-fill the contiguous region of the clicked tile's <c>type</c> with a new
    /// type (4-directional). Returns new tiles; no-op when types match.
    /// </summary>
    public static List<List<Tile>> FloodFill(List<List<Tile>> tiles, double startX, double startY, string type, VisualRef? visual = null)
    {
        var height = (double)tiles.Count;
        var width = (double)(tiles.Count > 0 ? tiles[0].Count : 0);
        if (startY < 0 || startY >= height || startX < 0 || startX >= width) return tiles;
        var sourceType = tiles[(int)startY][(int)startX].Type;
        if (sourceType == type && visual == null) return tiles;

        var next = CloneTiles(tiles);
        var queue = new Queue<(double X, double Y)>();
        queue.Enqueue((startX, startY));
        var seen = new HashSet<string> { $"{Js.Num(startX)},{Js.Num(startY)}" };
        (double Dx, double Dy)[] dirs = [(0, -1), (0, 1), (-1, 0), (1, 0)];
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            if (next[(int)y][(int)x].Type != sourceType) continue;
            next[(int)y][(int)x] = SetTileLayer(next[(int)y][(int)x], type, visual) with { Crop = null, Node = null };
            foreach (var (dx, dy) in dirs)
            {
                var nx = x + dx;
                var ny = y + dy;
                var key = $"{Js.Num(nx)},{Js.Num(ny)}";
                if (nx < 0 || nx >= width || ny < 0 || ny >= height || seen.Contains(key)) continue;
                if (next[(int)ny][(int)nx].Type != sourceType) continue;
                seen.Add(key);
                queue.Enqueue((nx, ny));
            }
        }
        return next;
    }

    /// <summary>Extract a deep-copied tile region (inclusive corners, clamped).</summary>
    public static List<List<Tile>> CopyTileRegion(List<List<Tile>> tiles, double x1, double y1, double x2, double y2)
    {
        var minX = Math.Max(0, Math.Min(x1, x2));
        var maxX = Math.Min((tiles.Count > 0 ? tiles[0].Count : 0) - 1, Math.Max(x1, x2));
        var minY = Math.Max(0, Math.Min(y1, y2));
        var maxY = Math.Min(tiles.Count - 1, Math.Max(y1, y2));
        var region = new List<List<Tile>>();
        for (var y = minY; y <= maxY; y++)
        {
            var row = new List<Tile>();
            for (var x = minX; x <= maxX; x++)
            {
                row.Add(JsonDefaults.DeepClone(tiles[(int)y][(int)x]));
            }
            region.Add(row);
        }
        return region;
    }

    /// <summary>Stamp a copied region with its top-left at (x, y), clamped to bounds.</summary>
    public static List<List<Tile>> PasteTileRegion(List<List<Tile>> tiles, List<List<Tile>> region, double x, double y)
    {
        var next = CloneTiles(tiles);
        var height = (double)tiles.Count;
        var width = (double)(tiles.Count > 0 ? tiles[0].Count : 0);
        for (var dy = 0; dy < region.Count; dy++)
        {
            for (var dx = 0; dx < region[dy].Count; dx++)
            {
                var tx = x + dx;
                var ty = y + dy;
                if (tx < 0 || tx >= width || ty < 0 || ty >= height) continue;
                next[(int)ty][(int)tx] = JsonDefaults.DeepClone(region[dy][dx]) with { X = tx, Y = ty };
            }
        }
        return next;
    }
}
