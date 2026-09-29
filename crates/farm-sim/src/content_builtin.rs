//! Built-in content definitions (port of `ContentBuiltin.cs` / content-builtin.ts). These
//! migrate into the `content-default` pack in M5; until then they live here so both the engine
//! and the editor consume a single copy.

use crate::schema::{
    AnimalSpeciesDefinition, CropDefinition, CropMultiTile, FishTable, FishTableEntry, Item, MachineTypeDefinition,
    MineBand, MineRockWeight, NodeDrop, NodeTypeDefinition, RecipeDefinition, RecipeIngredient, RecipeSkillRequirement,
    RecipeUnlock, ShopDefinition, ShopStockEntry,
};
use crate::units;
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};

/// TS `ToolDefinition` (content-builtin.ts).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ToolDefinition {
    /// One of [`crate::schema::tool_types`].
    pub r#type: String,
    pub name: String,
    pub description: String,
    /// 'till' | 'water' | 'harvest' | 'chop' | 'mine' | 'fish'.
    pub action: String,
    /// Tile types ([`crate::schema::tile_types`]).
    pub valid_targets: Vec<String>,
    #[serde(with = "crate::units::energy")]
    pub energy_cost: i32,
    #[serde(with = "crate::units::int")]
    pub power_level: i32,
}

fn strings(values: &[&str]) -> Vec<String> {
    values.iter().map(|value| (*value).to_owned()).collect()
}

#[allow(clippy::too_many_arguments)]
fn crop(
    id: &str,
    name: &str,
    seed_cost: i64,
    base_harvest_value: i64,
    growth_time: i64,
    growth_days: u32,
    stages: u32,
    seasons: &[&str],
    yield_min: u32,
    yield_max: u32,
    mutation_chance: f64,
) -> CropDefinition {
    CropDefinition {
        id: id.to_owned(),
        name: name.to_owned(),
        seed_cost,
        base_harvest_value,
        growth_time,
        growth_days: Some(growth_days),
        stages,
        seasons: strings(seasons),
        can_regrow: false,
        yield_min,
        yield_max,
        mutation_chance: Some(units::from_authoring::<units::Probability>(mutation_chance)),
        ..CropDefinition::default()
    }
}

fn regrowing(mut definition: CropDefinition, regrowth_time: i64, regrowth_days: u32) -> CropDefinition {
    definition.can_regrow = true;
    definition.regrowth_time = Some(regrowth_time);
    definition.regrowth_days = Some(regrowth_days);
    definition
}

fn multi_tile(mut definition: CropDefinition, width: u32, height: u32) -> CropDefinition {
    definition.multi_tile = Some(CropMultiTile { width, height });
    definition
}

/// TS `CROP_DEFINITIONS`, keyed by crop id in declaration order.
pub fn crop_definitions() -> IndexMap<String, CropDefinition> {
    let definitions = [
        crop("wheat", "Wheat", 10, 25, 15000, 3, 4, &["spring", "fall"], 1, 2, 0.01),
        crop("corn", "Corn", 15, 40, 25000, 5, 5, &["summer", "fall"], 1, 3, 0.015),
        regrowing(crop("tomato", "Tomato", 20, 50, 20000, 4, 5, &["summer"], 1, 3, 0.02), 8000, 2),
        crop("carrot", "Carrot", 8, 20, 12000, 2, 4, &["spring", "fall", "winter"], 1, 2, 0.008),
        crop("potato", "Potato", 12, 30, 18000, 4, 4, &["spring", "fall"], 2, 4, 0.012),
        regrowing(crop("strawberry", "Strawberry", 30, 60, 22000, 4, 5, &["spring"], 1, 2, 0.025), 10000, 2),
        multi_tile(crop("pumpkin", "Pumpkin", 50, 150, 35000, 7, 6, &["fall"], 1, 1, 0.05), 2, 2),
        multi_tile(crop("cauliflower", "Cauliflower", 40, 120, 28000, 6, 5, &["spring"], 1, 1, 0.04), 2, 2),
        regrowing(crop("blueberry", "Blueberry", 35, 70, 24000, 5, 5, &["summer"], 2, 5, 0.03), 9000, 2),
    ];
    definitions.into_iter().map(|definition| (definition.id.clone(), definition)).collect()
}

