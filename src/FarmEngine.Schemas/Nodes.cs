using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/nodes.ts.

/// <summary>Weighted drop-table entry for a gathering node.</summary>
public sealed record NodeDrop
{
    public string ItemId { get; init; } = "";
    /// <summary>int, nonnegative.</summary>
    public double Min { get; init; }
    /// <summary>int, nonnegative.</summary>
    public double Max { get; init; }
    /// <summary>positive.</summary>
    public double Weight { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// A gathering node type (tree, rock, weeds, …) — content-defined so mods
/// and projects can add their own.
/// </summary>
public sealed record NodeTypeDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public VisualRef? Visual { get; init; }
    /// <summary>Number of tool hits required to break the node. int, positive.</summary>
    public double Health { get; init; }
    /// <summary>One of <see cref="ToolTypes"/>.</summary>
    public string RequiredTool { get; init; } = "";
    /// <summary>Minimum tool tier required (tools default to tier 1). int, positive.</summary>
    public double RequiredToolTier { get; init; } = 1;
    /// <summary>Weighted drop table; each hit that depletes the node rolls once per entry range.</summary>
    public List<NodeDrop> Drops { get; init; } = [];
    /// <summary>
    /// Days until a depleted node respawns; null/absent = never. int, positive.
    /// TS is <c>.nullable().optional()</c>; C# cannot tell absent from null, so
    /// it is always written (as <c>null</c> when unset) — the built-in content
    /// always spells the key out.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? RespawnDays { get; init; }
    /// <summary>Renderer hint (hex color).</summary>
    public string Color { get; init; } = "#7a5a3a";
    /// <summary>Whether the node blocks movement while present.</summary>
    public bool BlocksMovement { get; init; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Live node instance state stored on a tile.</summary>
public sealed record TileNode
{
    public string TypeId { get; init; } = "";
    /// <summary>int.</summary>
    public double RemainingHealth { get; init; }
    /// <summary>Set when depleted; used for respawn scheduling. int.</summary>
    public double? DepletedOnDay { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
