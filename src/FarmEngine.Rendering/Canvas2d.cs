using SkiaSharp;

namespace FarmEngine.Rendering;

/// <summary>
/// Constants and pure helpers of the reference renderer (port of
/// packages/renderer-canvas2d/src/index.ts). The drawing itself lives in
/// <see cref="SkiaWorldRenderer"/>.
/// </summary>
public static class Canvas2d
{
    /// <summary>Color mapping for tile types (matching the web CSS oklch values).</summary>
    public static readonly IReadOnlyDictionary<string, string> TileColors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["grass"] = "#5b9a4a",
        ["soil"] = "#7a6545",
        ["water"] = "#3075b0",
        ["path"] = "#b5a48d",
        ["wall"] = "#5e5a68",
        ["door"] = "#8a6a3f",
        ["floor"] = "#b5a48d",
    };

    public static readonly IReadOnlyList<string> CropStageColors =
    [
        "#407546",
        "#558c52",
        "#72a94c",
        "#d6bd24",
    ];

    public const string WitheredCropColor = "#7d6a52";

    public const string PlayerColor = "#276b3c";
    public const string PlayerBorderColor = "#ffffff80";
    public const string NpcColor = "#c28020";
    public const string NpcBorderColor = "#00000030";
    public const string ItemColor = "#d6bd24";

    /// <summary>Unit vector per facing direction (unknown directions fall back to down).</summary>
    public static (int Dx, int Dy) DirectionOffset(string? direction) => direction switch
    {
        "up" => (0, -1),
        "left" => (-1, 0),
        "right" => (1, 0),
        _ => (0, 1),
    };

    /// <summary>
    /// Follow-camera over a world of <paramref name="worldWidth"/>×<paramref name="worldHeight"/>
    /// pixels: centers the target, clamps to world edges, and centers the whole world when it is
    /// smaller than the viewport. Pure — hosts call it per frame.
    /// </summary>
    public static SnapshotCamera ComputeCamera(
        double targetPx,
        double targetPy,
        double worldWidth,
        double worldHeight,
        double viewWidth,
        double viewHeight)
    {
        static double Axis(double target, double world, double view)
        {
            if (world <= view)
            {
                return -(view - world) / 2;
            }

            return Math.Min(Math.Max(target - (view / 2), 0), world - view);
        }

        return new SnapshotCamera(
            Axis(targetPx, worldWidth, viewWidth),
            Axis(targetPy, worldHeight, viewHeight),
            viewWidth,
            viewHeight);
    }

    /// <summary>
    /// World-pixel origin (top-left of the tile-sized draw box) for an entity coordinate:
    /// fractional coordinates are free-movement box CENTERS, integer coordinates are legacy
    /// tile indices.
    /// </summary>
    public static double EntityPixelOrigin(double coord, double padding, double pitch) =>
        IsInteger(coord) ? padding + (coord * pitch) : padding + ((coord - 0.5) * pitch);

    /// <summary>Color for a tile type, with the TS fallback when unknown.</summary>
    public static SKColor TileColor(string? type, string fallbackType) =>
        CssColor.Parse(type is not null && TileColors.TryGetValue(type, out var hex) ? hex : TileColors[fallbackType]);

    private static bool IsInteger(double x) => double.IsFinite(x) && Math.Floor(x) == x;
}
