namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/primitives.ts.
//
// Primitive enums shared by content, project and save schemas. zod string
// enums stay plain `string`s in C# (content is open-ended and JSON must
// round-trip); each enum gets a static class of constants plus `All`.

/// <summary>TS <c>TileTypeSchema</c>.</summary>
public static class TileTypes
{
    public const string Grass = "grass";
    public const string Soil = "soil";
    public const string Water = "water";
    public const string Path = "path";
    public const string Wall = "wall";
    public const string Door = "door";
    public const string Floor = "floor";

    public static readonly IReadOnlyList<string> All = [Grass, Soil, Water, Path, Wall, Door, Floor];
}

/// <summary>TS <c>DirectionSchema</c>.</summary>
public static class Directions
{
    public const string Up = "up";
    public const string Down = "down";
    public const string Left = "left";
    public const string Right = "right";

    public static readonly IReadOnlyList<string> All = [Up, Down, Left, Right];
}

/// <summary>TS <c>ItemTypeSchema</c>.</summary>
public static class ItemTypes
{
    public const string Seed = "seed";
    public const string Tool = "tool";
    public const string Crop = "crop";
    public const string Gift = "gift";
    public const string Quest = "quest";
    public const string Fertilizer = "fertilizer";
    public const string Material = "material";
    public const string Fish = "fish";

    public static readonly IReadOnlyList<string> All = [Seed, Tool, Crop, Gift, Quest, Fertilizer, Material, Fish];
}

/// <summary>TS <c>ToolTypeSchema</c>.</summary>
public static class ToolTypes
{
    public const string WateringCan = "watering-can";
    public const string Hoe = "hoe";
    public const string Axe = "axe";
    public const string Pickaxe = "pickaxe";
    public const string Scythe = "scythe";
    public const string FishingRod = "fishing-rod";

    public static readonly IReadOnlyList<string> All = [WateringCan, Hoe, Axe, Pickaxe, Scythe, FishingRod];
}

/// <summary>TS <c>EditorModeSchema</c>.</summary>
public static class EditorModes
{
    public const string Tiles = "tiles";
    public const string Npcs = "npcs";
    public const string Items = "items";
    public const string Events = "events";
    public const string Play = "play";
    public const string Quests = "quests";

    public static readonly IReadOnlyList<string> All = [Tiles, Npcs, Items, Events, Play, Quests];
}

/// <summary>TS <c>QuestStatusSchema</c>.</summary>
public static class QuestStatuses
{
    public const string NotStarted = "not-started";
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [NotStarted, Active, Completed, Failed];
}

/// <summary>TS <c>QuestObjectiveTypeSchema</c>.</summary>
public static class QuestObjectiveTypes
{
    public const string Collect = "collect";
    public const string Harvest = "harvest";
    public const string Talk = "talk";
    public const string Visit = "visit";
    public const string Craft = "craft";
    public const string Gift = "gift";

    public static readonly IReadOnlyList<string> All = [Collect, Harvest, Talk, Visit, Craft, Gift];
}

/// <summary>TS <c>SoilStateSchema</c>.</summary>
public static class SoilStates
{
    public const string Dry = "dry";
    public const string Watered = "watered";
    public const string Fertilized = "fertilized";
    public const string Tilled = "tilled";

    public static readonly IReadOnlyList<string> All = [Dry, Watered, Fertilized, Tilled];
}

/// <summary>TS <c>CropQualitySchema</c>.</summary>
public static class CropQualities
{
    public const string Normal = "normal";
    public const string Silver = "silver";
    public const string Gold = "gold";
    public const string Iridium = "iridium";

    public static readonly IReadOnlyList<string> All = [Normal, Silver, Gold, Iridium];
}

/// <summary>
/// TS <c>CropMutationSchema</c>: one of these or <c>null</c> (so C# fields
/// holding it are <c>string?</c> written as <c>null</c>).
/// </summary>
public static class CropMutations
{
    public const string Giant = "giant";
    public const string Golden = "golden";
    public const string Ancient = "ancient";

    public static readonly IReadOnlyList<string> All = [Giant, Golden, Ancient];
}

public static class PrimitivesSchema
{
    /// <summary>
    /// Seasons are open strings (creator-defined calendars, M9) so custom season
    /// ids validate the same way custom crops do. <c>CLASSIC_SEASONS</c> enumerates
    /// the four seasons the built-in calendar ships with, for defaults/editors.
    /// </summary>
    public static readonly IReadOnlyList<string> ClassicSeasons = ["spring", "summer", "fall", "winter"];

    /// <summary>
    /// Crop identifiers are open strings so that custom/modded crops share the
    /// same pipeline as built-in ones. <c>BUILTIN_CROP_IDS</c> enumerates the crops
    /// shipped with the default content pack.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltinCropIds =
    [
        "wheat", "corn", "tomato", "carrot", "potato",
        "strawberry", "pumpkin", "cauliflower", "blueberry",
    ];
}
