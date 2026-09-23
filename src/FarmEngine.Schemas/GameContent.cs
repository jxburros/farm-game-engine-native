namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/game-content.ts.
//
// Content family — everything that defines a game and is immutable during
// play. Content comes from the built-in pack + the project's custom content
// (+ enabled mods from M5), merged and validated at load time.

public sealed record GameContent
{
    /// <summary>int.</summary>
    public double ContentVersion { get; init; }
    /// <summary>Crop definitions by id (built-in merged with project custom crops).</summary>
    public OrderedDictionary<string, CropDefinition> Crops { get; init; } = [];
    public List<Item> Items { get; init; } = [];
    public List<Npc> Npcs { get; init; } = [];
    public List<Dialogue> Dialogues { get; init; } = [];
    public List<Quest> Quests { get; init; } = [];
    public List<GameEvent> Events { get; init; } = [];
    public List<ShopDefinition> Shops { get; init; } = [];
    public List<NodeTypeDefinition> NodeTypes { get; init; } = [];
    public ProjectSettings Settings { get; init; } = new();
    public List<RecipeDefinition> Recipes { get; init; } = [];
    public List<MachineTypeDefinition> MachineTypes { get; init; } = [];
    public WeatherConfig Weather { get; init; } = new();
    public List<AnimalSpeciesDefinition> AnimalSpecies { get; init; } = [];
    public List<FishTable> FishTables { get; init; } = [];
    public MineConfig Mine { get; init; } = new();
    /// <summary>Creator-defined actions (extensibility layer).</summary>
    public List<ActionDef> Actions { get; init; } = [];
    /// <summary>Declared minigames (extensibility layer).</summary>
    public List<MinigameDef> Minigames { get; init; } = [];
    /// <summary>Authored initial scenes — the template the world state is created from.</summary>
    public List<Scene> Scenes { get; init; } = [];
    public string StartSceneId { get; init; } = "";
}

public static class GameContentSchema
{
    public const double CurrentContentVersion = 1;
}
