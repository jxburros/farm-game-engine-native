//! What must hold of every state the engine reaches from a new game, whatever the content and
//! the commands (#116). The fuzz targets and the property tests check it after every step.

use farm_sim::schema::GameState;
use farm_sim::{inventory, units};

/// The first broken invariant of `state`, if any:
///
/// - money is never negative;
/// - energy is in `0..=max_energy`;
/// - every stack holds at least one unit and at most its item's stack cap;
/// - the inventory has at most `max_inventory_size` slots;
/// - the player stands inside a scene of the world (when it has one), on its grid;
/// - every scene's grid is `height` rows of `width` tiles.
pub fn check(state: &GameState) -> Result<(), String> {
    let player = &state.player;
    if player.money < 0 {
        return Err(format!("money is negative: {}", player.money));
    }
    if player.energy < 0 || player.energy > player.max_energy {
        return Err(format!("energy {} is outside 0..={}", player.energy, player.max_energy));
    }
    for (index, slot) in player.inventory.iter().enumerate() {
        let cap = inventory::stack_cap(&slot.item);
        if slot.quantity == 0 || slot.quantity > cap {
            return Err(format!("slot {index} holds {} × {} (cap {cap})", slot.quantity, slot.item.id));
        }
    }
    if player.inventory.len() > player.max_inventory_size as usize {
        return Err(format!(
            "{} inventory slots, more than the {} allowed",
            player.inventory.len(),
            player.max_inventory_size
        ));
    }
    for scene in &state.world.scenes {
        let rows_ok = usize::try_from(scene.height).is_ok_and(|height| scene.tiles.len() == height);
        let columns_ok = scene.tiles.iter().all(|row| usize::try_from(scene.width).is_ok_and(|w| row.len() == w));
        if !rows_ok || !columns_ok {
            return Err(format!("scene '{}' is not a {}×{} grid", scene.id, scene.width, scene.height));
        }
    }
    if state.world.scenes.is_empty() {
        // A game without scenes (an empty project) has nowhere to stand.
        return Ok(());
    }
    let Some(scene) = state.world.scenes.iter().find(|scene| scene.id == player.scene_id) else {
        return Err(format!("the player is in scene '{}', which the world does not have", player.scene_id));
    };
    let (x, y) = (units::tile_of(player.x), units::tile_of(player.y));
    if x < 0 || y < 0 || x >= scene.width || y >= scene.height {
        return Err(format!("the player stands on ({x}, {y}), outside the {}×{} scene", scene.width, scene.height));
    }
    Ok(())
}
