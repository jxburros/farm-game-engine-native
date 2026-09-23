using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/mining.ts.
// Mining (M4f) — procedurally generated floors, deterministic per seed.

/// <summary>TS <c>MineBandSchema.rocks</c> item (inline object): a weighted node type.</summary>
public sealed record MineRockWeight
{
    public string NodeTypeId { get; init; } = "";
    /// <summary>positive.</summary>
    public double Weight { get; init; }
}

public sealed record MineBand
{
    /// <summary>int, positive.</summary>
    public double FromFloor { get; init; }
    /// <summary>int, positive.</summary>
    public double ToFloor { get; init; }
    /// <summary>Weighted node types spawned in this depth band.</summary>
    public List<MineRockWeight> Rocks { get; init; } = [];
    /// <summary>Rock density (fraction of floor tiles occupied). 0..1.</summary>
    public double Density { get; init; } = 0.35;
}

public sealed record MineConfig
{
    public bool Enabled { get; init; } = false;
    /// <summary>Scene holding the mine entrance (descend via the entrance tile).</summary>
    public string? EntranceSceneId { get; init; }
    /// <summary>int.</summary>
    public double? EntranceX { get; init; }
    /// <summary>int.</summary>
    public double? EntranceY { get; init; }
    /// <summary>int, positive.</summary>
    public double Floors { get; init; } = 20;
    /// <summary>int, positive.</summary>
    public double FloorWidth { get; init; } = 14;
    /// <summary>int, positive.</summary>
    public double FloorHeight { get; init; } = 12;
    public List<MineBand> Bands { get; init; } = [];
    /// <summary>Chance a broken rock reveals the ladder down. 0..1.</summary>
    public double LadderChance { get; init; } = 0.18;
    /// <summary>Elevator checkpoint every N floors. int, positive.</summary>
    public double ElevatorEvery { get; init; } = 5;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
