using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/project.ts.

public sealed record GameProject
{
    /// <summary>int.</summary>
    public double SchemaVersion { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public List<Scene> Scenes { get; init; } = [];
    public List<Npc> Npcs { get; init; } = [];
    public List<Item> Items { get; init; } = [];
    public List<GameEvent> Events { get; init; } = [];
    public List<Dialogue> Dialogues { get; init; } = [];
    public List<Quest> Quests { get; init; } = [];
    public Player Player { get; init; } = new();
    public OrderedDictionary<string, bool> EventFlags { get; init; } = [];
    public string StartSceneId { get; init; } = "";
    /// <summary>One of <see cref="EditorModes"/>.</summary>
    public string Mode { get; init; } = "";
    /// <summary>One of <see cref="TileTypes"/>.</summary>
    public string SelectedTileType { get; init; } = "";
    public VisualRef? SelectedTileVisual { get; init; }
    [JsonPropertyName("selectedNPCId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SelectedNpcId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SelectedItemId { get; init; }
    public double CurrentTime { get; init; }
    public List<CustomAsset> CustomAssets { get; init; } = [];
    public List<CustomCropDefinition>? CustomCrops { get; init; }
    public string? PlayerCustomImage { get; init; }
    public VisualRef? PlayerVisual { get; init; }
    public GraphicsSettings? Graphics { get; init; }
    public List<GamePanel>? GamePanels { get; init; }
    public string CurrentSeason { get; init; } = "";
    public double CurrentDay { get; init; }
    /// <summary>Minute-of-day of the game clock (v4+).</summary>
    public double CurrentTimeMinutes { get; init; }
    /// <summary>int.</summary>
    public double CurrentYear { get; init; }
    public double GameStartTime { get; init; }
    public List<ShopDefinition> Shops { get; init; } = [];
    public List<NodeTypeDefinition> NodeTypes { get; init; } = [];
    public ProjectSettings Settings { get; init; } = new();
    public List<RecipeDefinition> Recipes { get; init; } = [];
    public List<MachineTypeDefinition> MachineTypes { get; init; } = [];
    public WeatherConfig Weather { get; init; } = new();
    public List<AnimalSpeciesDefinition> AnimalSpecies { get; init; } = [];
    public List<AnimalState> Animals { get; init; } = [];
    public List<FishTable> FishTables { get; init; } = [];
    public MineConfig Mine { get; init; } = new();
    public List<ActionDef> Actions { get; init; } = [];
    public List<MinigameDef> Minigames { get; init; } = [];
    /// <summary>Installed content packs (M5). Array order = load order.</summary>
    public List<PackInstallation> ContentPacks { get; init; } = [];
    /// <summary>Serialized PRNG state so play sessions resume deterministically (additive, optional).</summary>
    public RngState? RngState { get; init; }
    // Runtime state mirrored by applyStateToProject. These used to ride the
    // .passthrough() escape hatch and reach the simulation unvalidated.
    /// <summary>Today's weather id (mirrors GameState.clock.weatherId).</summary>
    public string? CurrentWeatherId { get; init; }
    /// <summary>Friendship &amp; gifting state per NPC (mirrors GameState.social).</summary>
    public OrderedDictionary<string, NpcSocialState>? SocialState { get; init; }
    /// <summary>Deepest mine floor reached (mirrors GameState.mine.deepestFloor). int, nonnegative.</summary>
    public double? MineDeepestFloor { get; init; }
    /// <summary>Items whose owning pack is missing/disabled (mirrors GameState.quarantinedItems).</summary>
    public List<InventorySlot>? QuarantinedItems { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record ExportedGame
{
    /// <summary>int.</summary>
    public double? SchemaVersion { get; init; }
    public string Version { get; init; } = "";
    public string Name { get; init; } = "";
    public List<Scene> Scenes { get; init; } = [];
    public List<Npc> Npcs { get; init; } = [];
    public List<Item> Items { get; init; } = [];
    public List<GameEvent> Events { get; init; } = [];
    public List<Dialogue> Dialogues { get; init; } = [];
    public List<Quest> Quests { get; init; } = [];
    public string StartSceneId { get; init; } = "";
    public List<CustomAsset> CustomAssets { get; init; } = [];
    public List<CustomCropDefinition>? CustomCrops { get; init; }
    public string? PlayerCustomImage { get; init; }
    public VisualRef? PlayerVisual { get; init; }
    public GraphicsSettings? Graphics { get; init; }
    public List<GamePanel>? GamePanels { get; init; }
    public string CurrentSeason { get; init; } = "";
    public double CurrentDay { get; init; }
    public double CurrentTimeMinutes { get; init; }
    /// <summary>int.</summary>
    public double CurrentYear { get; init; }
    public double GameStartTime { get; init; }
    public List<ShopDefinition> Shops { get; init; } = [];
    public List<NodeTypeDefinition> NodeTypes { get; init; } = [];
    public ProjectSettings Settings { get; init; } = new();
    public List<RecipeDefinition> Recipes { get; init; } = [];
    public List<MachineTypeDefinition> MachineTypes { get; init; } = [];
    public WeatherConfig Weather { get; init; } = new();
    public List<AnimalSpeciesDefinition> AnimalSpecies { get; init; } = [];
    public List<AnimalState> Animals { get; init; } = [];
    public List<FishTable> FishTables { get; init; } = [];
    public MineConfig Mine { get; init; } = new();
    public List<ActionDef> Actions { get; init; } = [];
    public List<MinigameDef> Minigames { get; init; } = [];
    public List<PackInstallation> ContentPacks { get; init; } = [];
    /// <summary>Player start state (M6): included so exported games start identically.</summary>
    public Player? Player { get; init; }
    // Runtime state mirrored into saves (exported-game saves are projected
    // through applyStateToProject); see GameProject.
    public string? CurrentWeatherId { get; init; }
    public OrderedDictionary<string, NpcSocialState>? SocialState { get; init; }
    /// <summary>int, nonnegative.</summary>
    public double? MineDeepestFloor { get; init; }
    public List<InventorySlot>? QuarantinedItems { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public static class ProjectSchema
{
    /// <summary>
    /// The project schema version. Bump when the persisted shape changes and add
    /// a migration plus a fixture in tests/.
    ///
    /// v1 — original prototype (tiles without layer fields)
    /// v2 — layered tiles (background/overlay/object)
    /// v3 — explicit schemaVersion + guaranteed presence of customAssets,
    ///      season/day fields, quests and player pixel/quest fields (replaces
    ///      the ad-hoc backfill effects that lived in App.tsx)
    /// v4 — day-based game time (M2): crop growthDays/plantedOnDay/daysGrown,
    ///      shops, gathering node types, project settings (energy/time),
    ///      player energy, currentTimeMinutes
    /// v5 — events v2 (M3): trigger + combinable conditions + sequenced
    ///      outcomes; NPC schedules; lockable transitions; quest availability
    /// v6 — simulation depth (M4): recipes, machine types, weather config,
    ///      animal species + animals, fish tables, mine config, gift tastes,
    ///      player skills
    /// v7 — content packs (M5): installed packs (with load order + enable flags)
    ///      travel inside the project
    /// </summary>
    public const double CurrentProjectSchemaVersion = 8;
}
