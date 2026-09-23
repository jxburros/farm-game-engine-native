using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/animals.ts.
// Animals & ranching (M4c).

public sealed record AnimalSpeciesDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public VisualRef? Visual { get; init; }
    /// <summary>nonnegative.</summary>
    public double PurchaseCost { get; init; } = 0;
    /// <summary>Item consumed daily; absent = grazes for free.</summary>
    public string? FeedItemId { get; init; }
    public string ProductItemId { get; init; } = "";
    /// <summary>Days between products (when fed and adult). int, positive.</summary>
    public double ProductIntervalDays { get; init; } = 1;
    /// <summary>int, nonnegative.</summary>
    public double DaysToAdult { get; init; } = 3;
    /// <summary>Renderer hint.</summary>
    public string Color { get; init; } = "#e8d8c3";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A live animal (persisted per project/save).</summary>
public sealed record AnimalState
{
    public string Id { get; init; } = "";
    public string SpeciesId { get; init; } = "";
    public string Name { get; init; } = "";
    public string SceneId { get; init; } = "";
    /// <summary>int.</summary>
    public double X { get; init; }
    /// <summary>int.</summary>
    public double Y { get; init; }
    /// <summary>0..100; fed &amp; petted raise it, neglect lowers it.</summary>
    public double Mood { get; init; } = 70;
    public bool FedToday { get; init; } = false;
    public bool PettedToday { get; init; } = false;
    /// <summary>int.</summary>
    public double AgeDays { get; init; } = 0;
    /// <summary>int.</summary>
    public double DaysSinceProduct { get; init; } = 0;
    /// <summary>Product waiting to be collected.</summary>
    public bool ProductReady { get; init; } = false;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
