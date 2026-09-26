//! Friendship and gifts (port of `Social.cs` / social.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{Dialogue, DialogueOption, GameState, Npc};

pub fn friendship_with(state: &GameState, npc_id: &str) -> f64 {
    let _ = (state, npc_id);
    todo!("port Social.FriendshipWith")
}

pub fn hearts(friendship: f64) -> f64 {
    let _ = friendship;
    todo!("port Social.Hearts")
}

pub fn gift_reaction(npc: &Npc, item_id: &str) -> String {
    let _ = (npc, item_id);
    todo!("port Social.GiftReaction")
}

pub fn handle_give_gift(ctx: &EngineContext, state: &mut GameState, item_id: &str) -> Effects {
    let _ = (ctx, state, item_id);
    todo!("port Social.HandleGiveGift")
}

pub fn visible_dialogue_options(ctx: &EngineContext, state: &GameState, dialogue: &Dialogue) -> Vec<DialogueOption> {
    let _ = (ctx, state, dialogue);
    todo!("port Social.VisibleDialogueOptions")
}
