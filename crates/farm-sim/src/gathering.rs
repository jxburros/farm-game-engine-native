//! Gathering nodes (M2): trees, rocks, weeds… content-defined node types with health, required
//! tool + tier, weighted drop tables and respawn rules. (Port of `Gathering.cs` /
//! engine-core/src/gathering.ts.)

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::hooks::{GatherDrop, HookEvent, ResourceGatherHookPayload};
use crate::rng::Rng;
use crate::schema::{tool_types, GameState, NodeTypeDefinition, Tile, TileNode};
use crate::{inventory, mines, quests, skills};

/// TS `NodeStrikeOutcome` minus the state (updated in place).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NodeStrikeOutcome {
    pub effects: Effects,
    /// True when the strike consumed the action (energy/durability should apply).
    pub struck: bool,
}

pub fn node_type_by_id<'a>(ctx: &'a EngineContext, type_id: &str) -> Option<&'a NodeTypeDefinition> {
    ctx.node_type(type_id)
}

pub fn is_node_active(tile: &Tile) -> bool {
    tile.node.as_ref().is_some_and(|node| node.remaining_health > 0)
}

/// JS `str.replace('-', ' ')`: replaces the FIRST occurrence only.
pub fn replace_first_dash(value: &str) -> String {
    value.replacen('-', " ", 1)
}

/// Strike the node on (sceneId, x, y) with the given tool. Assumes the caller already verified
/// a node is present.
#[allow(clippy::too_many_arguments)]
pub fn strike_node(
    ctx: &EngineContext,
    state: &mut GameState,
    scene_id: &str,
    x: i32,
    y: i32,
    tool_type: &str,
    tool_tier: i32,
    tool_power: i32,
) -> NodeStrikeOutcome {
    let Some(scene_index) = state.world.scenes.iter().position(|scene| scene.id == scene_id) else {
        return NodeStrikeOutcome::default();
    };
    let Some(node) = state.world.scenes[scene_index].tile(x, y).and_then(|tile| tile.node.clone()) else {
        return NodeStrikeOutcome::default();
    };
    if node.remaining_health <= 0 {
        return NodeStrikeOutcome::default();
    }

    let Some(definition) = node_type_by_id(ctx, &node.type_id) else {
        return NodeStrikeOutcome::default();
    };

    if let Some(refused) = tool_refusal(definition, tool_type, tool_tier) {
        return NodeStrikeOutcome { effects: vec![refused], struck: false };
    }

    let damage = tool_power.max(1);
    let remaining = node.remaining_health.saturating_sub(damage);

    let mut effects: Effects = Vec::new();
    let added_drops = if remaining > 0 {
        if let Some(tile) = state.world.scenes[scene_index].tile_mut(x, y) {
            tile.node = Some(TileNode { remaining_health: remaining, ..node.clone() });
        }
        effects.push(Effect::message(
            message_levels::INFO,
            format!("{}: {}/{}", definition.name, remaining, definition.health),
        ));
        Vec::new()
    } else {
        deplete_node(ctx, state, (scene_index, x, y), &node, definition, &mut effects)
    };

    // Gathered drops count toward collect objectives (M3).
    for drop in &added_drops {
        effects.extend(quests::progress_quests(ctx, state, "collect", &drop.item_id, drop.quantity));
    }

    if remaining <= 0 {
        // Skill XP (M4g): axes/scythes train foraging, pickaxes train mining.
        let skill = if tool_type == tool_types::PICKAXE { "mining" } else { "foraging" };
        effects.extend(skills::grant_xp(ctx, state, skill, 5));

        // Mine floors: breaking a rock can reveal the ladder down (M4f).
        effects.extend(mines::maybe_reveal_ladder(ctx, state, scene_id, x, y));
    }

    NodeStrikeOutcome { effects, struck: true }
}

/// Why the tool cannot strike a node of `definition` (the wrong tool, or too low a tier).
fn tool_refusal(definition: &NodeTypeDefinition, tool_type: &str, tool_tier: i32) -> Option<Effect> {
    if definition.required_tool != tool_type {
        return Some(Effect::message(
            message_levels::INFO,
            format!("{} needs a {}.", definition.name, replace_first_dash(&definition.required_tool)),
        ));
    }
    if tool_tier < definition.required_tool_tier {
        return Some(Effect::message(
            message_levels::INFO,
            format!(
                "Your {} isn't strong enough for {}.",
                replace_first_dash(tool_type),
                definition.name.to_lowercase()
            ),
        ));
    }
    None
}

