using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>TS <c>ToolDefinition</c> (content-builtin.ts).</summary>
public sealed record ToolDefinition
{
    /// <summary>One of <see cref="ToolTypes"/>.</summary>
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>'till' | 'water' | 'harvest' | 'chop' | 'mine' | 'fish'.</summary>
    public string Action { get; init; } = "";
    /// <summary>Tile types (<see cref="TileTypes"/>).</summary>
    public List<string> ValidTargets { get; init; } = [];
    public double EnergyCost { get; init; }
    public double PowerLevel { get; init; }
}

/// <summary>
/// Built-in content definitions (port of content-builtin.ts). These migrate
/// into the <c>content-default</c> pack in M5; until then they live here so
/// both the engine and the editor consume a single copy.
/// </summary>
public static class ContentBuiltin
{
    public static readonly OrderedDictionary<string, CropDefinition> CropDefinitions = new()
    {
        ["wheat"] = new()
        {
            Id = "wheat", Name = "Wheat", SeedCost = 10, BaseHarvestValue = 25,
            GrowthTime = 15000, GrowthDays = 3, Stages = 4, Seasons = ["spring", "fall"], CanRegrow = false,
            YieldMin = 1, YieldMax = 2, MutationChance = 0.01,
        },
        ["corn"] = new()
        {
            Id = "corn", Name = "Corn", SeedCost = 15, BaseHarvestValue = 40,
            GrowthTime = 25000, GrowthDays = 5, Stages = 5, Seasons = ["summer", "fall"], CanRegrow = false,
            YieldMin = 1, YieldMax = 3, MutationChance = 0.015,
        },
        ["tomato"] = new()
        {
            Id = "tomato", Name = "Tomato", SeedCost = 20, BaseHarvestValue = 50,
            GrowthTime = 20000, GrowthDays = 4, Stages = 5, Seasons = ["summer"], CanRegrow = true, RegrowthTime = 8000, RegrowthDays = 2,
            YieldMin = 1, YieldMax = 3, MutationChance = 0.02,
        },
        ["carrot"] = new()
        {
            Id = "carrot", Name = "Carrot", SeedCost = 8, BaseHarvestValue = 20,
            GrowthTime = 12000, GrowthDays = 2, Stages = 4, Seasons = ["spring", "fall", "winter"], CanRegrow = false,
            YieldMin = 1, YieldMax = 2, MutationChance = 0.008,
        },
        ["potato"] = new()
        {
            Id = "potato", Name = "Potato", SeedCost = 12, BaseHarvestValue = 30,
            GrowthTime = 18000, GrowthDays = 4, Stages = 4, Seasons = ["spring", "fall"], CanRegrow = false,
            YieldMin = 2, YieldMax = 4, MutationChance = 0.012,
        },
        ["strawberry"] = new()
        {
            Id = "strawberry", Name = "Strawberry", SeedCost = 30, BaseHarvestValue = 60,
            GrowthTime = 22000, GrowthDays = 4, Stages = 5, Seasons = ["spring"], CanRegrow = true, RegrowthTime = 10000, RegrowthDays = 2,
            YieldMin = 1, YieldMax = 2, MutationChance = 0.025,
        },
        ["pumpkin"] = new()
        {
            Id = "pumpkin", Name = "Pumpkin", SeedCost = 50, BaseHarvestValue = 150,
            GrowthTime = 35000, GrowthDays = 7, Stages = 6, Seasons = ["fall"], CanRegrow = false,
            MultiTile = new() { Width = 2, Height = 2 }, YieldMin = 1, YieldMax = 1, MutationChance = 0.05,
        },
        ["cauliflower"] = new()
        {
            Id = "cauliflower", Name = "Cauliflower", SeedCost = 40, BaseHarvestValue = 120,
            GrowthTime = 28000, GrowthDays = 6, Stages = 5, Seasons = ["spring"], CanRegrow = false,
            MultiTile = new() { Width = 2, Height = 2 }, YieldMin = 1, YieldMax = 1, MutationChance = 0.04,
        },
        ["blueberry"] = new()
        {
            Id = "blueberry", Name = "Blueberry", SeedCost = 35, BaseHarvestValue = 70,
            GrowthTime = 24000, GrowthDays = 5, Stages = 5, Seasons = ["summer"], CanRegrow = true, RegrowthTime = 9000, RegrowthDays = 2,
            YieldMin = 2, YieldMax = 5, MutationChance = 0.03,
        },
    };

