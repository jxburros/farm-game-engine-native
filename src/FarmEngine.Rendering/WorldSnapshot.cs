namespace FarmEngine.Rendering;

// Port of the snapshot types in packages/renderer-canvas2d/src/index.ts.
//
// The renderer consumes a plain-data WorldSnapshot (no engine state, no
// callbacks) and draws it. Unlike engine records these are mutable classes:
// hosts build a fresh snapshot per frame and `Graphics.ApplyGraphics`
// decorates it in place, exactly like the TS objects.
//
// Content ids (crop/node/machine/species/appearance) ride along so the
// renderer can pick built-in art when a `Sprite` is null; they are never
// interpreted by the renderer beyond that.

/// <summary>Crop drawn on a tile.</summary>
public sealed class SnapshotCrop
{
    public SnapshotSprite? Sprite { get; set; }

    /// <summary>Stage color index; the renderer clamps it to the palette.</summary>
    public int ColorIndex { get; set; }

    public bool Mature { get; set; }

    public bool Withered { get; set; }

    /// <summary>Crop definition id (built-in art lookup).</summary>
    public string? CropId { get; set; }

    /// <summary>Growth stages of the definition (0 = unknown).</summary>
    public int Stages { get; set; }
}

/// <summary>Gathering node occupying a tile.</summary>
public sealed class SnapshotNode
{
    public SnapshotSprite? Sprite { get; set; }

    public string Color { get; set; } = "#7a5a3a";

    /// <summary>Depleted nodes render faded while awaiting respawn.</summary>
    public bool Depleted { get; set; }

    /// <summary>Node type id (built-in art lookup).</summary>
    public string? TypeId { get; set; }
}

/// <summary>Placed machine (M4).</summary>
public sealed class SnapshotMachine
{
    public SnapshotSprite? Sprite { get; set; }

    public string Color { get; set; } = "#9a7b4f";

    public bool Working { get; set; }

    public bool OutputReady { get; set; }

    /// <summary>Machine type id (built-in art lookup).</summary>
    public string? TypeId { get; set; }
}

/// <summary>Item lying on a tile.</summary>
public sealed class SnapshotItem
{
    public string? ImageUrl { get; set; }

    public SnapshotSprite? Sprite { get; set; }

    /// <summary>Item type (<c>seed</c>, <c>crop</c>, <c>tool</c>, …) for the built-in art.</summary>
    public string? ItemType { get; set; }
}

public sealed class SnapshotTile
{
    public string Background { get; set; } = "grass";

    public string? Overlay { get; set; }

    public string? Object { get; set; }

    /// <summary>Pre-resolved custom image URL for the whole tile, if any.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Per-layer artwork: [background, overlay, object] (entries may be null).</summary>
    public SnapshotSprite?[]? ArtLayers { get; set; }

    public SnapshotCrop? Crop { get; set; }

    public SnapshotNode? Node { get; set; }

    public SnapshotMachine? Machine { get; set; }

    /// <summary>Revealed mine ladder going down (M4).</summary>
    public bool LadderDown { get; set; }

    public SnapshotItem? Item { get; set; }

    /// <summary>Soil that has been worked (hoed or planted): drawn with furrows.</summary>
    public bool Tilled { get; set; }

    /// <summary>Soil is wet (watered today or by rain).</summary>
    public bool Watered { get; set; }

    /// <summary>Fertilizer applied: a speckle marker is drawn over the soil.</summary>
    public bool Fertilized { get; set; }
}

/// <summary>
/// Sprite-sheet reference (M7): the renderer draws frame <see cref="Frame"/> from row
/// <see cref="Row"/> of a grid-sliced sheet, or the explicit source rectangle when
/// <see cref="SourceX"/>/<see cref="SourceY"/> are set. Zero frame sizes mean the full image.
/// </summary>
public sealed record SnapshotSprite
{
    public string ImageUrl { get; init; } = "";

    public double? SourceX { get; init; }

    public double? SourceY { get; init; }

    public double FrameWidth { get; init; }

    public double FrameHeight { get; init; }

    public double Frame { get; init; }

    public double Row { get; init; }
}

public class SnapshotEntity
{
    public double X { get; set; }

    public double Y { get; set; }

    public string? ImageUrl { get; set; }

    /// <summary>Animated sprite sheet — takes precedence over <see cref="ImageUrl"/> when present.</summary>
    public SnapshotSprite? Sprite { get; set; }

