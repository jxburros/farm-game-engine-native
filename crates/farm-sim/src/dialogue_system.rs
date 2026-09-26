//! Dialogue (port of `DialogueSystem.cs` / dialogue.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{Dialogue, GameState};

pub fn find_dialogue<'a>(ctx: &'a EngineContext, npc_id: &str, dialogue_id: &str) -> Option<&'a Dialogue> {
    let _ = (ctx, npc_id, dialogue_id);
    todo!("port DialogueSystem.FindDialogue")
}

pub fn handle_choose_dialogue_option(ctx: &EngineContext, state: &mut GameState, index: f64) -> Effects {
    let _ = (ctx, state, index);
    todo!("port DialogueSystem.HandleChooseDialogueOption")
}

pub fn handle_close_dialogue(state: &mut GameState) -> Effects {
    let _ = state;
    todo!("port DialogueSystem.HandleCloseDialogue")
}
