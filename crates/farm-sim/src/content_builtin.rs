//! Built-in content definitions (port of `ContentBuiltin.cs` / content-builtin.ts). These
//! migrate into the `content-default` pack in M5; until then they live here so both the engine
//! and the editor consume a single copy.

use crate::schema::{
    AnimalSpeciesDefinition, CropDefinition, CropMultiTile, FishTable, FishTableEntry, Item, MachineTypeDefinition,
    MineBand, MineRockWeight, NodeDrop, NodeTypeDefinition, RecipeDefinition, RecipeIngredient, RecipeSkillRequirement,
    RecipeUnlock, ShopDefinition, ShopStockEntry,
};
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
    pub energy_cost: f64,
    pub power_level: f64,
}

fn strings(values: &[&str]) -> Vec<String> {
    values.iter().map(|value| (*value).to_owned()).collect()
}

#[allow(clippy::too_many_arguments)]
fn crop(
    id: &str,
    name: &str,
    seed_cost: f64,
    base_harvest_value: f64,
    growth_time: f64,
    growth_days: f64,
    stages: f64,
    seasons: &[&str],
    yield_min: f64,
    yield_max: f64,
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
        mutation_chance: Some(mutation_chance),
        ..CropDefinition::default()
    }
}

fn regrowing(mut definition: CropDefinition, regrowth_time: f64, regrowth_days: f64) -> CropDefinition {
    definition.can_regrow = true;
    definition.regrowth_time = Some(regrowth_time);
    definition.regrowth_days = Some(regrowth_days);
    definition
}

fn multi_tile(mut definition: CropDefinition, width: f64, height: f64) -> CropDefinition {
    definition.multi_tile = Some(CropMultiTile { width, height });
    definition
}

/// TS `CROP_DEFINITIONS`, keyed by crop id in declaration order.
pub fn crop_definitions() -> IndexMap<String, CropDefinition> {
    let definitions = [
        crop("wheat", "Wheat", 10.0, 25.0, 15000.0, 3.0, 4.0, &["spring", "fall"], 1.0, 2.0, 0.01),
        crop("corn", "Corn", 15.0, 40.0, 25000.0, 5.0, 5.0, &["summer", "fall"], 1.0, 3.0, 0.015),
        regrowing(crop("tomato", "Tomato", 20.0, 50.0, 20000.0, 4.0, 5.0, &["summer"], 1.0, 3.0, 0.02), 8000.0, 2.0),
        crop("carrot", "Carrot", 8.0, 20.0, 12000.0, 2.0, 4.0, &["spring", "fall", "winter"], 1.0, 2.0, 0.008),
        crop("potato", "Potato", 12.0, 30.0, 18000.0, 4.0, 4.0, &["spring", "fall"], 2.0, 4.0, 0.012),
        regrowing(
            crop("strawberry", "Strawberry", 30.0, 60.0, 22000.0, 4.0, 5.0, &["spring"], 1.0, 2.0, 0.025),
            10000.0,
            2.0,
        ),
        multi_tile(crop("pumpkin", "Pumpkin", 50.0, 150.0, 35000.0, 7.0, 6.0, &["fall"], 1.0, 1.0, 0.05), 2.0, 2.0),
        multi_tile(
            crop("cauliflower", "Cauliflower", 40.0, 120.0, 28000.0, 6.0, 5.0, &["spring"], 1.0, 1.0, 0.04),
            2.0,
            2.0,
        ),
        regrowing(
            crop("blueberry", "Blueberry", 35.0, 70.0, 24000.0, 5.0, 5.0, &["summer"], 2.0, 5.0, 0.03),
            9000.0,
            2.0,
        ),
    ];
    definitions.into_iter().map(|definition| (definition.id.clone(), definition)).collect()
}

/// Keyed by [`crate::schema::crop_qualities`].
pub fn quality_multipliers() -> IndexMap<String, f64> {
    [("normal", 1.0), ("silver", 1.25), ("gold", 1.5), ("iridium", 2.0)]
        .into_iter()
        .map(|(key, value)| (key.to_owned(), value))
        .collect()
}

/// Keyed by 'none' | 'giant' | 'golden' | 'ancient'.
pub fn mutation_multipliers() -> IndexMap<String, f64> {
    [("none", 1.0), ("giant", 2.5), ("golden", 3.0), ("ancient", 4.0)]
        .into_iter()
        .map(|(key, value)| (key.to_owned(), value))
        .collect()
}

pub const DAYS_PER_SEASON: f64 = 28.0;
pub const SEASON_ORDER: &[&str] = &["spring", "summer", "fall", "winter"];

