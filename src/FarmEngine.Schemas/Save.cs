using System.Text.Json;
using System.Text.Json.Serialization;
using FarmEngine.Json;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/save.ts (schemas, constants and pure
// helpers; SAVE_MIGRATIONS / migrateGameState are ported separately).
//
// Save-data family (`GameState`) — a player's running game, fully
// serializable, versioned independently from project and content data.
//
// State is plain data: no classes, no functions. All mutation flows through
// engine commands and ticks (`FarmEngine.Core`).
//
// v1 — M1 extraction (wall-clock mirror for legacy growth)
// v2 — M2 game clock: minute-of-day time, day/season/year calendar,
//      player energy, shop session + daily purchase tracking
// v3 — M4 simulation depth: weather, skills, social state, animals,
//      mine progress
// v4 — free movement: fractional player position (tile units, player
//      center) + held movement intent

/// <summary>Serialized PRNG state — deterministic resume is a core guarantee.</summary>
public sealed record RngState
{
    /// <summary>Always <c>"xoshiro128ss"</c> (zod literal).</summary>
    public string Algorithm { get; init; } = "xoshiro128ss";
    /// <summary>The four 32-bit xoshiro128** state words (TS <c>[int, int, int, int]</c>, values <c>&gt;&gt;&gt; 0</c>).</summary>
    public uint[] S { get; init; } = new uint[4];
}

public sealed record ClockState
{
    /// <summary>Fixed-timestep tick counter since game start. int.</summary>
    public double Tick { get; init; }
    /// <summary>Minute-of-day of the game clock (e.g. 360 = 6:00).</summary>
    public double TimeMinutes { get; init; }
    /// <summary>Absolute in-game day, 1-based, monotonically increasing. int.</summary>
    public double Day { get; init; }
    public string Season { get; init; } = "";
    /// <summary>int.</summary>
    public double Year { get; init; }
    /// <summary>Today's weather (rolled at day start, M4).</summary>
    public string WeatherId { get; init; } = "sun";
}

public sealed record SkillState
{
    public double Xp { get; init; } = 0;
    /// <summary>int.</summary>
    public double Level { get; init; } = 0;
}

/// <summary>
/// Held movement input (v4 free movement). Player intent enters the command
/// log as <c>setMoveIntent</c>; the engine integrates position from it every tick,
/// so replays stay deterministic without logging per-tick positions.
/// </summary>
public sealed record MoveIntent
{
    /// <summary>int, -1..1.</summary>
    public double Dx { get; init; }
    /// <summary>int, -1..1.</summary>
    public double Dy { get; init; }
}

public sealed record PlayerState
{
    /// <summary>
    /// Player position in tile units — the CENTER of the player's collision
    /// box. Fractional since v4 (free movement); the occupied tile is
    /// <c>Math.floor(x/y)</c>. Tiles are layout/terrain, not a movement grid.
    /// </summary>
    public double X { get; init; }
    public double Y { get; init; }
    public MoveIntent MoveIntent { get; init; } = new() { Dx = 0, Dy = 0 };
    /// <summary>One of <see cref="Directions"/>.</summary>
    public string Direction { get; init; } = "";
    public string SceneId { get; init; } = "";
    public List<InventorySlot> Inventory { get; init; } = [];
    public double MaxInventorySize { get; init; }
    public double Money { get; init; }
    public double Energy { get; init; }
    public double MaxEnergy { get; init; }
    /// <summary>Per-category skill XP/levels (M4g).</summary>
    public OrderedDictionary<string, SkillState> Skills { get; init; } = [];
    public List<string> ActiveQuests { get; init; } = [];
    public List<string> CompletedQuests { get; init; } = [];
    public string? EquippedTool { get; init; }
}

public sealed record NpcState
{
    /// <summary>int.</summary>
    public double X { get; init; }
    /// <summary>int.</summary>
    public double Y { get; init; }
    public string SceneId { get; init; } = "";
    /// <summary>Remaining A* path steps toward the current destination (M3 schedules).</summary>
    public List<GridPoint>? Path { get; init; }
    /// <summary>Index of the patrol waypoint the NPC is heading to. int.</summary>
    public double? PatrolIndex { get; init; }
}

public sealed record QuestObjectiveProgress
{
    public double Progress { get; init; }
    public bool Completed { get; init; }
}

public sealed record QuestProgress
{
    /// <summary>One of <see cref="QuestStatuses"/>.</summary>
    public string Status { get; init; } = "";
    public OrderedDictionary<string, QuestObjectiveProgress> Objectives { get; init; } = [];
}

public sealed record DialogueState
{
    public string NpcId { get; init; } = "";
    public string DialogueId { get; init; } = "";
}

/// <summary>An open shop session.</summary>
public sealed record ShopSession
{
    public string ShopId { get; init; } = "";
}

/// <summary>TS <c>GameStateSchema.meta.packs</c> item (inline object).</summary>
public sealed record SavePackRef
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
}

