using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/fishing.ts.
// Fishing (M4e).

public sealed record FishTableEntry
{
    public string ItemId { get; init; } = "";
    /// <summary>positive.</summary>
    public double Weight { get; init; }
    /// <summary>0..1 — harder fish escape low-tier rods more often.</summary>
    public double Difficulty { get; init; } = 0.3;
}

public sealed record FishTable
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Restrict to seasons (absent = all).</summary>
    public List<string>? Seasons { get; init; }
    /// <summary>Restrict to scenes (absent = any water).</summary>
    public List<string>? SceneIds { get; init; }
    public List<FishTableEntry> Entries { get; init; } = [];
    /// <summary>Chance (0..1) a cast catches junk instead of rolling the table.</summary>
    public double JunkChance { get; init; } = 0.15;
    public string? JunkItemId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
