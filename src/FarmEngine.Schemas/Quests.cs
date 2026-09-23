using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/quests.ts.

public sealed record QuestObjective
{
    public string Id { get; init; } = "";
    /// <summary>One of <see cref="QuestObjectiveTypes"/>.</summary>
    public string Type { get; init; } = "";
    public string Description { get; init; } = "";
    public string? TargetItemId { get; init; }
    public double? TargetItemQuantity { get; init; }
    public string? TargetCropType { get; init; }
    public double? TargetCropQuantity { get; init; }
    [JsonPropertyName("targetNPCId")]
    public string? TargetNpcId { get; init; }
    public string? TargetSceneId { get; init; }
    public bool Completed { get; init; }
    public double Progress { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>TS <c>QuestRewardsSchema.items</c> item (inline object).</summary>
public sealed record QuestRewardItem
{
    public string ItemId { get; init; } = "";
    public double Quantity { get; init; }
}

public sealed record QuestRewards
{
    public double? Money { get; init; }
    public List<QuestRewardItem>? Items { get; init; }
    public double? Experience { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record Quest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Giver { get; init; }
    /// <summary>One of <see cref="QuestStatuses"/>.</summary>
    public string Status { get; init; } = "";
    public List<QuestObjective> Objectives { get; init; } = [];
    public QuestRewards Rewards { get; init; } = new();
    public List<string>? Prerequisites { get; init; }
    public bool? AutoStart { get; init; }
    public bool? Repeatable { get; init; }
    /// <summary>Seasonal availability (M3): quest only offered/auto-started in these seasons.</summary>
    public List<string>? AvailableSeasons { get; init; }
    /// <summary>Absolute-day window (M3): offered from/until these days (inclusive).</summary>
    public double? AvailableFromDay { get; init; }
    public double? AvailableToDay { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