    /// <summary>Fallback fill color (defaults to the NPC gold).</summary>
    public string? Color { get; set; }

    /// <summary><c>npc</c> (default) or <c>animal</c>.</summary>
    public string Kind { get; set; } = "npc";

    /// <summary>NPC appearance id (<c>farmer</c>, <c>merchant</c>, …) for the built-in art.</summary>
    public string? Appearance { get; set; }

    /// <summary>Animal species id for the built-in art.</summary>
    public string? SpeciesId { get; set; }

    /// <summary>Facing direction (down/left/right/up).</summary>
    public string Direction { get; set; } = "down";

    /// <summary>True while walking (drives the walk cycle; idle frame otherwise).</summary>
    public bool Moving { get; set; }
}

public sealed class SnapshotPlayer : SnapshotEntity
{
    /// <summary>Pixel-space override for interpolated movement (play mode).</summary>
    public double? PixelX { get; set; }

    public double? PixelY { get; set; }
}

/// <summary>Floating feedback text ("juice") — a renderer reaction, never sim state.</summary>
public sealed class SnapshotPop
{
    public double X { get; set; }

    public double Y { get; set; }

    public string Text { get; set; } = "";

    public string? Color { get; set; }

    /// <summary>0 (just spawned) → 1 (expired); drives rise + fade.</summary>
    public double Age { get; set; }
}

/// <summary>
/// Camera viewport in world-pixel space. When present the renderer translates the
/// world by (−x, −y) and culls tiles outside the viewport.
/// </summary>
public sealed record SnapshotCamera(double X, double Y, double Width, double Height);

/// <summary>
/// Time of day, weather and season for the atmosphere pass (day/night tint, seasonal
/// foliage, rain/snow). Null on a snapshot means no atmosphere (edit mode).
/// </summary>
/// <param name="TimeMinutes">Minute of day of the game clock (360 = 6:00).</param>
/// <param name="WeatherId">Today's weather id (<c>sun</c>, <c>rain</c>, <c>storm</c>, <c>snow</c>, …).</param>
/// <param name="Season">Season id (<c>spring</c>, <c>summer</c>, <c>fall</c>, <c>winter</c>, …).</param>
public sealed record SnapshotAtmosphere(double TimeMinutes, string? WeatherId, string? Season)
{
    /// <summary>Renderer overlay hint from the weather definition (<c>rain</c> | <c>snow</c>), when known.</summary>
    public string? WeatherOverlay { get; init; }
}

public sealed class WorldSnapshot
{
    public int Width { get; set; }

    public int Height { get; set; }

    public double TileSize { get; set; }

    public double Padding { get; set; }

    /// <summary>
    /// Pixel gap between tiles. Historical editor rendering uses 1 (visible grid
    /// seams); play mode passes 0 for a contiguous world. Null means 1.
    /// </summary>
    public double? TileGap { get; set; }

    public SnapshotCamera? Camera { get; set; }

    /// <summary>Null means "not set" (treated as pixel art, like the TS <c>pixelArt === false</c> check).</summary>
    public bool? PixelArt { get; set; }

    public bool GridOverlay { get; set; }

    /// <summary>Engine tick the frame shows (drives water, walk cycles and weather; 0 in edit mode).</summary>
    public double Tick { get; set; }

    /// <summary>Atmosphere for this frame; null draws the world as-is (edit mode).</summary>
    public SnapshotAtmosphere? Atmosphere { get; set; }

    /// <summary>Rows of tiles: <c>Tiles[y][x]</c>.</summary>
    public List<List<SnapshotTile>> Tiles { get; set; } = [];

    public List<SnapshotEntity> Npcs { get; set; } = [];

    public SnapshotPlayer Player { get; set; } = new();

    /// <summary>Transient overlay effects; omitted entirely under reduced motion.</summary>
    public List<SnapshotPop>? Pops { get; set; }

    /// <summary>Distance between tile origins (tile size + gap).</summary>
    public double Pitch => TileSize + (TileGap ?? 1);

    /// <summary>Full world size in pixels (what the canvas shows without a camera).</summary>
    public (double Width, double Height) WorldPixelSize() =>
        (Padding * 2 + (Width * Pitch) - (TileGap ?? 1), Padding * 2 + (Height * Pitch) - (TileGap ?? 1));
}
