using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/crafting.ts.
// Crafting & machines (M4a) — the "central system".

public sealed record RecipeIngredient
{
    public string ItemId { get; init; } = "";
    /// <summary>int, positive.</summary>
    public double Quantity { get; init; }
}

/// <summary>TS <c>RecipeUnlockSchema.skill</c> (inline object).</summary>
public sealed record RecipeSkillRequirement
{
    public string Skill { get; init; } = "";
    /// <summary>int.</summary>
    public double Level { get; init; }
}

public sealed record RecipeUnlock
{
    /// <summary>Requires a skill at a minimum level.</summary>
    public RecipeSkillRequirement? Skill { get; init; }
    /// <summary>Requires a completed quest.</summary>
    public string? QuestId { get; init; }
    /// <summary>Only craftable in these seasons.</summary>
    public List<string>? Seasons { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record RecipeDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<RecipeIngredient> Inputs { get; init; } = [];
    public List<RecipeIngredient> Outputs { get; init; } = [];
    /// <summary>In-game minutes a machine needs; 0 = instant hand-craft. nonnegative.</summary>
    public double ProcessingMinutes { get; init; } = 0;
    /// <summary>Machine type required; absent = craftable by hand.</summary>
    public string? MachineTypeId { get; init; }
    /// <summary>Freeform crafting discipline for UI grouping ('cooking', 'magic', 'carpentry', …).</summary>
    public string Category { get; init; } = "crafting";
    /// <summary>Hand-craftable only while near a machine providing this station category.</summary>
    public string? RequiresStationCategory { get; init; }
    public RecipeUnlock? Unlock { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record MachineTypeDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public VisualRef? Visual { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Renderer hint.</summary>
    public string Color { get; init; } = "#9a7b4f";
    /// <summary>Item consumed to place this machine (crafted or bought).</summary>
    public string? ItemId { get; init; }
    public bool BlocksMovement { get; init; } = true;
    /// <summary>Station categories a placed machine of this type provides to nearby hand-crafting (e.g. a Kitchen provides 'cooking').</summary>
    public List<string> StationCategories { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>TS <c>TileMachineSchema.processing</c> (inline object): an in-flight machine job.</summary>
public sealed record MachineProcessing
{
    public string RecipeId { get; init; } = "";
    public double CompletesAtMinute { get; init; }
}

/// <summary>Live machine instance on a tile.</summary>
public sealed record TileMachine
{
    public string TypeId { get; init; } = "";
    /// <summary>In-flight job: recipe + absolute game-minute it completes.</summary>
    public MachineProcessing? Processing { get; init; }
    /// <summary>Finished output awaiting collection.</summary>
    public List<RecipeIngredient>? Output { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