fn tool_definition(
    r#type: &str,
    name: &str,
    description: &str,
    action: &str,
    valid_targets: &[&str],
    energy_cost: f64,
    power_level: f64,
) -> ToolDefinition {
    ToolDefinition {
        r#type: r#type.to_owned(),
        name: name.to_owned(),
        description: description.to_owned(),
        action: action.to_owned(),
        valid_targets: strings(valid_targets),
        energy_cost,
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
            2.0,
            1.0,
        ),
        tool_definition("hoe", "Hoe", "Till grass into farmable soil", "till", &["grass", "path"], 4.0, 1.0),
        tool_definition("axe", "Axe", "Chop down trees and wooden obstacles", "chop", &["wall"], 6.0, 1.0),
        tool_definition("pickaxe", "Pickaxe", "Break rocks and mine for ore", "mine", &["wall"], 8.0, 1.0),
        tool_definition("scythe", "Scythe", "Harvest crops in a large area", "harvest", &["soil"], 5.0, 2.0),
        tool_definition("fishing-rod", "Fishing Rod", "Catch fish from water tiles", "fish", &["water"], 3.0, 1.0),
    ];
    definitions.into_iter().map(|definition| (definition.r#type.clone(), definition)).collect()
}

fn tool(id: &str, name: &str, description: &str, value: f64, tool_type: &str) -> Item {
    Item {
        id: id.to_owned(),
        name: name.to_owned(),
        description: description.to_owned(),
        r#type: "tool".to_owned(),
        stackable: false,
        max_stack: 1.0,
        value,
        tool_type: Some(tool_type.to_owned()),
        tool_power: Some(1.0),
        durability: Some(100.0),
        max_durability: Some(100.0),
        ..Item::default()
    }
}

/// Tier-2 tool upgrades (shop offers).
fn tool2(id: &str, name: &str, description: &str, value: f64, tool_type: &str) -> Item {
    Item {
        id: id.to_owned(),
        name: name.to_owned(),
        description: description.to_owned(),
        r#type: "tool".to_owned(),
        stackable: false,
        max_stack: 1.0,
        value,
        tool_type: Some(tool_type.to_owned()),
        tool_power: Some(2.0),
        tool_tier: Some(2.0),
        durability: Some(200.0),
        max_durability: Some(200.0),
        ..Item::default()
    }
}

fn plain(id: &str, name: &str, description: &str, r#type: &str, max_stack: f64, value: f64) -> Item {
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
            max_stack: 99.0,
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
            max_stack: 99.0,
            value: crop.base_harvest_value,
            crop_type: Some(crop.id.clone()),
            ..Item::default()
        });
    }

    items.push(tool("tool-hoe", "Hoe", "Till grass into soil for planting", 50.0, "hoe"));
    items.push(tool("tool-watering-can", "Watering Can", "Water your crops to help them grow", 50.0, "watering-can"));
    items.push(tool("tool-axe", "Axe", "Chop down trees and wooden objects", 100.0, "axe"));
    items.push(tool("tool-pickaxe", "Pickaxe", "Break rocks and mine ore", 150.0, "pickaxe"));
    items.push(tool("tool-scythe", "Scythe", "Harvest crops quickly in an area", 200.0, "scythe"));

    // Tier-2 tool upgrades (shop offers)
    items.push(tool2("tool-hoe-2", "Copper Hoe", "A sturdier hoe — tills with less effort", 250.0, "hoe"));
    items.push(tool2(
        "tool-watering-can-2",
        "Copper Watering Can",
        "Waters a wider area with less effort",
        250.0,
        "watering-can",
    ));
    items.push(tool2("tool-axe-2", "Copper Axe", "Fells trees in fewer swings", 400.0, "axe"));
    items.push(tool2("tool-pickaxe-2", "Copper Pickaxe", "Cracks boulders lesser picks cannot", 500.0, "pickaxe"));

    // Gathering materials
    items.push(plain("material-wood", "Wood", "Sturdy timber from trees and stumps", "material", 999.0, 5.0));
    items.push(plain("material-stone", "Stone", "Rough stone chipped from rocks", "material", 999.0, 4.0));
    items.push(plain("material-fiber", "Fiber", "Plant fiber cut from weeds", "material", 999.0, 2.0));

    items.push(plain(
        "fertilizer-basic",
        "Basic Fertilizer",
        "Improves soil quality and crop growth",
        "fertilizer",
        99.0,
        10.0,
    ));
    items.push(plain(
        "fertilizer-quality",
        "Quality Fertilizer",
        "Increases chance of higher quality crops",
        "fertilizer",
        99.0,
        25.0,
    ));
    items.push(plain("gift-flower", "Flower", "A beautiful flower that makes a nice gift", "gift", 99.0, 20.0));

    // M4: fishing rod + catches
    items.push(tool("tool-fishing-rod", "Fishing Rod", "Catch fish from water tiles", 120.0, "fishing-rod"));
    items.push(plain("fish-carp", "Carp", "A common pond fish", "fish", 99.0, 18.0));
    items.push(plain("fish-perch", "Perch", "A quick freshwater fish", "fish", 99.0, 30.0));
    items.push(plain("fish-catfish", "Catfish", "A prized whiskered catch", "fish", 99.0, 75.0));
    items.push(plain("junk-boot", "Old Boot", "Someone lost this a long time ago", "material", 99.0, 1.0));

    // M4: ranching
    items.push(plain("feed-hay", "Hay", "Animal feed — one serving a day keeps them happy", "material", 999.0, 3.0));
    items.push(plain("product-egg", "Egg", "A fresh egg", "crop", 99.0, 22.0));
    items.push(plain("product-milk", "Milk", "A pail of fresh milk", "crop", 99.0, 48.0));

    // M4: ores & bars (mining → crafting chain)
    items.push(plain("ore-copper", "Copper Ore", "Raw copper, ready for smelting", "material", 999.0, 12.0));
    items.push(plain("ore-iron", "Iron Ore", "Raw iron, ready for smelting", "material", 999.0, 20.0));
    items.push(plain("gem-quartz", "Quartz", "A translucent crystal", "material", 999.0, 40.0));
    items.push(plain("bar-copper", "Copper Bar", "A smelted copper ingot", "material", 999.0, 45.0));
    items.push(plain("bar-iron", "Iron Bar", "A smelted iron ingot", "material", 999.0, 80.0));

    // M4: placeable machines
    items.push(plain(
        "machine-furnace",
        "Furnace",
        "Smelts ore into bars (place it, then load a recipe)",
        "material",
        9.0,
        150.0,
    ));
    items.push(plain(
        "machine-preserves",
        "Preserves Jar",
        "Turns crops into preserves worth more",
        "material",
        9.0,
        200.0,
    ));
    items.push(plain("food-preserves", "Preserves", "Sweet preserved produce", "crop", 99.0, 120.0));

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