    /// <summary>Keyed by <see cref="CropQualities"/>.</summary>
    public static readonly OrderedDictionary<string, double> QualityMultipliers = new()
    {
        ["normal"] = 1.0,
        ["silver"] = 1.25,
        ["gold"] = 1.5,
        ["iridium"] = 2.0,
    };

    /// <summary>Keyed by 'none' | 'giant' | 'golden' | 'ancient'.</summary>
    public static readonly OrderedDictionary<string, double> MutationMultipliers = new()
    {
        ["none"] = 1.0,
        ["giant"] = 2.5,
        ["golden"] = 3.0,
        ["ancient"] = 4.0,
    };

    public const double DaysPerSeason = 28;
    public static readonly IReadOnlyList<string> SeasonOrder = ["spring", "summer", "fall", "winter"];

    /// <summary>Keyed by <see cref="ToolTypes"/>.</summary>
    public static readonly OrderedDictionary<string, ToolDefinition> ToolDefinitions = new()
    {
        ["watering-can"] = new()
        {
            Type = "watering-can", Name = "Watering Can",
            Description = "Water crops to help them grow faster",
            Action = "water", ValidTargets = ["soil"], EnergyCost = 2, PowerLevel = 1,
        },
        ["hoe"] = new()
        {
            Type = "hoe", Name = "Hoe",
            Description = "Till grass into farmable soil",
            Action = "till", ValidTargets = ["grass", "path"], EnergyCost = 4, PowerLevel = 1,
        },
        ["axe"] = new()
        {
            Type = "axe", Name = "Axe",
            Description = "Chop down trees and wooden obstacles",
            Action = "chop", ValidTargets = ["wall"], EnergyCost = 6, PowerLevel = 1,
        },
        ["pickaxe"] = new()
        {
            Type = "pickaxe", Name = "Pickaxe",
            Description = "Break rocks and mine for ore",
            Action = "mine", ValidTargets = ["wall"], EnergyCost = 8, PowerLevel = 1,
        },
        ["scythe"] = new()
        {
            Type = "scythe", Name = "Scythe",
            Description = "Harvest crops in a large area",
            Action = "harvest", ValidTargets = ["soil"], EnergyCost = 5, PowerLevel = 2,
        },
        ["fishing-rod"] = new()
        {
            Type = "fishing-rod", Name = "Fishing Rod",
            Description = "Catch fish from water tiles",
            Action = "fish", ValidTargets = ["water"], EnergyCost = 3, PowerLevel = 1,
        },
    };