/// Keyed by [`crate::schema::crop_qualities`].
/// Harvest value multipliers in thousandths.
pub fn quality_multipliers() -> IndexMap<String, u32> {
    [("normal", 1000), ("silver", 1250), ("gold", 1500), ("iridium", 2000)]
        .into_iter()
        .map(|(key, value)| (key.to_owned(), value))
        .collect()
}

/// Keyed by 'none' | 'giant' | 'golden' | 'ancient'.
/// Harvest value multipliers in thousandths.
pub fn mutation_multipliers() -> IndexMap<String, u32> {
    [("none", 1000), ("giant", 2500), ("golden", 3000), ("ancient", 4000)]
        .into_iter()
        .map(|(key, value)| (key.to_owned(), value))
        .collect()
}

pub const DAYS_PER_SEASON: u32 = 28;
pub const SEASON_ORDER: &[&str] = &["spring", "summer", "fall", "winter"];

fn tool_definition(
    r#type: &str,
    name: &str,
    description: &str,
    action: &str,
    valid_targets: &[&str],
    energy_cost: i32,
    power_level: i32,
) -> ToolDefinition {
    ToolDefinition {
        r#type: r#type.to_owned(),
        name: name.to_owned(),
        description: description.to_owned(),
        action: action.to_owned(),
        valid_targets: strings(valid_targets),
        energy_cost: units::points(energy_cost),
        power_level,
    }
}