fn ingredient(item_id: &str, quantity: f64) -> RecipeIngredient {
    RecipeIngredient { item_id: item_id.to_owned(), quantity }
}

fn skill_unlock(skill: &str, level: f64) -> Option<RecipeUnlock> {
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
    processing_minutes: f64,
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
            vec![ingredient("material-stone", 10.0), ingredient("ore-copper", 2.0)],
            vec![ingredient("machine-furnace", 1.0)],
            0.0,
            None,
            "crafting",
            None,
        ),
        recipe(
            "recipe-craft-preserves-jar",
            "Preserves Jar",
            vec![ingredient("material-wood", 12.0), ingredient("material-stone", 4.0)],
            vec![ingredient("machine-preserves", 1.0)],
            0.0,
            None,
            "crafting",
            skill_unlock("farming", 1.0),
        ),
        recipe(
            "recipe-craft-hay",
            "Hay Bundle",
            vec![ingredient("material-fiber", 3.0)],
            vec![ingredient("feed-hay", 2.0)],
            0.0,
            None,
            "farming",
            None,
        ),
        recipe(
            "recipe-smelt-copper",
            "Copper Bar",
            vec![ingredient("ore-copper", 3.0), ingredient("material-wood", 1.0)],
            vec![ingredient("bar-copper", 1.0)],
            120.0,
            Some("machine-furnace"),
            "smithing",
            None,
        ),
        recipe(
            "recipe-smelt-iron",
            "Iron Bar",
            vec![ingredient("ore-iron", 3.0), ingredient("material-wood", 1.0)],
            vec![ingredient("bar-iron", 1.0)],
            180.0,
            Some("machine-furnace"),
            "smithing",
            skill_unlock("mining", 2.0),
        ),
        recipe(
            "recipe-preserve-wheat",
            "Wheat Preserves",
            vec![ingredient("crop-wheat", 3.0)],
            vec![ingredient("food-preserves", 1.0)],
            360.0,
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
            purchase_cost: 400.0,
            feed_item_id: Some("feed-hay".to_owned()),
            product_item_id: "product-egg".to_owned(),
            product_interval_days: 1.0,
            days_to_adult: 3.0,
            color: "#e8e0c3".to_owned(),
            ..AnimalSpeciesDefinition::default()
        },
        AnimalSpeciesDefinition {
            id: "animal-cow".to_owned(),
            name: "Cow".to_owned(),
            purchase_cost: 1500.0,
            feed_item_id: Some("feed-hay".to_owned()),
            product_item_id: "product-milk".to_owned(),
            product_interval_days: 2.0,
            days_to_adult: 5.0,
            color: "#d3b28a".to_owned(),
            ..AnimalSpeciesDefinition::default()
        },
    ]
}

