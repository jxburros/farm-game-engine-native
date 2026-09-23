using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/world.ts.

public sealed record SceneTransition
{
    public double FromX { get; init; }
    public double FromY { get; init; }
    public string ToSceneId { get; init; } = "";
    public double ToX { get; init; }
    public double ToY { get; init; }
    /// <summary>Locked transitions don't fire; events can lock/unlock them (M3).</summary>
    public bool? Locked { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>TS <c>TileSchema.visuals</c> (inline object): per-layer art overrides.</summary>
public sealed record TileVisuals
{
    public VisualRef? Background { get; init; }
    public VisualRef? Overlay { get; init; }
    public VisualRef? Object { get; init; }
}

public sealed record Tile
{
    public double X { get; init; }
    public double Y { get; init; }
    /// <summary>One of <see cref="TileTypes"/>.</summary>
    public string Type { get; init; } = "";
    /// <summary>Background layer: grass, soil, water, floor</summary>
    public string Background { get; init; } = "";
    /// <summary>Overlay layer: path, rug, … renders on top of background</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Overlay { get; init; }
    /// <summary>Object layer: wall, door, … renders on top of overlay</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Object { get; init; }
    public Crop? Crop { get; init; }
    public Item? Item { get; init; }
    /// <summary>Gathering node instance (tree/rock/…) occupying this tile.</summary>
    public TileNode? Node { get; init; }
    /// <summary>Placed machine instance (M4 crafting).</summary>
    public TileMachine? Machine { get; init; }
    /// <summary>Mine ladder going down (M4 mining, generated floors).</summary>
    public bool? LadderDown { get; init; }
    public bool Collision { get; init; }
    public string? CustomImage { get; init; }
    public TileVisuals? Visuals { get; init; }
    /// <summary>One of <see cref="SoilStates"/>.</summary>
    public string? SoilState { get; init; }
    public double SoilMoisture { get; init; }
    public double SoilFertility { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record Scene
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>int, positive.</summary>
    public double Width { get; init; }
    /// <summary>int, positive.</summary>
    public double Height { get; init; }
    /// <summary>Row-major: <c>Tiles[y][x]</c>.</summary>
    public List<List<Tile>> Tiles { get; init; } = [];
    public List<SceneTransition> Transitions { get; init; } = [];
    public List<string> Npcs { get; init; } = [];
    public List<string> Events { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