/// <summary>TS <c>GameStateSchema.meta</c> (inline object).</summary>
public sealed record GameStateMeta
{
    /// <summary>int.</summary>
    public double SaveVersion { get; init; }
    public string EngineSeed { get; init; } = "";
    /// <summary>Packs this save was created with ("this save uses packs X, Y").</summary>
    public List<SavePackRef> Packs { get; init; } = [];
}

/// <summary>TS <c>GameStateSchema.world</c> (inline object).</summary>
public sealed record WorldState
{
    /// <summary>Live scene state during play (tiles, crops, nodes, dropped items).</summary>
    public List<Scene> Scenes { get; init; } = [];
}

/// <summary>TS <c>GameStateSchema.mine</c> (inline object): mining progress (M4f).</summary>
public sealed record MineProgress
{
    /// <summary>int.</summary>
    public double DeepestFloor { get; init; } = 0;
    /// <summary>int.</summary>
    public double CurrentFloor { get; init; } = 0;
}

public sealed record GameState
{
    public GameStateMeta Meta { get; init; } = new();
    public ClockState Clock { get; init; } = new();
    public WorldState World { get; init; } = new();
    public PlayerState Player { get; init; } = new();
    public OrderedDictionary<string, NpcState> Npcs { get; init; } = [];
    public OrderedDictionary<string, QuestProgress> Quests { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DialogueState? Dialogue { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ShopSession? Shop { get; init; }
    /// <summary>Open minigame session (modal, like dialogue/shop).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public MinigameSession? Minigame { get; init; } = null;
    /// <summary>Per-shop, per-item units bought today (daily stock limits); reset nightly.</summary>
    public OrderedDictionary<string, OrderedDictionary<string, double>> ShopPurchasesToday { get; init; } = [];
    /// <summary>Friendship &amp; gifting state per NPC (M4d).</summary>
    public OrderedDictionary<string, NpcSocialState> Social { get; init; } = [];
    /// <summary>Live animals (M4c).</summary>
    public List<AnimalState> Animals { get; init; } = [];
    /// <summary>Mining progress (M4f): deepest floor reached + current floor.</summary>
    public MineProgress Mine { get; init; } = new();
    /// <summary>Values are <c>boolean | number | string</c> (see <see cref="Js.Value(bool)"/>, <see cref="Js.Truthy"/>).</summary>
    public OrderedDictionary<string, JsonElement> Flags { get; init; } = [];
    /// <summary>
    /// Inventory items whose owning pack is missing/disabled — quarantined, not
    /// dropped; they return to the inventory when the pack comes back (M5).
    /// </summary>
    public List<InventorySlot> QuarantinedItems { get; init; } = [];
    public RngState Rng { get; init; } = new();
}

/// <summary>TS <c>SaveMigrationResult</c> (returned by the save migration pipeline).</summary>
public sealed record SaveMigrationResult
{
    public bool Ok { get; init; }
    public GameState? Data { get; init; }
    public double FromVersion { get; init; }
    public bool Migrated { get; init; }
    public List<string> Errors { get; init; } = [];
}

public static class SaveSchema
{
    public const double CurrentSaveVersion = 4;

    /// <summary>TS <c>SKILL_NAMES</c>.</summary>
    public static readonly IReadOnlyList<string> SkillNames = ["farming", "foraging", "fishing", "mining", "social"];

    /// <summary>Tile index → tile center; already-fractional coordinates pass through.</summary>
    public static double CenterCoordinate(double value) => Js.IsInteger(value) ? value + 0.5 : value;
}
