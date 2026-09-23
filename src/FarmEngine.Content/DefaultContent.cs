using FarmEngine.Core;
using FarmEngine.Schemas;

namespace FarmEngine.Content;

/// <summary>
/// Port of <c>packages/content-default/src/index.ts</c> — the built-in farming
/// game expressed as a content pack, in the exact format mods use
/// (DEVELOPMENT_PLAN.md M5). This package is the acceptance test for the pack
/// format: if the starter game can't be expressed here, the format is
/// incomplete. Deleting this package leaves a functional empty engine.
///
/// Also hosts the project factories the web app keeps in
/// <c>src/lib/game-helpers.ts</c> (<c>createInitialProject</c>,
/// <c>createDefaultPlayer</c>) and <c>src/lib/projects.ts</c>
/// (<c>createBlankProject</c>). Wall-clock fields (TS <c>Date.now()</c>) are
/// optional parameters so tests can pin them; they default to the current
/// Unix time in milliseconds.
/// </summary>
public static class DefaultContent
{
    /// <summary>TS <c>Date.now()</c>: Unix epoch milliseconds.</summary>
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Scene CreateStarterFarmScene()
    {
        var scene = Tiles.CreateEmptyScene("scene-farm", "Farm", 16, 12);
        var tiles = scene.Tiles;
        var width = (int)scene.Width;
        var height = (int)scene.Height;
        var centerX = (int)Math.Floor(scene.Width / 2);
        var centerY = (int)Math.Floor(scene.Height / 2);

        for (var y = centerY - 2; y <= centerY + 2; y++)
        {
            for (var x = centerX - 3; x <= centerX + 3; x++)
            {
                if (y >= 0 && y < height && x >= 0 && x < width)
                {
                    tiles[y][x] = Tiles.SetTileLayer(tiles[y][x], "soil");
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            tiles[y][0] = Tiles.SetTileLayer(tiles[y][0], "wall");
            tiles[y][width - 1] = Tiles.SetTileLayer(tiles[y][width - 1], "wall");
        }
        for (var x = 0; x < width; x++)
        {
            tiles[0][x] = Tiles.SetTileLayer(tiles[0][x], "wall");
            tiles[height - 1][x] = Tiles.SetTileLayer(tiles[height - 1][x], "wall");
        }
        tiles[height - 1][centerX] = Tiles.SetTileLayer(tiles[height - 1][centerX], "door");

        // Gathering nodes: trees, rocks and weeds around the field edges so every
        // starter tool has a use in the first ten minutes.
        void PlaceNode(int x, int y, string typeId, double health) =>
            tiles[y][x] = tiles[y][x] with { Node = new TileNode { TypeId = typeId, RemainingHealth = health } };
        PlaceNode(2, 2, "node-tree", 4);
        PlaceNode(3, 9, "node-tree", 4);
        PlaceNode(13, 2, "node-rock", 3);
        PlaceNode(13, 9, "node-rock", 3);
        PlaceNode(2, 6, "node-weeds", 1);
        PlaceNode(12, 5, "node-weeds", 1);

        return scene;
    }

    private static (List<Npc> Npcs, List<Dialogue> Dialogues) CreateStarterNpcs()
    {
        var dialogue = new Dialogue
        {
            Id = "dialogue-farmer-greeting",
            NpcId = "npc-farmer",
            Text = "Welcome to the farm! Plant crops in the soil and watch them grow. Come back when you need advice!",
            Options =
            [
                new DialogueOption { Text = "Thanks for the help!" },
                new DialogueOption { Text = "What crops grow best here?", NextDialogueId = "dialogue-farmer-crops" },
            ],
        };
        var dialogue2 = new Dialogue
        {
            Id = "dialogue-farmer-crops",
            NpcId = "npc-farmer",
            Text = "Wheat is the easiest crop to start with. Tomatoes take longer but sell for more!",
            Options = [new DialogueOption { Text = "Got it, thanks!" }],
        };
        var merchantDialogue = new Dialogue
        {
            Id = "dialogue-merchant-greeting",
            NpcId = "npc-merchant",
            Text = "Welcome! I buy crops and sell seeds, tools and fertilizer. I can also repair worn-out tools.",
            Options =
            [
                new DialogueOption { Text = "Let's trade.", OpenShopId = "shop-general" },
                new DialogueOption { Text = "Just passing by." },
            ],
        };

        var farmer = new Npc
        {
            Id = "npc-farmer",
            Name = "Old Farmer",
            X = 3,
            Y = 6,
            SceneId = "scene-farm",
            Dialogue = [dialogue, dialogue2],
            CanMove = false,
            MovePattern = "stationary",
            Appearance = "farmer",
        };
        var merchant = new Npc
        {
            Id = "npc-merchant",
            Name = "Merchant Mia",
            X = 12,
            Y = 3,
            SceneId = "scene-farm",
            Dialogue = [merchantDialogue],
            CanMove = false,
            MovePattern = "stationary",
            Appearance = "merchant",
        };

        return ([farmer, merchant], [dialogue, dialogue2, merchantDialogue]);
    }

    private static List<Quest> CreateStarterQuests() =>
    [
        new Quest
        {
            Id = "quest-first-harvest",
            Name = "First Harvest",
            Description = "Plant and harvest your first crop to learn the basics of farming.",
            Giver = "npc-farmer",
            Status = "not-started",
            Objectives =
            [
                new QuestObjective
                {
                    Id = "obj-harvest-wheat",
                    Type = "harvest",
                    Description = "Harvest 3 wheat",
                    TargetCropType = "wheat",
                    TargetCropQuantity = 3,
                    Completed = false,
                    Progress = 0,
                },
            ],
            Rewards = new QuestRewards { Money = 100, Items = [new QuestRewardItem { ItemId = "seed-tomato", Quantity = 5 }] },
            AutoStart = true,
            Repeatable = false,
        },
        new Quest
        {
            Id = "quest-go-shopping",
            Name = "Supply Run",
            Description = "Meet Merchant Mia — she buys your harvest and sells seeds, tools and fertilizer.",
            Giver = "npc-farmer",
            Status = "not-started",
            Objectives =
            [
                new QuestObjective
                {
                    Id = "obj-talk-merchant",
                    Type = "talk",
                    Description = "Talk to Merchant Mia",
                    TargetNpcId = "npc-merchant",
                    Completed = false,
                    Progress = 0,
                },
            ],
            Rewards = new QuestRewards { Money = 50 },
            Prerequisites = ["quest-first-harvest"],
            AutoStart = true,
            Repeatable = false,
        },
    ];

    private static Item MaterialItem(string id, string name, string description, string type, double maxStack, double value) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Type = type,
        Stackable = true,
        MaxStack = maxStack,
        Value = value,
    };

    /// <summary>
    /// A small crafting showcase (M4a follow-up): two hand-craftable stations
    /// (Kitchen → cooking, Workbench → carpentry) and a couple of recipes for
    /// each, so exported games have more than the smelting/preserves chain to
    /// show off category grouping and station gating. Additive to the built-in
    /// catalog — nothing here touches the starter economy or existing recipes.
    /// </summary>
    private static List<Item> CreateCraftingShowcaseItems() =>
    [
        MaterialItem("machine-kitchen", "Kitchen", "A cooking station — place it, then cook nearby", "material", 9, 180),
        MaterialItem("machine-workbench", "Workbench", "A carpentry station — place it, then build nearby", "material", 9, 150),
        MaterialItem("food-veggie-soup", "Veggie Soup", "A hearty soup simmered from garden vegetables", "crop", 99, 140),
        MaterialItem("food-fruit-tart", "Fruit Tart", "A sweet baked tart bursting with fruit", "crop", 99, 220),
        MaterialItem("furniture-tool-rack", "Tool Rack", "A sturdy wooden rack for hanging tools", "material", 9, 90),
        MaterialItem("furniture-planter-box", "Planter Box", "A handmade wooden planter box", "material", 9, 140),
    ];

    private static List<MachineTypeDefinition> CreateCraftingShowcaseMachineTypes() =>
    [
        new MachineTypeDefinition
        {
            Id = "machine-kitchen", Name = "Kitchen",
            Description = "Provides the cooking station needed for cooking recipes",
            Color = "#c2703a", ItemId = "machine-kitchen", BlocksMovement = true, StationCategories = ["cooking"],
        },
        new MachineTypeDefinition
        {
            Id = "machine-workbench", Name = "Workbench",
            Description = "Provides the carpentry station needed for carpentry recipes",
            Color = "#8a6a3f", ItemId = "machine-workbench", BlocksMovement = true, StationCategories = ["carpentry"],
        },
    ];

    private static RecipeIngredient Ing(string itemId, double quantity) => new() { ItemId = itemId, Quantity = quantity };

    private static List<RecipeDefinition> CreateCraftingShowcaseRecipes() =>
    [
        new RecipeDefinition
        {
            Id = "recipe-craft-kitchen", Name = "Kitchen",
            Inputs = [Ing("material-stone", 8), Ing("material-wood", 6)],
            Outputs = [Ing("machine-kitchen", 1)],
            ProcessingMinutes = 0, Category = "crafting",
        },
        new RecipeDefinition
        {
            Id = "recipe-craft-workbench", Name = "Workbench",
            Inputs = [Ing("material-wood", 10), Ing("material-stone", 2)],
            Outputs = [Ing("machine-workbench", 1)],
            ProcessingMinutes = 0, Category = "crafting",
        },
        new RecipeDefinition
        {
            Id = "recipe-cook-veggie-soup", Name = "Veggie Soup",
            Inputs = [Ing("crop-carrot", 2), Ing("crop-potato", 2)],
            Outputs = [Ing("food-veggie-soup", 1)],
            ProcessingMinutes = 0, Category = "cooking", RequiresStationCategory = "cooking",
        },
        new RecipeDefinition
        {
            Id = "recipe-cook-fruit-tart", Name = "Fruit Tart",
            Inputs = [Ing("crop-wheat", 2), Ing("crop-strawberry", 2)],
            Outputs = [Ing("food-fruit-tart", 1)],
            ProcessingMinutes = 0, Category = "cooking", RequiresStationCategory = "cooking",
        },
        new RecipeDefinition
        {
            Id = "recipe-craft-tool-rack", Name = "Tool Rack",
            Inputs = [Ing("material-wood", 8)],
            Outputs = [Ing("furniture-tool-rack", 1)],
            ProcessingMinutes = 0, Category = "carpentry", RequiresStationCategory = "carpentry",
        },
        new RecipeDefinition
        {
            Id = "recipe-craft-planter-box", Name = "Planter Box",
            Inputs = [Ing("material-wood", 6), Ing("material-stone", 3)],
            Outputs = [Ing("furniture-planter-box", 1)],
            ProcessingMinutes = 0, Category = "carpentry", RequiresStationCategory = "carpentry",
        },
    ];

    /// <summary>
    /// Extensibility showcase: a custom action + the fishing minigame, proving
    /// the "add your own verbs and minigames" surface works from a content pack.
    /// </summary>
    private static List<ActionDef> CreateStarterActions() =>
    [
        new ActionDef
        {
            Id = "action-growth-blessing",
            Name = "Growth Blessing",
            Description = "The charm blesses your fields for the day.",
            Conditions = [],
            FailMessage = "",
            Outcomes =
            [
                new EventOutcome { Type = "message", Message = "A warm green light washes over the farm…" },
                new EventOutcome { Type = "setFlag", FlagName = "growth-blessing-today" },
            ],
            EnergyCost = 0,
        },
        new ActionDef
        {
            Id = "action-forage-snack",
            Name = "Trail Snack",
            Description = "Munch a foraged snack to recover a little energy.",
            Conditions = [],
            FailMessage = "",
            // modifyEnergy isn't an outcome type; restore energy via a small
            // dedicated flag + message so the action showcases sequenced outcomes.
            Outcomes =
            [
                new EventOutcome { Type = "message", Message = "You feel refreshed!" },
                new EventOutcome { Type = "setFlag", FlagName = "snacked-today" },
            ],
            EnergyCost = 0,
        },
    ];

    private static List<Item> CreateExtensibilityShowcaseItems() =>
    [
        new Item
        {
            Id = "snack-trail-mix",
            Name = "Trail Mix",
            Description = "A foraged snack. Use it for a quick pick-me-up.",
            Type = "material",
            Stackable = true,
            MaxStack = 20,
            Value = 8,
            UseActionId = "action-forage-snack",
            ConsumeOnUse = true,
        },
        // Magic showcase: an enchanting station + a craftable, usable charm —
        // the full loop of crafting category → station gating → item-bound action.
        new Item
        {
            Id = "machine-altar",
            Name = "Enchanter's Altar",
            Description = "A humming altar — place it, then enchant nearby",
            Type = "material",
            Stackable = true,
            MaxStack = 9,
            Value = 400,
        },
        new Item
        {
            Id = "charm-growth",
            Name = "Growth Charm",
            Description = "A quartz charm warm to the touch. Use it to feel its blessing.",
            Type = "material",
            Stackable = true,
            MaxStack = 10,
            Value = 320,
            UseActionId = "action-growth-blessing",
            ConsumeOnUse = true,
        },
    ];

    private static List<MachineTypeDefinition> CreateMagicShowcaseMachineTypes() =>
    [
        new MachineTypeDefinition
        {
            Id = "machine-altar",
            Name = "Enchanter's Altar",
            Description = "Provides the magic station needed for enchanting recipes",
            Color = "#7a5bb5",
            ItemId = "machine-altar",
            BlocksMovement = true,
            StationCategories = ["magic"],
        },
    ];

    private static List<RecipeDefinition> CreateMagicShowcaseRecipes() =>
    [
        new RecipeDefinition
        {
            Id = "recipe-growth-charm",
            Name = "Growth Charm",
            Inputs = [Ing("gem-quartz", 1), Ing("material-fiber", 3)],
            Outputs = [Ing("charm-growth", 1)],
            ProcessingMinutes = 0,
            Category = "magic",
            RequiresStationCategory = "magic",
        },
    ];

    private static List<MinigameDef> CreateStarterMinigames() =>
    [
        new MinigameDef
        {
            // Declared under the reserved id: casting a rod now opens the timing
            // bar, and the score feeds the deterministic catch resolution.
            Id = "fishing",
            Name = "Fishing",
            Kind = "timing-bar",
            Config = new OrderedDictionary<string, System.Text.Json.JsonElement>
            {
                ["speed"] = FarmEngine.Json.Js.Value(0.9),
                ["targetSize"] = FarmEngine.Json.Js.Value(0.2),
                ["prompt"] = FarmEngine.Json.Js.Value("Hook the fish — stop the marker in the green zone!"),
            },
            ResultTiers = [],
        },
    ];

    /// <summary>
    /// Build the default farming content pack. (TS constructs it without a
    /// schema parse so object identity survives; C# records are immutable, so
    /// sharing is moot.) The acceptance tests validate the pack against the
    /// content-pack schema on every run.
    /// </summary>
    public static ContentPack CreateContentDefaultPack()
    {
        var (npcs, dialogues) = CreateStarterNpcs();
        return new ContentPack
        {
            Manifest = new PackManifest
            {
                Id = "content-default",
                Name = "Farm Essentials",
                Version = "1.0.0",
                Description = "The built-in farming game: crops, tools, recipes, animals, fishing, weather and the starter farm.",
                Author = "farm-game-engine",
                EngineCompatibility = "*",
                Base = true,
                Dependencies = [],
                Overrides = [],
                Permissions = new PackPermissions { Hooks = [], ContentInject = true, UiPanels = false },
            },
            Content = new PackContent
            {
                Crops = [.. ContentBuiltin.CropDefinitions.Values],
                Items = [.. ContentBuiltin.CreateDefaultItems(), .. CreateCraftingShowcaseItems(), .. CreateExtensibilityShowcaseItems()],
                Recipes = [.. ContentBuiltin.CreateDefaultRecipes(), .. CreateCraftingShowcaseRecipes(), .. CreateMagicShowcaseRecipes()],
                MachineTypes = [.. ContentBuiltin.CreateDefaultMachineTypes(), .. CreateCraftingShowcaseMachineTypes(), .. CreateMagicShowcaseMachineTypes()],
                NodeTypes = [.. ContentBuiltin.DefaultNodeTypes, .. ContentBuiltin.MineNodeTypes],
                AnimalSpecies = ContentBuiltin.CreateDefaultAnimalSpecies(),
                FishTables = ContentBuiltin.CreateDefaultFishTables(),
                WeatherTypes = MigrationsSchema.DefaultWeatherConfig().Types,
                Npcs = npcs,
                Dialogues = dialogues,
                Scenes = [CreateStarterFarmScene()],
                Events = [],
                Quests = CreateStarterQuests(),
                Shops = [ContentBuiltin.CreateDefaultShop()],
                Actions = CreateStarterActions(),
                Minigames = CreateStarterMinigames(),
                PlayerStart = new PackPlayerStart
                {
                    SceneId = "scene-farm",
                    X = 8,
                    Y = 9,
                    Money = 100,
                    Inventory =
                    [
                        new PackStartItem { ItemId = "seed-wheat", Quantity = 10 },
                        new PackStartItem { ItemId = "seed-tomato", Quantity = 5 },
                        new PackStartItem { ItemId = "tool-hoe", Quantity = 1 },
                        new PackStartItem { ItemId = "tool-watering-can", Quantity = 1 },
                        new PackStartItem { ItemId = "fertilizer-basic", Quantity = 10 },
                        // Usable-item showcase: try the inventory Use button on day one.
                        new PackStartItem { ItemId = "snack-trail-mix", Quantity = 2 },
                    ],
                },
            },
            Plugins = [],
        };
    }

    // ─── src/lib/game-helpers.ts ─────────────────────────────────────────

    /// <summary>TS <c>createDefaultPlayer(sceneId)</c> (game-helpers.ts).</summary>
    public static Player CreateDefaultPlayer(string sceneId) => new()
    {
        X = 5,
        Y = 5,
        Direction = "down",
        SceneId = sceneId,
        Inventory = [],
        MaxInventorySize = 20,
        Money = 100,
        ActiveQuests = [],
        CompletedQuests = [],
        EquippedTool = null,
        PixelX = 0,
        PixelY = 0,
        TargetX = 0,
        TargetY = 0,
    };

    /// <summary>TS <c>createDefaultItems()</c> (game-helpers.ts re-export of the core catalog).</summary>
    public static List<Item> CreateDefaultItems() => ContentBuiltin.CreateDefaultItems();

    /// <summary>
    /// The starter game is 100% the content-default pack (M5 exit criterion):
    /// this builds an empty project carcass and materializes the pack into it.
    /// Deleting the content-default pack leaves a functional empty engine.
    /// (game-helpers.ts <c>createInitialProject</c>.)
    /// </summary>
    /// <param name="now">TS <c>Date.now()</c> for <c>currentTime</c>/<c>gameStartTime</c>; defaults to the current time.</param>
    public static GameProject CreateInitialProject(double? now = null)
    {
        var time = now ?? Now();
        var carcass = new GameProject
        {
            SchemaVersion = ProjectSchema.CurrentProjectSchemaVersion,
            Id = "project-1",
            Name = "My Farming Game",
            Version = "2.0",
            Scenes = [],
            Npcs = [],
            Items = [],
            Events = [],
            Dialogues = [],
            Quests = [],
            Player = CreateDefaultPlayer(""),
            EventFlags = [],
            StartSceneId = "",
            Mode = "play",
            SelectedTileType = "grass",
            SelectedNpcId = null,
            SelectedItemId = null,
            CurrentTime = time,
            CustomAssets = [],
            PlayerCustomImage = null,
            CurrentSeason = "spring",
            CurrentDay = 1,
            CurrentTimeMinutes = SettingsSchema.DefaultProjectSettings.Time.DayStartMinute,
            CurrentYear = 1,
            Shops = [],
            NodeTypes = [],
            Settings = SettingsSchema.DefaultProjectSettings,
            Recipes = [],
            Actions = [],
            Minigames = [],
            MachineTypes = [],
            Weather = new WeatherConfig { Types = [], Table = [] },
            AnimalSpecies = [],
            Animals = [],
            FishTables = [],
            Mine = new MineConfig { Enabled = false },
            ContentPacks = [],
            GameStartTime = time,
        };

        var (project, problems) = Packs.ApplyPackToProject(carcass, CreateContentDefaultPack());
        if (problems.Count > 0)
        {
            // The default pack must always apply cleanly; problems here are bugs.
            System.Diagnostics.Debug.WriteLine(
                "content-default pack problems: " + string.Join("; ", problems.Select(p => $"{p.PackId}: {p.Message}")));
        }
        // The base pack ships weather types; the starter per-season odds table is
        // project tuning that comes from the engine defaults.
        return project with
        {
            Weather = MigrationsSchema.DefaultWeatherConfig() with { Types = project.Weather.Types },
        };
    }

    // ─── src/lib/projects.ts ─────────────────────────────────────────────

    /// <summary>TS <c>createBlankProject()</c> (projects.ts): an empty 12×12 scene and the default catalog.</summary>
    /// <param name="now">TS <c>Date.now()</c> for <c>currentTime</c>/<c>gameStartTime</c>; defaults to the current time.</param>
    public static GameProject CreateBlankProject(double? now = null)
    {
        var time = now ?? Now();
        var scene = Tiles.CreateEmptyScene("scene-main", "Main", 12, 12);
        var items = CreateDefaultItems();
        var player = CreateDefaultPlayer("scene-main");
        return new GameProject
        {
            SchemaVersion = ProjectSchema.CurrentProjectSchemaVersion,
            Id = "project-blank",
            Name = "Untitled Game",
            Version = "2.0",
            Scenes = [scene],
            Npcs = [],
            Items = items,
            Events = [],
            Dialogues = [],
            Quests = [],
            Player = player,
            EventFlags = [],
            StartSceneId = "scene-main",
            Mode = "tiles",
            SelectedTileType = "grass",
            SelectedNpcId = null,
            SelectedItemId = null,
            CurrentTime = time,
            CustomAssets = [],
            CurrentSeason = "spring",
            CurrentDay = 1,
            CurrentTimeMinutes = SettingsSchema.DefaultProjectSettings.Time.DayStartMinute,
            CurrentYear = 1,
            Shops = [ContentBuiltin.CreateDefaultShop()],
            NodeTypes = [],
            Settings = SettingsSchema.DefaultProjectSettings,
            Recipes = [],
            Actions = [],
            Minigames = [],
            MachineTypes = [],
            Weather = MigrationsSchema.DefaultWeatherConfig(),
            AnimalSpecies = [],
            Animals = [],
            FishTables = [],
            Mine = new MineConfig { Enabled = false },
            ContentPacks = [],
            GameStartTime = time,
        };
    }
}
