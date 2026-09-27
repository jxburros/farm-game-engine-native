//! Gathering nodes (M2): trees, rocks, weeds… content-defined node types with health, required
//! tool + tier, weighted drop tables and respawn rules. (Port of `Gathering.cs` /
//! engine-core/src/gathering.ts.)

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::hooks::{GatherDrop, HookEvent, ResourceGatherHookPayload};
use crate::js;
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
    ctx.content.node_types.iter().find(|def| def.id == type_id)
}

pub fn is_node_active(tile: &Tile) -> bool {
    tile.node.as_ref().is_some_and(|node| node.remaining_health > 0.0)
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
    x: f64,
    y: f64,
    tool_type: &str,
    tool_tier: f64,
    tool_power: f64,
) -> NodeStrikeOutcome {
    let Some(scene_index) = state.world.scenes.iter().position(|scene| scene.id == scene_id) else {
        return NodeStrikeOutcome::default();
    };
    let tile = &state.world.scenes[scene_index].tiles[y as usize][x as usize];
    let Some(node) = tile.node.clone() else {
        return NodeStrikeOutcome::default();
    };
    if node.remaining_health <= 0.0 {
        return NodeStrikeOutcome::default();
    }

    let Some(definition) = node_type_by_id(ctx, &node.type_id) else {
        return NodeStrikeOutcome::default();
    };

    if definition.required_tool != tool_type {
        return NodeStrikeOutcome {
            effects: vec![Effect::message(
                message_levels::INFO,
                format!("{} needs a {}.", definition.name, replace_first_dash(&definition.required_tool)),
            )],
            struck: false,
        };
    }
    if tool_tier < definition.required_tool_tier {
        return NodeStrikeOutcome {
            effects: vec![Effect::message(
                message_levels::INFO,
                format!(
                    "Your {} isn't strong enough for {}.",
                    replace_first_dash(tool_type),
                    definition.name.to_lowercase()
                ),
            )],
            struck: false,
        };
    }

    let damage = f64::max(1.0, tool_power);
    let remaining = node.remaining_health - damage;

    let mut effects: Effects = Vec::new();
    let mut added_drops: Vec<GatherDrop> = Vec::new();

    if remaining > 0.0 {
        state.world.scenes[scene_index].tiles[y as usize][x as usize].node =
            Some(TileNode { remaining_health: remaining, ..node.clone() });
        effects.push(Effect::message(
            message_levels::INFO,
            format!("{}: {}/{}", definition.name, js::num(remaining), js::num(definition.health)),
        ));
    } else {
        // Depleted: roll the weighted drop table once.
        let mut rng = Rng::new(state.rng.clone());
        let mut drops: Vec<GatherDrop> = Vec::new();
        if !definition.drops.is_empty() {
            let weights: Vec<f64> = definition.drops.iter().map(|drop| drop.weight).collect();
            let index = rng.weighted(&weights);
            if index >= 0 {
                let drop = &definition.drops[index as usize];
                let quantity = if drop.max > drop.min { rng.int(drop.min, drop.max) } else { drop.min };
                if quantity > 0.0 {
                    drops.push(GatherDrop { item_id: drop.item_id.clone(), quantity });
                }
            }
        }
        let rng_state = rng.state;

        let mut inventory = state.player.inventory.clone();
        let mut received: Vec<String> = Vec::new();
        for drop in &drops {
            let Some(item) = ctx.content.items.iter().find(|i| i.id == drop.item_id) else {
                continue;
            };
            let result = inventory::add_item(&inventory, item, drop.quantity, state.player.max_inventory_size, None);
            if result.added {
                inventory = result.inventory;
                received.push(format!("{}x {}", js::num(drop.quantity), item.name));
                added_drops.push(drop.clone());
            } else {
                effects.push(Effect::message(message_levels::ERROR, "Inventory is full!"));
            }
        }
        state.player.inventory = inventory;

        let depleted_on_day = state.clock.day;
        let tile = &mut state.world.scenes[scene_index].tiles[y as usize][x as usize];
        if definition.respawn_days.is_some() {
            tile.node = Some(TileNode {
                type_id: node.type_id.clone(),
                remaining_health: 0.0,
                depleted_on_day: Some(depleted_on_day),
                ..TileNode::default()
            });
        } else {
            tile.node = None;
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

        state.rng = rng_state;
    }

    // Gathered drops count toward collect objectives (M3).
    for drop in &added_drops {
        effects.extend(quests::progress_quests(ctx, state, "collect", &drop.item_id, drop.quantity));
    }

    if remaining <= 0.0 {
        // Skill XP (M4g): axes/scythes train foraging, pickaxes train mining.
        let skill = if tool_type == tool_types::PICKAXE { "mining" } else { "foraging" };
        effects.extend(skills::grant_xp(ctx, state, skill, 5.0));

        // Mine floors: breaking a rock can reveal the ladder down (M4f).
        effects.extend(mines::maybe_reveal_ladder(ctx, state, scene_id, x, y));
    }

    NodeStrikeOutcome { effects, struck: true }
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
        tile.node = Some(TileNode { type_id: "node-tree".to_owned(), remaining_health: 0.0, ..TileNode::default() });
        assert!(!is_node_active(&tile));
        tile.node = Some(TileNode { type_id: "node-tree".to_owned(), remaining_health: 1.0, ..TileNode::default() });
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

    fn with_tree(health: f64) -> (EngineContext, GameState) {
        make_m2_engine(|project| {
            project.scenes[0].tiles[3][3].node =
                Some(TileNode { type_id: "node-tree".to_owned(), remaining_health: health, ..TileNode::default() });
            let items = project.items.clone();
            project.player.inventory.push(slot(&items, "tool-axe", 1.0));
            project.player.inventory.push(slot(&items, "tool-pickaxe", 1.0));
        })
    }

    #[test]
    fn requires_the_right_tool() {
        let (ctx, mut state) = with_tree(4.0);
        let effects = apply_command(&ctx, &mut state, &use_tool("pickaxe"));
        assert!(has_message(&effects, |t| t.contains("needs a axe")));
        assert_eq!(state.world.scenes[0].tiles[3][3].node.as_ref().map(|n| n.remaining_health), Some(4.0));
    }

    #[test]
    fn striking_depletes_health_and_finally_drops_materials() {
        let (ctx, mut state) = with_tree(4.0);
        for _ in 0..4 {
            apply_command(&ctx, &mut state, &use_tool("axe"));
        }
        assert!(state.world.scenes[0].tiles[3][3].node.is_none()); // trees don't respawn
        let wood = state.player.inventory.iter().find(|s| s.item.id == "material-wood").expect("wood dropped");
        assert!((2.0..=4.0).contains(&wood.quantity));
    }

    #[test]
    fn nodes_block_movement_until_cleared() {
        let (ctx, state) = with_tree(4.0);
        // player (3,4); tree node at (3,3) above.
        let mut blocked = state.clone();
        apply_command(&ctx, &mut blocked, &Command::Move { dir: "up".to_owned() });
        assert_eq!(blocked.player.y, 4.5);

        let mut current = state;
        for _ in 0..4 {
            apply_command(&ctx, &mut current, &use_tool("axe"));
        }
        apply_command(&ctx, &mut current, &Command::Move { dir: "up".to_owned() });
        assert_eq!(current.player.y, 3.5);
    }

    #[test]
    fn respawning_nodes_come_back_after_their_respawn_window() {
        let (ctx, mut state) = make_m2_engine(|project| {
            project.scenes[0].tiles[3][3].node =
                Some(TileNode { type_id: "node-rock".to_owned(), remaining_health: 3.0, ..TileNode::default() });
            let items = project.items.clone();
            project.player.inventory.push(slot(&items, "tool-pickaxe", 1.0));
        });
        for _ in 0..3 {
            apply_command(&ctx, &mut state, &use_tool("pickaxe"));
        }
        let node = state.world.scenes[0].tiles[3][3].node.as_ref().expect("depleted node stays");
        assert_eq!(node.remaining_health, 0.0);
        assert_eq!(node.depleted_on_day, Some(1.0));

        // Rocks respawn after 3 days.
        for _ in 0..3 {
            apply_command(&ctx, &mut state, &Command::Sleep);
        }
        let node = state.world.scenes[0].tiles[3][3].node.as_ref().expect("respawned node");
        assert_eq!(node.remaining_health, 3.0);
        assert_eq!(node.depleted_on_day, None);
    }

    #[test]
    fn tier_gates_high_end_nodes() {
        let (ctx, state) = make_m2_engine(|project| {
            project.scenes[0].tiles[3][3].node =
                Some(TileNode { type_id: "node-boulder".to_owned(), remaining_health: 6.0, ..TileNode::default() });
            let items = project.items.clone();
            project.player.inventory.push(slot(&items, "tool-pickaxe", 1.0));
            project.player.inventory.push(slot(&items, "tool-pickaxe-2", 1.0));
        });
        let mut weak = state.clone();
        let effects = apply_command(&ctx, &mut weak, &use_tool("pickaxe"));
        assert!(has_message(&effects, |t| t.contains("isn't strong enough")));

        // Swap: remove the tier-1 pickaxe so the tier-2 one is found.
        let mut upgraded = state;
        upgraded.player.inventory.retain(|s| s.item.id != "tool-pickaxe");
        apply_command(&ctx, &mut upgraded, &use_tool("pickaxe"));
        // tier-2 pickaxe has power 2 → 6 → 4
        assert_eq!(upgraded.world.scenes[0].tiles[3][3].node.as_ref().map(|n| n.remaining_health), Some(4.0));
    }
}
