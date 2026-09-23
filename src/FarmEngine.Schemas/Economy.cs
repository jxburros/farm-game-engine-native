using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/economy.ts.

/// <summary>A single line in a shop's stock list.</summary>
public sealed record ShopStockEntry
{
    public string ItemId { get; init; } = "";
    /// <summary>Override price; defaults to the item's base value.</summary>
    public double? Price { get; init; }
    /// <summary>Restrict availability to these seasons (empty/absent = always).</summary>
    public List<string>? Seasons { get; init; }
    /// <summary>Max units purchasable per in-game day (absent = unlimited). int, positive.</summary>
    public double? DailyLimit { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record ShopDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<ShopStockEntry> Stock { get; init; } = [];
    /// <summary>Multiplier applied to item base value when the player sells here.</summary>
    public double SellPriceMultiplier { get; init; } = 1;
    /// <summary>Whether this shop buys player items at all.</summary>
    public bool BuysItems { get; init; } = true;
    /// <summary>Whether this shop repairs broken tools (for durability * repairCostPerPoint).</summary>
    public bool RepairsTools { get; init; } = false;
    public double RepairCostPerPoint { get; init; } = 0.5;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
