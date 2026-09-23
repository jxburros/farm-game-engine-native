using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/actors.ts.

public sealed record DialogueOption
{
    public string Text { get; init; } = "";
    public string? NextDialogueId { get; init; }
    public string? GiveItem { get; init; }
    public double? GiveItemQuantity { get; init; }
    public double? TakeMoney { get; init; }
    public double? GiveMoney { get; init; }
    public string? EventFlag { get; init; }
    public string? RequiresItem { get; init; }
    public string? RequiresFlag { get; init; }
    /// <summary>Choosing this option closes the dialogue and opens the given shop (M2).</summary>
    public string? OpenShopId { get; init; }
    /// <summary>Choosing this option offers/starts the given quest (M3 quest-giver binding).</summary>
    public string? OfferQuestId { get; init; }
    /// <summary>Option only shown at/above this friendship (M4 heart-gated dialogue).</summary>
    public double? RequiresFriendship { get; init; }
    /// <summary>Choosing this option performs a creator-defined action.</summary>
    public string? ActionId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record Dialogue
{
    public string Id { get; init; } = "";
    public string NpcId { get; init; } = "";
    public string Text { get; init; } = "";
    public List<DialogueOption> Options { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A scheduled destination: at <c>minute</c> (of day), head to (sceneId, x, y).</summary>
public sealed record NpcScheduleEntry
{
    public double Minute { get; init; }
    public string SceneId { get; init; } = "";
    /// <summary>int.</summary>
    public double X { get; init; }
    /// <summary>int.</summary>
    public double Y { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// An integer tile coordinate: TS inline <c>{ x: int, y: int }</c> objects in
/// <c>NPCSchema.patrolPoints</c> and <c>NpcStateSchema.path</c>.
/// </summary>
public sealed record GridPoint
{
    /// <summary>int.</summary>
    public double X { get; init; }
    /// <summary>int.</summary>
    public double Y { get; init; }
}

/// <summary>TS <c>NPCSchema.movePattern</c> enum.</summary>
public static class NpcMovePatterns
{
    public const string Stationary = "stationary";
    public const string Wander = "wander";
    public const string Patrol = "patrol";

    public static readonly IReadOnlyList<string> All = [Stationary, Wander, Patrol];
}

/// <summary>TS <c>NPCSchema.birthday</c> (inline object).</summary>
public sealed record NpcBirthday
{
    /// <summary>One of the classic seasons ('spring' | 'summer' | 'fall' | 'winter').</summary>
    public string Season { get; init; } = "";
    /// <summary>int.</summary>
    public double Day { get; init; }
}

/// <summary>TS <c>NPC</c> (<c>NPCSchema</c>).</summary>
public sealed record Npc
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public VisualRef? Visual { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public string SceneId { get; init; } = "";
    public List<Dialogue> Dialogue { get; init; } = [];
    public bool CanMove { get; init; }
    /// <summary>One of <see cref="NpcMovePatterns"/>.</summary>
    public string? MovePattern { get; init; }
    /// <summary>Max tiles from home for the wander pattern (default 3). int, positive.</summary>
    public double? WanderRadius { get; init; }
    /// <summary>Waypoints for the patrol pattern (visited in order, looping).</summary>
    public List<GridPoint>? PatrolPoints { get; init; }
    /// <summary>Time-based schedule (M3): sorted by minute; latest passed entry wins.</summary>
    public List<NpcScheduleEntry>? Schedule { get; init; }
    /// <summary>Gift preferences (M4 social); unlisted items are neutral.</summary>
    public GiftTastes? GiftTastes { get; init; }
    /// <summary>Birthday: day-of-season (1-28) within birthSeason, doubles gift effects.</summary>
    public NpcBirthday? Birthday { get; init; }
    public string Appearance { get; init; } = "";
    public string? CustomImage { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>The project's player start/record (TS <c>PlayerSchema</c>); runtime uses <see cref="PlayerState"/>.</summary>
public sealed record Player
{
    public double X { get; init; }
    public double Y { get; init; }
    /// <summary>One of <see cref="Directions"/>.</summary>
    public string Direction { get; init; } = "";
    public string SceneId { get; init; } = "";
    public List<InventorySlot> Inventory { get; init; } = [];
    public double MaxInventorySize { get; init; }
    public double Money { get; init; }
    public double? Energy { get; init; }
    public double? MaxEnergy { get; init; }
    /// <summary>Per-category skill XP/levels (M4g); mirrored from GameState on save.</summary>
    public OrderedDictionary<string, SkillState>? Skills { get; init; }
    public List<string> ActiveQuests { get; init; } = [];
    public List<string> CompletedQuests { get; init; } = [];
    public string? EquippedTool { get; init; }
    /// <summary>Pixel X position for smooth movement interpolation</summary>
    public double PixelX { get; init; }
    /// <summary>Pixel Y position for smooth movement interpolation</summary>
    public double PixelY { get; init; }
    /// <summary>Target pixel X for interpolation</summary>
    public double TargetX { get; init; }
    /// <summary>Target pixel Y for interpolation</summary>
    public double TargetY { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
