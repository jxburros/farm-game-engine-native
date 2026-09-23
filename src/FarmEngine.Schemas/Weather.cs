using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/weather.ts.
// Weather (M4b) — content-defined weather types + per-season roll tables.

public sealed record WeatherTypeDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Rain-like: outdoor soil is watered automatically at day start.</summary>
    public bool WatersOutdoorSoil { get; init; } = false;
    /// <summary>Chance (0..1) each outdoor crop is destroyed overnight (storms).</summary>
    public double CropDamageChance { get; init; } = 0;
    /// <summary>NPCs with schedules stay home (skip schedule walking).</summary>
    public bool NpcsStayInside { get; init; } = false;
    /// <summary>Renderer overlay hint: 'rain' | 'snow' | null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Overlay { get; init; } = null;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record WeatherTableEntry
{
    public string WeatherId { get; init; } = "";
    /// <summary>positive.</summary>
    public double Weight { get; init; }
}

public sealed record WeatherConfig
{
    public List<WeatherTypeDefinition> Types { get; init; } = [];
    /// <summary>Per-season weighted roll tables (rolled at each day start).</summary>
    public OrderedDictionary<string, List<WeatherTableEntry>> Table { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