    public static List<Item> CreateDefaultItems()
    {
        var items = new List<Item>();

        foreach (var crop in CropDefinitions.Values)
        {
            items.Add(new Item
            {
                Id = $"seed-{crop.Id}",
                Name = $"{crop.Name} Seeds",
                Description = $"Plant these to grow {crop.Name.ToLowerInvariant()}",
                Type = "seed", Stackable = true, MaxStack = 99,
                Value = crop.SeedCost, CropType = crop.Id,
            });
            items.Add(new Item
            {
                Id = $"crop-{crop.Id}",
                Name = crop.Name,
                Description = $"Fresh {crop.Name.ToLowerInvariant()}",
                Type = "crop", Stackable = true, MaxStack = 99,
                Value = crop.BaseHarvestValue, CropType = crop.Id,
            });
        }

        static Item Tool(string id, string name, string description, double value, string toolType) => new()
        {
            Id = id, Name = name, Description = description, Type = "tool", Stackable = false, MaxStack = 1, Value = value,
            ToolType = toolType, ToolPower = 1, Durability = 100, MaxDurability = 100,
        };

        items.Add(Tool("tool-hoe", "Hoe", "Till grass into soil for planting", 50, "hoe"));
        items.Add(Tool("tool-watering-can", "Watering Can", "Water your crops to help them grow", 50, "watering-can"));
        items.Add(Tool("tool-axe", "Axe", "Chop down trees and wooden objects", 100, "axe"));
        items.Add(Tool("tool-pickaxe", "Pickaxe", "Break rocks and mine ore", 150, "pickaxe"));
        items.Add(Tool("tool-scythe", "Scythe", "Harvest crops quickly in an area", 200, "scythe"));

        // Tier-2 tool upgrades (shop offers)
        static Item Tool2(string id, string name, string description, double value, string toolType) => new()
        {
            Id = id, Name = name, Description = description, Type = "tool", Stackable = false, MaxStack = 1, Value = value,
            ToolType = toolType, ToolPower = 2, ToolTier = 2, Durability = 200, MaxDurability = 200,
        };
        items.Add(Tool2("tool-hoe-2", "Copper Hoe", "A sturdier hoe — tills with less effort", 250, "hoe"));
        items.Add(Tool2("tool-watering-can-2", "Copper Watering Can", "Waters a wider area with less effort", 250, "watering-can"));
        items.Add(Tool2("tool-axe-2", "Copper Axe", "Fells trees in fewer swings", 400, "axe"));
        items.Add(Tool2("tool-pickaxe-2", "Copper Pickaxe", "Cracks boulders lesser picks cannot", 500, "pickaxe"));

        static Item Plain(string id, string name, string description, string type, double maxStack, double value) => new()
        {
            Id = id, Name = name, Description = description, Type = type, Stackable = true, MaxStack = maxStack, Value = value,
        };

        // Gathering materials
        items.Add(Plain("material-wood", "Wood", "Sturdy timber from trees and stumps", "material", 999, 5));
        items.Add(Plain("material-stone", "Stone", "Rough stone chipped from rocks", "material", 999, 4));
        items.Add(Plain("material-fiber", "Fiber", "Plant fiber cut from weeds", "material", 999, 2));

        items.Add(Plain("fertilizer-basic", "Basic Fertilizer", "Improves soil quality and crop growth", "fertilizer", 99, 10));
        items.Add(Plain("fertilizer-quality", "Quality Fertilizer", "Increases chance of higher quality crops", "fertilizer", 99, 25));
        items.Add(Plain("gift-flower", "Flower", "A beautiful flower that makes a nice gift", "gift", 99, 20));

        // M4: fishing rod + catches
        items.Add(Tool("tool-fishing-rod", "Fishing Rod", "Catch fish from water tiles", 120, "fishing-rod"));
        items.Add(Plain("fish-carp", "Carp", "A common pond fish", "fish", 99, 18));
        items.Add(Plain("fish-perch", "Perch", "A quick freshwater fish", "fish", 99, 30));
        items.Add(Plain("fish-catfish", "Catfish", "A prized whiskered catch", "fish", 99, 75));
        items.Add(Plain("junk-boot", "Old Boot", "Someone lost this a long time ago", "material", 99, 1));

        // M4: ranching
        items.Add(Plain("feed-hay", "Hay", "Animal feed — one serving a day keeps them happy", "material", 999, 3));
        items.Add(Plain("product-egg", "Egg", "A fresh egg", "crop", 99, 22));
        items.Add(Plain("product-milk", "Milk", "A pail of fresh milk", "crop", 99, 48));

        // M4: ores & bars (mining → crafting chain)
        items.Add(Plain("ore-copper", "Copper Ore", "Raw copper, ready for smelting", "material", 999, 12));
        items.Add(Plain("ore-iron", "Iron Ore", "Raw iron, ready for smelting", "material", 999, 20));
        items.Add(Plain("gem-quartz", "Quartz", "A translucent crystal", "material", 999, 40));
        items.Add(Plain("bar-copper", "Copper Bar", "A smelted copper ingot", "material", 999, 45));
        items.Add(Plain("bar-iron", "Iron Bar", "A smelted iron ingot", "material", 999, 80));

        // M4: placeable machines
        items.Add(Plain("machine-furnace", "Furnace", "Smelts ore into bars (place it, then load a recipe)", "material", 9, 150));
        items.Add(Plain("machine-preserves", "Preserves Jar", "Turns crops into preserves worth more", "material", 9, 200));
        items.Add(Plain("food-preserves", "Preserves", "Sweet preserved produce", "crop", 99, 120));

        return items;
    }

    /// <summary>Built-in machine types (M4a).</summary>
    public static List<MachineTypeDefinition> CreateDefaultMachineTypes() =>
    [
        new()
        {
            Id = "machine-furnace", Name = "Furnace",
            Description = "Smelts ore into metal bars",
            Color = "#8a4a3a", ItemId = "machine-furnace", BlocksMovement = true, StationCategories = [],
        },
        new()
        {
            Id = "machine-preserves", Name = "Preserves Jar",
            Description = "Preserves crops into higher-value goods",
            Color = "#7a4a8a", ItemId = "machine-preserves", BlocksMovement = true, StationCategories = [],
        },
    ];

    private static RecipeIngredient Ingredient(string itemId, double quantity) => new() { ItemId = itemId, Quantity = quantity };