fn fish(item_id: &str, weight: f64, difficulty: f64) -> FishTableEntry {
    FishTableEntry { item_id: item_id.to_owned(), weight, difficulty }
}

/// Built-in fish tables (M4e): any water, tuned per season.
pub fn create_default_fish_tables() -> Vec<FishTable> {
    vec![FishTable {
        id: "fish-table-default".to_owned(),
        name: "Pond Fish".to_owned(),
        entries: vec![fish("fish-carp", 6.0, 0.15), fish("fish-perch", 3.0, 0.35), fish("fish-catfish", 1.0, 0.6)],
        junk_chance: 0.15,
        junk_item_id: Some("junk-boot".to_owned()),
        ..FishTable::default()
    }]
}

fn drop(item_id: &str, min: f64, max: f64) -> NodeDrop {
    NodeDrop { item_id: item_id.to_owned(), min, max, weight: 1.0, ..NodeDrop::default() }
}

#[allow(clippy::too_many_arguments)]
fn node_type(
    id: &str,
    name: &str,
    health: f64,
    required_tool: &str,
    required_tool_tier: f64,
    drops: Vec<NodeDrop>,
    respawn_days: Option<f64>,
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
        respawn_days,
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
            2.0,
            "pickaxe",
            1.0,
            vec![drop("material-stone", 1.0, 2.0)],
            None,
            "#6e6e78",
            true,
        ),
        node_type(
            "node-copper-ore",
            "Copper Node",
            3.0,
            "pickaxe",
            1.0,
            vec![drop("ore-copper", 1.0, 3.0)],
            None,
            "#b87333",
            true,
        ),
        node_type(
            "node-iron-ore",
            "Iron Node",
            4.0,
            "pickaxe",
            2.0,
            vec![drop("ore-iron", 1.0, 3.0)],
            None,
            "#a19d94",
            true,
        ),
        node_type(
            "node-quartz",
            "Quartz Crystal",
            2.0,
            "pickaxe",
            1.0,
            vec![drop("gem-quartz", 1.0, 1.0)],
            None,
            "#cfe3ee",
            true,
        ),
    ]
}

fn rock(node_type_id: &str, weight: f64) -> MineRockWeight {
    MineRockWeight { node_type_id: node_type_id.to_owned(), weight }
}

/// Default mine configuration used when a project enables mining.
pub fn create_default_mine_bands() -> Vec<MineBand> {
    vec![
        MineBand {
            from_floor: 1.0,
            to_floor: 7.0,
            density: 0.3,
            rocks: vec![rock("node-mine-rock", 6.0), rock("node-copper-ore", 3.0), rock("node-quartz", 1.0)],
        },
        MineBand {
            from_floor: 8.0,
            to_floor: 20.0,
            density: 0.35,
            rocks: vec![
                rock("node-mine-rock", 4.0),
                rock("node-copper-ore", 3.0),
                rock("node-iron-ore", 3.0),
                rock("node-quartz", 1.0),
            ],
        },
    ]
}

/// Built-in gathering node types (content-defined; projects can add more).
pub fn default_node_types() -> Vec<NodeTypeDefinition> {
    vec![
        node_type("node-tree", "Tree", 4.0, "axe", 1.0, vec![drop("material-wood", 2.0, 4.0)], None, "#3f6d33", true),
        node_type(
            "node-stump",
            "Stump",
            2.0,
            "axe",
            1.0,
            vec![drop("material-wood", 1.0, 2.0)],
            Some(3.0),
            "#6d5233",
            true,
        ),
        node_type(
            "node-rock",
            "Rock",
            3.0,
            "pickaxe",
            1.0,
            vec![drop("material-stone", 1.0, 3.0)],
            Some(3.0),
            "#8a8a95",
            true,
        ),
        node_type(
            "node-boulder",
            "Boulder",
            6.0,
            "pickaxe",
            2.0,
            vec![drop("material-stone", 4.0, 8.0)],
            None,
            "#5e5e6b",
            true,
        ),
        node_type(
            "node-weeds",
            "Weeds",
            1.0,
            "scythe",
            1.0,
            vec![drop("material-fiber", 1.0, 2.0)],
            Some(2.0),
            "#7d9a3f",
            false,
        ),
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
        daily_limit: Some(5.0),
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
        sell_price_multiplier: 1.0,
        buys_items: true,
        repairs_tools: true,
        repair_cost_per_point: 0.5,
        ..ShopDefinition::default()
    }
}
