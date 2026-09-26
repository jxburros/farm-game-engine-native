//! NPC schedules and movement (port of `Npcs/NpcMovement.cs` / npcs/movement.ts).

use crate::engine_types::EngineContext;
use crate::schema::GameState;

/// C# returned a new state; here NPCs advance in place.
pub fn advance_npcs(ctx: &EngineContext, state: &mut GameState, minutes: f64) {
    let _ = (ctx, state, minutes);
    todo!("port NpcMovement.AdvanceNpcs")
}
