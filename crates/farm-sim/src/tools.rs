//! Tools (port of `Tools.cs` / tools.ts).

use crate::content_builtin::ToolDefinition;
use crate::schema::{Item, Tile};

pub fn get_tool_definition(tool_type: &str) -> ToolDefinition {
    let _ = tool_type;
    todo!("port Tools.GetToolDefinition")
}

pub fn can_use_tool(tool: &Item, target_tile: &Tile) -> bool {
    let _ = (tool, target_tile);
    todo!("port Tools.CanUseTool")
}

pub fn get_tool_from_item(item: &Item) -> Option<ToolDefinition> {
    let _ = item;
    todo!("port Tools.GetToolFromItem")
}

pub fn damage_tool_durability(tool: &Item, amount: f64) -> Item {
    let _ = (tool, amount);
    todo!("port Tools.DamageToolDurability")
}

pub fn is_tool_broken(tool: &Item) -> bool {
    let _ = tool;
    todo!("port Tools.IsToolBroken")
}

pub fn repair_tool(tool: &Item, amount: Option<f64>) -> Item {
    let _ = (tool, amount);
    todo!("port Tools.RepairTool")
}