/// Keyed by [`crate::schema::tool_types`].
pub fn tool_definitions() -> IndexMap<String, ToolDefinition> {
    let definitions = [
        tool_definition(
            "watering-can",
            "Watering Can",
            "Water crops to help them grow faster",
            "water",
            &["soil"],
            2,
            1,
        ),
        tool_definition("hoe", "Hoe", "Till grass into farmable soil", "till", &["grass", "path"], 4, 1),
        tool_definition("axe", "Axe", "Chop down trees and wooden obstacles", "chop", &["wall"], 6, 1),
        tool_definition("pickaxe", "Pickaxe", "Break rocks and mine for ore", "mine", &["wall"], 8, 1),
        tool_definition("scythe", "Scythe", "Harvest crops in a large area", "harvest", &["soil"], 5, 2),
        tool_definition("fishing-rod", "Fishing Rod", "Catch fish from water tiles", "fish", &["water"], 3, 1),
    ];
    definitions.into_iter().map(|definition| (definition.r#type.clone(), definition)).collect()
}

fn tool(id: &str, name: &str, description: &str, value: i64, tool_type: &str) -> Item {
    Item {
        id: id.to_owned(),
        name: name.to_owned(),
        description: description.to_owned(),
        r#type: "tool".to_owned(),
        stackable: false,
        max_stack: 1,
        value,
        tool_type: Some(tool_type.to_owned()),
        tool_power: Some(1),
        durability: Some(100),
        max_durability: Some(100),
        ..Item::default()
    }
}

/// Tier-2 tool upgrades (shop offers).
fn tool2(id: &str, name: &str, description: &str, value: i64, tool_type: &str) -> Item {
    Item {
        id: id.to_owned(),
        name: name.to_owned(),
        description: description.to_owned(),
        r#type: "tool".to_owned(),
        stackable: false,
        max_stack: 1,
        value,
        tool_type: Some(tool_type.to_owned()),
        tool_power: Some(2),
        tool_tier: Some(2),
        durability: Some(200),
        max_durability: Some(200),
        ..Item::default()
    }
}

fn plain(id: &str, name: &str, description: &str, r#type: &str, max_stack: u32, value: i64) -> Item {
    Item {
        id: id.to_owned(),
        name: name.to_owned(),
        description: description.to_owned(),
        r#type: r#type.to_owned(),
        stackable: true,
        max_stack,
        value,
        ..Item::default()
    }
}

pub fn create_default_items() -> Vec<Item> {
    let mut items = Vec::new();

    for crop in crop_definitions().values() {
        items.push(Item {
            id: format!("seed-{}", crop.id),
            name: format!("{} Seeds", crop.name),
            description: format!("Plant these to grow {}", crop.name.to_lowercase()),
            r#type: "seed".to_owned(),
            stackable: true,
            max_stack: 99,
            value: crop.seed_cost,
            crop_type: Some(crop.id.clone()),
            ..Item::default()
        });
        items.push(Item {
            id: format!("crop-{}", crop.id),
            name: crop.name.clone(),
            description: format!("Fresh {}", crop.name.to_lowercase()),
            r#type: "crop".to_owned(),
            stackable: true,
            max_stack: 99,
            value: crop.base_harvest_value,
            crop_type: Some(crop.id.clone()),
            ..Item::default()
        });
    }

    items.push(tool("tool-hoe", "Hoe", "Till grass into soil for planting", 50, "hoe"));
    items.push(tool("tool-watering-can", "Watering Can", "Water your crops to help them grow", 50, "watering-can"));
    items.push(tool("tool-axe", "Axe", "Chop down trees and wooden objects", 100, "axe"));
    items.push(tool("tool-pickaxe", "Pickaxe", "Break rocks and mine ore", 150, "pickaxe"));
    items.push(tool("tool-scythe", "Scythe", "Harvest crops quickly in an area", 200, "scythe"));

    // Tier-2 tool upgrades (shop offers)
    items.push(tool2("tool-hoe-2", "Copper Hoe", "A sturdier hoe — tills with less effort", 250, "hoe"));
    items.push(tool2(
        "tool-watering-can-2",
        "Copper Watering Can",
        "Waters a wider area with less effort",
        250,
        "watering-can",
    ));
    items.push(tool2("tool-axe-2", "Copper Axe", "Fells trees in fewer swings", 400, "axe"));
    items.push(tool2("tool-pickaxe-2", "Copper Pickaxe", "Cracks boulders lesser picks cannot", 500, "pickaxe"));

    // Gathering materials
    items.push(plain("material-wood", "Wood", "Sturdy timber from trees and stumps", "material", 999, 5));
    items.push(plain("material-stone", "Stone", "Rough stone chipped from rocks", "material", 999, 4));
    items.push(plain("material-fiber", "Fiber", "Plant fiber cut from weeds", "material", 999, 2));

    items.push(plain(
        "fertilizer-basic",
        "Basic Fertilizer",
        "Improves soil quality and crop growth",
        "fertilizer",
        99,
        10,
    ));
    items.push(plain(
        "fertilizer-quality",
        "Quality Fertilizer",
        "Increases chance of higher quality crops",
        "fertilizer",
        99,
        25,
    ));
    items.push(plain("gift-flower", "Flower", "A beautiful flower that makes a nice gift", "gift", 99, 20));

    // M4: fishing rod + catches
    items.push(tool("tool-fishing-rod", "Fishing Rod", "Catch fish from water tiles", 120, "fishing-rod"));
    items.push(plain("fish-carp", "Carp", "A common pond fish", "fish", 99, 18));
    items.push(plain("fish-perch", "Perch", "A quick freshwater fish", "fish", 99, 30));
    items.push(plain("fish-catfish", "Catfish", "A prized whiskered catch", "fish", 99, 75));
    items.push(plain("junk-boot", "Old Boot", "Someone lost this a long time ago", "material", 99, 1));

    // M4: ranching
    items.push(plain("feed-hay", "Hay", "Animal feed — one serving a day keeps them happy", "material", 999, 3));
    items.push(plain("product-egg", "Egg", "A fresh egg", "crop", 99, 22));
    items.push(plain("product-milk", "Milk", "A pail of fresh milk", "crop", 99, 48));

    // M4: ores & bars (mining → crafting chain)
    items.push(plain("ore-copper", "Copper Ore", "Raw copper, ready for smelting", "material", 999, 12));
    items.push(plain("ore-iron", "Iron Ore", "Raw iron, ready for smelting", "material", 999, 20));
    items.push(plain("gem-quartz", "Quartz", "A translucent crystal", "material", 999, 40));
    items.push(plain("bar-copper", "Copper Bar", "A smelted copper ingot", "material", 999, 45));
    items.push(plain("bar-iron", "Iron Bar", "A smelted iron ingot", "material", 999, 80));

    // M4: placeable machines
    items.push(plain(
        "machine-furnace",
        "Furnace",
        "Smelts ore into bars (place it, then load a recipe)",
        "material",
        9,
        150,
    ));
    items.push(plain(
        "machine-preserves",
        "Preserves Jar",
        "Turns crops into preserves worth more",
        "material",
        9,
        200,
    ));
    items.push(plain("food-preserves", "Preserves", "Sweet preserved produce", "crop", 99, 120));

    items
}

/// Built-in machine types (M4a).
pub fn create_default_machine_types() -> Vec<MachineTypeDefinition> {
    vec![
        MachineTypeDefinition {
            id: "machine-furnace".to_owned(),
            name: "Furnace".to_owned(),
            description: "Smelts ore into metal bars".to_owned(),
            color: "#8a4a3a".to_owned(),
            item_id: Some("machine-furnace".to_owned()),
            blocks_movement: true,
            station_categories: Vec::new(),
            ..MachineTypeDefinition::default()
        },
        MachineTypeDefinition {
            id: "machine-preserves".to_owned(),
            name: "Preserves Jar".to_owned(),
            description: "Preserves crops into higher-value goods".to_owned(),
            color: "#7a4a8a".to_owned(),
            item_id: Some("machine-preserves".to_owned()),
            blocks_movement: true,
            station_categories: Vec::new(),
            ..MachineTypeDefinition::default()
        },
    ]
}

fn ingredient(item_id: &str, quantity: u32) -> RecipeIngredient {
    RecipeIngredient { item_id: item_id.to_owned(), quantity }
}

fn skill_unlock(skill: &str, level: u32) -> Option<RecipeUnlock> {
    Some(RecipeUnlock {
        skill: Some(RecipeSkillRequirement { skill: skill.to_owned(), level }),
        ..RecipeUnlock::default()
    })
}

#[allow(clippy::too_many_arguments)]
fn recipe(
    id: &str,
    name: &str,
    inputs: Vec<RecipeIngredient>,
    outputs: Vec<RecipeIngredient>,
    processing_minutes: u32,
    machine_type_id: Option<&str>,
    category: &str,
    unlock: Option<RecipeUnlock>,
) -> RecipeDefinition {
    RecipeDefinition {
        id: id.to_owned(),
        name: name.to_owned(),
        inputs,
        outputs,
        processing_minutes,
        machine_type_id: machine_type_id.map(str::to_owned),
        category: category.to_owned(),
        unlock,
        ..RecipeDefinition::default()
    }
}

/// Built-in recipes (M4a): hand crafts + machine jobs.
pub fn create_default_recipes() -> Vec<RecipeDefinition> {
    vec![
        recipe(
            "recipe-craft-furnace",
            "Furnace",
            vec![ingredient("material-stone", 10), ingredient("ore-copper", 2)],
            vec![ingredient("machine-furnace", 1)],
            0,
            None,
            "crafting",
            None,
        ),
        recipe(
            "recipe-craft-preserves-jar",
            "Preserves Jar",
            vec![ingredient("material-wood", 12), ingredient("material-stone", 4)],
            vec![ingredient("machine-preserves", 1)],
            0,
            None,
            "crafting",
            skill_unlock("farming", 1),
        ),
        recipe(
            "recipe-craft-hay",
            "Hay Bundle",
            vec![ingredient("material-fiber", 3)],
            vec![ingredient("feed-hay", 2)],
            0,
            None,
            "farming",
            None,
        ),
        recipe(
            "recipe-smelt-copper",
            "Copper Bar",
            vec![ingredient("ore-copper", 3), ingredient("material-wood", 1)],
            vec![ingredient("bar-copper", 1)],
            120,
            Some("machine-furnace"),
            "smithing",
            None,
        ),
        recipe(
            "recipe-smelt-iron",
            "Iron Bar",
            vec![ingredient("ore-iron", 3), ingredient("material-wood", 1)],
            vec![ingredient("bar-iron", 1)],
            180,
            Some("machine-furnace"),
            "smithing",
            skill_unlock("mining", 2),
        ),
        recipe(
            "recipe-preserve-wheat",
            "Wheat Preserves",
            vec![ingredient("crop-wheat", 3)],
            vec![ingredient("food-preserves", 1)],
            360,
            Some("machine-preserves"),
            "cooking",
            None,
        ),
    ]
}

/// Built-in animal species (M4c).
pub fn create_default_animal_species() -> Vec<AnimalSpeciesDefinition> {
    vec![
        AnimalSpeciesDefinition {
            id: "animal-chicken".to_owned(),
            name: "Chicken".to_owned(),
            purchase_cost: 400,
            feed_item_id: Some("feed-hay".to_owned()),
            product_item_id: "product-egg".to_owned(),
            product_interval_days: 1,
            days_to_adult: 3,
            color: "#e8e0c3".to_owned(),
            ..AnimalSpeciesDefinition::default()
        },
        AnimalSpeciesDefinition {
            id: "animal-cow".to_owned(),
            name: "Cow".to_owned(),
            purchase_cost: 1500,
            feed_item_id: Some("feed-hay".to_owned()),
            product_item_id: "product-milk".to_owned(),
            product_interval_days: 2,
            days_to_adult: 5,
            color: "#d3b28a".to_owned(),
            ..AnimalSpeciesDefinition::default()
        },
    ]
}

fn fish(item_id: &str, weight: u32, difficulty: f64) -> FishTableEntry {
    FishTableEntry {
        item_id: item_id.to_owned(),
        weight,
        difficulty: units::from_authoring::<units::Probability>(difficulty),
    }
}

/// Built-in fish tables (M4e): any water, tuned per season.
pub fn create_default_fish_tables() -> Vec<FishTable> {
    vec![FishTable {
        id: "fish-table-default".to_owned(),
        name: "Pond Fish".to_owned(),
        entries: vec![fish("fish-carp", 6, 0.15), fish("fish-perch", 3, 0.35), fish("fish-catfish", 1, 0.6)],
        junk_chance: units::from_authoring::<units::Probability>(0.15),
        junk_item_id: Some("junk-boot".to_owned()),
        ..FishTable::default()
    }]
}

fn drop(item_id: &str, min: u32, max: u32) -> NodeDrop {
    NodeDrop { item_id: item_id.to_owned(), min, max, weight: 1, ..NodeDrop::default() }
}

#[allow(clippy::too_many_arguments)]
fn node_type(
    id: &str,
    name: &str,
    health: i32,
    required_tool: &str,
    required_tool_tier: i32,
    drops: Vec<NodeDrop>,
    respawn_days: Option<u32>,
    color: &str,
    blocks_movement: bool,
) -> NodeTypeDefinition {
    NodeTypeDefinition {
        id: id.to_owned(),
        name: name.to_owned(),
        health,
        required_tool: required_tool.to_owned(),
        required_tool_tier,
        drops,
        // The built-in content spells the key out (`respawnDays: null`).
        respawn_days: Some(respawn_days),
        color: color.to_owned(),
        blocks_movement,
        ..NodeTypeDefinition::default()
    }
}

/// Ore node types used by generated mine floors (M4f).
pub fn mine_node_types() -> Vec<NodeTypeDefinition> {
    vec![
        node_type(
            "node-mine-rock",
            "Mine Rock",
            2,
            "pickaxe",
            1,
            vec![drop("material-stone", 1, 2)],
            None,
            "#6e6e78",
            true,
        ),
        node_type(
            "node-copper-ore",
            "Copper Node",
            3,
            "pickaxe",
            1,
            vec![drop("ore-copper", 1, 3)],
            None,
            "#b87333",
            true,
        ),
        node_type("node-iron-ore", "Iron Node", 4, "pickaxe", 2, vec![drop("ore-iron", 1, 3)], None, "#a19d94", true),
        node_type(
            "node-quartz",
            "Quartz Crystal",
            2,
            "pickaxe",
            1,
            vec![drop("gem-quartz", 1, 1)],
            None,
            "#cfe3ee",
            true,
        ),
    ]
}

fn rock(node_type_id: &str, weight: u32) -> MineRockWeight {
    MineRockWeight { node_type_id: node_type_id.to_owned(), weight }
}

/// Default mine configuration used when a project enables mining.
pub fn create_default_mine_bands() -> Vec<MineBand> {
    vec![
        MineBand {
            from_floor: 1,
            to_floor: 7,
            density: 300,
            rocks: vec![rock("node-mine-rock", 6), rock("node-copper-ore", 3), rock("node-quartz", 1)],
        },
        MineBand {
            from_floor: 8,
            to_floor: 20,
            density: 350,
            rocks: vec![
                rock("node-mine-rock", 4),
                rock("node-copper-ore", 3),
                rock("node-iron-ore", 3),
                rock("node-quartz", 1),
            ],
        },
    ]
}

/// Built-in gathering node types (content-defined; projects can add more).
pub fn default_node_types() -> Vec<NodeTypeDefinition> {
    vec![
        node_type("node-tree", "Tree", 4, "axe", 1, vec![drop("material-wood", 2, 4)], None, "#3f6d33", true),
        node_type("node-stump", "Stump", 2, "axe", 1, vec![drop("material-wood", 1, 2)], Some(3), "#6d5233", true),
        node_type("node-rock", "Rock", 3, "pickaxe", 1, vec![drop("material-stone", 1, 3)], Some(3), "#8a8a95", true),
        node_type(
            "node-boulder",
            "Boulder",
            6,
            "pickaxe",
            2,
            vec![drop("material-stone", 4, 8)],
            None,
            "#5e5e6b",
            true,
        ),
        node_type("node-weeds", "Weeds", 1, "scythe", 1, vec![drop("material-fiber", 1, 2)], Some(2), "#7d9a3f", false),
    ]
}

fn stock(item_id: &str) -> ShopStockEntry {
    ShopStockEntry { item_id: item_id.to_owned(), ..ShopStockEntry::default() }
}

/// The starter game's general store.
pub fn create_default_shop() -> ShopDefinition {
    let mut entries: Vec<ShopStockEntry> = crop_definitions()
        .values()
        .map(|crop| ShopStockEntry {
            item_id: format!("seed-{}", crop.id),
            seasons: Some(crop.seasons.clone()),
            ..ShopStockEntry::default()
        })
        .collect();
    entries.push(stock("fertilizer-basic"));
    entries.push(ShopStockEntry {
        item_id: "fertilizer-quality".to_owned(),
        daily_limit: Some(5),
        ..ShopStockEntry::default()
    });
    for item_id in [
        "tool-hoe",
        "tool-watering-can",
        "tool-axe",
        "tool-pickaxe",
        "tool-scythe",
        "tool-hoe-2",
        "tool-watering-can-2",
        "tool-axe-2",
        "tool-pickaxe-2",
        "tool-fishing-rod",
        "feed-hay",
        "machine-furnace",
        "machine-preserves",
    ] {
        entries.push(stock(item_id));
    }
    ShopDefinition {
        id: "shop-general".to_owned(),
        name: "General Store".to_owned(),
        stock: entries,
        sell_price_multiplier: units::MILLI_ONE,
        buys_items: true,
        repairs_tools: true,
        repair_cost_per_point: 500,
        ..ShopDefinition::default()
    }
}
