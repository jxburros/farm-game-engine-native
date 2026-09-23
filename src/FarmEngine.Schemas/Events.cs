using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/events.ts.
//
// Event & trigger system (M3). Events belong to a scene (or '' = global),
// fire on a trigger kind, gate on combinable conditions, and run sequenced
// outcomes. Fire-once semantics use an auto-managed flag; repeatable is
// opt-in. Evaluation order is deterministic (content order).

/// <summary>TS <c>EventConditionSchema</c> — discriminated union on <c>type</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EnterTileCondition), "enterTile")]
[JsonDerivedType(typeof(InteractTileCondition), "interactTile")]
[JsonDerivedType(typeof(HasItemCondition), "hasItem")]
[JsonDerivedType(typeof(InventorySpaceCondition), "inventorySpace")]
[JsonDerivedType(typeof(FlagCondition), "flag")]
[JsonDerivedType(typeof(DayRangeCondition), "dayRange")]
[JsonDerivedType(typeof(SeasonCondition), "season")]
[JsonDerivedType(typeof(YearRangeCondition), "yearRange")]
[JsonDerivedType(typeof(TimeOfDayCondition), "timeOfDay")]
[JsonDerivedType(typeof(QuestStatusCondition), "questStatus")]
[JsonDerivedType(typeof(FriendshipCondition), "friendship")]
[JsonDerivedType(typeof(WeatherCondition), "weather")]
[JsonDerivedType(typeof(FestivalIdCondition), "festivalId")]
public abstract record EventCondition
{
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>Player entered a tile (or region when x2/y2 present).</summary>
public sealed record EnterTileCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "enterTile";
    public double X { get; init; }
    public double Y { get; init; }
    public double? X2 { get; init; }
    public double? Y2 { get; init; }
}

/// <summary>Player interacted while facing a tile (or region).</summary>
public sealed record InteractTileCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "interactTile";
    public double X { get; init; }
    public double Y { get; init; }
    public double? X2 { get; init; }
    public double? Y2 { get; init; }
}

public sealed record HasItemCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "hasItem";
    public string ItemId { get; init; } = "";
    public double Quantity { get; init; } = 1;
}

public sealed record InventorySpaceCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "inventorySpace";
    public string ItemId { get; init; } = "";
    /// <summary>int, positive.</summary>
    public double Quantity { get; init; } = 1;
}

public sealed record FlagCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "flag";
    public string Flag { get; init; } = "";
    public bool Value { get; init; } = true;
}

public sealed record DayRangeCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "dayRange";
    public double? MinDay { get; init; }
    public double? MaxDay { get; init; }
}

public sealed record SeasonCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "season";
    public List<string> Seasons { get; init; } = [];
}

public sealed record YearRangeCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "yearRange";
    public double? MinYear { get; init; }
    public double? MaxYear { get; init; }
}

public sealed record TimeOfDayCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "timeOfDay";
    public double MinMinute { get; init; }
    public double MaxMinute { get; init; }
}

public sealed record QuestStatusCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "questStatus";
    public string QuestId { get; init; } = "";
    /// <summary>One of <see cref="QuestStatuses"/>.</summary>
    public string Status { get; init; } = "";
}

/// <summary>Friendship threshold with an NPC (M4 heart events).</summary>
public sealed record FriendshipCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "friendship";
    public string NpcId { get; init; } = "";
    public double Min { get; init; }
}

/// <summary>Current weather (M4).</summary>
public sealed record WeatherCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "weather";
    public List<string> WeatherIds { get; init; } = [];
}

/// <summary>Today is the named festival (M9 calendar).</summary>
public sealed record FestivalIdCondition : EventCondition
{
    [JsonIgnore]
    public override string Type => "festivalId";
    public string FestivalId { get; init; } = "";
}

/// <summary>TS <c>EventOutcomeSchema.type</c> enum.</summary>
public static class EventOutcomeTypes
{
    public const string Message = "message";
    public const string ModifyFriendship = "modifyFriendship";
    public const string ModifyEnergy = "modifyEnergy";
    public const string WaterArea = "waterArea";
    public const string GiveItem = "giveItem";
    public const string TakeItem = "takeItem";
    public const string GiveMoney = "giveMoney";
    public const string TakeMoney = "takeMoney";
    public const string SetFlag = "setFlag";
    public const string ClearFlag = "clearFlag";
    public const string StartQuest = "startQuest";
    public const string CompleteQuest = "completeQuest";
    public const string SpawnNpc = "spawnNPC";
    public const string RemoveNpc = "removeNPC";
    public const string ChangeTile = "changeTile";
    public const string WarpPlayer = "warpPlayer";
    public const string StartDialogue = "startDialogue";
    public const string LockTransition = "lockTransition";
    public const string UnlockTransition = "unlockTransition";
    public const string PlaySound = "playSound";
    /// <summary>Run a creator-defined action (extensibility layer).</summary>
    public const string PerformAction = "performAction";
    /// <summary>Open a declared minigame; its score resolves via the command log.</summary>
    public const string StartMinigame = "startMinigame";
    /// <summary>legacy (pre-v5), still honored by the migration</summary>
    public const string UnlockScene = "unlockScene";

    public static readonly IReadOnlyList<string> All =
    [
        Message,
        ModifyFriendship, ModifyEnergy, WaterArea,
        GiveItem, TakeItem,
        GiveMoney, TakeMoney,
        SetFlag, ClearFlag,
        StartQuest, CompleteQuest,
        SpawnNpc, RemoveNpc,
        ChangeTile,
        WarpPlayer,
        StartDialogue,
        LockTransition, UnlockTransition,
        PlaySound,
        PerformAction,
        StartMinigame,
        UnlockScene,
    ];
}

/// <summary>
/// A single event/action outcome. Not a discriminated union: one flat object
/// whose <c>type</c> selects which optional fields apply.
/// </summary>
public sealed record EventOutcome
{
    /// <summary>One of <see cref="EventOutcomeTypes"/>.</summary>
    public string Type { get; init; } = "";
    public string? Message { get; init; }
    public string? ItemId { get; init; }
    public double? ItemQuantity { get; init; }
    /// <summary>finite.</summary>
    public double? Amount { get; init; }
    /// <summary>int, 0..10.</summary>
    public double? Radius { get; init; }
    public string? FlagName { get; init; }
    public string? QuestId { get; init; }
    public string? NpcId { get; init; }
    public string? DialogueId { get; init; }
    public double? TileX { get; init; }
    public double? TileY { get; init; }
    /// <summary>One of <see cref="TileTypes"/>.</summary>
    public string? NewTileType { get; init; }
    public string? SceneId { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public string? SoundId { get; init; }
    public string? ActionId { get; init; }
    public string? MinigameId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>TS <c>EventTriggerSchema</c>.</summary>
public static class EventTriggers
{
    public const string Enter = "enter";
    public const string Interact = "interact";
    public const string Tick = "tick";

    public static readonly IReadOnlyList<string> All = [Enter, Interact, Tick];
}

public sealed record GameEvent
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Scene the event lives in; empty string = evaluated in every scene.</summary>
    public string SceneId { get; init; } = "";
    /// <summary>One of <see cref="EventTriggers"/>.</summary>
    public string Trigger { get; init; } = "";
    public List<EventCondition> Conditions { get; init; } = [];
    public List<EventOutcome> Outcomes { get; init; } = [];
    public bool Active { get; init; }
    public bool Repeatable { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public static class EventsSchema
{
    /// <summary>Flag used to record that a non-repeatable event has fired.</summary>
    public static string EventFiredFlag(string eventId) => $"event:{eventId}:fired";
}
