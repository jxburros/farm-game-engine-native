using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/content.ts.
//
// Content definitions — immutable at runtime, authored in the editor or
// provided by content packs.

/// <summary>TS <c>CropDefinitionSchema.multiTile</c> (inline object).</summary>
public sealed record CropMultiTile
{
    /// <summary>int, positive.</summary>
    public double Width { get; init; }
    /// <summary>int, positive.</summary>
    public double Height { get; init; }
}

public sealed record CropDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public VisualRef? Visual { get; init; }
    public double SeedCost { get; init; }
    public double BaseHarvestValue { get; init; }
    /// <summary>Legacy wall-clock growth duration (ms). Kept for pre-v4 data; the engine uses growthDays.</summary>
    public double GrowthTime { get; init; }
    /// <summary>In-game days from planting to maturity (authoritative from schema v4). positive.</summary>
    public double? GrowthDays { get; init; }
    /// <summary>int, positive.</summary>
    public double Stages { get; init; }
    public List<string> Seasons { get; init; } = [];
    public double? RegrowthTime { get; init; }
    /// <summary>In-game days between repeat harvests for regrowing crops. positive.</summary>
    public double? RegrowthDays { get; init; }
    public bool CanRegrow { get; init; }
    public CropMultiTile? MultiTile { get; init; }
    public double? MutationChance { get; init; }
    public double YieldMin { get; init; }
    public double YieldMax { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// TS <c>CustomCropDefinitionSchema = CropDefinitionSchema.extend({ customAsset })</c>
/// (zod 3 <c>extend</c> keeps passthrough). C# records can't extend a sealed
/// record, so the fields are repeated.
/// </summary>
public sealed record CustomCropDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public VisualRef? Visual { get; init; }
    public double SeedCost { get; init; }
    public double BaseHarvestValue { get; init; }
    /// <summary>Legacy wall-clock growth duration (ms). Kept for pre-v4 data; the engine uses growthDays.</summary>
    public double GrowthTime { get; init; }
    /// <summary>In-game days from planting to maturity (authoritative from schema v4). positive.</summary>
    public double? GrowthDays { get; init; }
    /// <summary>int, positive.</summary>
    public double Stages { get; init; }
    public List<string> Seasons { get; init; } = [];
    public double? RegrowthTime { get; init; }
    /// <summary>In-game days between repeat harvests for regrowing crops. positive.</summary>
    public double? RegrowthDays { get; init; }
    public bool CanRegrow { get; init; }
    public CropMultiTile? MultiTile { get; init; }
    public double? MutationChance { get; init; }
    public double YieldMin { get; init; }
    public double YieldMax { get; init; }
    public string? CustomAsset { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record Item
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public VisualRef? Visual { get; init; }
    public string Description { get; init; } = "";
    /// <summary>One of <see cref="ItemTypes"/>.</summary>
    public string Type { get; init; } = "";
    public bool Stackable { get; init; }
    public double MaxStack { get; init; }
    public double Value { get; init; }
    public string? CropType { get; init; }
    public string? CustomImage { get; init; }
    /// <summary>One of <see cref="ToolTypes"/>.</summary>
    public string? ToolType { get; init; }
    public double? ToolPower { get; init; }
    /// <summary>Tool tier: 1 = basic. Higher tiers hit harder, cost less energy, gain AoE. int, positive.</summary>
    public double? ToolTier { get; init; }
    public double? Durability { get; init; }
    public double? MaxDurability { get; init; }
    /// <summary>Action performed when the item is used from the inventory.</summary>
    public string? UseActionId { get; init; }
    /// <summary>Consume one of the item on a successful use.</summary>
    public bool? ConsumeOnUse { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record InventorySlot
{
    public Item Item { get; init; } = new();
    public double Quantity { get; init; }
}

/// <summary>A dropped/growing crop instance on a tile (game state, not content).</summary>
public sealed record Crop
{
    public string Type { get; init; } = "";
    /// <summary>Legacy wall-clock plant timestamp (ms). Superseded by plantedOnDay from schema v4.</summary>
    public double PlantedAt { get; init; }
    /// <summary>Absolute in-game day the crop was planted (schema v4+). int.</summary>
    public double? PlantedOnDay { get; init; }
    /// <summary>Watered in-game days accumulated toward growthDays (schema v4+).</summary>
    public double? DaysGrown { get; init; }
    /// <summary>Killed by season change; renders dead and can be cleared.</summary>
    public bool? Withered { get; init; }
    public double Stage { get; init; }
    public bool Watered { get; init; }
    public double? LastWateredDay { get; init; }
    /// <summary>One of <see cref="CropQualities"/>.</summary>
    public string Quality { get; init; } = "";
    /// <summary>One of <see cref="CropMutations"/> or <c>null</c> (required key).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Mutation { get; init; }
    public bool? IsMultiTileRoot { get; init; }
    public string? MultiTileId { get; init; }
    public double HarvestCount { get; init; }
    public double DaysWithoutWater { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// Sprite-sheet slicing metadata (M7). A sheet is a grid: columns = walk
/// frames, rows = directions (down, left, right, up) when <c>directional</c>.
/// Assets without this stay static images; games with zero art still render
/// via the colored-rectangle fallback.
/// </summary>
public sealed record SpriteSheet
{
    /// <summary>int, positive.</summary>
    public double FrameWidth { get; init; }
    /// <summary>int, positive.</summary>
    public double FrameHeight { get; init; }
    /// <summary>int, positive.</summary>
    public double Frames { get; init; }
    /// <summary>int, positive.</summary>
    public double TicksPerFrame { get; init; } = 6;
    public bool Directional { get; init; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>TS <c>CustomAssetSchema.type</c> enum.</summary>
public static class CustomAssetTypes
{
    public const string Tile = "tile";
    public const string Npc = "npc";
    public const string Item = "item";
    public const string Player = "player";
    public const string Art = "art";

    public static readonly IReadOnlyList<string> All = [Tile, Npc, Item, Player, Art];
}

public sealed record CustomAsset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>One of <see cref="CustomAssetTypes"/>.</summary>
    public string Type { get; init; } = "";
    /// <summary>int, positive.</summary>
    public double? Width { get; init; }
    /// <summary>int, positive.</summary>
    public double? Height { get; init; }
    public List<AnimationClip>? Animations { get; init; }
    public string DataUrl { get; init; } = "";
    public string? TileType { get; init; }
    /// <summary>Present when the image is an animated sprite sheet (M7).</summary>
    public SpriteSheet? Sheet { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
