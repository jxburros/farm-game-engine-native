//! Tool rules — extracted from src/lib/tools.ts, behavior-identical (characterization-tested).
//! Port of `Tools.cs` / tools.ts.

use crate::content_builtin::{self, ToolDefinition};
use crate::schema::{Item, Tile};

/// TS `TOOL_DEFINITIONS[toolType]`. The TS reads `undefined` for an unknown type; this signature
/// cannot say so, so an unknown type yields `ToolDefinition::default()`. Every engine caller
/// resolves a tool slot first (`Inventory.FindToolSlot`), so it only ever sees the six known
/// types; [`try_get_tool_definition`] keeps the JS shape for everything else.
pub fn get_tool_definition(tool_type: &str) -> ToolDefinition {
    try_get_tool_definition(tool_type).unwrap_or_default()
}

/// TS `TOOL_DEFINITIONS[toolType]`; unknown types yield `undefined` (`None`) like TS.
pub fn try_get_tool_definition(tool_type: &str) -> Option<ToolDefinition> {
    content_builtin::tool_definitions().get(tool_type).cloned()
}

/// JS truthiness of an optional string: `undefined` and `""` are falsy.
fn tool_type_of(item: &Item) -> Option<&str> {
    item.tool_type.as_deref().filter(|tool_type| !tool_type.is_empty())
}