    /// <summary>Built-in recipes (M4a): hand crafts + machine jobs.</summary>
    public static List<RecipeDefinition> CreateDefaultRecipes() =>
    [
        new()
        {
            Id = "recipe-craft-furnace", Name = "Furnace",
            Inputs = [Ingredient("material-stone", 10), Ingredient("ore-copper", 2)],
            Outputs = [Ingredient("machine-furnace", 1)],
            ProcessingMinutes = 0,
            Category = "crafting",
        },
        new()
        {
            Id = "recipe-craft-preserves-jar", Name = "Preserves Jar",
            Inputs = [Ingredient("material-wood", 12), Ingredient("material-stone", 4)],
            Outputs = [Ingredient("machine-preserves", 1)],
            ProcessingMinutes = 0,
            Category = "crafting",
            Unlock = new() { Skill = new() { Skill = "farming", Level = 1 } },
        },
        new()
        {
            Id = "recipe-craft-hay", Name = "Hay Bundle",
            Inputs = [Ingredient("material-fiber", 3)],
            Outputs = [Ingredient("feed-hay", 2)],
            ProcessingMinutes = 0,
            Category = "farming",
        },
        new()
        {
            Id = "recipe-smelt-copper", Name = "Copper Bar",
            Inputs = [Ingredient("ore-copper", 3), Ingredient("material-wood", 1)],
            Outputs = [Ingredient("bar-copper", 1)],
            ProcessingMinutes = 120,
            MachineTypeId = "machine-furnace",
            Category = "smithing",
        },
        new()
        {
            Id = "recipe-smelt-iron", Name = "Iron Bar",
            Inputs = [Ingredient("ore-iron", 3), Ingredient("material-wood", 1)],
            Outputs = [Ingredient("bar-iron", 1)],
            ProcessingMinutes = 180,
            MachineTypeId = "machine-furnace",
            Category = "smithing",
            Unlock = new() { Skill = new() { Skill = "mining", Level = 2 } },
        },
        new()
        {
            Id = "recipe-preserve-wheat", Name = "Wheat Preserves",
            Inputs = [Ingredient("crop-wheat", 3)],
            Outputs = [Ingredient("food-preserves", 1)],
            ProcessingMinutes = 360,
            MachineTypeId = "machine-preserves",
            Category = "cooking",
        },
    ];

    /// <summary>Built-in animal species (M4c).</summary>
    public static List<AnimalSpeciesDefinition> CreateDefaultAnimalSpecies() =>
    [
        new()
        {
            Id = "animal-chicken", Name = "Chicken", PurchaseCost = 400,
            FeedItemId = "feed-hay", ProductItemId = "product-egg",
            ProductIntervalDays = 1, DaysToAdult = 3, Color = "#e8e0c3",
        },
        new()
        {
            Id = "animal-cow", Name = "Cow", PurchaseCost = 1500,
            FeedItemId = "feed-hay", ProductItemId = "product-milk",
            ProductIntervalDays = 2, DaysToAdult = 5, Color = "#d3b28a",
        },
    ];

    /// <summary>Built-in fish tables (M4e): any water, tuned per season.</summary>
    public static List<FishTable> CreateDefaultFishTables() =>
    [
        new()
        {
            Id = "fish-table-default", Name = "Pond Fish",
            Entries =
            [
                new() { ItemId = "fish-carp", Weight = 6, Difficulty = 0.15 },
                new() { ItemId = "fish-perch", Weight = 3, Difficulty = 0.35 },
                new() { ItemId = "fish-catfish", Weight = 1, Difficulty = 0.6 },
            ],
            JunkChance = 0.15,
            JunkItemId = "junk-boot",
        },
    ];

    private static NodeDrop Drop(string itemId, double min, double max) => new() { ItemId = itemId, Min = min, Max = max, Weight = 1 };