/// Roll the weighted drop table once: at most one drop.
fn roll_drops(definition: &NodeTypeDefinition, rng: &mut Rng) -> Vec<GatherDrop> {
    let mut drops: Vec<GatherDrop> = Vec::new();
    if definition.drops.is_empty() {
        return drops;
    }
    let weights: Vec<u32> = definition.drops.iter().map(|drop| drop.weight).collect();
    if let Some(index) = rng.weighted(&weights) {
        let drop = &definition.drops[index];
        let quantity = if drop.max > drop.min {
            // min ≤ result ≤ max
            rng.int(i64::from(drop.min), i64::from(drop.max)) as u32
        } else {
            drop.min
        };
        if quantity > 0 {
            drops.push(GatherDrop { item_id: drop.item_id.clone(), quantity });
        }
    }
    drops
}

/// The last strike: roll the drops into the inventory (what does not fit stays behind),
/// deplete or clear the node, announce it and emit `onResourceGather`. Returns the drops the
/// player received.
fn deplete_node(
    ctx: &EngineContext,
    state: &mut GameState,
    (scene_index, x, y): (usize, i32, i32),
    node: &TileNode,
    definition: &NodeTypeDefinition,
    effects: &mut Effects,
) -> Vec<GatherDrop> {
    let mut rng = Rng::new(state.rng.clone());
    let drops = roll_drops(definition, &mut rng);

    let mut added_drops: Vec<GatherDrop> = Vec::new();
    let mut inventory = state.player.inventory.clone();
    let mut received: Vec<String> = Vec::new();
    for drop in &drops {
        let Some(item) = ctx.item(&drop.item_id) else {
            continue;
        };
        let result = inventory::add_item(&inventory, item, drop.quantity, state.player.max_inventory_size, None);
        if result.added {
            inventory = result.inventory;
            received.push(format!("{}x {}", drop.quantity, item.name));
            added_drops.push(drop.clone());
        } else {
            effects.push(Effect::message(message_levels::ERROR, "Inventory is full!"));
        }
    }
    state.player.inventory = inventory;

    let depleted_on_day = state.clock.day;
    // The tile exists: the node was read from it.
    if let Some(tile) = state.world.scenes[scene_index].tile_mut(x, y) {
        if definition.respawn_after().is_some() {
            tile.node = Some(TileNode {
                type_id: node.type_id.clone(),
                remaining_health: 0,
                depleted_on_day: Some(depleted_on_day),
                ..TileNode::default()
            });
        } else {
            tile.node = None;
        }
    }

    effects.push(Effect::message(
        message_levels::SUCCESS,
        if received.is_empty() {
            format!("{} cleared!", definition.name)
        } else {
            format!("{} cleared! Got {}", definition.name, received.join(", "))
        },
    ));
    ctx.emit(HookEvent::ResourceGather(ResourceGatherHookPayload { node_type_id: definition.id.clone(), drops }));

    state.rng = rng.state;
    added_drops
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replace_first_dash_only_touches_the_first_occurrence() {
        assert_eq!(replace_first_dash("watering-can"), "watering can");
        assert_eq!(replace_first_dash("fishing-rod-2"), "fishing rod-2");
        assert_eq!(replace_first_dash("axe"), "axe");
        assert_eq!(replace_first_dash(""), "");
    }

    #[test]
    fn a_node_is_active_only_while_it_has_health() {
        let mut tile = Tile::default();
        assert!(!is_node_active(&tile));
        tile.node = Some(TileNode { type_id: "node-tree".to_owned(), remaining_health: 0, ..TileNode::default() });
        assert!(!is_node_active(&tile));
        tile.node = Some(TileNode { type_id: "node-tree".to_owned(), remaining_health: 1, ..TileNode::default() });
        assert!(is_node_active(&tile));
    }

    #[test]
    fn node_type_by_id_reads_the_content_list() {
        let content = crate::schema::GameContent {
            node_types: crate::content_builtin::default_node_types(),
            ..crate::schema::GameContent::default()
        };
        let ctx = EngineContext::new(content);
        assert_eq!(node_type_by_id(&ctx, "node-tree").map(|def| def.required_tool.as_str()), Some("axe"));
        assert!(node_type_by_id(&ctx, "node-nope").is_none());
    }
}

/// Port of m2-systems.test.ts (`M2SystemsTests.cs`): the gathering cases. They drive the engine
/// dispatch (`useTool`, `move`, `sleep`), so they wait for those ports.
#[cfg(test)]
mod m2_systems_tests {
    use crate::commands::Command;
    use crate::engine::apply_command;
    use crate::engine_types::EngineContext;
    use crate::farming::farming_actions::test_support::{has_message, make_m2_engine, slot};
    use crate::schema::{GameState, TileNode};