pub fn can_use_tool(tool: &Item, target_tile: &Tile) -> bool {
    let Some(tool_type) = tool_type_of(tool) else {
        return false;
    };
    let Some(definition) = try_get_tool_definition(tool_type) else {
        return false;
    };
    definition.valid_targets.contains(&target_tile.r#type)
}

pub fn get_tool_from_item(item: &Item) -> Option<ToolDefinition> {
    let tool_type = tool_type_of(item)?;
    try_get_tool_definition(tool_type)
}

/// JS truthiness of an optional number: `undefined`, `0` and `NaN` are falsy (`None`).
fn truthy_number(value: Option<f64>) -> Option<f64> {
    value.filter(|v| *v != 0.0 && !v.is_nan())
}

pub fn damage_tool_durability(tool: &Item, amount: f64) -> Item {
    let (Some(durability), Some(_)) = (truthy_number(tool.durability), truthy_number(tool.max_durability)) else {
        return tool.clone();
    };
    Item { durability: Some(f64::max(0.0, durability - amount)), ..tool.clone() }
}

pub fn is_tool_broken(tool: &Item) -> bool {
    // M2 fix: a tool at exactly 0 durability IS broken (the old falsy check
    // meant tools could never break; repair shops make breakage meaningful).
    match (tool.durability, tool.max_durability) {
        (Some(durability), Some(_)) => durability <= 0.0,
        _ => false,
    }
}

pub fn repair_tool(tool: &Item, amount: Option<f64>) -> Item {
    let (Some(durability), Some(max_durability)) = (truthy_number(tool.durability), truthy_number(tool.max_durability))
    else {
        return tool.clone();
    };
    let repair_amount = truthy_number(amount).unwrap_or(max_durability);
    Item { durability: Some(f64::min(max_durability, durability + repair_amount)), ..tool.clone() }
}

/// Port of tests/unit/tools.characterization.test.ts (`ToolsCharacterizationTests.cs`;
/// src/lib/tools.ts re-exports the Core tool rules). Pins current behavior, quirks included:
/// durability 0 / maxDurability 0 are falsy no-ops for damage and repair, repair amount 0 is a
/// FULL repair, negative damage heals without a clamp, and canUseTool only inspects tile.type.
#[cfg(test)]
mod tests {
    use super::*;
    use crate::js;
    use crate::stable_json;

    const ALL_TOOL_TYPES: [&str; 6] = ["watering-can", "hoe", "axe", "pickaxe", "scythe", "fishing-rod"];

    fn make_item(r#type: &str, tool_type: Option<&str>, durability: Option<f64>, max_durability: Option<f64>) -> Item {
        Item {
            id: "item-1".to_owned(),
            name: "Test Item".to_owned(),
            description: "A test item".to_owned(),
            r#type: r#type.to_owned(),
            stackable: false,
            max_stack: 1.0,
            value: 10.0,
            tool_type: tool_type.map(str::to_owned),
            durability,
            max_durability,
            ..Item::default()
        }
    }

    fn make_tool(tool_type: &str, durability: Option<f64>, max_durability: Option<f64>) -> Item {
        make_item("tool", Some(tool_type), durability, max_durability)
    }

    fn make_tile(r#type: &str, background: &str, overlay: Option<&str>, object: Option<&str>) -> Tile {
        Tile {
            x: 0.0,
            y: 0.0,
            r#type: r#type.to_owned(),
            background: background.to_owned(),
            overlay: overlay.map(str::to_owned),
            object: object.map(str::to_owned),
            collision: false,
            soil_moisture: 0.0,
            soil_fertility: 0.0,
            ..Tile::default()
        }
    }

    fn tile(r#type: &str) -> Tile {
        make_tile(r#type, "grass", None, None)
    }

    // --- TOOL_DEFINITIONS content ---

    #[test]
    fn contains_exactly_the_six_known_tool_types() {
        let definitions = content_builtin::tool_definitions();
        let mut keys: Vec<&str> = definitions.keys().map(String::as_str).collect();
        keys.sort_by(|a, b| js::compare_strings(a, b));
        let mut expected = ALL_TOOL_TYPES.to_vec();
        expected.sort_by(|a, b| js::compare_strings(a, b));
        assert_eq!(keys, expected);
    }

    #[test]
    fn pins_the_full_definition_of_every_tool() {
        assert_eq!(
            stable_json::stringify(&content_builtin::tool_definitions()),
            r#"{"axe":{"action":"chop","description":"Chop down trees and wooden obstacles","energyCost":6,"name":"Axe","powerLevel":1,"type":"axe","validTargets":["wall"]},"fishing-rod":{"action":"fish","description":"Catch fish from water tiles","energyCost":3,"name":"Fishing Rod","powerLevel":1,"type":"fishing-rod","validTargets":["water"]},"hoe":{"action":"till","description":"Till grass into farmable soil","energyCost":4,"name":"Hoe","powerLevel":1,"type":"hoe","validTargets":["grass","path"]},"pickaxe":{"action":"mine","description":"Break rocks and mine for ore","energyCost":8,"name":"Pickaxe","powerLevel":1,"type":"pickaxe","validTargets":["wall"]},"scythe":{"action":"harvest","description":"Harvest crops in a large area","energyCost":5,"name":"Scythe","powerLevel":2,"type":"scythe","validTargets":["soil"]},"watering-can":{"action":"water","description":"Water crops to help them grow faster","energyCost":2,"name":"Watering Can","powerLevel":1,"type":"watering-can","validTargets":["soil"]}}"#
        );
    }

    #[test]
    fn only_the_scythe_has_power_level_2() {
        let definitions = content_builtin::tool_definitions();
        for tool_type in ALL_TOOL_TYPES {
            assert_eq!(definitions[tool_type].power_level, if tool_type == "scythe" { 2.0 } else { 1.0 });
        }
    }

    // --- getToolDefinition ---

    #[test]
    fn returns_the_tool_definitions_entry() {
        let definitions = content_builtin::tool_definitions();
        for tool_type in ALL_TOOL_TYPES {
            let definition = get_tool_definition(tool_type);
            assert_eq!(definition, definitions[tool_type]);
            assert_eq!(definition.r#type, tool_type);
            assert_eq!(try_get_tool_definition(tool_type).as_ref(), Some(&definitions[tool_type]));
        }
    }

    #[test]
    fn returns_undefined_for_an_unknown_tool_type_no_guard() {
        assert_eq!(try_get_tool_definition("chainsaw"), None);
        assert_eq!(get_tool_definition("chainsaw"), ToolDefinition::default());
    }

    // --- canUseTool ---

    #[test]
    fn returns_false_for_non_tools_and_unknown_tool_types() {
        assert!(!can_use_tool(&make_item("seed", None, None, None), &tile("soil")));
        assert!(!can_use_tool(&make_item("tool", Some("chainsaw"), None, None), &tile("grass")));
        assert!(!can_use_tool(&make_item("tool", Some(""), None, None), &tile("grass")));
    }

    #[test]
    fn tool_on_tile() {
        let cases: [(&str, &str, bool); 14] = [
            ("watering-can", "soil", true),
            ("watering-can", "grass", false),
            ("watering-can", "water", false),
            ("hoe", "grass", true),
            ("hoe", "path", true),
            ("hoe", "soil", false),
            ("axe", "wall", true),
            ("axe", "grass", false),
            ("pickaxe", "wall", true),
            ("pickaxe", "water", false),
            ("scythe", "soil", true),
            ("scythe", "grass", false),
            ("fishing-rod", "water", true),
            ("fishing-rod", "soil", false),
        ];
        for (tool_type, tile_type, expected) in cases {
            assert_eq!(
                can_use_tool(&make_tool(tool_type, None, None), &tile(tile_type)),
                expected,
                "{tool_type} on {tile_type}"
            );
        }
    }

    #[test]
    fn no_tool_is_usable_on_door_or_floor_tiles() {
        for tool_type in ALL_TOOL_TYPES {
            assert!(!can_use_tool(&make_tool(tool_type, None, None), &tile("door")));
            assert!(!can_use_tool(&make_tool(tool_type, None, None), &tile("floor")));
        }
    }

    #[test]
    fn only_inspects_tile_type_ignoring_layers() {
        // Tile whose layers say "soil object on water background" but type says grass
        let layered = make_tile("grass", "water", Some("path"), Some("soil"));
        assert!(can_use_tool(&make_tool("hoe", None, None), &layered));
        assert!(!can_use_tool(&make_tool("watering-can", None, None), &layered));
        assert!(!can_use_tool(&make_tool("fishing-rod", None, None), &layered));
    }

    #[test]
    fn ignores_durability_entirely() {
        assert!(can_use_tool(&make_tool("hoe", Some(0.0), Some(100.0)), &tile("grass")));
    }

    // --- getToolFromItem ---

    #[test]
    fn get_tool_from_item_returns_the_definition_or_null() {
        let definitions = content_builtin::tool_definitions();
        for tool_type in ALL_TOOL_TYPES {
            assert_eq!(get_tool_from_item(&make_tool(tool_type, None, None)).as_ref(), Some(&definitions[tool_type]));
        }
        assert_eq!(get_tool_from_item(&make_item("crop", None, None, None)), None);
        assert_eq!(get_tool_from_item(&make_item("tool", Some("laser"), None, None)), None);
    }

    // --- damageToolDurability ---

    #[test]
    fn subtracts_the_given_amount_and_returns_a_new_object() {
        let tool = make_tool("axe", Some(50.0), Some(100.0));
        let result = damage_tool_durability(&tool, 10.0);
        assert_ne!(tool, result);
        assert_eq!(result.durability, Some(40.0));
        assert_eq!(result.max_durability, Some(100.0));
        assert_eq!(tool.durability, Some(50.0));
    }

    #[test]
    fn damage_pins_its_behavior() {
        assert_eq!(damage_tool_durability(&make_tool("axe", Some(50.0), Some(100.0)), 1.0).durability, Some(49.0)); // the TS default amount
        assert_eq!(damage_tool_durability(&make_tool("axe", Some(3.0), Some(100.0)), 10.0).durability, Some(0.0)); // clamps at 0
        let no_durability = make_tool("axe", None, Some(100.0));
        assert_eq!(damage_tool_durability(&no_durability, 5.0), no_durability);
        let no_max = make_tool("axe", Some(50.0), None);
        assert_eq!(damage_tool_durability(&no_max, 5.0), no_max);
        // QUIRK: durability 0 is falsy, so a fully depleted tool is a no-op
        let depleted = make_tool("axe", Some(0.0), Some(100.0));
        assert_eq!(damage_tool_durability(&depleted, 5.0), depleted);
        // QUIRK: maxDurability 0 is falsy
        let zero_max = make_tool("axe", Some(50.0), Some(0.0));
        assert_eq!(damage_tool_durability(&zero_max, 5.0), zero_max);
        // QUIRK: a negative amount heals the tool with no upper clamp
        assert_eq!(damage_tool_durability(&make_tool("axe", Some(95.0), Some(100.0)), -10.0).durability, Some(105.0));
    }

    // --- isToolBroken ---

    #[test]
    fn is_tool_broken_pins_its_behavior() {
        assert!(!is_tool_broken(&make_tool("hoe", None, None)));
        assert!(is_tool_broken(&make_tool("hoe", Some(0.0), Some(100.0)))); // M2 fix: exactly 0 IS broken
        assert!(is_tool_broken(&make_tool("hoe", Some(-1.0), Some(100.0))));
        assert!(is_tool_broken(&make_tool("hoe", Some(-100.0), Some(100.0))));
        assert!(!is_tool_broken(&make_tool("hoe", Some(1.0), Some(100.0))));
        assert!(!is_tool_broken(&make_tool("hoe", Some(100.0), Some(100.0))));
        assert!(!is_tool_broken(&make_tool("hoe", Some(-5.0), None))); // requires maxDurability
    }

    // --- repairTool ---

    #[test]
    fn partial_repair_adds_the_given_amount_and_returns_a_new_object() {
        let tool = make_tool("pickaxe", Some(40.0), Some(100.0));
        let result = repair_tool(&tool, Some(25.0));
        assert_ne!(tool, result);
        assert_eq!(result.durability, Some(65.0));
        assert_eq!(tool.durability, Some(40.0));
    }

    #[test]
    fn repair_pins_its_behavior() {
        assert_eq!(repair_tool(&make_tool("pickaxe", Some(7.0), Some(100.0)), None).durability, Some(100.0)); // full repair
        assert_eq!(repair_tool(&make_tool("pickaxe", Some(90.0), Some(100.0)), Some(50.0)).durability, Some(100.0)); // clamps
        let no_durability = make_tool("pickaxe", None, Some(100.0));
        assert_eq!(repair_tool(&no_durability, Some(10.0)), no_durability);
        let no_max = make_tool("pickaxe", Some(40.0), None);
        assert_eq!(repair_tool(&no_max, Some(10.0)), no_max);
        // QUIRK: a fully depleted tool can NEVER be repaired
        let depleted = make_tool("pickaxe", Some(0.0), Some(100.0));
        assert_eq!(repair_tool(&depleted, Some(50.0)), depleted);
        assert_eq!(repair_tool(&depleted, None), depleted);
        // QUIRK: amount 0 is falsy → FULL repair
        assert_eq!(repair_tool(&make_tool("pickaxe", Some(30.0), Some(100.0)), Some(0.0)).durability, Some(100.0));
        // a negative amount reduces durability (upper clamp only)
        assert_eq!(repair_tool(&make_tool("pickaxe", Some(30.0), Some(100.0)), Some(-10.0)).durability, Some(20.0));
    }
}