    /// <summary>Ore node types used by generated mine floors (M4f).</summary>
    public static readonly IReadOnlyList<NodeTypeDefinition> MineNodeTypes =
    [
        new()
        {
            Id = "node-mine-rock", Name = "Mine Rock", Health = 2, RequiredTool = "pickaxe", RequiredToolTier = 1,
            Drops = [Drop("material-stone", 1, 2)],
            RespawnDays = null, Color = "#6e6e78", BlocksMovement = true,
        },
        new()
        {
            Id = "node-copper-ore", Name = "Copper Node", Health = 3, RequiredTool = "pickaxe", RequiredToolTier = 1,
            Drops = [Drop("ore-copper", 1, 3)],
            RespawnDays = null, Color = "#b87333", BlocksMovement = true,
        },
        new()
        {
            Id = "node-iron-ore", Name = "Iron Node", Health = 4, RequiredTool = "pickaxe", RequiredToolTier = 2,
            Drops = [Drop("ore-iron", 1, 3)],
            RespawnDays = null, Color = "#a19d94", BlocksMovement = true,
        },
        new()
        {
            Id = "node-quartz", Name = "Quartz Crystal", Health = 2, RequiredTool = "pickaxe", RequiredToolTier = 1,
            Drops = [Drop("gem-quartz", 1, 1)],
            RespawnDays = null, Color = "#cfe3ee", BlocksMovement = true,
        },
    ];

    private static MineRockWeight Rock(string nodeTypeId, double weight) => new() { NodeTypeId = nodeTypeId, Weight = weight };

    /// <summary>Default mine configuration used when a project enables mining.</summary>
    public static List<MineBand> CreateDefaultMineBands() =>
    [
        new()
        {
            FromFloor = 1, ToFloor = 7, Density = 0.3,
            Rocks =
            [
                Rock("node-mine-rock", 6),
                Rock("node-copper-ore", 3),
                Rock("node-quartz", 1),
            ],
        },
        new()
        {
            FromFloor = 8, ToFloor = 20, Density = 0.35,
            Rocks =
            [
                Rock("node-mine-rock", 4),
                Rock("node-copper-ore", 3),
                Rock("node-iron-ore", 3),
                Rock("node-quartz", 1),
            ],
        },
    ];

    /// <summary>Built-in gathering node types (content-defined; projects can add more).</summary>
    public static readonly IReadOnlyList<NodeTypeDefinition> DefaultNodeTypes =
    [
        new()
        {
            Id = "node-tree", Name = "Tree", Health = 4, RequiredTool = "axe", RequiredToolTier = 1,
            Drops = [Drop("material-wood", 2, 4)],
            RespawnDays = null, Color = "#3f6d33", BlocksMovement = true,
        },
        new()
        {
            Id = "node-stump", Name = "Stump", Health = 2, RequiredTool = "axe", RequiredToolTier = 1,
            Drops = [Drop("material-wood", 1, 2)],
            RespawnDays = 3, Color = "#6d5233", BlocksMovement = true,
        },
        new()
        {
            Id = "node-rock", Name = "Rock", Health = 3, RequiredTool = "pickaxe", RequiredToolTier = 1,
            Drops = [Drop("material-stone", 1, 3)],
            RespawnDays = 3, Color = "#8a8a95", BlocksMovement = true,
        },
        new()
        {
            Id = "node-boulder", Name = "Boulder", Health = 6, RequiredTool = "pickaxe", RequiredToolTier = 2,
            Drops = [Drop("material-stone", 4, 8)],
            RespawnDays = null, Color = "#5e5e6b", BlocksMovement = true,
        },
        new()
        {
            Id = "node-weeds", Name = "Weeds", Health = 1, RequiredTool = "scythe", RequiredToolTier = 1,
            Drops = [Drop("material-fiber", 1, 2)],
            RespawnDays = 2, Color = "#7d9a3f", BlocksMovement = false,
        },
    ];

    private static ShopStockEntry Stock(string itemId) => new() { ItemId = itemId };

    /// <summary>The starter game's general store.</summary>
    public static ShopDefinition CreateDefaultShop() => new()
    {
        Id = "shop-general", Name = "General Store",
        Stock =
        [
            .. CropDefinitions.Values.Select(crop => new ShopStockEntry
            {
                ItemId = $"seed-{crop.Id}",
                Seasons = crop.Seasons,
            }),
            Stock("fertilizer-basic"),
            new() { ItemId = "fertilizer-quality", DailyLimit = 5 },
            Stock("tool-hoe"),
            Stock("tool-watering-can"),
            Stock("tool-axe"),
            Stock("tool-pickaxe"),
            Stock("tool-scythe"),
            Stock("tool-hoe-2"),
            Stock("tool-watering-can-2"),
            Stock("tool-axe-2"),
            Stock("tool-pickaxe-2"),
            Stock("tool-fishing-rod"),
            Stock("feed-hay"),
            Stock("machine-furnace"),
            Stock("machine-preserves"),
        ],
        SellPriceMultiplier = 1,
        BuysItems = true,
        RepairsTools = true,
        RepairCostPerPoint = 0.5,
    };
}