    fn use_tool(tool: &str) -> Command {
        Command::UseTool { tool: tool.to_owned() }
    }

    fn with_tree(health: i32) -> (EngineContext, GameState) {
        make_m2_engine(|project| {
            project.scenes[0].tiles[3][3].node =
                Some(TileNode { type_id: "node-tree".to_owned(), remaining_health: health, ..TileNode::default() });
            let items = project.items.clone();
            project.player.inventory.push(slot(&items, "tool-axe", 1));
            project.player.inventory.push(slot(&items, "tool-pickaxe", 1));
        })
    }

    #[test]
    fn requires_the_right_tool() {
        let (ctx, mut state) = with_tree(4);
        let effects = apply_command(&ctx, &mut state, &use_tool("pickaxe"));
        assert!(has_message(&effects, |t| t.contains("needs a axe")));
        assert_eq!(state.world.scenes[0].tiles[3][3].node.as_ref().map(|n| n.remaining_health), Some(4));
    }

    #[test]
    fn striking_depletes_health_and_finally_drops_materials() {
        let (ctx, mut state) = with_tree(4);
        for _ in 0..4 {
            apply_command(&ctx, &mut state, &use_tool("axe"));
        }
        assert!(state.world.scenes[0].tiles[3][3].node.is_none()); // trees don't respawn
        let wood = state.player.inventory.iter().find(|s| s.item.id == "material-wood").expect("wood dropped");
        assert!((2..=4).contains(&wood.quantity));
    }

    #[test]
    fn nodes_block_movement_until_cleared() {
        let (ctx, state) = with_tree(4);
        // player (3,4); tree node at (3,3) above.
        let mut blocked = state.clone();
        apply_command(&ctx, &mut blocked, &Command::Move { dir: "up".to_owned() });
        assert_eq!(blocked.player.y, crate::units::tile_center(4));

        let mut current = state;
        for _ in 0..4 {
            apply_command(&ctx, &mut current, &use_tool("axe"));
        }
        apply_command(&ctx, &mut current, &Command::Move { dir: "up".to_owned() });
        assert_eq!(current.player.y, crate::units::tile_center(3));
    }

    #[test]
    fn respawning_nodes_come_back_after_their_respawn_window() {
        let (ctx, mut state) = make_m2_engine(|project| {
            project.scenes[0].tiles[3][3].node =
                Some(TileNode { type_id: "node-rock".to_owned(), remaining_health: 3, ..TileNode::default() });
            let items = project.items.clone();
            project.player.inventory.push(slot(&items, "tool-pickaxe", 1));
        });
        for _ in 0..3 {
            apply_command(&ctx, &mut state, &use_tool("pickaxe"));
        }
        let node = state.world.scenes[0].tiles[3][3].node.as_ref().expect("depleted node stays");
        assert_eq!(node.remaining_health, 0);
        assert_eq!(node.depleted_on_day, Some(1));

        // Rocks respawn after 3 days.
        for _ in 0..3 {
            apply_command(&ctx, &mut state, &Command::Sleep);
        }
        let node = state.world.scenes[0].tiles[3][3].node.as_ref().expect("respawned node");
        assert_eq!(node.remaining_health, 3);
        assert_eq!(node.depleted_on_day, None);
    }

    #[test]
    fn tier_gates_high_end_nodes() {
        let (ctx, state) = make_m2_engine(|project| {
            project.scenes[0].tiles[3][3].node =
                Some(TileNode { type_id: "node-boulder".to_owned(), remaining_health: 6, ..TileNode::default() });
            let items = project.items.clone();
            project.player.inventory.push(slot(&items, "tool-pickaxe", 1));
            project.player.inventory.push(slot(&items, "tool-pickaxe-2", 1));
        });
        let mut weak = state.clone();
        let effects = apply_command(&ctx, &mut weak, &use_tool("pickaxe"));
        assert!(has_message(&effects, |t| t.contains("isn't strong enough")));

        // Swap: remove the tier-1 pickaxe so the tier-2 one is found.
        let mut upgraded = state;
        upgraded.player.inventory.retain(|s| s.item.id != "tool-pickaxe");
        apply_command(&ctx, &mut upgraded, &use_tool("pickaxe"));
        // tier-2 pickaxe has power 2 → 6 → 4
        assert_eq!(upgraded.world.scenes[0].tiles[3][3].node.as_ref().map(|n| n.remaining_health), Some(4));
    }
}
